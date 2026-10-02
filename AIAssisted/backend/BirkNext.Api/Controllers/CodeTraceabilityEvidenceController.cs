using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/code-traceability")]
public sealed class CodeTraceabilityEvidenceController(CodeTraceabilityService codeTraceability) : ControllerBase
{
    /// <summary>Read-only projection of persisted CodeLinks for the shared SDD evidence graph.</summary>
    [HttpGet("links")]
    public async Task<ActionResult<IReadOnlyList<CodeLinkEvidenceDto>>> Links([FromQuery] string projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return BadRequest("projectId is required.");
        return Ok(await codeTraceability.GetProjectLinksAsync(projectId, ct));
    }
}
