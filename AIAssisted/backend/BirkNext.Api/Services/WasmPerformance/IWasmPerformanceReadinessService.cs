namespace BirkNext.Api.Services.WasmPerformance;

public interface IWasmPerformanceReadinessService
{
    PerformanceReadinessReport GenerateReport(WasmAssetDiscoveryResult result);
}
