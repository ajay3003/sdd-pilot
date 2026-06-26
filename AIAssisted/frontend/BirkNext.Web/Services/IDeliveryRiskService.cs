using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Future: Delivery risk forecasting based on readiness and compliance signals.
/// Extension point for Delivery Risk Forecasting and Release Readiness Gate features.
/// </summary>
public interface IDeliveryRiskService
{
    // TODO: Compute a delivery risk score (0–100 where 100 = highest risk).
    // int ComputeDeliveryRisk(QAReadinessReport readiness, ConstitutionComplianceReport compliance);

    // TODO: Forecast whether a feature can ship by a target date given current velocity.
    // DeliveryForecast ForecastDelivery(QAReadinessReport readiness, DateOnly targetDate, int openTasksCount);

    // TODO: Identify the minimum set of actions required to unblock release.
    // IEnumerable<string> FindReleaseBlockers(QAReadinessReport readiness);
}
