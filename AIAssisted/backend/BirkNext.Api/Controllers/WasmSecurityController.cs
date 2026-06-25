using BirkNext.Api.Services.WasmSecurity;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/wasm-security")]
public class WasmSecurityController : ControllerBase
{
    private readonly IBlazorWasmSecurityReviewService _scanner;
    private readonly ILogger<WasmSecurityController> _logger;

    public WasmSecurityController(
        IBlazorWasmSecurityReviewService scanner,
        ILogger<WasmSecurityController> logger)
    {
        _scanner = scanner;
        _logger  = logger;
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan([FromBody] WasmScanRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return BadRequest(new { message = "TargetUrl must be a valid http or https URL." });
        }

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "WASM security scan requested for {Host} CorrelationId: {CorrelationId}",
            uri.Host, correlationId);

        try
        {
            var report = await _scanner.ScanAsync(request, ct);
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WASM security scan failed. CorrelationId: {CorrelationId}", correlationId);
            return StatusCode(500, new { message = "Scan failed unexpectedly. Check backend logs.", correlationId });
        }
    }
}
