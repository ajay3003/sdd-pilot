using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IPlanAnalysisService
{
    PlanDocument Parse(string markdown);

    IEnumerable<PlanRisk> SearchRisks(IEnumerable<PlanRisk> risks, string query);
    IEnumerable<PlanRisk> FilterRisksBySeverity(IEnumerable<PlanRisk> risks, RiskSeverity? severity);

    IEnumerable<PlanConstraint> SearchConstraints(IEnumerable<PlanConstraint> constraints, string query);
    IEnumerable<PlanConstraint> FilterConstraintsByType(IEnumerable<PlanConstraint> constraints, ConstraintType? type);

    IEnumerable<PlanArchitectureDecision> SearchDecisions(IEnumerable<PlanArchitectureDecision> decisions, string query);

    IEnumerable<PlanComplexityItem> SearchComplexity(IEnumerable<PlanComplexityItem> items, string query);
    IEnumerable<PlanComplexityItem> FilterComplexityByLevel(IEnumerable<PlanComplexityItem> items, ComplexityLevel? level);

    IEnumerable<PlanConstitutionCheckItem> SearchConstitutionCheck(IEnumerable<PlanConstitutionCheckItem> items, string query);
    IEnumerable<PlanConstitutionCheckItem> FilterCheckByStatus(IEnumerable<PlanConstitutionCheckItem> items, ConstitutionCheckStatus? status);

    IEnumerable<PlanGate> SearchGates(IEnumerable<PlanGate> gates, string query);
    IEnumerable<PlanGate> FilterGatesByStatus(IEnumerable<PlanGate> gates, PlanGateStatus? status);

    IEnumerable<PlanImplementationPhase> SearchPhases(IEnumerable<PlanImplementationPhase> phases, string query);

    bool MatchesSearch(string query, params string?[] fields);
}
