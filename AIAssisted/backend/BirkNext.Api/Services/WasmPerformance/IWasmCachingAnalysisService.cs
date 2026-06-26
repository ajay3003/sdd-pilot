namespace BirkNext.Api.Services.WasmPerformance;

public interface IWasmCachingAnalysisService
{
    CachingAnalysisResult Analyze(
        IReadOnlyList<DiscoveredAsset> assets,
        CachingAnalysisThresholds? thresholds = null);
}
