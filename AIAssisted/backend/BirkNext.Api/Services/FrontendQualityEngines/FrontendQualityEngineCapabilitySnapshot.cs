namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed record FrontendQualityEngineCapabilitySnapshot(
    IReadOnlyDictionary<FrontendQualityEngineId, bool> Allowed,
    IReadOnlyDictionary<FrontendQualityEngineId, bool> Enabled,
    ReviewAuthenticationMode AuthMode,
    IReadOnlyDictionary<FrontendQualityEngineId, bool> AuthSupported,
    DateTime CapturedAtUtc);
