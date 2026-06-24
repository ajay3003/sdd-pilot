using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// TODO: Implement for Constitution Traceability features:
//   - Constitution vs Spec Analysis    (compare PP/PS rules against spec requirements)
//   - Constitution vs Tasks Analysis   (check task coverage against constitution constraints)
//   - Constitution vs Plan Analysis    (verify plan decisions adhere to constitution principles)
//   - Constitution Coverage Score      (percentage of spec requirements governed by a constitution rule)
//
// Depends on: IConstitutionAnalysisService, ISpecComparisonService (or spec models)
// Entry point for the future "Constitution Traceability" tab inside Constitution Explorer.

public interface IConstitutionTraceabilityService
{
    // TODO: Compare constitution rules against a parsed spec document
    // Task<ConstitutionSpecCoverageReport> AnalyseSpecCoverage(
    //     ConstitutionDocument constitution, string specMarkdown);

    // TODO: Evaluate whether a tasks.md adheres to constitution constraints
    // Task<ConstitutionTasksAdherenceReport> AnalyseTasksAdherence(
    //     ConstitutionDocument constitution, string tasksMarkdown);

    // TODO: Compute a coverage score (0–100) showing how well a spec is governed
    // int ComputeCoverageScore(ConstitutionDocument constitution, string specMarkdown);
}
