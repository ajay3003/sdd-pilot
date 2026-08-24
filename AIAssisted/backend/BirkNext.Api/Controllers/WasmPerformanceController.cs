using BirkNext.Api.Services.WasmPerformance;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/wasm-performance")]
public class WasmPerformanceController : ControllerBase
{
    private readonly IWasmAssetDiscoveryService          _discovery;
    private readonly IWasmStartupAnalysisService         _startup;
    private readonly IWasmCachingAnalysisService         _caching;
    private readonly IWasmApiAnalysisService             _api;
    private readonly IWasmPerformanceReadinessService    _readiness;
    private readonly ILogger<WasmPerformanceController> _logger;

    public WasmPerformanceController(
        IWasmAssetDiscoveryService          discovery,
        IWasmStartupAnalysisService         startup,
        IWasmCachingAnalysisService         caching,
        IWasmApiAnalysisService             api,
        IWasmPerformanceReadinessService    readiness,
        ILogger<WasmPerformanceController> logger)
    {
        _discovery = discovery;
        _startup   = startup;
        _caching   = caching;
        _api       = api;
        _readiness = readiness;
        _logger    = logger;
    }

    [HttpPost("discover-assets")]
    public async Task<IActionResult> DiscoverAssets(
        [FromBody] WasmAssetDiscoveryRequest request,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest(new { message = "TargetUrl must be a valid http or https URL." });
        }

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "WASM performance review requested for {Host} CorrelationId: {CorrelationId}",
            uri.Host, correlationId);

        try
        {
            var discovery = await _discovery.DiscoverAssetsAsync(request.TargetUrl, ct);

            if (discovery.Error is not null)
                return Ok(discovery);

            // Start async API probe immediately so it runs while synchronous analyses execute
            var apiTask = _api.AnalyzeAsync(request.TargetUrl, ct: ct);

            // Synchronous analyses — pure computation over discovered assets (milliseconds)
            var thresholds = request.Thresholds is not null ? MapThresholds(request.Thresholds) : null;
            var startupAnalysis  = _startup.Analyze(discovery.Assets, thresholds);
            var cachingAnalysis  = _caching.Analyze(discovery.Assets);

            // Wait for API probing to complete
            await apiTask;

            // Build intermediate result so readiness service can aggregate all phase outputs
            var intermediate = new WasmAssetDiscoveryResult
            {
                TargetUrl       = discovery.TargetUrl,
                DiscoveredAt    = discovery.DiscoveredAt,
                IsBlazorWasm    = discovery.IsBlazorWasm,
                Assets          = discovery.Assets,
                StartupMetrics  = startupAnalysis.StartupMetrics,
                Findings        = startupAnalysis.Findings.ToList(),
                Metrics         = startupAnalysis.DisplayMetrics.ToList(),
                Recommendations = startupAnalysis.Recommendations.ToList(),
                ApiAnalysis     = apiTask.Result,
                CachingAnalysis = cachingAnalysis
            };

            return Ok(new WasmAssetDiscoveryResult
            {
                TargetUrl       = intermediate.TargetUrl,
                DiscoveredAt    = intermediate.DiscoveredAt,
                IsBlazorWasm    = intermediate.IsBlazorWasm,
                Assets          = intermediate.Assets,
                StartupMetrics  = intermediate.StartupMetrics,
                Findings        = intermediate.Findings,
                Metrics         = intermediate.Metrics,
                Recommendations = intermediate.Recommendations,
                ApiAnalysis     = intermediate.ApiAnalysis,
                CachingAnalysis = intermediate.CachingAnalysis,
                ReadinessReport = _readiness.GenerateReport(intermediate)
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WASM performance review failed. CorrelationId: {CorrelationId}",
                correlationId);
            return StatusCode(500, new { message = "Review failed unexpectedly. Check backend logs.", correlationId });
        }
    }

    private static StartupAnalysisThresholds? MapThresholds(dynamic payload)
    {
        if (payload?.MaxStartupRequests == null && payload?.MaxStartupDownloadMB == null)
            return null;

        return new StartupAnalysisThresholds
        {
            MaxStartupRequests = payload?.MaxStartupRequests ?? 150,
            MaxStartupDownloadMB = payload?.MaxStartupDownloadMB ?? 5.0,
            MaxFrameworkMB = payload?.MaxFrameworkMB ?? 3.0,
            MaxApplicationMB = payload?.MaxApplicationMB ?? 1.0,
            MaxIndividualAssetMB = payload?.MaxIndividualAssetMB ?? 0.5,
        };
    }
}
