using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Whether a target's Core Web Vitals thresholds equal the defaults. Describes configuration only — never a result.</summary>
public enum CoreWebVitalsProfile { Default, Custom }

/// <summary>One metric row: what it measures and the three bands its configured thresholds produce.</summary>
public sealed record CoreWebVitalsRow(string Abbreviation, string Name, string Measures, string Unit, string Good, string NeedsImprovement, string Poor);

/// <summary>
/// Presentation of Target Environment → Core Web Vitals. The bands mirror <see cref="PerformanceQualityRules.Classify"/> exactly:
/// Good ≤ good threshold; Needs improvement &gt; good and ≤ poor; Poor &gt; poor (the poor threshold itself is still Needs improvement).
/// Numbers use the invariant culture, like every metric string in Frontend Quality Review. Configuration only: nothing is measured here.
/// </summary>
public static class CoreWebVitalsPresentation
{
    public const string OwnerNote =
        "These thresholds are used by Frontend Quality Review — the BirkNext Performance Quality engine (LCP, INP, CLS) and the Browser Quality engine (LCP, CLS) — when Core Web Vitals evidence is available. They are configuration values, not current measurements.";

    /// <summary>Browser Quality applies the same boundaries but reports only some bands (BrowserQualityRules).</summary>
    public const string BrowserQualityNote =
        "Browser Quality reports LCP above the good threshold and CLS above the poor threshold. A metric without evidence is Not measured — never zero.";

    public const string BaselineNote = "Adjust thresholds only when you have an application-specific performance baseline.";

    public const string RestoreLabel = "Restore default Core Web Vitals";

    public const string RestoreConfirmation =
        "This restores the default LCP, INP and CLS thresholds for this Target Environment. It does not change Performance Thresholds, Frontend Review Engines, other Target Environments, browser capabilities, collected evidence or previous review results.";

    private static readonly CoreWebVitalsThresholds Defaults = new();

    /// <summary>Default when every threshold equals the default (same tolerance as <see cref="PerformanceQualityRules.ResolveThresholds"/>), otherwise Custom.</summary>
    public static CoreWebVitalsProfile Profile(CoreWebVitalsThresholds t)
    {
        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
        return Same(t.LcpGoodMs, Defaults.LcpGoodMs) && Same(t.LcpPoorMs, Defaults.LcpPoorMs) && Same(t.InpGoodMs, Defaults.InpGoodMs)
            && Same(t.InpPoorMs, Defaults.InpPoorMs) && Same(t.ClsGood, Defaults.ClsGood) && Same(t.ClsPoor, Defaults.ClsPoor)
            ? CoreWebVitalsProfile.Default : CoreWebVitalsProfile.Custom;
    }

    public static IReadOnlyList<CoreWebVitalsRow> Rows(CoreWebVitalsThresholds t) =>
    [
        Row("LCP", "Largest Contentful Paint", "Loading performance", "ms", t.LcpGoodMs, t.LcpPoorMs),
        Row("INP", "Interaction to Next Paint", "Interaction responsiveness", "ms", t.InpGoodMs, t.InpPoorMs),
        Row("CLS", "Cumulative Layout Shift", "Visual stability", "unitless", t.ClsGood, t.ClsPoor),
    ];

    /// <summary>A threshold as configured: whole milliseconds, or a unitless layout-shift score (no "ms").</summary>
    public static string Value(double value, string unit) =>
        unit == "ms" ? PerformanceFormat.Inv(value, "0") + " ms" : PerformanceFormat.Inv(value, "0.###");

    private static CoreWebVitalsRow Row(string abbreviation, string name, string measures, string unit, double good, double poor) =>
        new(abbreviation, name, measures, unit, $"≤ {Value(good, unit)}", $"> {Value(good, unit)} and ≤ {Value(poor, unit)}", $"> {Value(poor, unit)}");
}
