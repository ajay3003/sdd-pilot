using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Integration journeys (IQR → journey packs). Readiness, journey definitions, architecture rules and the journey catalog are configuration
/// metadata and stay readable (like the integration catalog and Active Event readiness) so the review explains what is missing. Starting a run,
/// run detail and history require an authenticated principal with ActiveEventExecute — the same execution permission as Active Event
/// Testing, not a second one. No endpoint accepts an endpoint URL, namespace, topic, payload, credential or environment type.
/// </summary>
[ApiController]
[Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
[Route("api/integration-journeys")]
public sealed class IntegrationJourneysController(IIntegrationJourneyService journeys) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("packs")]
    public async Task<ActionResult<IReadOnlyList<IntegrationJourneyPackView>>> Packs([FromQuery] string environmentId, CancellationToken ct)
    {
        try { return Ok(await journeys.PacksAsync(environmentId, ct)); }
        catch (IntegrationJourneyRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpGet("packs/{packId}/architecture-rules")]
    public async Task<ActionResult<ArchitectureRuleReport>> Rules(string packId, [FromQuery] string environmentId, [FromQuery] Guid? snapshotId, CancellationToken ct)
    {
        try { return Ok(await journeys.RulesAsync(environmentId ?? "", packId, snapshotId, ct)); }
        catch (IntegrationJourneyRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Journey ids and design-level edges for Test Coverage, Traceability and Impact Analysis (suggested relations, never confirmed).</summary>
    [AllowAnonymous]
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<IntegrationJourneyCatalogEntry>> Catalog() => Ok(journeys.Catalog());

    [HttpPost("runs")]
    public async Task<ActionResult<IntegrationJourneyRun>> Start([FromBody] IntegrationJourneyRunRequest request, CancellationToken ct)
    {
        try { return Ok(await journeys.StartAsync(request, ct)); }
        catch (IntegrationJourneyRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<IntegrationJourneyRunSummary>>> History([FromQuery] string environmentId, [FromQuery] string? packId, [FromQuery] string? journeyId,
        [FromQuery] string? scenarioId, [FromQuery] JourneyRunState? state, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." })
            : Ok(await journeys.HistoryAsync(new IntegrationJourneyHistoryQuery(environmentId, packId, journeyId, scenarioId, state, from, to), ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<IntegrationJourneyRun>> Get(Guid runId, CancellationToken ct) =>
        await journeys.GetAsync(runId, ct) is { } run ? Ok(run) : NotFound();
}
