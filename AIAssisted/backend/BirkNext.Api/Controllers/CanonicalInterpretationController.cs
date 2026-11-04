using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Interpretation;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Canonical interpretation and correlation, scoped to one project (<c>{scope}</c>, the Target Environment profile id). Interpretation reads stored
/// Source Analysis / Delivery snapshots only; review decisions are the shared registry every consumer reads. Listings are paged.
/// </summary>
[ApiController]
[Route("api/canonical/{scope}")]
public sealed class CanonicalInterpretationController(CanonicalInterpretationService service) : ControllerBase
{
    [HttpPost("interpret")]
    public Task<ActionResult<InterpretationSnapshotView>> Interpret(string scope, [FromBody] InterpretRequest request, CancellationToken ct) =>
        Run(() => service.InterpretAsync(scope, request, ct));

    /// <summary>Summary-first counts (latest interpretation): what Dashboard and consumers show before any listing is loaded.</summary>
    [HttpGet("summary")]
    public Task<ActionResult<CanonicalSummaryView>> Summary(string scope, CancellationToken ct) => Run(() => service.SummaryAsync(scope, ct));

    [HttpGet("snapshots")]
    public Task<ActionResult<IReadOnlyList<InterpretationSnapshotView>>> Snapshots(string scope, [FromQuery] int take = 20, CancellationToken ct = default) =>
        Run(() => service.SnapshotsAsync(scope, take, ct));

    [HttpGet("snapshots/{id:guid}/relations")]
    public Task<ActionResult<CanonicalPage<CanonicalRelationView>>> SnapshotRelations(string scope, Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        RunOrNotFound(() => service.SnapshotRelationsAsync(scope, id, skip, take, ct));

    [HttpGet("snapshots/compare")]
    public Task<ActionResult<InterpretationComparison>> Compare(string scope, [FromQuery] Guid from, [FromQuery] Guid to, CancellationToken ct) =>
        RunOrNotFound(() => service.CompareAsync(scope, from, to, ct));

    [HttpGet("entities")]
    public Task<ActionResult<CanonicalPage<CanonicalEntityView>>> Entities(string scope, [FromQuery] CanonicalEntityType? type, [FromQuery] string? search,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Run(() => service.EntitiesAsync(scope, type, search, skip, take, ct));

    [HttpGet("entities/{id:guid}")]
    public Task<ActionResult<CanonicalEntityDetail>> Entity(string scope, Guid id, CancellationToken ct) => RunOrNotFound(() => service.EntityAsync(scope, id, ct));

    [HttpGet("entities/{id:guid}/chain")]
    public Task<ActionResult<CorrelationChainView>> Chain(string scope, Guid id, [FromQuery] CanonicalEntityType[]? path, CancellationToken ct) =>
        RunOrNotFound(() => service.ChainAsync(scope, id, path, ct));

    [HttpGet("relations")]
    public Task<ActionResult<CanonicalPage<CanonicalRelationView>>> Relations(string scope, [FromQuery] RelationResolution? resolution, [FromQuery] RelationReviewState? review,
        [FromQuery] string? rule, [FromQuery] bool needsReview = false, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Run(() => service.RelationsAsync(scope, resolution, review, rule, needsReview, skip, take, ct));

    [HttpGet("aliases/resolve")]
    public Task<ActionResult<AliasResolution>> ResolveAlias(string scope, [FromQuery] string name, CancellationToken ct) =>
        Run(() => service.ResolveAliasAsync(scope, name ?? "", ct));

    [HttpGet("operations")]
    public Task<ActionResult<IReadOnlyList<CanonicalEntityDetail>>> Operations(string scope, [FromQuery] string method, [FromQuery] string path, CancellationToken ct) =>
        Run(() => service.OperationsAsync(scope, method ?? "GET", path ?? "/", ct));

    [HttpGet("decisions")]
    public Task<ActionResult<IReadOnlyList<ReviewDecisionView>>> Decisions(string scope, [FromQuery] string? target, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Run(() => service.DecisionsAsync(scope, target, take, ct));

    /// <summary>Records a decision. The reviewer is the authenticated user when there is one; otherwise the name given (local single-user mode).</summary>
    [HttpPost("decisions")]
    public Task<ActionResult<ReviewDecisionView>> Decide(string scope, [FromBody] ReviewDecisionRequest request, CancellationToken ct) =>
        Run(() => service.DecideAsync(scope, request, User.Identity?.IsAuthenticated == true ? User.Identity.Name : null, ct));

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (CanonicalRequestException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task<ActionResult<T>> RunOrNotFound<T>(Func<Task<T?>> action) where T : class
    {
        try { return await action() is { } value ? value : NotFound(); }
        catch (CanonicalRequestException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
