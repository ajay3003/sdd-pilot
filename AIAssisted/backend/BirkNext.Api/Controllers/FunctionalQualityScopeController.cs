using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Functional Quality Scope (Delivery Management): available scopes, summary-first resolution, lazy item detail, saved scopes holding
/// references only, test-design assessments and frozen scope snapshots. Reads and writes the delivery_reporting schema (plus read-only
/// canonical relations); never calls an external system.
/// </summary>
[ApiController]
[Route("api/delivery-reporting/projects/{projectId:guid}/scope")]
public sealed class FunctionalQualityScopeController(FunctionalQualityScopeService scopes) : ControllerBase
{
    [HttpGet("available")]
    public async Task<ActionResult<IReadOnlyList<AvailableScope>>> Available(Guid projectId, CancellationToken ct) => Ok(await scopes.AvailableAsync(projectId, ct));

    [HttpPost("resolve")]
    public Task<IActionResult> Resolve(Guid projectId, ScopeQuery query, CancellationToken ct) => Guarded(async () => Ok(await scopes.ResolveAsync(projectId, query, ct)));

    /// <summary>Top-level items of the newest snapshot matching a search, bounded (the custom-scope builder's available list).</summary>
    [HttpGet("items")]
    public async Task<ActionResult<ScopeResolution>> Items(Guid projectId, [FromQuery] bool includeTechnical, [FromQuery] string? search, [FromQuery] int take = 60,
        CancellationToken ct = default) =>
        Ok(await scopes.CandidatesAsync(projectId, includeTechnical, search, take, ct));

    [HttpGet("items/detail")]
    public async Task<IActionResult> Detail(Guid projectId, [FromQuery] string key, CancellationToken ct) =>
        await scopes.DetailAsync(projectId, key, ct) is { } detail ? Ok(detail) : NotFound();

    [HttpPost("scopes")]
    public Task<IActionResult> Create(Guid projectId, ScopeDefinition definition, CancellationToken ct) =>
        Guarded(async () => Ok(await scopes.SaveAsync(projectId, definition with { Id = null }, ct)));

    [HttpPut("scopes/{scopeId:guid}")]
    public Task<IActionResult> Update(Guid projectId, Guid scopeId, ScopeDefinition definition, CancellationToken ct) =>
        Guarded(async () => Ok(await scopes.SaveAsync(projectId, definition with { Id = scopeId }, ct)));

    [HttpPost("scopes/{scopeId:guid}/clone")]
    public Task<IActionResult> Clone(Guid projectId, Guid scopeId, CancellationToken ct) => Guarded(async () => Ok(await scopes.CloneAsync(projectId, scopeId, ct)));

    [HttpDelete("scopes/{scopeId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid scopeId, CancellationToken ct) => await scopes.DeleteAsync(projectId, scopeId, ct) ? NoContent() : NotFound();

    [HttpPost("scopes/{scopeId:guid}/references")]
    public Task<IActionResult> Reference(Guid projectId, Guid scopeId, ScopeReferenceRequest request, CancellationToken ct) =>
        Guarded(async () => Ok(await scopes.ReferenceAsync(projectId, scopeId, request, ct)));

    [HttpDelete("scopes/{scopeId:guid}/references")]
    public Task<IActionResult> RemoveReference(Guid projectId, Guid scopeId, [FromQuery] string key, CancellationToken ct) =>
        Guarded(async () => Ok(await scopes.RemoveReferenceAsync(projectId, scopeId, key, ct)));

    [HttpPut("assessments")]
    public async Task<ActionResult<TestDesignAssessmentView>> Assess(Guid projectId, TestDesignAssessmentView assessment, CancellationToken ct) =>
        Ok(await scopes.AssessAsync(projectId, assessment, ct));

    [HttpPost("snapshots")]
    public Task<IActionResult> Freeze(Guid projectId, ScopeQuery query, CancellationToken ct) => Guarded(async () => Ok(await scopes.FreezeAsync(projectId, query, ct)));

    [HttpGet("snapshots")]
    public async Task<ActionResult<IReadOnlyList<ScopeSnapshotView>>> Snapshots(Guid projectId, CancellationToken ct) => Ok(await scopes.SnapshotsAsync(projectId, ct));

    private async Task<IActionResult> Guarded(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (FunctionalQualityScopeException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
