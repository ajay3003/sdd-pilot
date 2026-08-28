namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed record FrontendQualityEngineSelectionContext(
    IReadOnlyDictionary<FrontendQualityEngineId, bool> Selected);
