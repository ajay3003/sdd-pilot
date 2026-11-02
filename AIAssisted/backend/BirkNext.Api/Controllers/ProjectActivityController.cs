using BirkNext.Api.Services.ProjectActivity;
using BirkNext.ProjectActivity;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>Persistent project activity (Dashboard → Recent activity). Scoped by the logical project/application id; newest first.</summary>
[ApiController]
[Route("api/project-activity")]
public sealed class ProjectActivityController(ProjectActivityService activity) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectActivityItem>>> List([FromQuery] string projectContextId, [FromQuery] int take = 10, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(projectContextId) ? BadRequest(new { message = "projectContextId is required." }) : Ok(await activity.ListAsync(projectContextId, take, ct));

    /// <summary>Records one event; recording the same (project context, idempotency key) again returns the stored event.</summary>
    [HttpPost]
    public async Task<ActionResult<ProjectActivityItem>> Record([FromBody] ProjectActivityRecordRequest request, CancellationToken ct)
    {
        try { return Ok(await activity.RecordAsync(request, ct)); }
        catch (ProjectActivityRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
