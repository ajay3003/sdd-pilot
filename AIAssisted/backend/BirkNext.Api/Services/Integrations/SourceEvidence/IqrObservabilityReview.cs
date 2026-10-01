using BirkNext.Integrations;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Integration Quality Review's reading of the Source Analysis Observability evidence stored on the selected snapshots: correlation,
/// error handling, retry/failure and message-flow observations for the IQR domains. Read from the snapshot (never a rescan) and always
/// source-derived — runtime telemetry, propagation and delivery stay with the runtime evidence.
/// </summary>
public static class IqrObservabilityReview
{
    public const string RuntimeLimitation = "Source observability shows implemented correlation and logging only; it does not prove context propagates or telemetry is delivered at runtime";

    private static IEnumerable<SourceObservabilitySnapshot> Of(IEnumerable<IqrSourceSnapshot> snapshots) => snapshots.Select(s => s.Observability).OfType<SourceObservabilitySnapshot>();

    private static int Needs(SourceObservabilitySnapshot o, params ObservabilityCategory[] categories) =>
        o.Findings.Where(f => categories.Contains(f.Category) && f.Kind is ObservabilityFindingKind.Finding or ObservabilityFindingKind.Unresolved && f.Severity >= ObservabilitySeverity.NeedsReview).Sum(f => f.Occurrences);

    private static string Dim(SourceObservabilitySnapshot o, string id) => o.Logging.Dimensions.FirstOrDefault(d => d.Id == id) is { } d ? $"{ObservabilitySnapshot.Label(d.State)} — {d.Detail}" : "Not assessed";

    public static int EvidenceCount(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots) => Of(snapshots).Sum(o => domain switch
    {
        IntegrationReviewDomain.Observability => o.Mechanisms.Count + o.Boundaries.Count + o.Telemetry.Exporters.Count,
        IntegrationReviewDomain.ErrorHandling => o.Logging.ExceptionPreserved + o.Logging.ExceptionMessageOnly + o.Findings.Where(f => f.Category == ObservabilityCategory.CatchAndSwallow).Sum(f => f.Occurrences),
        IntegrationReviewDomain.Reliability => o.Findings.Where(f => f.Category == ObservabilityCategory.RetryFailure).Sum(f => f.Occurrences),
        IntegrationReviewDomain.MessageFlow => o.Boundaries.Count(b => b.Type is CorrelationBoundaryType.MessageProduce or CorrelationBoundaryType.MessageConsume),
        _ => 0,
    });

    public static string? Available(IReadOnlyList<IqrSourceSnapshot> snapshots) =>
        Of(snapshots).Any() ? $"Source Analysis observability evidence available ({Of(snapshots).Sum(o => o.Boundaries.Count)} correlation boundaries, {Of(snapshots).Sum(o => o.Findings.Count)} findings); runtime telemetry not established" : null;

    public static (List<string> Observed, List<string> Missing) Domain(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        var observed = new List<string>();
        foreach (var o in Of(snapshots))
        {
            var at = $"(source snapshot {o.SourceFingerprint[..Math.Min(8, o.SourceFingerprint.Length)]}, Observability v{o.AnalyzerVersion})";
            switch (domain)
            {
                case IntegrationReviewDomain.Observability:
                    observed.Add($"Source observability {at}: tracing registered in {o.Correlation.ComponentsWithTracing} component(s); exporters {(o.Telemetry.Exporters.Count == 0 ? "none found" : string.Join(", ", o.Telemetry.Exporters.Take(4)))}");
                    observed.Add($"Correlation propagation in source: {Dim(o, "propagation")}");
                    observed.Add($"Correlation context in logs: {Dim(o, "context")}");
                    if (Needs(o, ObservabilityCategory.Correlation, ObservabilityCategory.Tracing, ObservabilityCategory.Telemetry) is > 0 and var n)
                        observed.Add($"Source correlation/telemetry findings needing review: {n}");
                    break;
                case IntegrationReviewDomain.ErrorHandling:
                    observed.Add($"Source exception logging {at}: {Dim(o, "exceptions")}");
                    observed.Add($"Source catch-and-swallow: {Dim(o, "swallow")}");
                    break;
                case IntegrationReviewDomain.Reliability:
                    if (o.Findings.Any(f => f.Category == ObservabilityCategory.RetryFailure))
                        observed.Add($"Source retry/failure observability {at}: {Dim(o, "retry")}");
                    break;
                case IntegrationReviewDomain.MessageFlow:
                    var messaging = o.Boundaries.Where(b => b.Type is CorrelationBoundaryType.MessageProduce or CorrelationBoundaryType.MessageConsume).ToList();
                    if (messaging.Count > 0)
                        observed.Add($"Source message correlation {at}: {messaging.Count} produce/consume boundaries — "
                            + string.Join(", ", messaging.GroupBy(b => b.Propagation).OrderBy(g => g.Key).Select(g => $"{g.Count()} {ObservabilitySnapshot.Label(g.Key).ToLowerInvariant()}"))
                            + $"; handlers: {Dim(o, "handlers")}");
                    break;
            }
        }
        return observed.Count == 0 ? ([], []) : (observed, [RuntimeLimitation]);
    }
}
