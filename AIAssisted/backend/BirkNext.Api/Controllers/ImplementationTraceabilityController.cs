using BirkNext.Api.Configuration;
using BirkNext.Api.Services.ImplementationTraceability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/implementation-traceability")]
public class ImplementationTraceabilityController : ControllerBase
{
    private readonly IImplementationEvidenceProvider _provider;
    private readonly AzureDevOpsOptions _adoOptions;
    private readonly bool _usingMock;
    private readonly ILogger<ImplementationTraceabilityController> _logger;

    public ImplementationTraceabilityController(
        IImplementationEvidenceProvider provider,
        IOptions<AzureDevOpsOptions> adoOptions,
        ILogger<ImplementationTraceabilityController> logger)
    {
        _provider   = provider;
        _adoOptions = adoOptions.Value;
        _usingMock  = provider is MockImplementationEvidenceProvider;
        _logger     = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var response = new ProviderStatusResponse
        {
            Configured = _adoOptions.IsConfigured,
            UsingMock  = _usingMock,
            Message    = _usingMock
                ? "Azure DevOps integration is not configured. Showing local/demo evidence."
                : "Azure DevOps integration is configured.",
        };
        return Ok(response);
    }

    [HttpPost("fetch")]
    public async Task<IActionResult> Fetch(
        [FromBody] FetchEvidenceRequest request,
        CancellationToken ct)
    {
        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "Implementation traceability fetch requested. WorkItemIds: [{WorkItemIds}] CorrelationId: {CorrelationId}",
            string.Join(", ", request.WorkItemIds),
            correlationId);

        try
        {
            var report = await _provider.FetchAsync(
                request.WorkItemIds,
                request.RepositoryId,
                request.Branch,
                ct);

            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Implementation traceability fetch failed. CorrelationId: {CorrelationId}",
                correlationId);

            return StatusCode(500, new
            {
                message = "Failed to retrieve implementation evidence. Please try again.",
                correlationId,
            });
        }
    }
}
