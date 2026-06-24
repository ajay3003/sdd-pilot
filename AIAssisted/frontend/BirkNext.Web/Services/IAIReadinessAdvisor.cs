using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: AI-driven readiness advisory.
/// Extension point for AI Readiness Advisor — analyzes readiness report with an LLM and
/// generates natural-language guidance, blockers, and release recommendations.
/// </summary>
public interface IAIReadinessAdvisor
{
    // TODO: Generate an AI executive summary of the readiness report.
    // Task<string> GenerateReadinessSummaryAsync(QAReadinessReport report, CancellationToken ct = default);

    // TODO: Identify the top N blockers preventing release readiness.
    // Task<IEnumerable<string>> IdentifyBlockersAsync(QAReadinessReport report, int topN = 5, CancellationToken ct = default);

    // TODO: Generate targeted advice for a specific category.
    // Task<string> AdviseCategoryAsync(ReadinessScore categoryScore, CancellationToken ct = default);
}
