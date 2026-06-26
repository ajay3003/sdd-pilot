using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: AI-driven constitution compliance auditing.
/// Extension point for AI Compliance Auditor and Automated Constitution Enforcement features.
/// </summary>
public interface IAIComplianceAuditor
{
    // TODO: Use an LLM to detect semantic violations — rules that are technically present
    // but implemented incorrectly or in a way that contradicts the rule's intent.
    // Task<IEnumerable<ComplianceViolation>> DetectSemanticViolationsAsync(
    //     ConstitutionDocument constitution, string artifactMarkdown, CancellationToken ct = default);

    // TODO: Suggest improvements when a rule is partially covered.
    // Task<IEnumerable<ComplianceRecommendation>> SuggestImprovementsAsync(
    //     ConstitutionRule rule, string artifactMarkdown, CancellationToken ct = default);

    // TODO: Automatically flag new compliance issues when a constitution rule changes.
    // Task<IEnumerable<ComplianceViolation>> AuditAmendmentImpactAsync(
    //     ConstitutionRule oldRule, ConstitutionRule newRule,
    //     IEnumerable<string> artifactMarkdowns, CancellationToken ct = default);
}
