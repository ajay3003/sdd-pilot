using System.Globalization;
using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Display rules for the Delivery Reporting page: story filters, tabs, dates and status tones. Pure, so the page stays declarative.</summary>
public static class DeliveryReportingPresentation
{
    public const string Route = "delivery-reporting";

    public static readonly IReadOnlyList<(string Id, string Label)> Tabs =
        [("overview", "Overview"), ("stories", "Stories"), ("snapshots", "Snapshots"), ("history", "Report history"), ("scope", "Functional Quality Scope")];

    public static string TabOrDefault(string? tab) => Tabs.Any(t => t.Id == tab) ? tab! : "overview";

    /// <summary>Story filters. The default is "changed this week + active attention"; unchanged old stories only appear under All.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> StoryFilters =
    [
        ("default", "Changed + attention"), ("all", "All"), ("progressed", "Progressed"), ("new", "New"), ("removed", "Removed"), ("completed", "All tasks checked"),
        ("blocked", "Blocked"), ("clarification", "Needs clarification"), ("specification", "Changed specification"), ("stories", "User stories changed"),
        ("criteria", "Acceptance criteria changed"), ("attention", "Needs attention"),
    ];

    /// <summary>Phases across the report's features (key + title of the first occurrence), only where explicit phases exist.</summary>
    public static IReadOnlyList<(string Key, string Title)> Phases(WeeklyDeliveryReport report) =>
        report.Stories.SelectMany(s => s.Hierarchy?.Phases ?? []).GroupBy(p => p.Key).Select(g => (g.Key, g.First().Title))
            .OrderBy(p => int.TryParse(p.Key.Replace("phase-", ""), out var n) ? n : 999).ToList();

