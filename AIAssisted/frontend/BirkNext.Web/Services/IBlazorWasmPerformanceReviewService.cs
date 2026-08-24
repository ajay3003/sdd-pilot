using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IBlazorWasmPerformanceReviewService
{
    Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default);
    Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default);
    WasmPerformanceReviewReport? GetCached();
    void ClearCache();
}
