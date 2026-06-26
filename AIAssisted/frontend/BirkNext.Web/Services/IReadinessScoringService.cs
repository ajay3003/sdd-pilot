using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: QA delivery readiness scoring.
/// Extension point for QA Readiness Score, Delivery Readiness Assessment, and Code Traceability Integration features.
/// </summary>
public interface IReadinessScoringService
{
    // TODO: Compute a 0–100 QA readiness score from the traceability report
    // int ComputeReadinessScore(ArtifactTraceabilityReport report);

    // TODO: Identify which requirements are blockers for release readiness
    // IEnumerable<string> FindReadinessBlockers(ArtifactTraceabilityReport report);

    // TODO: Assess delivery readiness — are all high-priority gaps resolved?
    // DeliveryReadinessAssessment AssessDeliveryReadiness(ArtifactTraceabilityReport report, ReadinessThresholds thresholds);

    // TODO: Link code file paths to spec requirements via task references
    // IEnumerable<CodeTraceabilityLink> BuildCodeTraceability(ArtifactTraceabilityReport report, IEnumerable<string> changedFilePaths);
}
