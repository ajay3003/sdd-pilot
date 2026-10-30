using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

// The normalized content of one stored snapshot, as the comparison reads it. Loaded from delivery_reporting only.

public sealed record DeliveryContentTask(string Key, string Text, bool Completed);

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
    int FeaturesAdded, int FeaturesRemoved, int FeaturesChanged);

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
        foreach (var warning in current.Warnings.Where(w => w.Code == "DUPLICATE_FEATURE_ID"))
            attention.Add(new(DeliveryAttentionKind.AmbiguousFeatureIdentity, DeliveryAttentionSeverity.Review, "Duplicate feature number", warning.Message));

        var attentionKeys = attention.Where(a => a.FeatureKey is not null).GroupBy(a => a.FeatureKey!).ToDictionary(g => g.Key, g => g.Select(a => DeliveryReportingText.Attention(a.Kind)).Distinct().ToList());
        stories = stories.Select(s => attentionKeys.TryGetValue(s.FeatureKey, out var labels) ? s with { Attention = labels, HasActiveAttention = true } : s).ToList();

        return new(stories, events, attention,
            stories.Count(s => s.NewFeature), stories.Count(s => s.RemovedFeature), stories.Count(s => s.Changed));
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
            TasksCompletedThisPeriod = c.TaskCompleted, NewFeature = true,
            Reasons = [c.TaskTotal > 0 ? $"New this period with {c.TaskCompleted} of {c.TaskTotal} tasks completed" : "New this period; no task progress yet"],
            Evidence = new DeliveryStoryEvidence
            {
                SpecPath = c.SpecPath, TasksPath = c.TasksPath, PlanPath = c.PlanPath, CurrentStatusReason = c.StatusReason,
                TasksCompleted = c.Tasks.Where(t => t.Completed).Select(t => t.Text).Take(EvidenceListLimit).ToList(),
            },
        };
    }

    private static DeliveryStoryRow Removed(DeliveryContentFeature b, List<DeliveryStatusChange> events, List<DeliveryAttentionItem> attention)
    {
        events.Add(new(b.Key, "FeatureRemoved", b.Status.ToString(), null, "Feature folder no longer present"));
        attention.Add(new(DeliveryAttentionKind.FeatureRemoved, DeliveryAttentionSeverity.Review, b.Title,
            $"\"{b.Title}\" is in the baseline but not in the current snapshot ({b.TaskCompleted}/{b.TaskTotal} tasks were completed). Removed is not completed: confirm whether it was descoped, merged or moved.", b.Key));
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
        var completedNow = c.Tasks.Where(t => t.Completed && !(baseTasks.TryGetValue(t.Key, out var bt) && bt.Completed)).ToList();
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
        else if (delta > 0 || completedNow.Count > 0)
        { movement = DeliveryMovement.Progressed; reasons.Add(delta > 0 ? $"Progressed: {progress}" : $"{completedNow.Count} task(s) completed this period"); }
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
        if (acceptanceChanged && b.TaskCompleted > 0)
            attention.Add(new(DeliveryAttentionKind.AcceptanceCriteriaChanged, DeliveryAttentionSeverity.Review, c.Title,
                $"Acceptance criteria changed after work started ({acceptanceAdded.Count} added, {acceptanceRemoved.Count} removed). Check that completed tasks still cover them.", c.Key));

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
