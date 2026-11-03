using BirkNext.Api.Services.Snapshots;
using BirkNext.Snapshots;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Shared snapshot reference queries (metadata only; domain payloads stay behind their own endpoints).</summary>
[ApiController]
[Route("api/snapshots")]
public sealed class SnapshotsController(SnapshotReferenceIndex index) : ControllerBase
{
    /// <summary>What references a Source Analysis snapshot, and which stores could not be checked.</summary>
    [HttpGet("source-analysis/{id:guid}/references")]
    public async Task<ActionResult<SnapshotReferenceReport>> SourceAnalysisReferences(Guid id, CancellationToken ct)
    {
        var report = await index.SourceAnalysisAsync(id, ct);
        return report.Exists ? report : NotFound(report);
    }
}
