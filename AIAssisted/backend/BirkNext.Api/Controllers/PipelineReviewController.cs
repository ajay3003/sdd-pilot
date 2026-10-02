using BirkNext.Api.Services.PipelineReview;
using BirkNext.PipelineReview;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Pipeline Review: delivery-flow interpretation of the pipeline definitions in a Source Analysis snapshot. Read-only — nothing is uploaded,
/// parsed again or executed; the optional Azure DevOps metadata is configuration read with GETs only.
/// </summary>
[ApiController]
[Route("api/pipeline-review")]
public sealed class PipelineReviewController(IPipelineReviewService service) : ControllerBase
{
    [HttpGet("sources")]
    public async Task<ActionResult<PipelineReviewSources>> Sources([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await service.SourcesAsync(environmentId, ct));

    [HttpGet]
    public async Task<ActionResult<PipelineReviewResult>> Review([FromQuery] string environmentId, [FromQuery] Guid? snapshotId, [FromQuery] bool metadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await service.ReviewAsync(environmentId, snapshotId, metadata, ct) is { } result ? Ok(result)
            : NotFound(new { message = snapshotId is null ? "No Source Analysis snapshot with pipeline definitions exists for this Target Environment." : "The selected source snapshot is unavailable; nothing is substituted." });
    }

    /// <summary>"A developer changed this path — which pipelines start, and what validates it before each environment?"</summary>
    [HttpGet("probe")]
    public async Task<ActionResult<PathProbeResult>> Probe([FromQuery] string environmentId, [FromQuery] Guid snapshotId, [FromQuery] string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(path)) return BadRequest("environmentId and path are required.");
        if (path.Length > 400 || path.Contains("..", StringComparison.Ordinal)) return BadRequest("Give a repository-relative path.");
        return await service.ProbeAsync(environmentId, snapshotId, path, ct) is { } result ? Ok(result) : NotFound();
    }

    [HttpGet("compare")]
    public async Task<ActionResult<PipelineReviewComparison>> Compare([FromQuery] string environmentId, [FromQuery] Guid previous, [FromQuery] Guid current, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.")
            : await service.CompareAsync(environmentId, previous, current, ct) is { } result ? Ok(result) : NotFound();
}
