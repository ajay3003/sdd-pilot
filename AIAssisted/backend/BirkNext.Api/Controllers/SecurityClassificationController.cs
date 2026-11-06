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
    /// Read-only: nothing is uploaded, stored or called at runtime. Source evidence belongs to the source snapshot, not to a Target Environment,
    /// so environmentId is optional here (as for Dependency Review): without one, the same snapshots and evidence are returned.</summary>
    [HttpGet("source-scope")]
    public async Task<ActionResult<ClassificationScopeOptions>> SourceScope([FromQuery] string? environmentId, [FromQuery] Guid? primary, [FromQuery] Guid[]? related,
        [FromQuery] string[]? excluded, CancellationToken ct) =>
        Ok(await reviews.SourceScopeAsync(environmentId?.Trim() ?? "", primary is { } p ? new ReviewSourceScopeRequest { PrimarySnapshotId = p, RelatedSnapshotIds = [.. related ?? []], ExcludedSuggestions = [.. excluded ?? []] } : null, ct));

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

    /// <summary>Static security review only (source evidence; no identity, token or network call). environmentId is optional: without one the
    /// review is stored under its source scope and read back with <c>GET ?environmentId=source:{snapshot id}</c>.</summary>
    [HttpPost("static-reviews")]
    public async Task<ActionResult<ClassificationReviewResult>> RunStatic([FromQuery] string? environmentId, [FromBody] ClassificationRunRequest request, CancellationToken ct)
    {
        try { return Ok(await reviews.RunStaticAsync(environmentId, request, ct)); }
        catch (InvalidSourceSelectionException e) { return BadRequest(e.Message); }
    }

    /// <summary>Runtime verification only, behind the live probe gate (approved DEV/QA context, synthetic test children, a per-run token).
    /// A refused gate is 409 with the reason; nothing is stored or called.</summary>
    [HttpPost("runtime-verifications")]
    public async Task<ActionResult<ClassificationReviewResult>> RunRuntime([FromQuery] string environmentId, [FromBody] ClassificationRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        try
        {
            var (result, blocked) = await reviews.RunRuntimeAsync(environmentId, request, ct);
            return blocked is not null ? Conflict(new { message = blocked }) : Ok(result);
        }
        catch (InvalidSourceSelectionException e) { return BadRequest(e.Message); }
    }

    /// <summary>Each consumer's assessment (M01, Skolenærvær …) of the same shared evidence: latest stored review, canonical test context,
    /// approved controlled identity references. Read-only.</summary>
    [HttpGet("consumers")]
    public async Task<ActionResult<IReadOnlyList<SecurityClassificationConsumerAssessment>>> Consumers([FromServices] BirkNext.Api.Services.SecurityClassification.SecurityClassificationConsumerService consumers,
        [FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await consumers.AssessAsync(environmentId, ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ClassificationReviewResult>> GetRun(Guid runId, CancellationToken ct) =>
        await reviews.GetRunAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
