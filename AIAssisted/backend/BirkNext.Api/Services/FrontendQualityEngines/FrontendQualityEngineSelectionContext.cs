namespace BirkNext.Api.Services.FrontendQualityEngines;

/// <summary>
/// Review-scoped engine selection sent by the frontend. <paramref name="Selected"/> is the per-review selection state;
/// <paramref name="ReadinessEngines"/> lists the engines that are ACTIVE for the review and therefore need the expensive Layer 3
/// readiness probe. Null keeps the legacy behaviour (probe every engine); an empty list probes none.
/// </summary>
public sealed record FrontendQualityEngineSelectionContext(
    IReadOnlyDictionary<FrontendQualityEngineId, bool> Selected,
    IReadOnlyList<FrontendQualityEngineId>? ReadinessEngines = null,
    FrontendQualityEngineTargetContext? Target = null);

/// <summary>
/// The Target Environment the review is about. Passive Security only runs against a target registered by the server
/// (FrontendPassiveSecurity:TrustedProfiles), so status reports an untrusted target as unavailable instead of Ready.
/// </summary>
public sealed record FrontendQualityEngineTargetContext(string ProfileId, string TargetUrl, string? EnvironmentType);
