using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations.Messaging;
using BirkNext.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Message observations: import a Service Bus peek export or a Wolverine envelope export (redacted before it is stored), list, view with processing
/// state, timeline and correlation, delete. There is deliberately no endpoint that connects to a namespace or a database, and none that receives,
/// completes, abandons, dead-letters, resubmits, replays or purges a message.
/// </summary>
[ApiController]
[Route("api/message-observations")]
public sealed class MessageObservationsController(MessageObservationService observations, TimeProvider? clock = null) : ControllerBase
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    [HttpPost]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [RequestSizeLimit(MessagingObservationRules.MaxFileBytes * 2 + 64 * 1024)]
    public async Task<ActionResult<MessageObservationSummary>> Import([FromBody] MessageObservationImport request, CancellationToken ct)
    {
        var (set, error) = await observations.ImportAsync(request, Initiator, Now, ct);
        if (set is null) return UnprocessableEntity(new { message = error });
        return Ok(new MessageObservationSummary(set.Id, set.Name, set.Source, set.BodyPolicy, set.ImportedAt, set.ImportedBy, set.ObservedAt, set.Messages.Count,
            set.Messages.Count(m => m.Entity.SubQueue == MessageSubQueue.DeadLetter), set.Envelopes.Count, set.RedactedFields));
    }

    [HttpGet]
    public async Task<ActionResult<List<MessageObservationSummary>>> List([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await observations.ListAsync(environmentId, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MessageObservationView>> Get([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await observations.ViewAsync(environmentId, id, ct) is { } view ? Ok(view) : NotFound();

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await observations.DeleteAsync(environmentId, id, ct) ? NoContent() : NotFound();
}
