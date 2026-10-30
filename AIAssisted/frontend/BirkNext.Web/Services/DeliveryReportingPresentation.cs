using System.Globalization;
using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Display rules for the Delivery Reporting page: story filters, tabs, dates and status tones. Pure, so the page stays declarative.</summary>
public static class DeliveryReportingPresentation
{
    public const string Route = "delivery-reporting";

    public static readonly IReadOnlyList<(string Id, string Label)> Tabs =
        [("overview", "Overview"), ("stories", "Stories"), ("snapshots", "Snapshots"), ("history", "Report history")];

    public static string TabOrDefault(string? tab) => Tabs.Any(t => t.Id == tab) ? tab! : "overview";

    /// <summary>Story filters. The default is "changed this week + active attention"; unchanged old stories only appear under All.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> StoryFilters =
    [
        ("default", "Changed + attention"), ("all", "All"), ("progressed", "Progressed"), ("new", "New"), ("removed", "Removed"), ("completed", "All tasks completed"),
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
        DeliveryMovement.Removed or DeliveryMovement.NeedsReview => "dlr-tone-warn",
        _ => "dlr-tone-neutral",
    };

    public static string CoverageTone(DeliveryCoverage coverage) => coverage switch
    {
        DeliveryCoverage.Complete => "dlr-tone-good",
        DeliveryCoverage.Partial => "dlr-tone-warn",
        _ => "dlr-tone-bad",
    };

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
