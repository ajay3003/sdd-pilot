namespace BirkNext.Api.Services.WasmPerformance;

public interface IWasmStartupAnalysisService
{
    StartupAnalysisResult Analyze(
        IReadOnlyList<DiscoveredAsset> assets,
        StartupAnalysisThresholds? thresholds = null);
}
