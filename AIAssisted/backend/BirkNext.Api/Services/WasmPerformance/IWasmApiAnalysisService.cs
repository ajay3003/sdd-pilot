namespace BirkNext.Api.Services.WasmPerformance;

public interface IWasmApiAnalysisService
{
    Task<ApiAnalysisResult> AnalyzeAsync(
        string targetUrl,
        ApiAnalysisThresholds? thresholds = null,
        CancellationToken ct = default);
}
