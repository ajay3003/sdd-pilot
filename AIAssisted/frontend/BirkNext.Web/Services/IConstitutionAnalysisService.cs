using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IConstitutionAnalysisService
{
    ConstitutionDocument Parse(string markdown);

    // Section-level search (existing)
    IEnumerable<ConstitutionPrinciple> SearchPrinciples(IEnumerable<ConstitutionPrinciple> principles, string query);
    IEnumerable<ConstitutionStandard> SearchStandards(IEnumerable<ConstitutionStandard> standards, string query);
    IEnumerable<ConstitutionConstraint> SearchConstraints(IEnumerable<ConstitutionConstraint> constraints, string query);
    IEnumerable<ConstitutionGovernanceItem> SearchGovernance(IEnumerable<ConstitutionGovernanceItem> items, string query);

    // Rule catalog operations (Phase 2)
    IEnumerable<ConstitutionRule> SearchRules(IEnumerable<ConstitutionRule> rules, string query);
    IEnumerable<ConstitutionRule> FilterRulesByType(IEnumerable<ConstitutionRule> rules, ConstitutionRuleType? type);
    List<ConstitutionMapNode> BuildMapTree(IEnumerable<ConstitutionRule> catalog);

    bool MatchesSearch(string query, params string?[] fields);
}
