using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>
/// The Delivery Reports header for a saved report: what it is ("Delivery Change Report" unless the comparison covers a full reporting week), the week as
/// secondary context, and the comparison it froze ("Baseline: X → Current: Y · Same-day comparison"). Read from the frozen report only.
/// </summary>
public static class DeliveryReportContext
{
    /// <summary>A same-day or partial comparison is never called weekly. A report frozen before coverage was recorded (Unknown) keeps its weekly name: an old
    /// report is not silently reclassified.</summary>
    public static string Title(WeeklyDeliveryReport r) =>
        $"{r.ProjectName} — {(r.Kind == DeliveryReportKind.Weekly && r.PeriodCoverage is DeliveryPeriodCoverage.FullPeriod or DeliveryPeriodCoverage.Unknown ? "Weekly Delivery Report" : "Delivery Change Report")}";

    /// <summary>"W44" from "2026-W44" — secondary context only; never a claim of weekly coverage.</summary>
    public static string? Week(WeeklyDeliveryReport r) =>
        r.WeekLabel.Contains("-W", StringComparison.Ordinal) ? r.WeekLabel[(r.WeekLabel.IndexOf("-W", StringComparison.Ordinal) + 1)..] : r.WeekLabel.Length > 0 ? r.WeekLabel : null;

    public static string Coverage(DeliveryPeriodCoverage coverage) => coverage switch
    {
        DeliveryPeriodCoverage.FullPeriod => "Full-period comparison",
        DeliveryPeriodCoverage.PartialPeriod => "Partial-period comparison",
        DeliveryPeriodCoverage.SameDay => "Same-day comparison",
        _ => "Coverage not determined",
    };

    public static string Line(WeeklyDeliveryReport r) =>
        $"Baseline: {Label(r.Baseline)} → Current: {Label(r.Current)} · {Coverage(r.PeriodCoverage)}";

    private static string Label(DeliveryReportSnapshotRef s) => s.Label.Length > 0 ? s.Label : DeliveryReportingPresentation.Day(s.EffectiveAt);
}
