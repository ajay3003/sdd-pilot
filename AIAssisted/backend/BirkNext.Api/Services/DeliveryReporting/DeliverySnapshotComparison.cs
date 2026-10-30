using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

// The normalized content of one stored snapshot, as the comparison reads it. Loaded from delivery_reporting only.

public sealed record DeliveryContentTask(string Key, string Text, bool Completed)
{
    public string? TaskId { get; init; }
    public string? PhaseKey { get; init; }
    public string? UserStoryKey { get; init; }
    public string? SourcePath { get; init; }
}

public sealed record DeliveryContentPhase(string Key, string Title, int Position);

public sealed record DeliveryContentStory(string Key, string Title, string? Description, string? Priority, List<string> PhaseKeys, int Position, int Line);

public sealed record DeliveryContentCriterion(string Key, string Text, string? StoryKey, int Position);

public sealed record DeliveryContentFeature
{
    public string Key { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Module { get; init; } = "";
    public string Title { get; init; } = "";
    public int Position { get; init; }
    public string? SpecPath { get; init; }
    public string? TasksPath { get; init; }
    public string? PlanPath { get; init; }
    public string? SpecFingerprint { get; init; }
    public string? AcceptanceFingerprint { get; init; }
    public List<string> AcceptanceCriteria { get; init; } = [];
    public DeliveryStatus Status { get; init; }
    public string StatusReason { get; init; } = "";
    public int TaskTotal { get; init; }
    public int TaskCompleted { get; init; }
    public List<DeliveryContentTask> Tasks { get; init; } = [];
    /// <summary>False for snapshots imported before hierarchy support: no phases/stories/criteria rows exist, none are invented.</summary>
    public bool HierarchyCaptured { get; init; }
    public List<DeliveryContentPhase> Phases { get; init; } = [];
    public List<DeliveryContentStory> Stories { get; init; } = [];
    public List<DeliveryContentCriterion> Criteria { get; init; } = [];
}

public sealed record DeliverySnapshotContent
{
    public DeliveryReportSnapshotRef Ref { get; init; } = new();
    public DeliveryCoverage Coverage { get; init; }
    public List<DeliveryContentFeature> Features { get; init; } = [];
    public List<DeliveryImportWarning> Warnings { get; init; } = [];
    /// <summary>Path → fingerprint of readable source files, for supporting evidence only.</summary>
    public Dictionary<string, (string Module, string Fingerprint)> SourceFiles { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DeliveryStatusChange(string FeatureKey, string Kind, string? From, string? To, string Detail);

public sealed record DeliveryComparisonResult(List<DeliveryStoryRow> Stories, List<DeliveryStatusChange> Events, List<DeliveryAttentionItem> Attention,
    int FeaturesAdded, int FeaturesRemoved, int FeaturesChanged)
{
    public List<DeliveryScopeChange> ScopeChanges { get; init; } = [];
}

/// <summary>
/// Compares two Delivery Reporting snapshots feature by feature. Deterministic: the same two snapshots always give the same rows, in the same
/// order. Identity is the module + feature folder; a feature whose module path changed (e.g. a different archive wrapper) is matched by its
/// unique folder name. Movement comes from task checkboxes, explicit statuses and specification fingerprints — never from timestamps — and
/// source changes are attached as supporting evidence only.
/// </summary>
public static class DeliverySnapshotComparison
{
    private const int EvidenceListLimit = 50;
    private static readonly Regex FeatureNumberRe = new(@"^(\d{1,4})[-_]", RegexOptions.Compiled);

    public static DeliveryComparisonResult Compare(DeliverySnapshotContent baseline, DeliverySnapshotContent current)
    {
        var pairs = Match(baseline.Features, current.Features);
        var stories = new List<DeliveryStoryRow>();
        var events = new List<DeliveryStatusChange>();
        var attention = new List<DeliveryAttentionItem>();
        var sourceByModule = SourceChanges(baseline.SourceFiles, current.SourceFiles);

        foreach (var (b, c) in pairs)
        {
            var row = c is null ? Removed(b!, events, attention)
                : b is null ? Added(c, events, attention)
                : Changed(b, c, events, attention);
            row = row with { Hierarchy = Hierarchy(b, c) };
            if (c is not null && row.Hierarchy is { Captured: true } h) HierarchyAttention(c, h, attention);
            var module = (c ?? b)!.Module;
            if (c is not null && sourceByModule.TryGetValue(module, out var source))
                row = row with { Evidence = row.Evidence with { SourceSummary = source } };
            stories.Add(row);
        }

        // Ambiguous identity: a removed and an added feature share a module and feature number (renamed folder?). Reported, never merged.
        var removed = stories.Where(s => s.RemovedFeature).ToList();
        foreach (var added in stories.Where(s => s.NewFeature))
        {
            var number = Number(added.FeatureKey);
            if (number is null) continue;
            var twin = removed.FirstOrDefault(r => r.Module == added.Module && Number(r.FeatureKey) == number);
            if (twin is null) continue;
            attention.Add(new(DeliveryAttentionKind.AmbiguousFeatureIdentity, DeliveryAttentionSeverity.Review, added.Title,
                $"Feature {number:000} appears as \"{added.Title}\" now and as \"{twin.Title}\" in the baseline. It may have been renamed; check before reading it as new + removed.", added.FeatureKey));
        }

        foreach (var warning in current.Warnings.Where(w => w.Code == "TASKS_UNMAPPED"))
            attention.Add(new(DeliveryAttentionKind.UnmappedArtifact, DeliveryAttentionSeverity.Review, "Task file outside a feature",
                "A tasks.md file is not inside a feature folder, so its tasks are not counted in any story."));
        foreach (var warning in current.Warnings.Where(w => w.Code == "TASK_STORY_UNKNOWN"))
            attention.Add(new(DeliveryAttentionKind.BrokenTraceability, DeliveryAttentionSeverity.Review, "Task references an unknown user story", warning.Message));
        foreach (var warning in current.Warnings.Where(w => w.Code == "DUPLICATE_FEATURE_ID"))
            attention.Add(new(DeliveryAttentionKind.AmbiguousFeatureIdentity, DeliveryAttentionSeverity.Review, "Duplicate feature number", warning.Message));

        var attentionKeys = attention.Where(a => a.FeatureKey is not null).GroupBy(a => a.FeatureKey!).ToDictionary(g => g.Key, g => g.Select(a => DeliveryReportingText.Attention(a.Kind)).Distinct().ToList());
        stories = stories.Select(s => attentionKeys.TryGetValue(s.FeatureKey, out var labels) ? s with { Attention = labels, HasActiveAttention = true } : s).ToList();

        var scope = stories.Where(s => s.NewFeature || s.RemovedFeature).Select(s => s.RemovedFeature
            ? new DeliveryScopeChange("Removed", s.FeatureKey, s.Title, s.Module, $"In the baseline, not in the current snapshot. Last known baseline: {s.BaselineTaskCompleted}/{s.BaselineTaskTotal} tasks. Confirm whether the removal was intentional.")
            : new DeliveryScopeChange("Added", s.FeatureKey, s.Title, s.Module, $"New this period. Current state: {s.CurrentTaskCompleted}/{s.CurrentTaskTotal} tasks (not counted as completed this period).")).ToList();
        return new(stories, events, attention,
            stories.Count(s => s.NewFeature), stories.Count(s => s.RemovedFeature), stories.Count(s => s.Changed)) { ScopeChanges = scope };
    }

    private static List<(DeliveryContentFeature? Baseline, DeliveryContentFeature? Current)> Match(List<DeliveryContentFeature> baseline, List<DeliveryContentFeature> current)
    {
        var byKey = baseline.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var pairs = new List<(DeliveryContentFeature?, DeliveryContentFeature?)>();
        var unmatchedCurrent = new List<DeliveryContentFeature>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in current.OrderBy(f => f.Position))
            if (byKey.TryGetValue(feature.Key, out var b)) { pairs.Add((b, feature)); used.Add(b.Key); }
            else unmatchedCurrent.Add(feature);

        var remainingBaseline = baseline.Where(f => !used.Contains(f.Key)).ToList();
        foreach (var feature in unmatchedCurrent)
        {
            var candidates = remainingBaseline.Where(b => b.Folder.Equals(feature.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
            var sameFolderInCurrent = unmatchedCurrent.Count(c => c.Folder.Equals(feature.Folder, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 1 && sameFolderInCurrent == 1)
            {
                pairs.Add((candidates[0], feature));
                remainingBaseline.Remove(candidates[0]);
            }
            else pairs.Add((null, feature));
        }
        foreach (var feature in remainingBaseline.OrderBy(f => f.Position)) pairs.Add((feature, null));
        return pairs.OrderBy(p => (p.Item2 ?? p.Item1)!.Module, StringComparer.Ordinal)
            .ThenBy(p => (p.Item2 ?? p.Item1)!.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DeliveryStoryRow Added(DeliveryContentFeature c, List<DeliveryStatusChange> events, List<DeliveryAttentionItem> attention)
    {
        events.Add(new(c.Key, "FeatureAdded", null, c.Status.ToString(), $"New feature with {c.TaskCompleted}/{c.TaskTotal} tasks completed"));
        CurrentStateAttention(c, attention);
        return new DeliveryStoryRow
        {
            FeatureKey = c.Key, Title = c.Title, Module = c.Module, CurrentStatus = c.Status, Movement = DeliveryMovement.NewThisPeriod,
            CurrentTaskCompleted = c.TaskCompleted, CurrentTaskTotal = c.TaskTotal, TaskDelta = c.TaskCompleted,
            // The baseline cannot prove when a new feature's checked tasks were completed: they are inventory, not completed this period.
            TasksCompletedThisPeriod = 0, NewFeature = true,
            Reasons = [c.TaskTotal > 0 ? $"New this period; current state {c.TaskCompleted} of {c.TaskTotal} tasks checked" : "New this period; no task progress yet"],
            Evidence = new DeliveryStoryEvidence
            {
                SpecPath = c.SpecPath, TasksPath = c.TasksPath, PlanPath = c.PlanPath, CurrentStatusReason = c.StatusReason,
                TasksCompleted = c.Tasks.Where(t => t.Completed).Select(t => t.Text).Take(EvidenceListLimit).ToList(),
            },
        };
    }

    private static DeliveryStoryRow Removed(DeliveryContentFeature b, List<DeliveryStatusChange> events, List<DeliveryAttentionItem> attention)
    {
        // A removal is a scope change (listed under Scope changes), not a quality problem.
        events.Add(new(b.Key, "FeatureRemoved", b.Status.ToString(), null, "Feature folder no longer present"));
        return new DeliveryStoryRow
        {
            FeatureKey = b.Key, Title = b.Title, Module = b.Module, BaselineStatus = b.Status, Movement = DeliveryMovement.Removed,
            BaselineTaskCompleted = b.TaskCompleted, BaselineTaskTotal = b.TaskTotal, TaskDelta = -b.TaskCompleted, RemovedFeature = true,
            Reasons = ["Not present in the current snapshot – needs review (not treated as completed)"],
            Evidence = new DeliveryStoryEvidence { SpecPath = b.SpecPath, TasksPath = b.TasksPath, PlanPath = b.PlanPath, BaselineStatusReason = b.StatusReason },
        };
    }

    private static DeliveryStoryRow Changed(DeliveryContentFeature b, DeliveryContentFeature c, List<DeliveryStatusChange> events, List<DeliveryAttentionItem> attention)
    {
        var baseTasks = b.Tasks.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var curTasks = c.Tasks.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // Completed this period = matched task, open in the baseline and checked now. A new task that is already checked is inventory growth.
        var completedNow = c.Tasks.Where(t => t.Completed && baseTasks.TryGetValue(t.Key, out var bt) && !bt.Completed).ToList();
        var reopened = c.Tasks.Where(t => !t.Completed && baseTasks.TryGetValue(t.Key, out var bt) && bt.Completed).ToList();
        var added = c.Tasks.Where(t => !baseTasks.ContainsKey(t.Key)).ToList();
        var removedTasks = b.Tasks.Where(t => !curTasks.ContainsKey(t.Key)).ToList();

        var specChanged = b.SpecFingerprint != c.SpecFingerprint && (b.SpecFingerprint is not null || c.SpecFingerprint is not null);
        var acceptanceChanged = b.AcceptanceFingerprint is not null && c.AcceptanceFingerprint is not null && b.AcceptanceFingerprint != c.AcceptanceFingerprint;
        var acceptanceAdded = c.AcceptanceCriteria.Except(b.AcceptanceCriteria, StringComparer.Ordinal).ToList();
        var acceptanceRemoved = b.AcceptanceCriteria.Except(c.AcceptanceCriteria, StringComparer.Ordinal).ToList();
        var delta = c.TaskCompleted - b.TaskCompleted;
        var progress = $"task completion changed from {b.TaskCompleted}/{b.TaskTotal} to {c.TaskCompleted}/{c.TaskTotal}";
        var reasons = new List<string>();

        DeliveryMovement movement;
        if (c.Status == DeliveryStatus.Blocked && b.Status != DeliveryStatus.Blocked)
        { movement = DeliveryMovement.Regressed; reasons.Add("Became blocked"); }
        else if (delta < 0)
        { movement = DeliveryMovement.Regressed; reasons.Add($"Task completion went backwards: {progress}"); }
        else if (DeliveryStatusDerivation.IsComplete(c.Status) && !DeliveryStatusDerivation.IsComplete(b.Status))
        { movement = DeliveryMovement.Completed; reasons.Add(c.Status == DeliveryStatus.Done ? "Marked Done" : $"All tasks completed: {progress}"); }
        else if (completedNow.Count > 0)
        { movement = DeliveryMovement.Progressed; reasons.Add($"{completedNow.Count} task(s) completed this period; {progress}"); }
        else if (DeliveryStatusDerivation.Rank(c.Status) > DeliveryStatusDerivation.Rank(b.Status) && DeliveryStatusDerivation.Rank(b.Status) >= 0)
        { movement = DeliveryMovement.Progressed; reasons.Add($"Status moved from {DeliveryReportingText.Status(b.Status)} to {DeliveryReportingText.Status(c.Status)}"); }
        else if (b.Status == DeliveryStatus.Blocked && c.Status != DeliveryStatus.Blocked)
        { movement = DeliveryMovement.Progressed; reasons.Add("No longer blocked"); }
        else if (specChanged || added.Count > 0 || removedTasks.Count > 0 || reopened.Count > 0 || b.Status != c.Status)
        { movement = DeliveryMovement.NeedsReview; }
        else movement = DeliveryMovement.NoSignificantChange;

        if (b.Status != c.Status)
        {
            events.Add(new(c.Key, "StatusChanged", b.Status.ToString(), c.Status.ToString(), $"{DeliveryReportingText.Status(b.Status)} → {DeliveryReportingText.Status(c.Status)}"));
            if (movement == DeliveryMovement.NeedsReview) reasons.Add($"Status changed from {DeliveryReportingText.Status(b.Status)} to {DeliveryReportingText.Status(c.Status)}");
        }
        if (acceptanceChanged) reasons.Add($"Acceptance criteria changed ({acceptanceAdded.Count} added, {acceptanceRemoved.Count} removed)");
        else if (specChanged) reasons.Add("Specification text changed");
        if (added.Count > 0) reasons.Add($"{added.Count} task(s) added");
        if (removedTasks.Count > 0) reasons.Add($"{removedTasks.Count} task(s) removed");
        if (reopened.Count > 0) reasons.Add($"{reopened.Count} task(s) reopened");
        if (movement == DeliveryMovement.NoSignificantChange) reasons.Add($"Unchanged at {c.TaskCompleted}/{c.TaskTotal} tasks");

        foreach (var t in completedNow) events.Add(new(c.Key, "TaskCompleted", null, null, t.Text));
        foreach (var t in reopened) events.Add(new(c.Key, "TaskReopened", null, null, t.Text));
        foreach (var t in added) events.Add(new(c.Key, "TaskAdded", null, null, t.Text));
        foreach (var t in removedTasks) events.Add(new(c.Key, "TaskRemoved", null, null, t.Text));

        CurrentStateAttention(c, attention);
        if (delta < 0 || reopened.Count > 0)
            attention.Add(new(DeliveryAttentionKind.TaskCompletionWentBackwards, DeliveryAttentionSeverity.Review, c.Title,
                $"Completed tasks went from {b.TaskCompleted} to {c.TaskCompleted}{(reopened.Count > 0 ? $"; {reopened.Count} task(s) were unchecked" : "")}.", c.Key));
        if (removedTasks.Count > 0 && c.TaskTotal < b.TaskTotal)
            attention.Add(new(DeliveryAttentionKind.TasksDisappeared, DeliveryAttentionSeverity.Review, c.Title,
                $"{removedTasks.Count} task(s) disappeared from tasks.md (total {b.TaskTotal} → {c.TaskTotal}).", c.Key));

        return new DeliveryStoryRow
        {
            FeatureKey = c.Key, Title = c.Title, Module = c.Module, BaselineStatus = b.Status, CurrentStatus = c.Status, Movement = movement,
            BaselineTaskCompleted = b.TaskCompleted, BaselineTaskTotal = b.TaskTotal, CurrentTaskCompleted = c.TaskCompleted, CurrentTaskTotal = c.TaskTotal,
            TaskDelta = delta, TasksCompletedThisPeriod = completedNow.Count, SpecificationChanged = specChanged, AcceptanceCriteriaChanged = acceptanceChanged,
            Reasons = reasons,
            Evidence = new DeliveryStoryEvidence
            {
                SpecPath = c.SpecPath, TasksPath = c.TasksPath, PlanPath = c.PlanPath,
                BaselineStatusReason = b.StatusReason, CurrentStatusReason = c.StatusReason,
                AcceptanceAdded = acceptanceAdded.Take(EvidenceListLimit).ToList(), AcceptanceRemoved = acceptanceRemoved.Take(EvidenceListLimit).ToList(),
                TasksCompleted = completedNow.Select(t => t.Text).Take(EvidenceListLimit).ToList(),
                TasksReopened = reopened.Select(t => t.Text).Take(EvidenceListLimit).ToList(),
                TasksAdded = added.Select(t => t.Text).Take(EvidenceListLimit).ToList(),
                TasksRemoved = removedTasks.Select(t => t.Text).Take(EvidenceListLimit).ToList(),
            },
        };
    }

    // ── Hierarchy ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Feature → Phase → User Story → Acceptance Criteria with traceable tasks and their change. Removed feature = baseline state, new = current
    /// state. Stories match by key (US2); criteria match by exact text first, then by key; tasks by task key. Nothing is linked by similar text.
    /// </summary>
    public static DeliveryFeatureHierarchy Hierarchy(DeliveryContentFeature? b, DeliveryContentFeature? c)
    {
        var side = c ?? b!;
        if (!side.HierarchyCaptured || b is not null && c is not null && !b.HierarchyCaptured)
            return new DeliveryFeatureHierarchy { Captured = false, Tasks = TaskRows(b, c) };
        var tasks = TaskRows(b, c);
        var criteria = CriterionRows(b?.Criteria ?? [], c?.Criteria ?? [], b is null, c is null);
        var storyKeys = (c?.Stories ?? []).Select(s => s.Key).Concat((b?.Stories ?? []).Select(s => s.Key)).Distinct(StringComparer.Ordinal).ToList();
        var stories = storyKeys.Select(key =>
        {
            var bs = b?.Stories.FirstOrDefault(s => s.Key == key);
            var cs = c?.Stories.FirstOrDefault(s => s.Key == key);
            var s = cs ?? bs!;
            var own = criteria.Where(x => x.StoryKey == key).ToList();
            var storyTasks = tasks.Where(t => t.UserStoryKey == key && t.Change != DeliveryTaskChange.Removed).ToList();
            var textChanged = bs is not null && cs is not null && (bs.Title != cs.Title || bs.Description != cs.Description);
            var criteriaChanged = own.Any(x => x.Change != DeliveryItemChange.Unchanged);
            var change = b is null || bs is null ? DeliveryItemChange.Added : c is null || cs is null ? DeliveryItemChange.Removed
                : textChanged || criteriaChanged ? DeliveryItemChange.Changed : DeliveryItemChange.Unchanged;
            if (b is null) change = DeliveryItemChange.Unchanged;
            if (c is null) change = DeliveryItemChange.Unchanged;
            return new DeliveryUserStoryRow(key, s.Title, change)
            {
                Description = s.Description, Priority = s.Priority, PhaseKeys = [.. s.PhaseKeys], Criteria = own, TaskTotal = storyTasks.Count, TaskCompleted = storyTasks.Count(t => t.Completed),
                TextChanged = textChanged, CriteriaChanged = criteriaChanged, SourceLine = s.Line,
            };
        }).OrderBy(s => (c?.Stories ?? b!.Stories).FindIndex(x => x.Key == s.Key) is var i and >= 0 ? i : 1000 + (b?.Stories.FindIndex(x => x.Key == s.Key) ?? 0)).ToList();
        var phaseKeys = (c?.Phases ?? []).Concat(b?.Phases ?? []).GroupBy(p => p.Key).Select(g => g.First()).ToList();
        var phases = phaseKeys.Select(p =>
        {
            var inB = b?.Phases.Any(x => x.Key == p.Key) == true;
            var inC = c?.Phases.Any(x => x.Key == p.Key) == true;
            var phaseTasks = tasks.Where(t => t.PhaseKey == p.Key).ToList();
            var changes = phaseTasks.Count(t => t.Change != DeliveryTaskChange.Unchanged);
            var change = b is null || c is null ? DeliveryItemChange.Unchanged : !inB ? DeliveryItemChange.Added : !inC ? DeliveryItemChange.Removed
                : changes > 0 ? DeliveryItemChange.Changed : DeliveryItemChange.Unchanged;
            var live = phaseTasks.Where(t => t.Change != DeliveryTaskChange.Removed).ToList();
            return new DeliveryPhaseRow(p.Key, p.Title, change)
            {
                Position = p.Position, TaskTotal = live.Count, TaskCompleted = live.Count(t => t.Completed), Changes = changes,
                StoryKeys = [.. stories.Where(s => s.PhaseKeys.Contains(p.Key)).Select(s => s.Key)],
            };
        }).OrderBy(p => p.Position).ToList();
        var pair = b is not null && c is not null;
        return new DeliveryFeatureHierarchy
        {
            Captured = true, Phases = phases, Stories = stories, FeatureCriteria = [.. criteria.Where(x => x.StoryKey is null)], Tasks = tasks,
            CriteriaBaseline = b?.Criteria.Count ?? 0, CriteriaCurrent = c?.Criteria.Count ?? 0,
            CriteriaAdded = pair ? criteria.Count(x => x.Change == DeliveryItemChange.Added) : 0, CriteriaChanged = criteria.Count(x => x.Change == DeliveryItemChange.Changed),
            CriteriaRemoved = pair ? criteria.Count(x => x.Change == DeliveryItemChange.Removed) : 0,
            StoriesAdded = stories.Count(s => s.Change == DeliveryItemChange.Added), StoriesRemoved = stories.Count(s => s.Change == DeliveryItemChange.Removed),
            StoriesChanged = stories.Count(s => s.Change == DeliveryItemChange.Changed),
            TasksAdded = pair ? tasks.Count(t => t.Change == DeliveryTaskChange.New) : 0, TasksRemoved = tasks.Count(t => t.Change == DeliveryTaskChange.Removed),
            TasksReopened = tasks.Count(t => t.Change == DeliveryTaskChange.Reopened), TasksTextChanged = tasks.Count(t => t.Change == DeliveryTaskChange.TextChanged),
        };
    }

    private static List<DeliveryTaskRow> TaskRows(DeliveryContentFeature? b, DeliveryContentFeature? c)
    {
        DeliveryTaskRow Row(DeliveryContentTask t, DeliveryTaskChange change) =>
            new(t.Key, t.TaskId, t.Text, t.Completed, change) { PhaseKey = t.PhaseKey, UserStoryKey = t.UserStoryKey, SourcePath = t.SourcePath };
        if (c is null) return [.. b!.Tasks.Select(t => Row(t, DeliveryTaskChange.Unchanged))];
        if (b is null) return [.. c.Tasks.Select(t => Row(t, DeliveryTaskChange.Unchanged))];
        var baseTasks = b.Tasks.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var curKeys = c.Tasks.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var rows = c.Tasks.Select(t => Row(t, !baseTasks.TryGetValue(t.Key, out var bt) ? DeliveryTaskChange.New
            : !bt.Completed && t.Completed ? DeliveryTaskChange.CompletedThisPeriod
            : bt.Completed && !t.Completed ? DeliveryTaskChange.Reopened
            : bt.Text != t.Text ? DeliveryTaskChange.TextChanged : DeliveryTaskChange.Unchanged)).ToList();
        rows.AddRange(b.Tasks.Where(t => !curKeys.Contains(t.Key)).Select(t => Row(t, DeliveryTaskChange.Removed)));
        return rows;
    }

    /// <summary>Per owner (story or feature): identical text = unchanged; then same key with different text = changed; the rest added/removed.</summary>
    private static List<DeliveryCriterionRow> CriterionRows(List<DeliveryContentCriterion> b, List<DeliveryContentCriterion> c, bool onlyCurrent, bool onlyBaseline)
    {
        var rows = new List<DeliveryCriterionRow>();
        if (onlyCurrent || onlyBaseline)
            return [.. (onlyCurrent ? c : b).Select(x => new DeliveryCriterionRow(x.Key, x.Text, DeliveryItemChange.Unchanged) { StoryKey = x.StoryKey })];
        foreach (var owner in c.Select(x => x.StoryKey).Concat(b.Select(x => x.StoryKey)).Distinct())
        {
            var bo = b.Where(x => x.StoryKey == owner).ToList();
            var co = c.Where(x => x.StoryKey == owner).ToList();
            var unmatchedB = bo.Where(x => co.All(y => y.Text != x.Text)).ToList();
            foreach (var x in co)
            {
                if (bo.Any(y => y.Text == x.Text)) { rows.Add(new(x.Key, x.Text, DeliveryItemChange.Unchanged) { StoryKey = owner }); continue; }
                var same = unmatchedB.FirstOrDefault(y => y.Key == x.Key);
                if (same is not null) { unmatchedB.Remove(same); rows.Add(new(x.Key, x.Text, DeliveryItemChange.Changed) { StoryKey = owner, BaselineText = same.Text }); }
                else rows.Add(new(x.Key, x.Text, DeliveryItemChange.Added) { StoryKey = owner });
            }
            rows.AddRange(unmatchedB.Select(x => new DeliveryCriterionRow(x.Key, x.Text, DeliveryItemChange.Removed) { StoryKey = owner }));
        }
        return rows;
    }

    /// <summary>Broken or missing traceability in the current snapshot (quality problems, not change events).</summary>
    private static void HierarchyAttention(DeliveryContentFeature c, DeliveryFeatureHierarchy h, List<DeliveryAttentionItem> attention)
    {
        // Story task coverage is expected only when the feature's tasks use story tags at all.
        if (c.Tasks.Any(t => t.UserStoryKey is not null))
            foreach (var story in h.Stories.Where(s => s.Change != DeliveryItemChange.Removed && s.TaskTotal == 0))
                attention.Add(new(DeliveryAttentionKind.BrokenTraceability, DeliveryAttentionSeverity.Review, c.Title,
                    $"{story.Key} \"{story.Title}\" has no traceable tasks, while other stories of this feature do.", c.Key));
    }

    /// <summary>Attention that holds in the current snapshot regardless of movement: blocked, clarification, specification without tasks.</summary>
    private static void CurrentStateAttention(DeliveryContentFeature c, List<DeliveryAttentionItem> attention)
    {
        if (c.Status == DeliveryStatus.Blocked)
            attention.Add(new(DeliveryAttentionKind.Blocked, DeliveryAttentionSeverity.Blocker, c.Title, c.StatusReason, c.Key));
        if (c.Status == DeliveryStatus.NeedsClarification)
            attention.Add(new(DeliveryAttentionKind.NeedsClarification, DeliveryAttentionSeverity.Review, c.Title, c.StatusReason, c.Key));
        if (c.SpecPath is not null && c.TasksPath is null)
            attention.Add(new(DeliveryAttentionKind.SpecificationWithoutTasks, DeliveryAttentionSeverity.Review, c.Title,
                "The feature has a specification but no tasks.md, so its progress cannot be tracked.", c.Key));
    }

    private static Dictionary<string, string> SourceChanges(Dictionary<string, (string Module, string Fingerprint)> baseline, Dictionary<string, (string Module, string Fingerprint)> current)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseline.Count == 0 && current.Count == 0) return result;
        var modules = baseline.Values.Select(v => v.Module).Concat(current.Values.Select(v => v.Module)).Distinct(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var b = baseline.Where(p => p.Value.Module == module).ToDictionary(p => p.Key, p => p.Value.Fingerprint, StringComparer.Ordinal);
            var c = current.Where(p => p.Value.Module == module).ToDictionary(p => p.Key, p => p.Value.Fingerprint, StringComparer.Ordinal);
            var addedCount = c.Keys.Count(k => !b.ContainsKey(k));
            var removedCount = b.Keys.Count(k => !c.ContainsKey(k));
            var modified = c.Count(p => b.TryGetValue(p.Key, out var f) && f != p.Value);
            var total = addedCount + removedCount + modified;
            result[module] = total == 0
                ? $"No source file changes in {Label(module)} (supporting evidence only)"
                : $"{total} source file(s) changed in {Label(module)}: {modified} modified, {addedCount} added, {removedCount} removed (supporting evidence only, not a status)";
        }
        return result;
    }

    private static string Label(string module) => module.Length == 0 ? "the project" : module == "Project-level" ? "the project" : module;

    private static int? Number(string featureKey)
    {
        var folder = featureKey[(featureKey.LastIndexOf("::", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..];
        var match = FeatureNumberRe.Match(folder);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
}
