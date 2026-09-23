using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.HeadlessAuthDiagnostic;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/headless-auth-diagnostic")]
public sealed class HeadlessAuthDiagnosticController(IHeadlessDiagnosticService diagnostic, BrowserAutomationEvidenceStore evidence) : ControllerBase
{
    [HttpPost("prerequisite")]
    public ActionResult<HeadlessPrerequisite> Prerequisite(HeadlessDiagnosticRequest request) => Ok(evidence.Check(request));
    [HttpPost("run")]
    public async Task<ActionResult<HeadlessDiagnosticReport>> Run(HeadlessDiagnosticRequest request, CancellationToken ct) => Ok(await diagnostic.RunAsync(request, ct));
}
