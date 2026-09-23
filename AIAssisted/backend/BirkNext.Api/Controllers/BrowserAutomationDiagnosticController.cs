using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Runs the browser automation diagnostic. One endpoint, one run, one report.
///
/// The run is long (it starts a browser), so the request's cancellation token is passed straight through: a client that
/// disconnects or cancels stops the diagnostic and its cleanup still runs.
/// </summary>
[ApiController]
[Route("api/browser-automation-diagnostic")]
public sealed class BrowserAutomationDiagnosticController(
    IBrowserAutomationDiagnosticService diagnostic,
    ILogger<BrowserAutomationDiagnosticController> logger,
    BirkNext.Api.Services.HeadlessAuthDiagnostic.BrowserAutomationEvidenceStore? evidence = null) : ControllerBase
{
    [HttpPost("run")]
    [ProducesResponseType(typeof(BrowserAutomationDiagnosticReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<BrowserAutomationDiagnosticReport>> Run(
        [FromBody] BrowserAutomationDiagnosticRequest request, CancellationToken ct)
    {
        logger.LogInformation("Browser automation diagnostic requested for {TargetEnvironmentId}", request.TargetEnvironmentId);
        // A blocked or failed diagnostic is still a result the user needs to read, so it comes back as 200 with a
        // report rather than as an error status with nothing in it.
        var report = await diagnostic.RunAsync(request, ct);
        evidence?.Record(report);
        return Ok(report);
    }
}
