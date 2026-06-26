using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: AI-powered QA audit layer built on top of deterministic QaAuditorService results.
/// Consumes a QaAuditReport and enriches it with LLM-generated prioritization, explanations,
/// and deeper recommendations that cannot be produced deterministically.
/// </summary>
public interface IAIQaAuditorService
{
    // TODO: Generate natural-language explanations for each finding.
    // Task<IEnumerable<string>> ExplainFindingsAsync(QaAuditReport report, CancellationToken ct = default);

    // TODO: Prioritize findings beyond severity — account for business context and risk appetite.
    // Task<IReadOnlyList<QaFinding>> ReprioritizeFindingsAsync(QaAuditReport report, string context, CancellationToken ct = default);

    // TODO: Generate deeper, context-aware recommendations from the audit report.
    // Task<IEnumerable<QaRecommendation>> GenerateAIRecommendationsAsync(QaAuditReport report, CancellationToken ct = default);

    // TODO: Write an executive summary of the audit results for stakeholder reporting.
    // Task<string> WriteAuditSummaryAsync(QaAuditReport report, CancellationToken ct = default);
}
