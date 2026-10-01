using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Application messaging (Wolverine) evidence for a Target Environment: build it from Source Analysis snapshots (no upload here — Source
/// Analysis owns source ingestion), read the extracted evidence, bind an analyzed application to an Integrations consumer.
/// </summary>
[ApiController]
[Route("api/integrations/application-messaging")]
public sealed class ApplicationMessagingController(IApplicationMessagingStore store) : ControllerBase
{
    public sealed record BindingRequest(string? Consumer);

    [HttpGet]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> Get([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await store.GetAsync(environmentId, ct) is { } set ? Ok(set) : NoContent();
    }

    /// <summary>Source Analysis snapshots for application messaging (read-only).</summary>
    [HttpGet("source-scope")]
    public async Task<ActionResult<ReviewSourceOptions>> SourceScope([FromQuery] string environmentId, [FromQuery] Guid? primary, [FromQuery] Guid[]? related, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.")
            : Ok(await store.SourceScopeAsync(environmentId, primary is { } p ? new ReviewSourceScopeRequest { PrimarySnapshotId = p, RelatedSnapshotIds = [.. related ?? []] } : null, ct));

    /// <summary>Builds the evidence set from exactly the chosen Source Analysis snapshots.</summary>
    [HttpPost("source-scope")]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> UseSourceScope([FromQuery] string environmentId, [FromBody] ReviewSourceScopeRequest scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        var (set, error) = await store.UseSourceScopeAsync(environmentId, scope, ct);
        return error is not null ? BadRequest(error) : Ok(set);
    }

    [HttpPut("{applicationId}/binding")]
    public async Task<ActionResult<ApplicationMessagingEvidenceSet>> Bind([FromQuery] string environmentId, string applicationId, [FromBody] BindingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest("environmentId is required.");
        return await store.BindAsync(environmentId, applicationId, request.Consumer, ct) is { } set ? Ok(set) : NotFound();
    }

    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : await store.DeleteAsync(environmentId, ct) ? NoContent() : NotFound();
}
