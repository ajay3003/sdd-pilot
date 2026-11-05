using BirkNext.Api.Services.Snapshots;
using BirkNext.Snapshots;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Shared snapshot registry: metadata-only listing, lineage, current/latest pointers, retention and the deletion check. Domain payloads stay behind
/// their own endpoints; nothing here loads or returns a payload.
/// </summary>
[ApiController]
[Route("api/snapshots")]
public sealed class SnapshotsController(SnapshotReferenceIndex index, SnapshotRegistryService registry) : ControllerBase
{
    /// <summary>What references a Source Analysis snapshot, and which stores could not be checked.</summary>
    [HttpGet("source-analysis/{id:guid}/references")]
    public async Task<ActionResult<SnapshotReferenceReport>> SourceAnalysisReferences(Guid id, CancellationToken ct)
    {
        var report = await index.SourceAnalysisAsync(id, ct);
        return report.Exists ? report : NotFound(report);
    }

    /// <summary>Metadata-only listing (filters, paging, sorting, pointers held). Never deserializes a payload.</summary>
    [HttpGet]
    public Task<SnapshotListPage> List([FromQuery] string? project, [FromQuery] SnapshotType? type, [FromQuery] string? module, [FromQuery] SnapshotProcessingStatus? status,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? sort, [FromQuery] int skip = 0, [FromQuery] int take = 25,
        [FromQuery] string? search = null, CancellationToken ct = default) =>
        registry.ListAsync(project, type, module, status, from, to, sort, skip, take, ct, search);

    /// <summary>Registers what is new in every owner store (idempotent; also runs automatically, throttled).</summary>
    [HttpPost("sync")]
    public Task<SnapshotSyncResult> Sync(CancellationToken ct) => registry.SyncAsync(true, ct);

    [HttpGet("{type}/{id}")]
    public async Task<ActionResult<SnapshotMetadata>> Get(SnapshotType type, string id, CancellationToken ct) =>
        await registry.GetAsync(type, id, ct) is { } metadata ? metadata : NotFound();

    [HttpGet("{type}/{id}/lineage")]
    public async Task<ActionResult<SnapshotLineageView>> Lineage(SnapshotType type, string id, [FromQuery] int depth = 3, CancellationToken ct = default) =>
        await registry.LineageAsync(type, id, depth, ct) is { } lineage ? lineage : NotFound();

    [HttpGet("pointers")]
    public Task<IReadOnlyList<SnapshotPointerView>> Pointers([FromQuery] string? project, [FromQuery] SnapshotType type, [FromQuery] string module, CancellationToken ct) =>
        registry.PointersAsync(project, type, module, ct);

    /// <summary>Sets the current-selected or current-approved pointer explicitly (viewing a snapshot never moves a pointer).</summary>
    [HttpPut("pointers")]
    public async Task<IActionResult> SetPointer([FromBody] SnapshotPointerRequest request, CancellationToken ct) =>
        await registry.SetPointerAsync(request, User.Identity?.IsAuthenticated == true ? User.Identity.Name ?? "an authenticated user" : "the local tester", ct) is { } error
            ? Conflict(new { message = error }) : NoContent();

    [HttpPut("{type}/{id}/retention")]
    public async Task<IActionResult> SetRetention(SnapshotType type, string id, [FromBody] SnapshotRetentionRequest request, CancellationToken ct) =>
        await registry.SetRetentionAsync(type, id, request.Retention, ct) is { } error ? Conflict(new { message = error }) : NoContent();

    /// <summary>Whether the snapshot may be deleted (Protected/frozen/inbound references), plus the stores that cannot be checked. Deletes nothing.</summary>
    [HttpGet("{type}/{id}/deletion-check")]
    public async Task<ActionResult<SnapshotDeletionCheck>> DeletionCheck(SnapshotType type, string id, CancellationToken ct)
    {
        IReadOnlyList<string>? notChecked = type == SnapshotType.SourceAnalysis ? SnapshotReferenceIndex.UncheckedSourceAnalysisReferrers : null;
        return await registry.DeletionCheckAsync(type, id, notChecked, ct) is { } check ? check : NotFound();
    }
}
