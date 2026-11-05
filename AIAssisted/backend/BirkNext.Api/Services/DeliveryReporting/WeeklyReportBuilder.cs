using System.Globalization;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Builds the frozen weekly report document from a comparison. Pure and deterministic: the inputs (two stored snapshots, the report id and
/// generation time, the reporting time zone) fully decide the output. The report week is the ISO week of the current snapshot's effective
/// date; coverage says how well the baseline matches that week.
/// </summary>
public static class WeeklyReportBuilder
{
    public static WeeklyDeliveryReport Build(Guid reportId, Guid projectId, string projectName, DateTimeOffset generatedAt,
        DeliverySnapshotContent baseline, DeliverySnapshotContent current, DeliveryComparisonResult comparison, TimeZoneInfo zone,
        DeliveryDirectionReview? direction = null)
    {
        var currentLocal = TimeZoneInfo.ConvertTime(current.Ref.EffectiveAt, zone);
        var baselineLocal = TimeZoneInfo.ConvertTime(baseline.Ref.EffectiveAt, zone);
        var weekStartDate = DateOnly.FromDateTime(currentLocal.Date).AddDays(-(((int)currentLocal.DayOfWeek + 6) % 7));
        var weekStart = new DateTimeOffset(weekStartDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(weekStartDate.ToDateTime(TimeOnly.MinValue)));
        var weekLabel = $"{ISOWeek.GetYear(currentLocal.DateTime)}-W{ISOWeek.GetWeekOfYear(currentLocal.DateTime):00}";

        var (coverage, notes) = Coverage(baseline, current, DateOnly.FromDateTime(baselineLocal.DateTime), weekStartDate);
        var (kind, warnings) = Period(baseline, current, DateOnly.FromDateTime(baselineLocal.DateTime), DateOnly.FromDateTime(currentLocal.DateTime), weekStartDate);
        var stories = comparison.Stories;
        var currentStatuses = current.Features.Select(f => f.Status).ToList();
        var explicitEvidence = current.Features.Concat(baseline.Features).Any(f => DeliveryReportingText.IsExplicitOnly(f.Status));

        // Rows are features (a Spec-Kit feature folder), so the metrics say Features; user stories are counted from the parsed hierarchy only.
        var hierarchies = stories.Select(s => s.Hierarchy).OfType<DeliveryFeatureHierarchy>().Where(h => h.Captured).ToList();
        var paired = stories.Where(s => !s.NewFeature && !s.RemovedFeature && s.Hierarchy is { Captured: true }).Select(s => s.Hierarchy!).ToList();
        const string Inventory = DeliveryMetricGroups.Inventory, Quality = DeliveryMetricGroups.Quality;
        var metrics = new List<DeliveryReportMetric>
        {
            new("features-changed", "Features changed", stories.Count(s => s.Changed), "Features with any change between baseline and current"),
            new("moved-forward", "Moved forward", stories.Count(s => s.Movement is DeliveryMovement.Progressed or DeliveryMovement.Completed), "Features with tasks completed this period, or all tasks completed"),
            new("completed", "All tasks completed", stories.Count(s => s.Movement == DeliveryMovement.Completed), "All tasks checked (or marked Done) this period — task completion is not delivery done"),
            new("tasks-completed", "Tasks completed this period", stories.Sum(s => s.TasksCompletedThisPeriod), "Matched tasks open in the baseline and checked now"),
            new("new-features", "New features", stories.Count(s => s.NewFeature)),
            new("removed-features", "Removed features", stories.Count(s => s.RemovedFeature), "Scope changes to confirm"),
            new("specifications-changed", "Specifications changed", stories.Count(s => s.SpecificationChanged && !s.NewFeature && !s.RemovedFeature)),
        };
        if (paired.Count > 0)
        {
            metrics.Add(new("stories-changed", "User stories changed", paired.Sum(h => h.StoriesAdded + h.StoriesRemoved + h.StoriesChanged), "Added, removed or changed user stories in retained features"));
            metrics.Add(new("criteria-changed", "Acceptance criteria changed", paired.Sum(h => h.CriteriaAdded + h.CriteriaChanged + h.CriteriaRemoved), "Added, changed or removed criteria in retained features"));
            metrics.Add(new("tasks-added", "Tasks added to inventory", paired.Sum(h => h.TasksAdded), "New tasks in retained features (inventory growth, not work completed)"));
            metrics.Add(new("tasks-removed", "Tasks removed from inventory", paired.Sum(h => h.TasksRemoved), "Tasks no longer present in retained features (never counted as reopened)"));
            metrics.Add(new("tasks-reopened", "Matched tasks reopened", paired.Sum(h => h.TasksReopened), "Matched tasks checked in the baseline and unchecked now"));
        }
        // Current inventory: the current snapshot's totals — never a period delta.
        metrics.Add(new("inventory-features", "Features", current.Features.Count) { Group = Inventory });
        var liveStories = stories.Where(s => !s.RemovedFeature).SelectMany(s => s.Hierarchy?.Stories ?? []).Where(x => x.Change != DeliveryItemChange.Removed && !x.Removed).ToList();
        if (hierarchies.Count > 0) metrics.Add(new("inventory-stories", "User stories", liveStories.Count(x => x.Kind != DeliveryWorkItemRelationships.ScenarioKind), "User Stories defined in spec.md (scenarios and removed stories excluded)") { Group = Inventory });
        if (liveStories.Any(x => x.Kind == DeliveryWorkItemRelationships.ScenarioKind))
            metrics.Add(new("inventory-scenarios", "Scenarios", liveStories.Count(x => x.Kind == DeliveryWorkItemRelationships.ScenarioKind), "Scenario headings of scenario-based specifications") { Group = Inventory });
        metrics.Add(new("inventory-tasks", "Tasks", current.Features.Sum(f => f.TaskTotal)) { Group = Inventory });
        metrics.Add(new("inventory-checked", "Checked tasks", current.Features.Sum(f => f.TaskCompleted)) { Group = Inventory });
        metrics.Add(new("inventory-unchecked", "Unchecked tasks", current.Features.Sum(f => f.TaskTotal - f.TaskCompleted)) { Group = Inventory });
        metrics.Add(new("inventory-no-tasks", "Features with no task data", current.Features.Count(f => f.TasksPath is null), "No tasks.md: progress cannot be derived from tasks") { Group = Inventory });
        // Quality and review: signals about the evidence, never delivery status.
        metrics.Add(new("quality-scope-review", "Scope changes to review", comparison.ScopeChanges.Count, "Added, removed or missing features to confirm — not defects") { Group = Quality });
        metrics.Add(new("quality-traceability", "Traceability and source issues", comparison.Attention.Count(a => DeliveryAttentionCategories.Of(a.Kind) == DeliveryAttentionCategory.DataQuality),
            "Unresolved or removed story references, task files outside features, ambiguous identities, suspected source encoding damage") { Group = Quality });
        // Every current task in exactly one relationship (one shared classifier). Shared & foundational work is normal Spec-Kit structure, not a gap.
        var currentTasks = stories.Where(s => !s.RemovedFeature).SelectMany(s => s.Hierarchy?.Tasks ?? []).Where(t => t.Change != DeliveryTaskChange.Removed).ToList();
        int Rel(DeliveryTaskRelationship r) => currentTasks.Count(t => t.Relationship == r);
        metrics.Add(new("tasks-explicit-story", "Tasks linked to a user story", Rel(DeliveryTaskRelationship.ExplicitStory), "[USn] references a User Story defined in spec.md") { Group = Inventory });
        if (Rel(DeliveryTaskRelationship.StoryPhase) > 0)
            metrics.Add(new("tasks-story-phase", "Tasks in a story phase (no tag)", Rel(DeliveryTaskRelationship.StoryPhase), "No [USn] tag, inside a phase titled for a story") { Group = Inventory });
        metrics.Add(new("tasks-shared", "Shared & foundational tasks", Rel(DeliveryTaskRelationship.SharedFoundational), "Setup, foundational, polish and cross-cutting work not owned by one story — normal structure, not a defect") { Group = Inventory });
        if (Rel(DeliveryTaskRelationship.FunctionalGroup) > 0)
            metrics.Add(new("tasks-functional-group", "Tasks in functional groups", Rel(DeliveryTaskRelationship.FunctionalGroup), "[USn] groups in features without spec.md user stories — derived from tasks.md") { Group = Inventory });
        if (Rel(DeliveryTaskRelationship.ScenarioGroup) > 0)
            metrics.Add(new("tasks-scenario-group", "Tasks in scenario groups", Rel(DeliveryTaskRelationship.ScenarioGroup), "[USn] groups in scenario-based specifications") { Group = Inventory });
        metrics.Add(new("quality-removed-story-references", "Tasks referencing a removed story", Rel(DeliveryTaskRelationship.RemovedStoryReference), "The specification marks the referenced story removed — review scope") { Group = Quality });
        metrics.Add(new("quality-unresolved-references", "Unresolved story references", Rel(DeliveryTaskRelationship.UnresolvedReference), "[USn] names no story in spec.md — shown as written, never re-pointed") { Group = Quality });
        if (direction is { State: not DeliveryDirectionState.Chronological })
            metrics.Add(new("quality-direction", "Snapshot direction warnings", 1, direction.State.ToString()) { Group = Quality });
        if (explicitEvidence)
        {
            metrics.Add(new("ready-for-test", "Ready for test", currentStatuses.Count(s => s == DeliveryStatus.ReadyForTest), "Explicitly marked in the feature documents"));
            metrics.Add(new("testing", "Testing", currentStatuses.Count(s => s == DeliveryStatus.Testing), "Explicitly marked in the feature documents"));
            metrics.Add(new("blocked", "Blocked", currentStatuses.Count(s => s == DeliveryStatus.Blocked), "Explicitly marked in the feature documents"));
        }
        if (currentStatuses.Contains(DeliveryStatus.NeedsClarification))
            metrics.Add(new("needs-clarification", "Needs clarification", currentStatuses.Count(s => s == DeliveryStatus.NeedsClarification), "Open clarification markers in the specification"));
        metrics.Add(new("attention", "Needs attention", comparison.Attention.Count) { Group = Quality });
        var (periodCoverage, coverageText) = PeriodCoverage(kind, DateOnly.FromDateTime(baselineLocal.DateTime), DateOnly.FromDateTime(currentLocal.DateTime), weekStartDate);

        return new WeeklyDeliveryReport
        {
            ReportId = reportId,
            ProjectId = projectId,
            ProjectName = projectName,
            Title = kind == DeliveryReportKind.Weekly ? $"{projectName} Weekly Delivery Status" : $"{projectName} Delivery Change Report",
            Kind = kind,
            ComparisonWarnings = warnings,
            CalculationVersion = CalculationVersion,
            WeekLabel = weekLabel,
            WeekStart = weekStart,
            PeriodStart = baseline.Ref.EffectiveAt,
            PeriodEnd = current.Ref.EffectiveAt,
            GeneratedAt = generatedAt,
            TimeZone = zone.Id,
            Baseline = baseline.Ref,
            Current = current.Ref,
            Coverage = coverage,
            CoverageNotes = notes,
            HasExplicitStatusEvidence = explicitEvidence,
            Metrics = metrics,
            ExecutiveSummary = Summary(stories, comparison, current, baselineLocal, currentLocal),
            Stories = stories,
            Attention = comparison.Attention.OrderBy(a => a.Severity).ThenBy(a => a.Kind).ThenBy(a => a.Title, StringComparer.Ordinal).ToList(),
            NextWeekFocus = Focus(current, stories, comparison.Attention.Count),
            ScopeChanges = comparison.ScopeChanges,
            PeriodCoverage = periodCoverage,
            CoverageText = coverageText,
            Direction = direction,
            ParserVersion = DeliverySnapshotParser.ParserVersion, InterpretationRuleSet = BirkNext.Interpretation.WorkItemInterpreter.RuleSetVersion,
            QualityRuleSet = FunctionalQualityClassifier.RuleSetVersion,
        };
    }

    /// <summary>
    /// What the dates can claim: Same-day (one day only — never a week), Full-period (the baseline covers the reporting week), Partial-period
    /// (part of the week, or more than it), Unknown (inverted dates). The text says it in plain words for headers, exports and History.
    /// </summary>
    public static (DeliveryPeriodCoverage, string) PeriodCoverage(DeliveryReportKind kind, DateOnly baselineDate, DateOnly currentDate, DateOnly weekStart)
    {
        if (baselineDate > currentDate) return (DeliveryPeriodCoverage.Unknown, "Coverage: unknown — the baseline is later than the current snapshot");
        if (baselineDate == currentDate) return (DeliveryPeriodCoverage.SameDay, $"Coverage: {currentDate.ToString("d MMM", CultureInfo.InvariantCulture)} only (same-day comparison)");
        if (kind == DeliveryReportKind.Weekly) return (DeliveryPeriodCoverage.FullPeriod, $"Coverage: {Range(baselineDate, currentDate)} (full reporting week)");
        return baselineDate > weekStart
            ? (DeliveryPeriodCoverage.PartialPeriod, $"Coverage: {Range(baselineDate, currentDate)} — part of the week starting {weekStart.ToString("d MMM", CultureInfo.InvariantCulture)}")
            : (DeliveryPeriodCoverage.PartialPeriod, $"Coverage: {Range(baselineDate, currentDate)} — longer than the reporting week");
    }

    /// <summary>
    /// The comparison preview shown before generation: inventories, deltas, direction and coverage. Fewer features/tasks in the current snapshot
    /// is a possible scope reduction, rollback or reversed pair — it needs explicit confirmation, it is never reversed automatically.
    /// </summary>
    /// <summary>
    /// Same effective date: source order cannot be confirmed from the dates. Azure DevOps commit times (recorded at import) can clarify it; otherwise only
    /// BirkNext's import order is known, and that is said as import order — never as a newer source revision.
    /// </summary>
    public static string SameEffectiveDateWarning(DeliveryReportSnapshotRef baseline, DeliveryReportSnapshotRef current)
    {
        var text = "Same effective date. Source revision order cannot be confirmed from the effective dates.";
        if (baseline.AzureDevOps is { } b && current.AzureDevOps is { } c && string.Equals(b.Repository, c.Repository, StringComparison.OrdinalIgnoreCase) && b.CommitAt != c.CommitAt)
            return text + (c.CommitAt > b.CommitAt
                ? $" Azure DevOps commit times show the current snapshot's commit ({c.ShortCommit}) is later than the baseline's ({b.ShortCommit})."
                : $" Azure DevOps commit times show the current snapshot's commit ({c.ShortCommit}) is earlier than the baseline's ({b.ShortCommit}): check the direction.");
        if (current.ImportedAt != baseline.ImportedAt)
            return text + (current.ImportedAt > baseline.ImportedAt
                ? " The current snapshot was imported later in BirkNext; import order is not proof of a newer source revision."
                : " The current snapshot was imported earlier in BirkNext than the baseline; import order is not proof of source order.");
        return text;
    }

    public static DeliveryComparisonPreview Preview(DeliverySnapshotContent baseline, DeliverySnapshotContent current, TimeZoneInfo zone)
    {
        var baselineLocal = TimeZoneInfo.ConvertTime(baseline.Ref.EffectiveAt, zone);
        var currentLocal = TimeZoneInfo.ConvertTime(current.Ref.EffectiveAt, zone);
        var bDate = DateOnly.FromDateTime(baselineLocal.DateTime);
        var cDate = DateOnly.FromDateTime(currentLocal.DateTime);
        var weekStart = cDate.AddDays(-(((int)currentLocal.DayOfWeek + 6) % 7));
        var (kind, warnings) = Period(baseline, current, bDate, cDate, weekStart);
        var bInv = new DeliveryInventory(baseline.Features.Count, baseline.Features.Sum(f => f.TaskTotal), baseline.Features.Sum(f => f.TaskCompleted));
        var cInv = new DeliveryInventory(current.Features.Count, current.Features.Sum(f => f.TaskTotal), current.Features.Sum(f => f.TaskCompleted));
        var reduced = cInv.Features < bInv.Features || cInv.Tasks < bInv.Tasks;
        var state = baseline.Ref.EffectiveAt > current.Ref.EffectiveAt ? DeliveryDirectionState.BaselineLater
            : reduced ? DeliveryDirectionState.InventoryReduced
            : bDate == cDate ? DeliveryDirectionState.SameEffectiveDate
            : DeliveryDirectionState.Chronological;
        if (reduced)
            warnings.Insert(0, $"Current snapshot contains fewer {(cInv.Features < bInv.Features && cInv.Tasks < bInv.Tasks ? "features and tasks" : cInv.Features < bInv.Features ? "features" : "tasks")} than Baseline. "
                + "This may be a legitimate scope reduction, a rollback, or a reversed pair. Verify that snapshot direction is correct.");
        // Same effective date matters whatever else the direction says (a same-day pair can also be a reduced inventory).
        if (bDate == cDate && state != DeliveryDirectionState.BaselineLater)
            warnings.Add(SameEffectiveDateWarning(baseline.Ref, current.Ref));
        var bKeys = baseline.Features.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var cKeys = current.Features.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var (coverage, text) = PeriodCoverage(kind, bDate, cDate, weekStart);
        return new DeliveryComparisonPreview
        {
            Baseline = baseline.Ref, Current = current.Ref, BaselineInventory = bInv, CurrentInventory = cInv,
            MissingFeatures = [.. baseline.Features.Where(f => !cKeys.Contains(f.Key)).Select(f => f.Title).Order(StringComparer.Ordinal).Take(50)],
            NewFeatures = [.. current.Features.Where(f => !bKeys.Contains(f.Key)).Select(f => f.Title).Order(StringComparer.Ordinal).Take(50)],
            Direction = state, Warnings = warnings, RequiresConfirmation = state == DeliveryDirectionState.InventoryReduced,
            CanGenerate = state != DeliveryDirectionState.BaselineLater, PeriodCoverage = coverage, CoverageText = text, Kind = kind,
        };
    }

    /// <summary>2: report kind, comparison warnings, unconfirmed removals under incomplete coverage. 3: period coverage, direction review,
    /// grouped metrics (period / inventory / quality), functional requirements and task markers in the hierarchy.
    /// 4: every task classified once by <see cref="DeliveryWorkItemRelationships"/>; "Tasks without a user story" replaced by relationship counts.</summary>
    public const int CalculationVersion = 4;

    /// <summary>
    /// Weekly only when the baseline is on or before the start of the current snapshot's ISO week and at most seven days before it, so the
    /// comparison covers that week; everything else is a Delivery Change Report. Warnings name what makes the period weak.
    /// </summary>
    internal static (DeliveryReportKind Kind, List<string> Warnings) Period(DeliverySnapshotContent baseline, DeliverySnapshotContent current, DateOnly baselineDate, DateOnly currentDate, DateOnly weekStart)
    {
        var warnings = new List<string>();
        if (baseline.Ref.EffectiveAt > current.Ref.EffectiveAt) warnings.Add("The baseline is later than the current snapshot: the date range is inverted.");
        if (baselineDate == currentDate) warnings.Add($"Same-day comparison: both snapshots are from {Day(currentDate)}. This is a change report, not weekly coverage.");
        if (baseline.Ref.Fingerprint.Length > 0 && baseline.Ref.Fingerprint == current.Ref.Fingerprint) warnings.Add("Both snapshots have identical content: no change can be measured.");
        if (baseline.Ref.GitCommit is null && baseline.Ref.AzureDevOps is null || current.Ref.GitCommit is null && current.Ref.AzureDevOps is null)
            warnings.Add("The source revision of at least one snapshot is unknown (no Git commit recorded).");
        var weekly = baselineDate <= weekStart && baselineDate >= weekStart.AddDays(-7) && baselineDate < currentDate;
        return (weekly ? DeliveryReportKind.Weekly : DeliveryReportKind.Change, warnings);
    }

    private static (DeliveryCoverage, List<string>) Coverage(DeliverySnapshotContent baseline, DeliverySnapshotContent current, DateOnly baselineDate, DateOnly weekStart)
    {
        var notes = new List<string>();
        if (baseline.Features.Count == 0 || current.Features.Count == 0)
        {
            notes.Add(baseline.Features.Count == 0 ? "The baseline snapshot has no features, so movement cannot be measured." : "The current snapshot has no features.");
            return (DeliveryCoverage.Insufficient, notes);
        }
        var partial = false;
        var currentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(current.Ref.EffectiveAt, TimeZoneInfo.Utc).Date);
        if (baselineDate == currentDate)
        {
            partial = true;
            notes.Add($"Baseline and current are both from {Day(baselineDate)}: the report covers changes within that day only, not a full week.");
        }
        else if (baselineDate < weekStart)
        {
            partial = true;
            notes.Add($"Baseline is from {Day(baselineDate)}. The report may include changes from {Range(baselineDate, weekStart)}, before this week started.");
        }
        else if (baselineDate > weekStart)
        {
            partial = true;
            notes.Add($"Baseline is from {Day(baselineDate)}. Changes from {Range(weekStart, baselineDate)} are not included. Import an earlier baseline to cover the whole week.");
        }
        if (baseline.Ref.AzureDevOps is { } ba && current.Ref.AzureDevOps is { } ca)
        {
            if (!string.Equals(ba.Repository, ca.Repository, StringComparison.OrdinalIgnoreCase) || !string.Equals(ba.Project, ca.Project, StringComparison.OrdinalIgnoreCase))
            { partial = true; notes.Add($"The snapshots come from different repositories ({ba.Project}/{ba.Repository} and {ca.Project}/{ca.Repository}): the comparison may not describe one codebase."); }
            else if (!string.Equals(ba.Branch, ca.Branch, StringComparison.Ordinal))
                notes.Add($"The snapshots come from different branches ({ba.Branch} and {ca.Branch}).");
        }
        if (baseline.Coverage != DeliveryCoverage.Complete || current.Coverage != DeliveryCoverage.Complete)
        {
            partial = true;
            notes.Add("One of the snapshots could not be read completely (see its import warnings); some stories may be missing.");
        }
        return (partial ? DeliveryCoverage.Partial : DeliveryCoverage.Complete, notes);
    }

    private static List<string> Summary(List<DeliveryStoryRow> stories, DeliveryComparisonResult comparison, DeliverySnapshotContent current,
        DateTimeOffset baselineLocal, DateTimeOffset currentLocal)
    {
        var lines = new List<string>();
        var changed = stories.Count(s => s.Changed);
        if (changed == 0)
        {
            lines.Add($"No significant changes between {Day(baselineLocal)} and {Day(currentLocal)}: {current.Features.Count} features, {current.Features.Sum(f => f.TaskCompleted)} of {current.Features.Sum(f => f.TaskTotal)} tasks completed.");
        }
        else
        {
            lines.Add($"{changed} of {stories.Count} features changed between {Day(baselineLocal)} and {Day(currentLocal)}.");
            var forward = stories.Count(s => s.Movement == DeliveryMovement.Progressed);
            var completed = stories.Count(s => s.Movement == DeliveryMovement.Completed);
            var tasks = stories.Sum(s => s.TasksCompletedThisPeriod);
            if (forward + completed + tasks > 0)
                lines.Add($"{forward} progressed and {completed} completed all tasks; {tasks} task{(tasks == 1 ? " was" : "s were")} completed in the period.");
            if (comparison.FeaturesAdded > 0) lines.Add($"{comparison.FeaturesAdded} new feature{(comparison.FeaturesAdded == 1 ? " was" : "s were")} added.");
            if (comparison.FeaturesRemoved > 0) lines.Add($"{comparison.FeaturesRemoved} feature{(comparison.FeaturesRemoved == 1 ? " is" : "s are")} no longer present and need review.");
            var regressed = stories.Count(s => s.Movement == DeliveryMovement.Regressed);
            if (regressed > 0) lines.Add($"{regressed} feature{(regressed == 1 ? "" : "s")} went backwards or became blocked.");
        }
        var total = current.Features.Sum(f => f.TaskTotal);
        if (total > 0) lines.Add($"Overall task completion is {current.Features.Sum(f => f.TaskCompleted)} of {total} ({100 * current.Features.Sum(f => f.TaskCompleted) / total}%). Task completion is not the same as delivered or tested.");
        var blockers = comparison.Attention.Count(a => a.Severity == DeliveryAttentionSeverity.Blocker);
        if (comparison.Attention.Count > 0)
            lines.Add($"{comparison.Attention.Count} item{(comparison.Attention.Count == 1 ? " needs" : "s need")} attention{(blockers > 0 ? $", {blockers} blocked" : "")}.");
        return lines;
    }

    /// <summary>"Potential focus": observations from the snapshots, never a priority (no source declares one). Lists are alphabetical.</summary>
    private static List<DeliveryFocusItem> Focus(DeliverySnapshotContent current, List<DeliveryStoryRow> stories, int attentionCount)
    {
        var focus = new List<DeliveryFocusItem>();
        static List<string> Titles(IEnumerable<DeliveryContentFeature> features) => features.Select(f => f.Title).Order(StringComparer.Ordinal).Take(10).ToList();
        var newKeys = stories.Where(s => s.NewFeature).Select(s => s.FeatureKey).ToHashSet(StringComparer.Ordinal);

        var blocked = current.Features.Where(f => f.Status == DeliveryStatus.Blocked).ToList();
        if (blocked.Count > 0) focus.Add(new($"{blocked.Count} feature{Plural(blocked.Count)} {(blocked.Count == 1 ? "is" : "are")} blocked", Titles(blocked)));
        var inProgress = current.Features.Where(f => f.Status == DeliveryStatus.InProgress).ToList();
        if (inProgress.Count > 0)
        {
            var nearly = inProgress.Count(f => f.TaskTotal > 0 && f.TaskCompleted * 10 > f.TaskTotal * 9);
            focus.Add(new($"{inProgress.Count} feature{Plural(inProgress.Count)} remain{(inProgress.Count == 1 ? "s" : "")} in progress{(nearly > 0 ? $"; {nearly} {(nearly == 1 ? "has" : "have")} more than 90% of tasks checked" : "")}",
                Titles(inProgress)));
        }
        var tasksDone = current.Features.Where(f => f.Status == DeliveryStatus.TasksComplete).ToList();
        if (tasksDone.Count > 0) focus.Add(new($"{tasksDone.Count} feature{Plural(tasksDone.Count)} {(tasksDone.Count == 1 ? "has" : "have")} all tasks completed: confirm whether {(tasksDone.Count == 1 ? "it is" : "they are")} ready for test", Titles(tasksDone)));
        var clarification = current.Features.Where(f => f.Status == DeliveryStatus.NeedsClarification).ToList();
        if (clarification.Count > 0) focus.Add(new($"{clarification.Count} feature{Plural(clarification.Count)} need{(clarification.Count == 1 ? "s" : "")} clarification", Titles(clarification)));
        var planning = current.Features.Where(f => newKeys.Contains(f.Key) && f.Status is DeliveryStatus.NotStarted or DeliveryStatus.NotAssessed).ToList();
        if (planning.Count > 0) focus.Add(new($"{planning.Count} newly added feature{Plural(planning.Count)} need{(planning.Count == 1 ? "s" : "")} planning", Titles(planning)));
        var removed = stories.Where(s => s.RemovedFeature).ToList();
        if (removed.Count > 0) focus.Add(new($"{removed.Count} removed feature{Plural(removed.Count)} need{(removed.Count == 1 ? "s" : "")} confirmation", removed.Select(s => s.Title).Order(StringComparer.Ordinal).Take(10).ToList()));
        if (attentionCount > 0) focus.Add(new($"{attentionCount} attention item{Plural(attentionCount)} remain{(attentionCount == 1 ? "s" : "")}", []));
        return focus;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    public static string Day(DateTimeOffset value) => value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    public static string Day(DateOnly value) => value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Range(DateOnly from, DateOnly to) =>
        from.Month == to.Month && from.Year == to.Year
            ? $"{from.Day}–{to.ToString("d MMM", CultureInfo.InvariantCulture)}"
            : $"{from.ToString("d MMM", CultureInfo.InvariantCulture)}–{to.ToString("d MMM", CultureInfo.InvariantCulture)}";

    /// <summary>Human-readable, deterministic export file name: M2LB-Weekly-Delivery-Report-2026-W44.md.</summary>
    public static string FileName(WeeklyDeliveryReport report, string extension)
    {
        var name = new string(report.ProjectName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        while (name.Contains("--", StringComparison.Ordinal)) name = name.Replace("--", "-", StringComparison.Ordinal);
        name = name.Trim('-');
        if (name.Length == 0) name = "Project";
        return $"{name}-Weekly-Delivery-Report-{report.WeekLabel}.{extension}";
    }
}
