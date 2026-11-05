using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.SourceEvidence;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Remembered "same repository" declarations used by change reviews (Test Coverage &amp; Overlap, Code Risk) to compare snapshots of
/// different repository identities. Declarations are explicit and revocable; nothing is detected or deleted here.</summary>
[ApiController]
[Route("api/source-repository-associations")]
public sealed class SourceRepositoryAssociationsController(SourceRepositoryAssociationService associations) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SourceRepositoryAssociation>>> List(CancellationToken ct) => Ok(await associations.ActiveAsync(ct));

    [HttpPost]
    public async Task<ActionResult<SourceRepositoryAssociation>> Declare([FromBody] DeclareRepositoryAssociationRequest request, CancellationToken ct)
    {
        var (association, error) = await associations.DeclareAsync(request, User.Identity?.Name ?? "local user", ct);
        return association is null ? BadRequest(new { message = error }) : Ok(association);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await associations.RevokeAsync(id, User.Identity?.Name ?? "local user", ct) ? NoContent() : NotFound();
}
