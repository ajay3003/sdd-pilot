using BirkNext.Api.Services;
using BirkNext.MarkdownDiagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/system-diagnostics/markdown")]
public sealed class MarkdownDiagnosticsController(MarkdownDiagnosticsService diagnostics) : ControllerBase
{
    [HttpPost("content-integrity/run")]
    public ActionResult<ContentIntegrityRun> ContentIntegrity(CancellationToken cancellationToken) => Ok(diagnostics.RunContentIntegrity(cancellationToken));

    [HttpPost("explorer-coverage/run")]
    public ActionResult<ExplorerCoverageRun> ExplorerCoverage(CancellationToken cancellationToken) => Ok(diagnostics.RunExplorerCoverage(cancellationToken));
}
