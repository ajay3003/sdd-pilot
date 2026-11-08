using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ExecutionEvidence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Execution Evidence: start a user-initiated run (gated by the API environment safety policy), and read saved runs, calls, attempts,
/// comparisons and redacted exports. Reading never executes anything; there is no replay endpoint (a new run is always a new request).
/// </summary>
[ApiController]
[Route("api/execution-evidence")]
public sealed class ExecutionEvidenceController(ExecutionEvidenceStore store) : ControllerBase
{
    /// <summary>Starting (or cancelling) a run sends requests to a target: it needs the execute permission (or the Development loopback grant).</summary>
    [HttpPost("runs")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<ExecutionRunStartResult>> Start([FromServices] ExecutionRunService runs, [FromBody] ExecutionRunRequest request, CancellationToken ct)
    {
        var result = await runs.StartAsync(request, "Local session", ct);
        return result.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    public IActionResult Cancel(Guid runId) => ExecutionRunService.Cancel(runId) ? Accepted() : NotFound(new { message = "The run is not running in this BirkNext instance." });

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ExecutionRunSummary>>> List([FromQuery] string? environmentId, [FromQuery] int take = 50, [FromQuery] ExecutionState? state = null,
        [FromQuery] string? search = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null, [FromQuery(Name = "protected")] bool? isProtected = null,
        CancellationToken ct = default) =>
        Ok(await store.ListAsync(new(environmentId, take, state, search, from, to, isProtected), ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ExecutionRunDetail>> Get(Guid runId, [FromQuery] int skip = 0, [FromQuery] int take = 50, [FromQuery] ExecutionState? state = null,
        [FromQuery] ExecutionProtocol? protocol = null, [FromQuery] string? method = null, [FromQuery] string? search = null, [FromQuery] string sort = "sequence", CancellationToken ct = default) =>
        await store.GetAsync(runId, new(skip, take, state, protocol, method, search, sort), ct) is { } detail ? Ok(detail) : NotFound();

    [HttpGet("runs/{runId:guid}/calls/{callId:guid}")]
    public async Task<ActionResult<ExecutionCallRecord>> Call(Guid runId, Guid callId, CancellationToken ct) =>
        await store.CallAsync(runId, callId, ct) is { } call ? Ok(call) : NotFound();

    [HttpGet("compare")]
    public async Task<ActionResult<AttemptComparison>> Compare([FromQuery] Guid runA, [FromQuery] Guid callA, [FromQuery] int attemptA, [FromQuery] Guid runB, [FromQuery] Guid callB,
        [FromQuery] int attemptB, CancellationToken ct) =>
        await store.CompareAsync(runA, callA, attemptA, runB, callB, attemptB, ct) is { } c ? Ok(c) : NotFound();

    /// <summary>Export of one run (built from stored, redacted evidence; never executes anything).</summary>
    [HttpGet("runs/{runId:guid}/export")]
    public async Task<IActionResult> Export([FromServices] ExecutionEvidenceManagement manage, Guid runId, CancellationToken ct)
    {
        var (zip, error) = await manage.ExportAsync([runId], Initiator, ct);
        return zip is not null ? File(zip, "application/zip", $"execution-evidence-{runId:N}.zip") : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });
    }

    /// <summary>Export of selected runs (at most 20 runs and 50 MB of stored evidence).</summary>
    [HttpPost("runs/export")]
    public async Task<IActionResult> ExportMany([FromServices] ExecutionEvidenceManagement manage, [FromBody] ExecutionExportRequest request, CancellationToken ct)
    {
        var (zip, error) = await manage.ExportAsync(request.RunIds, Initiator, ct);
        return zip is not null ? File(zip, "application/zip", $"execution-evidence-{request.RunIds.Count}-runs.zip") : error == "Not found." ? NotFound() : UnprocessableEntity(new { message = error });
    }

    /// <summary>Deletes one finished, unprotected run with its calls (the owner's confirmation is the request itself; protected runs are refused).</summary>
    [HttpDelete("runs/{runId:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Delete([FromServices] ExecutionEvidenceManagement manage, Guid runId, CancellationToken ct)
    {
        var result = await manage.DeleteAsync(new() { RunIds = [runId], Confirm = 1 }, Initiator, ct);
        return result.Deleted == 1 ? NoContent() : result.Skipped is [{ Reason: "Not found." }] ? NotFound() : Conflict(new { message = result.Skipped.FirstOrDefault()?.Reason ?? result.Message });
    }

    /// <summary>Bulk deletion: selected runs, or every eligible run of an environment; Confirm must equal the number shown.</summary>
    [HttpPost("runs/delete")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ExecutionDeletionResult>> DeleteMany([FromServices] ExecutionEvidenceManagement manage, [FromBody] ExecutionDeletionRequest request, CancellationToken ct) =>
        Ok(await manage.DeleteAsync(request, Initiator, ct));

    [HttpGet("deletion-preview")]
    public async Task<ActionResult<ExecutionRetentionPreview>> DeletionPreview([FromServices] ExecutionEvidenceManagement manage, [FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest() : Ok(await manage.EligibleAsync(environmentId, ct));

    [HttpPut("runs/{runId:guid}/protection")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> Protect([FromServices] ExecutionEvidenceManagement manage, Guid runId, [FromQuery(Name = "protected")] bool isProtected, CancellationToken ct) =>
        (await manage.ProtectAsync(runId, isProtected, Initiator, ct)).Done ? NoContent() : NotFound();

    /// <summary>Removes one call's stored request/response bodies; metadata, sizes, hashes, assertions and outputs stay.</summary>
    [HttpDelete("runs/{runId:guid}/calls/{callId:guid}/bodies")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> RemoveBodies([FromServices] ExecutionEvidenceManagement manage, Guid runId, Guid callId, CancellationToken ct)
    {
        var (removed, error) = await manage.RemoveBodiesAsync(runId, callId, Initiator, ct);
        return error is null ? Ok(new { removed }) : error == "Not found." ? NotFound() : Conflict(new { message = error });
    }

    [HttpGet("runs/{runId:guid}/integrity")]
    public async Task<ActionResult<ExecutionIntegrityReport>> Integrity([FromServices] ExecutionEvidenceManagement manage, Guid runId, CancellationToken ct) =>
        await manage.IntegrityAsync(runId, ct) is { } report ? Ok(report) : NotFound();

    [HttpGet("storage")]
    public async Task<ActionResult<ExecutionStorageUsage>> Storage([FromServices] ExecutionEvidenceManagement manage, [FromQuery] string? environmentId, CancellationToken ct) =>
        Ok(await manage.UsageAsync(environmentId, ct));

    [HttpGet("retention")]
    public async Task<ActionResult<ExecutionRetentionSettings>> Retention([FromServices] ExecutionEvidenceManagement manage, CancellationToken ct) => Ok(await manage.RetentionAsync(ct));

    [HttpPut("retention")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ExecutionRetentionSettings>> SaveRetention([FromServices] ExecutionEvidenceManagement manage, [FromBody] ExecutionRetentionSettings settings, CancellationToken ct)
    {
        var (saved, error) = await manage.SaveRetentionAsync(settings, Initiator, ct);
        return saved is null ? UnprocessableEntity(new { message = error }) : Ok(saved);
    }

    /// <summary>Dry run: which runs applying retention now would delete. Deletes nothing.</summary>
    [HttpGet("retention/preview")]
    public async Task<ActionResult<ExecutionRetentionPreview>> RetentionPreview([FromServices] ExecutionEvidenceManagement manage, [FromQuery] string? environmentId, CancellationToken ct) =>
        Ok(await manage.PreviewAsync(environmentId, ct));

    [HttpPost("retention/apply")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<ExecutionDeletionResult>> ApplyRetention([FromServices] ExecutionEvidenceManagement manage, [FromQuery] string? environmentId, [FromQuery] int confirm, CancellationToken ct) =>
        Ok(await manage.ApplyRetentionAsync(environmentId, confirm, Initiator, ct));

    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<ExecutionAuditEntry>>> Audit([FromServices] ExecutionEvidenceManagement manage, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await manage.AuditAsync(take, ct));

    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";
}
