using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: Cross-artifact compliance analysis.
/// Extension point for Constitution Compliance Review, QA Readiness Score, and AI QA Auditor features.
/// </summary>
public interface IComplianceAnalysisService
{
    // TODO: Evaluate plan compliance against the full constitution rule set
    // Task<ComplianceReport> EvaluateAsync(ConstitutionDocument constitution, PlanDocument plan, CancellationToken ct = default);

    // TODO: Compute overall compliance score (0–100) across all artifacts
    // int ComputeComplianceScore(ArtifactTraceabilityReport report);

    // TODO: Find rules that are defined in the constitution but never enforced in any plan gate
    // IEnumerable<string> FindUnenforceableRules(ConstitutionDocument constitution, IEnumerable<PlanDocument> plans);

    // TODO: Traceability heat map data — per-rule coverage intensity
    // IEnumerable<RuleCoverageHeat> ComputeHeatMap(ArtifactTraceabilityReport report);
}
