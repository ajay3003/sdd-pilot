using BirkNext.Api.Services.WasmPerformance;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/wasm-performance")]
public class WasmPerformanceController : ControllerBase
{
    private readonly IWasmAssetDiscoveryService  _discovery;
    private readonly IWasmStartupAnalysisService _startup;
    private readonly IWasmApiAnalysisService     _api;
    private readonly ILogger<WasmPerformanceController> _logger;

    public WasmPerformanceController(
        IWasmAssetDiscoveryService  discovery,
        IWasmStartupAnalysisService startup,
        IWasmApiAnalysisService     api,
        ILogger<WasmPerformanceController> logger)
    {
        _discovery = discovery;
        _startup   = startup;
        _api       = api;
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

            // Startup analysis is synchronous; API analysis makes HTTP probes — run in parallel
            var startupAnalysis = _startup.Analyze(discovery.Assets);
            var apiTask         = _api.AnalyzeAsync(request.TargetUrl, ct: ct);

            await apiTask;
            var apiAnalysis = apiTask.Result;

            return Ok(new WasmAssetDiscoveryResult
            {
                TargetUrl       = discovery.TargetUrl,
                DiscoveredAt    = discovery.DiscoveredAt,
                IsBlazorWasm    = discovery.IsBlazorWasm,
                Assets          = discovery.Assets,
                StartupMetrics  = startupAnalysis.StartupMetrics,
                Findings        = startupAnalysis.Findings.ToList(),
                Metrics         = startupAnalysis.DisplayMetrics.ToList(),
                Recommendations = startupAnalysis.Recommendations.ToList(),
                ApiAnalysis     = apiAnalysis
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
}
