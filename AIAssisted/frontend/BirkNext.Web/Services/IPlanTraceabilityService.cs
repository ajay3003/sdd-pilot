using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// TODO: Implement for Plan cross-artifact analysis features:
//   - Plan vs Specification Analysis   (verify plan decisions align with spec requirements)
//   - Plan vs Tasks Analysis           (check tasks.md coverage against plan milestones and ADRs)
//   - Constitution Compliance Analysis (evaluate plan against constitution principles and standards)
//
// Depends on: IPlanAnalysisService, IConstitutionAnalysisService, ISpecComparisonService
// Entry point for future "Plan Alignment" page and AI-assisted plan review.

public interface IPlanTraceabilityService
{
    // TODO: Verify that all spec requirements FR-NN are addressed by plan ADRs or milestones
    // Task<PlanSpecAlignmentReport> AnalyseSpecAlignment(
    //     PlanDocument plan, string specMarkdown);

    // TODO: Check whether tasks.md deliverables cover every plan milestone
    // Task<PlanTasksCoverageReport> AnalyseTasksCoverage(
    //     PlanDocument plan, string tasksMarkdown);

    // TODO: Evaluate plan decisions against constitution rules (PP-NN, PS-NN)
    // Task<PlanConstitutionAlignmentReport> AnalyseConstitutionAlignment(
    //     PlanDocument plan, ConstitutionDocument constitution);
}
