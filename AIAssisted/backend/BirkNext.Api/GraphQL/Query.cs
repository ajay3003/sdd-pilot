using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;

namespace BirkNext.Api.GraphQL;

/// <summary>Root query type.</summary>
public class Query
{
    /// <summary>Health-check; returns "pong".</summary>
    public string? Ping() => "pong";

    /// <summary>Returns all scenarios for the given project, newest first.</summary>
    public async Task<IReadOnlyList<Scenario>> ScenariosAsync(
        string projectId,
        [Service] ScenarioService scenarioService,
        CancellationToken cancellationToken)
    {
        return await scenarioService.GetAllAsync(projectId, cancellationToken);
    }

    /// <summary>Returns reviewed candidates for the given project, optionally filtered by session.</summary>
    public async Task<IReadOnlyList<ReviewedCandidate>> ReviewedCandidatesAsync(
        string projectId,
        string? sessionId,
        [Service] ReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        return await reviewedCandidateService.GetByProjectAsync(projectId, sessionId, cancellationToken);
    }

    /// <summary>Returns candidate links for the given project, optionally filtered by session.</summary>
    public async Task<IReadOnlyList<CandidateLink>> CandidateLinksAsync(
        string projectId,
        string? sessionId,
        [Service] CandidateLinkService candidateLinkService,
        CancellationToken cancellationToken)
    {
        return await candidateLinkService.GetByProjectAsync(projectId, sessionId, cancellationToken);
    }

    /// <summary>Returns all delta reviews for the given project, newest first.</summary>
    public async Task<IReadOnlyList<QaDeltaReview>> QaDeltaReviewsAsync(
        string projectId,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        CancellationToken cancellationToken)
    {
        return await qaDeltaReviewService.GetAllAsync(projectId, cancellationToken);
    }

    /// <summary>Returns a single delta review by ID, or null if not found.</summary>
    public async Task<QaDeltaReview?> QaDeltaReviewAsync(
        [ID] string id,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        CancellationToken cancellationToken)
    {
        return await qaDeltaReviewService.GetByIdAsync(id, cancellationToken);
    }

    /// <summary>Returns the traceability matrix for the given project. Only Requirement and Test scenarios are included.</summary>
    public async Task<IReadOnlyList<TraceabilityMatrixRow>> TraceabilityMatrixAsync(
        string projectId,
        [Service] TraceLinkService traceLinkService,
        CancellationToken cancellationToken)
    {
        return await traceLinkService.GetTraceabilityMatrixAsync(projectId, cancellationToken);
    }

    /// <summary>Returns aggregate coverage statistics for the given project.</summary>
    public async Task<CoverageSummary> CoverageSummaryAsync(
        string projectId,
        [Service] TraceLinkService traceLinkService,
        CancellationToken cancellationToken)
    {
        return await traceLinkService.GetCoverageSummaryAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// Returns the full impact analysis for a single requirement, or null when not found.
    /// Includes linked tests, risk level, and regression recommendation.
    /// </summary>
    public async Task<RequirementImpact?> RequirementImpactAsync(
        string projectId,
        [ID] string requirementId,
        [Service] ImpactAnalysisService impactAnalysisService,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(requirementId, out var guid))
            return null;

        return await impactAnalysisService.GetRequirementImpactAsync(projectId, guid, cancellationToken);
    }

    /// <summary>Returns the project-wide impact summary with all requirements ranked by risk level.</summary>
    public async Task<ImpactSummary> ImpactSummaryAsync(
        string projectId,
        [Service] ImpactAnalysisService impactAnalysisService,
        CancellationToken cancellationToken)
    {
        return await impactAnalysisService.GetImpactSummaryAsync(projectId, cancellationToken);
    }

    /// <summary>Returns all registered code files for the given project.</summary>
    public async Task<IReadOnlyList<CodeFile>> CodeFilesAsync(
        string projectId,
        [Service] CodeTraceabilityService codeService,
        CancellationToken cancellationToken)
    {
        return await codeService.GetCodeFilesAsync(projectId, cancellationToken);
    }

    /// <summary>Returns project-wide code traceability summary counts.</summary>
    public async Task<CodeSummary> CodeSummaryAsync(
        string projectId,
        [Service] CodeTraceabilityService codeService,
        CancellationToken cancellationToken)
    {
        return await codeService.GetCodeSummaryAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// Returns the full code impact for a single file: all linked requirements and tests.
    /// Returns null if the file is not found.
    /// </summary>
    public async Task<CodeImpact?> CodeImpactAsync(
        string projectId,
        [ID] string codeFileId,
        [Service] CodeTraceabilityService codeService,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(codeFileId, out var guid)) return null;
        return await codeService.GetCodeImpactAsync(projectId, guid, cancellationToken);
    }

    /// <summary>
    /// Returns a deterministic spec drift report: coverage gaps, orphan tests,
    /// at-risk requirements, and recommended actions. Reuses ImpactAnalysisService
    /// for risk levels; adds orphan test detection.
    /// </summary>
    public async Task<SpecDriftReport> SpecDriftReportAsync(
        string projectId,
        [Service] SpecDriftDetectionService driftService,
        CancellationToken cancellationToken)
    {
        return await driftService.GetSpecDriftReportAsync(projectId, cancellationToken);
    }
}
