using BirkNext.Api.Services;
using BirkNext.SourceImpact;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/impact-analysis/runs")]
public sealed class ImpactAnalysisRunsController(ImpactAnalysisRunService runs) : ControllerBase
{
    [HttpGet("requirements")]
    public async Task<IActionResult> Requirements([FromQuery] string projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return BadRequest("A current project identity is required.");
        return Ok(await runs.RequirementsAsync(projectId, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Run([FromBody] ImpactAnalysisRunRequest request, CancellationToken ct)
    {
        try { return Ok(await runs.RunAsync(request, ct)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] string projectId, [FromQuery] string? projectImportId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return BadRequest("A project identity is required.");
        return Ok(await runs.HistoryAsync(projectId, projectImportId, ct));
    }

    [HttpGet("history/{runId:guid}")]
    public async Task<IActionResult> HistoryItem(Guid runId, CancellationToken ct)
    {
        var report = await runs.HistoryItemAsync(runId, ct);
        return report is null ? NotFound() : Ok(report);
    }
}
