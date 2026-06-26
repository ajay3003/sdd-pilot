using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: AI-powered delivery advisory layer.
/// Provides natural-language explanations and risk narratives on top of
/// the deterministic <see cref="IDeliveryReadinessAssessmentService"/> output.
/// </summary>
public interface IAIDeliveryAdvisor
{
    // TODO: Generate a natural-language executive summary of the delivery readiness report.
    // Task<string> GenerateSummary(DeliveryReadinessReport report, CancellationToken ct = default);

    // TODO: Suggest the highest-leverage remediation steps for a blocked gate.
    // Task<IEnumerable<string>> SuggestRemediation(DeliveryGate gate, CancellationToken ct = default);

    // TODO: Estimate risk probability for release based on current readiness metrics.
    // Task<double> EstimateReleaseRisk(DeliveryReadinessReport report, CancellationToken ct = default);
}
