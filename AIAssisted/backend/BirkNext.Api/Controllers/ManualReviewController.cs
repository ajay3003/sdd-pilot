using BirkNext.Api.Services.ManualReview;
using BirkNext.ManualReview;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Manual review decisions (append-only, with history). Behind the local session and CSRF guard like every other API; a decision is a person's
/// recorded judgement and never changes an automated result.
/// </summary>
[ApiController]
[Route("api/manual-reviews")]
public sealed class ManualReviewController(ManualReviewDecisionStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ManualReviewDecisionView>>> List([FromQuery] string module, [FromQuery] string scope, [FromQuery] string? subject, CancellationToken ct)
    {
        if (!ManualReviewModules.All.Contains(module ?? "")) return BadRequest("Unknown review module.");
        if (string.IsNullOrWhiteSpace(scope)) return BadRequest("scope is required.");
        return Ok(await store.ListAsync(module!, scope.Trim(), subject?.Trim(), ct));
    }

    [HttpPost]
    public async Task<ActionResult<ManualReviewDecisionView>> Record([FromBody] ManualReviewDecisionRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequest("A decision is required.");
        var (decision, error) = await store.RecordAsync(request, ct);
        return error is not null ? BadRequest(error) : Ok(decision);
    }
}
