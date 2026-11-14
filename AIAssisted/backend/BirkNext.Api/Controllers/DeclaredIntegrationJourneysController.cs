using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Project-declared Integration Journeys (the Integration Journeys owner): a name and the ordered component chain, per project. Design metadata
/// only — no endpoint runs, binds, confirms or associates anything. Test Coverage reads them to propose association candidates; the Scope Registry
/// lists them as Journey members ("project.{projectKey}:{journeyId}").
/// </summary>
[ApiController]
[Route("api/integration-journeys/declared")]
public sealed class DeclaredIntegrationJourneysController(DeclaredIntegrationJourneyStore store, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DeclaredIntegrationJourney>> List([FromQuery] string? projectKey) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "A project key is required." }) : Ok(store.ForProject(projectKey));

    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public ActionResult<DeclaredIntegrationJourney> Declare([FromBody] DeclaredIntegrationJourneyRequest request)
    {
        var (journey, existing, error) = store.Declare(request, clock.GetUtcNow());
        return journey is not null ? Ok(journey) : existing is not null ? Conflict(new { message = error, existingId = existing.Id }) : BadRequest(new { message = error });
    }

    [HttpDelete("{projectKey}/{journeyId}")]
    public IActionResult Remove(string projectKey, string journeyId) => store.Remove(projectKey, journeyId) ? NoContent() : NotFound();
}
