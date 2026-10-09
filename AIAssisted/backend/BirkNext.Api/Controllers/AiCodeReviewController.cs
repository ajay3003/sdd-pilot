using BirkNext.AiCodeReview;
using BirkNext.Api.Services.AiCodeReview;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// AI-Generated Code Review: deterministic risk review over Source Analysis snapshots (current, optionally against a baseline of the same
/// repository). Source comes only from Source Analysis — there is no upload here; no language model is called; authorship is never inferred.
/// </summary>
[ApiController]
[Route("api/ai-code-review")]
public sealed class AiCodeReviewController(IAiCodeReviewService service) : ControllerBase
{
    [HttpGet("sources")]
    public async Task<ActionResult<ReviewSourceOptions>> Sources(CancellationToken ct) => Ok(await service.SourcesAsync(ct));

    [HttpPost("runs")]
    public async Task<ActionResult<AiCodeReviewResult>> Run([FromBody] AiCodeReviewRequest request, CancellationToken ct)
    {
        var outcome = await service.RunAsync(request, ct);
        return outcome.Result is { } result ? Ok(result) : Problem(detail: outcome.Error, statusCode: outcome.Status);
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<AiCodeReviewRunSummary>>> History(CancellationToken ct) => Ok(await service.HistoryAsync(ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<AiCodeReviewResult>> Get(Guid runId, CancellationToken ct) =>
        await service.GetAsync(runId, ct) is { } result ? Ok(result) : NotFound();
}
