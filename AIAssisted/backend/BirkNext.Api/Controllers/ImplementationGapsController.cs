using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ImplementationGaps;
using BirkNext.ImplementationGaps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Implementation-gap evaluations: created by Project Import (one per import with SDD documents), listed, read, re-evaluated with recorded test
/// executions, and reviewed. No endpoint changes a source snapshot or a document; review decisions are append-only.
/// </summary>
[ApiController]
[Route("api/implementation-gaps")]
public sealed class ImplementationGapsController(ImplementationGapService gaps) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GapEvaluationSummary>>> List([FromQuery] string? scopeKey, CancellationToken ct) => Ok(await gaps.ListAsync(scopeKey, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GapEvaluation>> Get(Guid id, CancellationToken ct) => await gaps.GetAsync(id, ct) is { } e ? Ok(e) : NotFound();

    [HttpPost("{id:guid}/reevaluate")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<GapEvaluation>> Reevaluate(Guid id, [FromBody] GapReevaluateRequest request, CancellationToken ct)
    {
        if (request.Executions.Count > 50_000) return BadRequest(new { error = "At most 50,000 executions per request." });
        return await gaps.ReevaluateAsync(id, request, ct) is { } e ? Ok(e) : NotFound();
    }

    [HttpPost("{id:guid}/reviews")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Review(Guid id, [FromBody] GapReviewRequest request, CancellationToken ct)
    {
        var reviewer = User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";
        var (decision, error) = await gaps.ReviewAsync(id, request, reviewer, ct);
        return error is null ? Ok(decision) : error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? NotFound(new { error }) : BadRequest(new { error });
    }
}
