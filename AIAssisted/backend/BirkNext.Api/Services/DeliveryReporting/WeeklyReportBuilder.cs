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
        DeliverySnapshotContent baseline, DeliverySnapshotContent current, DeliveryComparisonResult comparison, TimeZoneInfo zone)
    {
        var currentLocal = TimeZoneInfo.ConvertTime(current.Ref.EffectiveAt, zone);
        var baselineLocal = TimeZoneInfo.ConvertTime(baseline.Ref.EffectiveAt, zone);
        var weekStartDate = DateOnly.FromDateTime(currentLocal.Date).AddDays(-(((int)currentLocal.DayOfWeek + 6) % 7));
        var weekStart = new DateTimeOffset(weekStartDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(weekStartDate.ToDateTime(TimeOnly.MinValue)));
        var weekLabel = $"{ISOWeek.GetYear(currentLocal.DateTime)}-W{ISOWeek.GetWeekOfYear(currentLocal.DateTime):00}";

        var (coverage, notes) = Coverage(baseline, current, DateOnly.FromDateTime(baselineLocal.DateTime), weekStartDate);
        var stories = comparison.Stories;
        var currentStatuses = current.Features.Select(f => f.Status).ToList();
        var explicitEvidence = current.Features.Concat(baseline.Features).Any(f => DeliveryReportingText.IsExplicitOnly(f.Status));

        // Rows are features (a Spec-Kit feature folder), so the metrics say Features; user stories are counted from the parsed hierarchy only.
        var hierarchies = stories.Select(s => s.Hierarchy).OfType<DeliveryFeatureHierarchy>().Where(h => h.Captured).ToList();
        var paired = stories.Where(s => !s.NewFeature && !s.RemovedFeature && s.Hierarchy is { Captured: true }).Select(s => s.Hierarchy!).ToList();
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
        }
        if (explicitEvidence)
        {
            metrics.Add(new("ready-for-test", "Ready for test", currentStatuses.Count(s => s == DeliveryStatus.ReadyForTest), "Explicitly marked in the feature documents"));
            metrics.Add(new("testing", "Testing", currentStatuses.Count(s => s == DeliveryStatus.Testing), "Explicitly marked in the feature documents"));
            metrics.Add(new("blocked", "Blocked", currentStatuses.Count(s => s == DeliveryStatus.Blocked), "Explicitly marked in the feature documents"));
        }
        if (currentStatuses.Contains(DeliveryStatus.NeedsClarification))
            metrics.Add(new("needs-clarification", "Needs clarification", currentStatuses.Count(s => s == DeliveryStatus.NeedsClarification), "Open clarification markers in the specification"));
        metrics.Add(new("attention", "Needs attention", comparison.Attention.Count));

        return new WeeklyDeliveryReport
        {
            ReportId = reportId,
            ProjectId = projectId,
            ProjectName = projectName,
            Title = $"{projectName} Weekly Delivery Status",
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
        };
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
