using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IDeliveryReadinessAssessmentService
{
    DeliveryReadinessReport Assess(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks);

    IEnumerable<ReadinessBlocker> FilterBlockersBySeverity(
        IEnumerable<ReadinessBlocker> blockers,
        GateSeverity? severity);

    IEnumerable<ReadinessBlocker> FilterBlockersByPhase(
        IEnumerable<ReadinessBlocker> blockers,
        string? phase);

    IEnumerable<DeliveryRecommendation> FilterRecommendationsByPhase(
        IEnumerable<DeliveryRecommendation> recs,
        string? phase);

    IEnumerable<DeliveryRecommendation> SearchRecommendations(
        IEnumerable<DeliveryRecommendation> recs,
        string query);
}
