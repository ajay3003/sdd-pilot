using BirkNext.Api.Services.ProjectImport;
using BirkNext.ProjectImport;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/system-diagnostics/project-compatibility")]
public sealed class ProjectCompatibilityController(ProjectCompatibilityDiagnosticService diagnostics) : ControllerBase
{
    [HttpPost("run")]
    public ActionResult<ProjectCompatibilityRun> Run(CancellationToken cancellationToken) => Ok(diagnostics.Run(cancellationToken));
}
