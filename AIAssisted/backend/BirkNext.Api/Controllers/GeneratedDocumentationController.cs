using BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;
using BirkNext.GeneratedDocumentation;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Generated Documentation Health diagnostic. Reads stored Source Analysis evidence only; authored artifact content in the request is
/// used for this run and never stored.</summary>
[ApiController]
[Route("api/system-diagnostics/generated-documentation")]
public sealed class GeneratedDocumentationController(GeneratedDocumentationDiagnosticService diagnostics) : ControllerBase
{
    private const int MaxAuthoredArtifacts = 16;
    private const int MaxAuthoredCharacters = 2 * 1024 * 1024;

    [HttpPost("run")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<GeneratedDocumentationDiagnosticRun>> Run([FromBody] GeneratedDocumentationDiagnosticRequest? request, CancellationToken cancellationToken)
    {
        request ??= new GeneratedDocumentationDiagnosticRequest();
        if (request.AuthoredArtifacts.Count > MaxAuthoredArtifacts || request.AuthoredArtifacts.Any(a => (a.Content?.Length ?? 0) > MaxAuthoredCharacters))
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too many or too large authored artifacts",
                detail: $"At most {MaxAuthoredArtifacts} authored artifacts of up to {MaxAuthoredCharacters / (1024 * 1024)} MB each are compared.");
        return Ok(await diagnostics.RunAsync(request, cancellationToken));
    }
}
