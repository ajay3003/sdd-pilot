using BirkNext.Api.Services;
using BirkNext.SourceImpact;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/impact-analysis/source-change")]
public sealed class SourceChangeImpactController(SourceChangeImpactService impact) : ControllerBase
{
    [HttpGet("snapshots")]
    public async Task<IActionResult> Snapshots([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return Ok(await impact.SnapshotListAsync(environmentId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Analyze([FromBody] SourceChangeImpactRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return BadRequest("Environment is required.");
        if (request.BaselineSnapshotId == Guid.Empty || request.TargetSnapshotId == Guid.Empty || request.BaselineSnapshotId == request.TargetSnapshotId)
            return BadRequest("Choose two different source snapshots.");
        var report = await impact.AnalyzeAsync(request, ct);
        return report is null ? NotFound("The selected snapshots are unavailable or do not belong to the same repository.") : Ok(report);
    }
}
