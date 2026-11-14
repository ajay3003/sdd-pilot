using BirkNext.Api.Services.TaskAlignment;
using BirkNext.TaskAlignment;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Persisted Task Alignment runs. Saving a completed analysis is open to the local session (like drafting a scope): it records an analysis
/// result, not a configuration decision or a review. Every read is scoped by projectKey — a run of another project is never returned.
/// Task Alignment is traceability between tasks and the specification, not implementation evidence.
/// </summary>
[ApiController]
[Route("api/task-alignment/runs")]
public sealed class TaskAlignmentRunsController(TaskAlignmentRunService runs) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<TaskAlignmentRunSummary>> Save([FromBody] SaveTaskAlignmentRunRequest request, CancellationToken ct)
    {
        var (run, error) = await runs.SaveAsync(request, ct);
        return run is null ? BadRequest(new { error }) : Ok(run);
    }

    /// <summary>The newest run for exactly this project and Tasks document path (204 when there is none).</summary>
    [HttpGet("latest")]
    public async Task<ActionResult<TaskAlignmentRun>> Latest([FromQuery] string projectKey, [FromQuery] string tasksPath, [FromQuery] string? projectName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(tasksPath)) return BadRequest(new { error = "projectKey and tasksPath are required." });
        return await runs.LatestAsync(projectKey, tasksPath, projectName, ct) is { } run ? Ok(run) : NoContent();
    }

    /// <summary>Run history of a project (newest first), optionally for one Tasks document path.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskAlignmentRunSummary>>> List([FromQuery] string projectKey, [FromQuery] string? tasksPath,
        [FromQuery] int limit = 20, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { error = "projectKey is required." }) : Ok(await runs.ListAsync(projectKey, tasksPath, limit, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaskAlignmentRun>> Get(Guid id, [FromQuery] string projectKey, CancellationToken ct) =>
        await runs.GetAsync(projectKey, id, ct) is { } run ? Ok(run) : NotFound();
}
