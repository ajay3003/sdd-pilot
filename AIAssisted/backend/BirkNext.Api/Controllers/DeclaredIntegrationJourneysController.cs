using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Project-declared Integration Journeys (the Integration Journeys owner): a name and the ordered component chain, per project, stored in the
/// database with append-only revisions. Design metadata only — no endpoint runs, binds, confirms or associates anything. Test Coverage reads them
/// to propose association candidates; the Scope Registry lists them as Journey members ("project.{projectKey}:{journeyId}").
/// </summary>
[ApiController]
[Route("api/integration-journeys/declared")]
public sealed class DeclaredIntegrationJourneysController(DeclaredIntegrationJourneyStore store, DeclaredIntegrationJourneyMigrationReports reports, TimeProvider clock) : ControllerBase
{
    /// <summary>The current (not retired) declared journeys of one project.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeclaredIntegrationJourney>>> List([FromQuery] string? projectKey, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "A project key is required." }) : Ok(await store.ForProjectAsync(projectKey, ct));

    /// <summary>The report of the legacy JSON file import (run at start, idempotent); with a project key only that project's entries are listed.</summary>
    [HttpGet("migration-report")]
    public ActionResult<DeclaredIntegrationJourneyMigrationReport> MigrationReport([FromQuery] string? projectKey)
    {
        var report = reports.Latest;
        if (string.IsNullOrWhiteSpace(projectKey)) return Ok(report);
        var key = projectKey.Trim();
        return Ok(report with { Items = [.. report.Items.Where(i => string.Equals(i.ProjectKey, key, StringComparison.Ordinal))] });
    }

    /// <summary>One journey of the project with every revision, newest first (also when retired).</summary>
    [HttpGet("{projectKey}/{journeyId}/revisions")]
    public async Task<ActionResult<DeclaredIntegrationJourneyHistory>> Revisions(string projectKey, string journeyId, CancellationToken ct) =>
        await store.HistoryAsync(projectKey, journeyId, ct) is { } history ? Ok(history) : NotFound(new { message = "This project declares no journey with that id." });

    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<DeclaredIntegrationJourney>> Declare([FromBody] DeclaredIntegrationJourneyRequest request, CancellationToken ct) =>
        Result(await store.DeclareAsync(request, clock.GetUtcNow(), ct));

    /// <summary>Edits a journey: appends the next revision (earlier revisions never change; the id stays).</summary>
    [HttpPut("{projectKey}/{journeyId}")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<DeclaredIntegrationJourney>> Revise(string projectKey, string journeyId, [FromBody] DeclaredIntegrationJourneyRequest request, CancellationToken ct) =>
        Result(await store.ReviseAsync(projectKey, journeyId, request, clock.GetUtcNow(), ct));

    /// <summary>Removes (retires) a journey: no longer listed or proposed; its revision history is kept.</summary>
    [HttpDelete("{projectKey}/{journeyId}")]
    public async Task<IActionResult> Remove(string projectKey, string journeyId, [FromQuery] string? retiredBy, CancellationToken ct) =>
        await store.RetireAsync(projectKey, journeyId, retiredBy, clock.GetUtcNow(), ct) ? NoContent() : NotFound();

    private ActionResult<DeclaredIntegrationJourney> Result(DeclaredJourneyWrite write) => write switch
    {
        { Journey: { } journey } => Ok(journey),
        { Status: 404 } => NotFound(new { message = write.Error }),
        { Status: 409 } => Conflict(new { message = write.Error, existingId = write.ExistingId }),
        _ => BadRequest(new { message = write.Error }),
    };
}
