using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Security Classification / Gradert tilgang review. Source evidence comes from Source Analysis snapshots (no upload here); the temporary test context (synthetic ids,
/// identity labels, endpoint) is held in backend memory only — never in the database — and a run's tokens are used for that run and never stored. Live checks are fixed GraphQL queries against configured
/// test children in an approved DEV/QA context — never Production, never a mutation, never a search for real classified children.
/// </summary>
[ApiController]
[Route("api/security-classification")]
public sealed class SecurityClassificationController(IClassificationReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ClassificationOverview>> Overview([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await reviews.OverviewAsync(environmentId, ct));

    /// <summary>Source Analysis snapshots for this review and, for a primary (+ related) selection, its candidates and combined source evidence.
    /// Read-only: nothing is uploaded, stored or called at runtime.</summary>
    [HttpGet("source-scope")]
    public async Task<ActionResult<ClassificationScopeOptions>> SourceScope([FromQuery] string environmentId, [FromQuery] Guid? primary, [FromQuery] Guid[]? related,
        [FromQuery] string[]? excluded, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.")
            : Ok(await reviews.SourceScopeAsync(environmentId, primary is { } p ? new ReviewSourceScopeRequest { PrimarySnapshotId = p, RelatedSnapshotIds = [.. related ?? []], ExcludedSuggestions = [.. excluded ?? []] } : null, ct));

    [HttpPut("context")]
    public async Task<ActionResult<ClassificationTestContext>> SaveContext([FromQuery] string environmentId, [FromBody] ClassificationTestContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (saved, error) = await reviews.SaveContextAsync(environmentId, context, ct);
        return error is not null ? BadRequest(error) : Ok(saved);
    }

    /// <summary>Clears the temporary in-memory test context. Stored reviews and any context row an earlier version may have written are not touched.</summary>
    [HttpDelete("context")]
    public IActionResult ClearContext([FromQuery] string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        reviews.ClearContext(environmentId);
        return NoContent();
    }

    [HttpPost("runs")]
    public async Task<ActionResult<ClassificationReviewResult>> Run([FromQuery] string environmentId, [FromBody] ClassificationRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        try { return Ok(await reviews.RunAsync(environmentId, request, ct)); }
        catch (InvalidSourceSelectionException e) { return BadRequest(e.Message); }
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ClassificationReviewResult>> GetRun(Guid runId, CancellationToken ct) =>
        await reviews.GetRunAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
