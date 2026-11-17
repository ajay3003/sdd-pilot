using BirkNext.Api.Services.TechnicalHistory;
using BirkNext.ApiReview;
using BirkNext.TechnicalHistory;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Stored technical runs (API Quality Review) and the follow-up of their findings. Runs are append-only; nothing here sends a request to a target.</summary>
[ApiController]
[Route("api/technical-history")]
public sealed class TechnicalHistoryController(TechnicalHistoryService history) : ControllerBase
{
    [HttpPost("api-review-runs")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<StoredApiReviewRun>> StoreRun([FromBody] StoreApiReviewRunRequest request, CancellationToken ct) =>
        Result(await history.StoreApiReviewRunAsync(request, ct));

    [HttpGet("api-review-runs")]
    public async Task<ActionResult<IReadOnlyList<StoredApiReviewRun>>> Runs([FromQuery] string? environmentId, [FromQuery] string? projectKey, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await history.ApiReviewRunsAsync(environmentId, projectKey, take, ct));

    [HttpGet("api-review-runs/{id:guid}")]
    public async Task<ActionResult<ApiReviewReport>> Report(Guid id, [FromQuery] string? projectKey, CancellationToken ct) =>
        await history.ApiReviewReportAsync(id, ct, projectKey) is { } report ? Ok(report) : NotFound(new { message = "No such stored run." });

    /// <summary>Stores a finished Frontend Quality Review, safe fuzzing or authorization run (append-only; a report holding a credential is refused).</summary>
    [HttpPost("review-runs")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<StoredTechnicalReviewRun>> StoreReviewRun([FromBody] StoreTechnicalReviewRunRequest request, CancellationToken ct) =>
        Result(await history.StoreReviewRunAsync(request, ct));

    [HttpGet("review-runs")]
    public async Task<ActionResult<IReadOnlyList<StoredTechnicalReviewRun>>> ReviewRuns([FromQuery] string? kind, [FromQuery] string? environmentId, [FromQuery] string? projectKey,
        [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await history.ReviewRunsAsync(kind, environmentId, projectKey, take, ct));

    [HttpGet("review-runs/{id:guid}/report")]
    public async Task<IActionResult> ReviewRunReport(Guid id, [FromQuery] string? projectKey, CancellationToken ct) =>
        await history.ReviewRunReportAsync(id, projectKey, ct) is { } json ? Content(json, "application/json") : NotFound(new { message = "No such stored run." });

    /// <summary>Follow up a failed stage of a composite integration suite run.</summary>
    [HttpPost("findings/stage")]
    public async Task<ActionResult<TechnicalFinding>> TrackStage([FromBody] TrackStageFindingRequest request, CancellationToken ct) =>
        Result(await history.TrackStageAsync(request, "local-user", ct));

    [HttpGet("findings")]
    public async Task<ActionResult<IReadOnlyList<TechnicalFinding>>> Findings([FromQuery] string? projectKey, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "A project key is required." }) : Ok(await history.FindingsAsync(projectKey, ct));

    [HttpPost("findings")]
    public async Task<ActionResult<TechnicalFinding>> Track([FromBody] TrackTechnicalFindingRequest request, CancellationToken ct) =>
        Result(await history.TrackAsync(request, "local-user", ct));

    [HttpPost("findings/{id:guid}")]
    public async Task<ActionResult<TechnicalFinding>> Update(Guid id, [FromQuery] string projectKey, [FromBody] UpdateTechnicalFindingRequest request, CancellationToken ct) =>
        Result(await history.UpdateAsync(projectKey ?? "", id, request, "local-user", ct));

    private ActionResult<T> Result<T>(TechnicalHistoryService.Result<T> r) => r switch
    {
        { Value: { } v } => Ok(v),
        { Status: 404 } => NotFound(new { message = r.Error }),
        { Status: 409 } => Conflict(new { message = r.Error }),
        { Status: 413 } => StatusCode(413, new { message = r.Error }),
        _ => BadRequest(new { message = r.Error }),
    };
}
