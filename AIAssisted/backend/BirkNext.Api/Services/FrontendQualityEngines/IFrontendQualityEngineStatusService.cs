namespace BirkNext.Api.Services.FrontendQualityEngines;

public interface IFrontendQualityEngineStatusService
{
    Task<FrontendQualityEngineStatusReport> GetStatusAsync(FrontendQualityEngineStatusQuery? query = null, CancellationToken ct = default);
    FrontendQualityEngineCapabilitySnapshot CaptureSnapshot(ReviewAuthenticationMode authMode);
}
