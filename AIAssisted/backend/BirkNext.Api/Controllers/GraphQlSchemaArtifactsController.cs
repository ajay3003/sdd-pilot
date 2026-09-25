using BirkNext.ApiReview;
using BirkNext.Api.Services.ApiQuality;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Trusted GraphQL schema artifacts for API Quality Review, one per (Target Environment, GraphQL API target). Metadata only is
/// returned — never the SDL text. Uploads are validated immediately; an invalid artifact is rejected with its reason and never stored.
/// </summary>
[ApiController]
[Route("api/graphql-schema-artifacts")]
[RequestSizeLimit(GraphQlSchemaArtifactRules.MaxBytes * 2 + 64 * 1024)]   // JSON-escaped SDL can be larger than the SDL itself
public sealed class GraphQlSchemaArtifactsController(IGraphQlSchemaArtifactStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GraphQlSchemaArtifact>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." }) : Ok(await store.ListAsync(environmentId, ct));

    [HttpPut]
    public async Task<ActionResult<GraphQlSchemaArtifact>> Save([FromBody] GraphQlSchemaArtifactUpload upload, CancellationToken ct)
    {
        var (artifact, error) = await store.SaveAsync(upload, ct);
        return artifact is null ? BadRequest(new { message = error }) : Ok(artifact);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, [FromQuery] string targetId, CancellationToken ct) =>
        await store.DeleteAsync(environmentId, targetId, ct) ? NoContent() : NotFound();
}
