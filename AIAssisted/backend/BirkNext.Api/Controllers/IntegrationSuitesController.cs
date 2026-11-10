using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.IntegrationSuites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Generated integration suites. Generating writes drafts to their existing owners (Contract Registry/Testing, Test Data) and a reference
/// manifest — never a network call. Executing is a separate, explicitly authorised action that needs the same permission as contract runs.
/// </summary>
[ApiController]
[Route("api/integration-suites")]
public sealed class IntegrationSuitesController(IntegrationSuiteService suites, BirkNext.Api.Services.IntegrationSuites.Composite.CompositeContextService contexts) : ControllerBase
{
    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    [HttpGet("scopes")]
    public ActionResult<IReadOnlyList<IntegrationSuiteScope>> Scopes() => Ok(IntegrationSuiteComposer.Scopes);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IntegrationSuiteSummary>>> List(CancellationToken ct) => Ok(await suites.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IntegrationSuite>> Get(Guid id, CancellationToken ct) => await suites.GetAsync(id, ct) is { } s ? Ok(s) : NotFound();

    [HttpPost]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<IntegrationSuiteGenerateResult>> Generate([FromBody] IntegrationSuiteGenerateRequest request, CancellationToken ct)
    {
        var result = await suites.GenerateAsync(request, Initiator, ct);
        return result.Error is null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/reviews")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Review(Guid id, [FromBody] IntegrationSuiteReviewRequest request, CancellationToken ct)
    {
        var (decision, error) = await suites.ReviewAsync(id, request, Initiator, ct);
        return error is null ? Ok(decision) : error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? NotFound(new { error }) : BadRequest(new { error });
    }

    // ── Multi-source (composite) ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Candidate source snapshots per stage of a scope (read-only; nothing is associated).</summary>
    [HttpGet("composite/discovery")]
    public async Task<ActionResult<CompositeDiscovery>> Discover([FromQuery] string scopeId, [FromQuery] Guid? contextId, CancellationToken ct) =>
        await contexts.DiscoverAsync(scopeId, contextId, ct) is { } d ? Ok(d) : BadRequest(new { error = "This scope has no stage source requirements." });

    [HttpGet("composite/contexts")]
    public async Task<ActionResult<IReadOnlyList<CompositeContextSummary>>> Contexts(CancellationToken ct) => Ok(await contexts.ListAsync(ct));

    [HttpGet("composite/contexts/revisions")]
    public async Task<ActionResult<IReadOnlyList<CompositeContextSummary>>> ContextRevisions([FromQuery] string key, CancellationToken ct) => Ok(await contexts.RevisionsAsync(key, ct));

    [HttpGet("composite/contexts/{id:guid}")]
    public async Task<ActionResult<CompositeSourceContext>> Context(Guid id, CancellationToken ct) => await contexts.GetAsync(id, ct) is { } c ? Ok(c) : NotFound();

    /// <summary>Saves the next revision of a context (associations, decisions, accepted transitions). Associating sources grants no access.</summary>
    [HttpPost("composite/contexts")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<CompositeContextSaveResult>> SaveContext([FromBody] CompositeContextSaveRequest request, CancellationToken ct)
    {
        var result = await contexts.SaveAsync(request, Initiator, ct);
        return result.Context is not null ? Ok(result) : result.Conflict ? Conflict(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/plan/approve")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<CompositePlan>> ApprovePlan(Guid id, CancellationToken ct)
    {
        var (plan, error) = await suites.ApprovePlanAsync(id, Initiator, ct);
        return plan is not null ? Ok(plan) : BadRequest(new { error });
    }

    /// <summary>What a composite run would do (kind, stages, data-changing operations, problems) — nothing is sent.</summary>
    [HttpPost("{id:guid}/composite-runs/preflight")]
    public async Task<ActionResult<CompositePreflight>> Preflight(Guid id, [FromBody] CompositeRunRequest request, CancellationToken ct) =>
        await suites.PreflightAsync(id, request, ct) is { } p ? Ok(p) : NotFound();

    [HttpPost("{id:guid}/composite-runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<CompositeRunResult>> RunComposite(Guid id, [FromBody] CompositeRunRequest request, CancellationToken ct)
    {
        var result = await suites.ExecuteCompositeAsync(id, request, Initiator, ct);
        return result.State switch { "NotFound" => NotFound(result), "NotAuthorized" or "Refused" => BadRequest(result), _ => Ok(result) };
    }

    [HttpGet("{id:guid}/composite-runs/{runId:guid}")]
    public async Task<ActionResult<CompositeRunView>> CompositeRun(Guid id, Guid runId, CancellationToken ct) =>
        await suites.CompositeRunAsync(id, runId, ct) is { } r ? Ok(r) : NotFound();

    [HttpPost("{id:guid}/runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public async Task<ActionResult<IntegrationSuiteExecuteResult>> Execute(Guid id, [FromBody] IntegrationSuiteExecuteRequest request, CancellationToken ct)
    {
        var result = await suites.ExecuteAsync(id, request, Initiator, ct);
        return result.State switch { "NotFound" => NotFound(result), "NotAuthorized" or "Refused" => BadRequest(result), _ => Ok(result) };
    }
}
