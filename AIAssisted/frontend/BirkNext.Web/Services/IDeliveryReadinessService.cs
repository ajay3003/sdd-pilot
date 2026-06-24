using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: Delivery readiness assessment based on compliance and traceability data.
/// Extension point for Delivery Readiness Assessment and automated gate enforcement.
/// </summary>
public interface IDeliveryReadinessService
{
    // TODO: Compute a 0–100 delivery readiness score combining compliance + traceability.
    // int ComputeReadinessScore(ConstitutionComplianceReport compliance, ArtifactTraceabilityReport traceability);

    // TODO: Identify blockers — rules that must be compliant before release.
    // IEnumerable<string> FindReadinessBlockers(ConstitutionComplianceReport compliance);

    // TODO: Assess whether the artifact set meets a minimum compliance threshold for release.
    // bool MeetsReleaseThreshold(ConstitutionComplianceReport compliance, double minimumPercentage = 80.0);
}
