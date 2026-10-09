using BirkNext.Api.Services;
using BirkNext.SourceImpact;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/impact-analysis/source-change")]
public sealed class SourceChangeImpactController(SourceChangeImpactService impact) : ControllerBase
{
    [HttpGet("snapshots")]
    public async Task<IActionResult> Snapshots([FromQuery] string? environmentId, CancellationToken ct)
    {
        return Ok(await impact.SnapshotListAsync(environmentId ?? string.Empty, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Analyze([FromBody] SourceChangeImpactRequest request, CancellationToken ct)
    {
        if (request.BaselineSnapshotId == Guid.Empty || request.TargetSnapshotId == Guid.Empty || request.BaselineSnapshotId == request.TargetSnapshotId)
            return BadRequest("Choose two different source snapshots.");
        var report = await impact.AnalyzeAsync(request, ct);
        return report is null ? NotFound("The selected snapshots are unavailable or do not belong to the same repository.") : Ok(report);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] string projectId, [FromQuery] string? projectImportId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return BadRequest("A project identity is required.");
        return Ok(await impact.ListHistoryAsync(projectId, projectImportId, ct));
    }

    [HttpGet("history/{runId:guid}")]
    public async Task<IActionResult> HistoryItem(Guid runId, CancellationToken ct)
    {
        var report = await impact.GetHistoryAsync(runId, ct);
        return report is null ? NotFound() : Ok(report);
    }
}