    public static IReadOnlyList<DeliveryStoryRow> Filter(WeeklyDeliveryReport report, string filter, string? search, string? module)
    {
        IEnumerable<DeliveryStoryRow> rows = report.Stories;
        rows = filter switch
        {
            "all" => rows,
            "progressed" => rows.Where(s => s.Movement == DeliveryMovement.Progressed),
            "new" => rows.Where(s => s.NewFeature),
            "completed" => rows.Where(s => s.Movement == DeliveryMovement.Completed),
            "blocked" => rows.Where(s => s.CurrentStatus == DeliveryStatus.Blocked || s.Movement == DeliveryMovement.Regressed),
            "clarification" => rows.Where(s => s.CurrentStatus == DeliveryStatus.NeedsClarification),
            "specification" => rows.Where(s => s.SpecificationChanged),
            "removed" => rows.Where(s => s.RemovedFeature),
            "stories" => rows.Where(s => s.Hierarchy is { } h && (h.StoriesAdded + h.StoriesRemoved + h.StoriesChanged) > 0),
            "criteria" => rows.Where(s => s.Hierarchy is { } h && (h.CriteriaAdded + h.CriteriaChanged + h.CriteriaRemoved) > 0 || s.AcceptanceCriteriaChanged),
            "attention" => rows.Where(s => s.HasActiveAttention),
            _ => rows.Where(DeliveryReportStories.IsDefaultVisible),
        };
        if (!string.IsNullOrWhiteSpace(module)) rows = rows.Where(s => s.Module == module);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(s => s.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || s.Module.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (s.Hierarchy?.Stories ?? []).Any(st => st.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || st.Key.Equals(term, StringComparison.OrdinalIgnoreCase)));
        }
        return rows.ToList();
    }

    public static IReadOnlyList<string> Modules(WeeklyDeliveryReport report) =>
        report.Stories.Select(s => s.Module).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>Tone class for a movement badge. The badge always carries the text too: colour is never the only signal.</summary>
    public static string MovementTone(DeliveryMovement movement) => movement switch
    {
        DeliveryMovement.Progressed or DeliveryMovement.Completed => "dlr-tone-good",
        DeliveryMovement.Regressed => "dlr-tone-bad",
        DeliveryMovement.NewThisPeriod => "dlr-tone-info",
        DeliveryMovement.NeedsReview => "dlr-tone-warn",
        DeliveryMovement.Removed => "dlr-tone-neutral", // removed is a change, not a problem
        _ => "dlr-tone-neutral",
    };

    public static string CoverageTone(DeliveryCoverage coverage) => coverage switch
    {
        DeliveryCoverage.Complete => "dlr-tone-good",
        DeliveryCoverage.Partial => "dlr-tone-warn",
        _ => "dlr-tone-warn", // insufficient coverage means evidence is missing, not that delivery failed
    };

    // ---- Snapshot selection: identity is the SnapshotId; the role and date are metadata, never the name ----

    /// <summary>
    /// The project's current snapshot (Delivery's pointer rule): the newest snapshot imported with role Current — newest effective date, then newest import,
    /// then id. Older snapshots imported as Current are not "Current" any more; they are shown as earlier current imports. Null when none was imported as Current.
    /// </summary>
    public static Guid? CurrentPointer(IReadOnlyList<DeliverySnapshotSummary> snapshots) =>
        Sorted(snapshots.Where(s => s.Role == DeliverySnapshotRole.Current)).FirstOrDefault()?.Id;

    public static IReadOnlyList<DeliverySnapshotSummary> Sorted(IEnumerable<DeliverySnapshotSummary> snapshots) =>
        [.. snapshots.OrderByDescending(s => s.EffectiveAt).ThenByDescending(s => s.ImportedAt).ThenBy(s => s.Id)];

    /// <summary>The snapshot's own name: a name someone gave it, or null when the stored name is the generated "{role} {date}" form (shown from metadata).</summary>
    public static string? GivenName(DeliverySnapshotSummary s) => BirkNext.Snapshots.SnapshotSelectionLabels.IsGeneratedRoleDateName(s.Label) ? null : s.Label;

    /// <summary>A readable name for messages and the snapshot list: the given name, else "4 Nov 2026 · imported 13:11".</summary>
    public static string DisplayName(DeliverySnapshotSummary s) => GivenName(s) ?? $"{Day(s.EffectiveAt)} · imported {s.ImportedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>Unique one-line option text per snapshot (the option value is always the SnapshotId), e.g.
    /// "4 Nov 2026 · imported 4 Nov 13:11 · 38 features · 2038/2142 tasks · Current".</summary>
    public static IReadOnlyDictionary<Guid, string> SnapshotOptions(IReadOnlyList<DeliverySnapshotSummary> snapshots)
    {
        var current = CurrentPointer(snapshots);
        var options = snapshots.Select(s => new BirkNext.Snapshots.SnapshotSelectionOption(s.Id.ToString(), "Delivery", s.EffectiveAt, s.ImportedAt,
            s.AzureDevOps?.Branch is { } branch ? branch : System.IO.Path.GetFileNameWithoutExtension(s.SourceFileName),
            string.Join(" · ", new[] { GivenName(s), $"{s.FeatureCount} features", $"{s.CompletedTaskCount}/{s.TaskCount} tasks" }.OfType<string>()),
            [.. Pointers(s, current)])).ToList();
        return BirkNext.Snapshots.SnapshotSelectionLabels.Labels(options, TimeZoneInfo.Local).ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value);
    }

    /// <summary>Badges for one snapshot: "Current" only for the pointer; the import role of the others is said as what it is.</summary>
    public static IEnumerable<string> Pointers(DeliverySnapshotSummary s, Guid? current)
    {
        if (s.Id == current) yield return "Current";
        else if (s.Role == DeliverySnapshotRole.Current) yield return "earlier current import";
        else if (s.Role == DeliverySnapshotRole.Baseline) yield return "imported as baseline";
    }

    public static string Day(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    public static string DayTime(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"26–30 Oct 2026" for a period inside one month, otherwise both full dates.</summary>
    public static string Period(DateTimeOffset start, DateTimeOffset end)
    {
        var s = start.ToLocalTime();
        var e = end.ToLocalTime();
        return s.Year == e.Year && s.Month == e.Month
            ? $"{s.Day}–{e.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : $"{Day(start)} – {Day(end)}";
    }

    public static string SnapshotLine(DeliveryReportSnapshotRef snapshot) =>
        $"{Day(snapshot.EffectiveAt)} · {DeliveryReportingText.Source(snapshot.SourceType)}";

    /// <summary>A value for an &lt;input type="datetime-local"&gt;.</summary>
    public static string LocalInput(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseLocalInput(string? value) =>
        DateTime.TryParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local) : null;
}
