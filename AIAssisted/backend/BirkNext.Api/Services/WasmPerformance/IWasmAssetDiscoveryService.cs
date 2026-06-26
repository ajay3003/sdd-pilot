namespace BirkNext.Api.Services.WasmPerformance;

public interface IWasmAssetDiscoveryService
{
    Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, CancellationToken ct = default);
}
