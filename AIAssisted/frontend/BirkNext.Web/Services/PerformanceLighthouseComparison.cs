using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>One side-by-side row: BirkNext field measurement (user's browser) vs Lighthouse lab measurement for an equivalent metric.</summary>
public sealed record PerformanceLighthouseRow(string Metric, string BirkNext, string Lighthouse, string Difference, string Note);

/// <summary>
/// Validation/comparison view between BirkNext Performance Quality (field: PerformanceObserver in the user's signed-in managed Edge) and
/// Lighthouse (lab: synthetic anonymous navigation). Only metrics with an equivalent definition are compared (LCP, CLS, FCP, TTFB, transfer);
/// differences are expected because the observation windows, network, cache state and authentication differ. Never adjusts either side.
/// </summary>
public static class PerformanceLighthouseComparison
{
    public const string Methodology = "BirkNext measures the user's real, authenticated browser session with PerformanceObserver (field); Lighthouse runs a synthetic anonymous lab navigation with throttling. Values are not expected to match; large gaps point at cache state, authentication redirects or throttling, not at a defect in either tool.";

    public static IReadOnlyList<PerformanceLighthouseRow> Build(PagePerformanceSnapshot page, LighthouseResultDto? lighthouse)
    {
        if (lighthouse is null || lighthouse.ExecutionStatus != LighthouseExecutionStatusDto.Assessed || page.ObservationType != PerformancePhase.InitialLoad) return [];
        var rows = new List<PerformanceLighthouseRow>();
        Add(rows, "LCP", page.Metric("lcp"), Lab(lighthouse, "LCP", "largest-contentful-paint"), "ms");
        Add(rows, "CLS", page.Metric("cls"), Lab(lighthouse, "CLS", "cumulative-layout-shift"), "score");
        Add(rows, "FCP", page.Metric("fcp"), Lab(lighthouse, "FCP", "first-contentful-paint"), "ms");
        Add(rows, "TTFB", page.Metric("ttfb"), Lab(lighthouse, "TTFB", "server-response-time"), "ms");
        Add(rows, "Transfer", page.Metric("transfer"), Lab(lighthouse, "Total Byte Weight", "total-byte-weight"), "bytes");
        return rows;
    }

    private static LighthouseMetricDto? Lab(LighthouseResultDto lighthouse, string name, string auditId) => (lighthouse.Metrics ?? [])
        .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.AuditId, auditId, StringComparison.OrdinalIgnoreCase));

    private static void Add(List<PerformanceLighthouseRow> rows, string name, PerformanceQualityMetric? field, LighthouseMetricDto? lab, string unit)
    {
        if (field is null && lab is null) return;
        var fieldValue = field?.Status == PerformanceMetricStatus.NotMeasured ? null : field?.Value;
        var labValue = lab?.ObservedValue;
        if (labValue is { } lv && unit == "ms" && string.Equals(lab?.Unit, "s", StringComparison.OrdinalIgnoreCase)) labValue = lv * 1000;
        if (labValue is { } kb && unit == "bytes" && (lab?.Unit ?? "").Contains("KiB", StringComparison.OrdinalIgnoreCase)) labValue = kb * 1024;
        var diff = fieldValue is { } f && labValue is { } l
            ? (l == 0 ? "n/a" : $"{(f - l >= 0 ? "+" : "")}{PerformanceFormat.Inv((f - l) / l * 100, "0")}% (field vs lab)")
            : "n/a";
        rows.Add(new PerformanceLighthouseRow(name,
            fieldValue is null ? "Not measured" : PerformanceFormat.Value(fieldValue, unit),
            labValue is null ? "Not available" : PerformanceFormat.Value(labValue, unit),
            diff,
            fieldValue is null ? "BirkNext: not measured in this observation." : labValue is null ? "Lighthouse: metric not reported." : "Different observation windows; see methodology."));
    }
}
