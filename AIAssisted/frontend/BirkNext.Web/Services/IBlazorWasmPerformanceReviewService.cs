using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IBlazorWasmPerformanceReviewService
{
    Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, CancellationToken cancellationToken = default);
    Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, CancellationToken cancellationToken = default);
    WasmPerformanceReviewReport? GetCached();
    void ClearCache();
}
