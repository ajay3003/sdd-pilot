using BirkNext.Api.Services.Specifications;
using BirkNext.Specifications;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>The server-held requirement catalog of an imported project (read-only) and the check of recorded requirement references against it.
/// A check never changes or removes a link.</summary>
[ApiController]
[Route("api/specification-catalog")]
public sealed class SpecificationCatalogController(SpecificationCatalogService catalogs) : ControllerBase
{
    [HttpGet("{projectKey}")]
    public async Task<ActionResult<SpecificationCatalogView>> Current(string projectKey, CancellationToken ct) =>
        await catalogs.CurrentAsync(projectKey, ct) is { } catalog ? Ok(catalog) : NotFound(new { message = "This project has no server copy of its specification. Import the project again to create one." });

    [HttpPost("{projectKey}/check")]
    public async Task<ActionResult<RequirementReferenceCheckResult>> Check(string projectKey, [FromBody] RequirementReferenceCheckRequest request, CancellationToken ct)
    {
        if (request.References is not { Count: <= SpecificationCatalogService.MaxRequirements })
            return BadRequest(new { message = $"Check at most {SpecificationCatalogService.MaxRequirements} references at a time." });
        return Ok(SpecificationCatalogService.Check(projectKey, await catalogs.CurrentAsync(projectKey, ct), request.References));
    }
}
