using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.CriticalE2E.Suggestions;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.CriticalE2E;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Critical E2E Regression. Same loopback-peer / loopback-host / configured-frontend-Origin gate as the other
/// workstation-local capabilities, because running a flow drives an authenticated browser session and reaches a target
/// environment's APIs — neither is something a page on the internet gets to ask for.
/// </summary>
[ApiController]
[Route("api/critical-e2e")]
[ServiceFilter(typeof(ManagedEdgeLocalCallerFilter))]
public sealed class CriticalE2EController(ICriticalE2EService service) : ControllerBase
{
    [HttpPost("overview")]
    public IActionResult Overview(CriticalE2EOverviewRequest request) => Run(() => Ok(service.Overview(request)));

    [HttpGet("flows")]
    public IActionResult Flows([FromQuery] string environmentId) => Run(() => Ok(service.Flows(environmentId ?? "")));

    [HttpPost("flows")]
    public IActionResult SaveFlow(CriticalE2EFlowDefinition flow) => Run(() => Ok(service.SaveFlow(flow)));

    /// <summary>Registers a delivery module for release coverage (explicit; a flow's module label never does this).</summary>
    [HttpPost("modules/register")]
    public IActionResult RegisterModule(CriticalE2EModuleRequest request) => Run(() => Ok(service.RegisterModule(request)));

    [HttpPost("modules/unregister")]
    public IActionResult UnregisterModule(CriticalE2EModuleRequest request) => Run(() => Ok(service.UnregisterModule(request)));

    /// <summary>The flow's immutable definition versions, newest first. Read-only.</summary>
    [HttpGet("flows/{flowId}/versions")]
    public IActionResult FlowVersions(string flowId, [FromServices] ICriticalE2EStore store) => Run(() => Ok(store.FlowVersions(flowId)));

    [HttpDelete("flows/{flowId}")]
    public IActionResult DeleteFlow(string flowId) => Run(() => service.DeleteFlow(flowId) ? Ok(new { deleted = true }) : NotFound());

    /// <summary>What archiving or clearing one flow's history would touch. Read-only.</summary>
    [HttpPost("history/preview")]
    public IActionResult HistoryPreview(CriticalE2EHistoryPreviewRequest request) => Run(() => Ok(service.HistoryPreview(request)));

    /// <summary>Hides old runs of one flow from the default history. Reversible; never changes a result or release evidence.</summary>
    [HttpPost("history/archive")]
    public IActionResult ArchiveRuns(CriticalE2EArchiveRequest request) => Run(() => Ok(service.ArchiveRuns(request)));

    [HttpPost("history/restore")]
    public IActionResult RestoreRuns(CriticalE2ERestoreRequest request) => Run(() => Ok(service.RestoreRuns(request)));

    /// <summary>Permanently removes one flow's runs. Needs the typed confirmation; refused while the flow holds release evidence.</summary>
    [HttpPost("history/clear")]
    public IActionResult ClearHistory(CriticalE2EClearRequest request) => Run(() => Ok(service.ClearHistory(request)));

    /// <summary>Runs one flow, or every enabled flow of one mode. Returns the runs and the refreshed overview together.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await service.RunAsync(request, cancellationToken)); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status499ClientClosedRequest, new { message = "Run cancelled." }); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the Critical E2E safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }

    /// <summary>Authoring: the tester picks one element on the live page; returns its identity and ranked selectors.</summary>
    [HttpPost("pick-element")]
    public async Task<IActionResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await service.PickElementAsync(request, cancellationToken)); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status499ClientClosedRequest, new { message = "Element picking cancelled." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }

    // ── Suggested tests: generation is offline; verification asks the paired Edge Companion (identity only); approval saves a Diagnostic flow. ──

    [HttpGet("suggestions/snapshots")]
    public async Task<IActionResult> SuggestionSnapshotsAsync([FromServices] CriticalE2ESuggestionService suggestions, CancellationToken ct) =>
        await RunAsync(async () => Ok(await suggestions.SnapshotsAsync(ct)));

    [HttpPost("suggestions/generate")]
    public async Task<IActionResult> GenerateSuggestionsAsync([FromServices] CriticalE2ESuggestionService suggestions, SuggestionGenerateRequest request, CancellationToken ct) =>
        await RunAsync(async () => await suggestions.GenerateAsync(request, ct) is { } result ? Ok(result) : NotFound(new { message = "Source snapshot not found." }));

    [HttpPost("suggestions/verify")]
    public async Task<IActionResult> VerifySuggestionAsync([FromServices] CriticalE2ESuggestionService suggestions, SuggestionVerifyRequest request, CancellationToken ct) =>
        await RunAsync(async () => Ok(await suggestions.VerifyAsync(request, ct)));

    [HttpPost("suggestions/decide")]
    public async Task<IActionResult> DecideSuggestionAsync([FromServices] CriticalE2ESuggestionService suggestions, SuggestionDecisionRequest request, CancellationToken ct) =>
        await RunAsync(async () => await suggestions.DecideAsync(request, ct) is var (s, problem) && s is not null ? Ok(s) : BadRequest(new { message = problem }));

    [HttpPost("suggestions/manual-review")]
    public async Task<IActionResult> ManualReviewSuggestionAsync([FromServices] CriticalE2ESuggestionService suggestions, SuggestionManualReviewRequest request, CancellationToken ct) =>
        await RunAsync(async () => await suggestions.ManualReviewAsync(request, ct) is var (s, problem) && s is not null ? Ok(s) : BadRequest(new { message = problem }));

    [HttpPost("suggestions/approve")]
    public async Task<IActionResult> ApproveSuggestionAsync([FromServices] CriticalE2ESuggestionService suggestions, SuggestionApproveRequest request, CancellationToken ct) =>
        await RunAsync(async () => await suggestions.ApproveAsync(request, ct) is var r && r.Flow is not null ? Ok(r) : UnprocessableEntity(r));

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (OperationCanceledException) { return StatusCode(StatusCodes.Status499ClientClosedRequest, new { message = "Cancelled." }); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the Critical E2E safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }

    private IActionResult Run(Func<IActionResult> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return action(); }
        catch (ArgumentException) { return BadRequest(new { message = "Request rejected by the Critical E2E safety policy." }); }
        catch (Exception) { return Conflict(new { message = "Critical E2E operation unavailable." }); }
    }
}
