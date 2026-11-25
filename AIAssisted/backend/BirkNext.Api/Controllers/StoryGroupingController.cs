using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Manual Testing → user story grouping and US → FR review decisions (2026-11-24). Reading is open like the rest of Manual Testing; changing needs
/// the same permission as saving a test plan (there is no separate test-lead role in BirkNext).
/// </summary>
[ApiController]
[Route("api/test-plan-suggestions/story-grouping")]
public sealed class StoryGroupingController(StoryGroupingService service) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    [HttpGet]
    public Task<ActionResult> Get([FromQuery] string projectKey, [FromQuery] string specificationPath, CancellationToken ct) =>
        Run(async () => Ok(await service.GetAsync(projectKey, specificationPath, ct)));

    [HttpPost]
    [Authorize(Policy = BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Apply([FromQuery] string projectKey, [FromQuery] string specificationPath, [FromBody] StoryGroupingChangeRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.ApplyAsync(projectKey, specificationPath, request, Actor, ct)));

    private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (StoryGroupingService.GroupingException e) { return StatusCode(e.Status, new { code = e.Code, message = e.Message, error = e.Message, currentRevision = e.CurrentRevision }); }
    }
}
