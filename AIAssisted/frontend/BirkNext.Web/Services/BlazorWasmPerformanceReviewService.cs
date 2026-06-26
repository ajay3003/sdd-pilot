using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class BlazorWasmPerformanceReviewService : IBlazorWasmPerformanceReviewService
{
    private readonly HttpClient _client;
    private WasmPerformanceReviewReport? _cached;

    public BlazorWasmPerformanceReviewService(HttpClient client) => _client = client;

    public async Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(
        string targetUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var request  = new WasmAssetDiscoveryRequest { TargetUrl = targetUrl };
            var response = await _client.PostAsJsonAsync(
                "api/wasm-performance/discover-assets", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var msg  = code switch
                {
                    400 => "Invalid URL. Enter a full https:// address.",
                    499 => "Discovery cancelled or timed out.",
                    _   => $"Discovery failed (HTTP {code}). Check that the backend is running."
                };
                return new WasmAssetDiscoveryResult { TargetUrl = targetUrl, Error = msg };
            }

            var result = await response.Content
                .ReadFromJsonAsync<WasmAssetDiscoveryResult>(cancellationToken: cancellationToken);

            return result ?? new WasmAssetDiscoveryResult
            {
                TargetUrl = targetUrl,
                Error     = "Empty response from backend."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WasmAssetDiscoveryResult
            {
                TargetUrl = targetUrl,
                Error     = "Could not reach the backend. Check that the server is running."
            };
        }
    }

    public async Task<WasmPerformanceReviewReport> RunReviewAsync(
        string targetUrl, CancellationToken cancellationToken = default)
    {
        var result = await DiscoverAssetsAsync(targetUrl, cancellationToken);

        if (result.Error is not null)
        {
            _cached = null;
            return new WasmPerformanceReviewReport
            {
                TargetUrl    = targetUrl,
                ReviewedAt   = DateTime.UtcNow,
                ErrorMessage = result.Error
            };
        }

        var totalBytes = result.Assets.Sum(a => a.ContentLength ?? a.DownloadedBytes);

        // Merge startup findings + API findings for the overview/recommendations tabs
        var allFindings = new List<PerformanceFinding>(result.Findings);
        var allRecs     = new List<PerformanceRecommendation>(result.Recommendations);

        if (result.ApiAnalysis is not null)
        {
            allFindings.AddRange(result.ApiAnalysis.Findings);
            allRecs.AddRange(result.ApiAnalysis.Recommendations
                .Select(r => new PerformanceRecommendation
                {
                    Priority    = result.Recommendations.Count + r.Priority,
                    Title       = r.Title,
                    Description = r.Description,
                    Category    = r.Category
                }));
        }

        if (result.CachingAnalysis is not null)
        {
            allFindings.AddRange(result.CachingAnalysis.Findings);
            var apiOffset = result.Recommendations.Count + (result.ApiAnalysis?.Recommendations.Count ?? 0);
            allRecs.AddRange(result.CachingAnalysis.Recommendations
                .Select(r => new PerformanceRecommendation
                {
                    Priority    = apiOffset + r.Priority,
                    Title       = r.Title,
                    Description = r.Description,
                    Category    = r.Category
                }));
        }

        var report = new WasmPerformanceReviewReport
        {
            TargetUrl       = targetUrl,
            ReviewedAt      = DateTime.UtcNow,
            IsBlazorWasm    = result.IsBlazorWasm,
            Assets          = result.Assets,
            StartupMetrics  = result.StartupMetrics,
            ApiAnalysis     = result.ApiAnalysis,
            CachingAnalysis = result.CachingAnalysis,
            ReadinessReport = result.ReadinessReport,
            Findings        = allFindings,
            Metrics         = result.Metrics,
            Recommendations = allRecs,
            Health          = new PerformanceHealth
            {
                AssetsDiscovered   = result.Assets.Count,
                TotalTransferBytes = totalBytes,
                FindingsCount      = allFindings.Count,
                Critical           = allFindings.Count(f => f.Severity == PerformanceSeverity.Critical),
                High               = allFindings.Count(f => f.Severity == PerformanceSeverity.High),
                Medium             = allFindings.Count(f => f.Severity == PerformanceSeverity.Medium),
                Low                = allFindings.Count(f => f.Severity == PerformanceSeverity.Low),
                Info               = allFindings.Count(f => f.Severity == PerformanceSeverity.Info)
            },
            Limitations =
            [
                "Phase 5 complete: startup, caching & compression, and API analysis are active.",
                "API operations shown are those observed during the automated scan; runtime request interception requires browser instrumentation.",
                "Runtime metrics (FCP, LCP, TTI) require browser instrumentation.",
                "Blazor runtime analysis not yet implemented."
            ]
        };

        _cached = report;
        return report;
    }

    public WasmPerformanceReviewReport? GetCached() => _cached;
    public void ClearCache() => _cached = null;
}
