using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ExecutionEvidence;
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
    [HttpPost("runs")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<ActionResult<ExecutionRunStartResult>> Start([FromServices] ExecutionRunService runs, [FromBody] ExecutionRunRequest request, CancellationToken ct)
    {
        var result = await runs.StartAsync(request, "Local session", ct);
        return result.RunId is null ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public IActionResult Cancel(Guid runId) => ExecutionRunService.Cancel(runId) ? Accepted() : NotFound(new { message = "The run is not running in this BirkNext instance." });

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ExecutionRunSummary>>> List([FromQuery] string? environmentId, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await store.ListAsync(environmentId, take, ct));

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

    [HttpGet("runs/{runId:guid}/export")]
    public async Task<IActionResult> Export(Guid runId, CancellationToken ct) =>
        await store.ExportAsync(runId, ct) is { } zip ? File(zip, "application/zip", $"execution-evidence-{runId:N}.zip") : NotFound();

    [HttpDelete("runs/{runId:guid}")]
    public async Task<IActionResult> Delete(Guid runId, CancellationToken ct)
    {
        var (deleted, error) = await store.DeleteAsync(runId, ct);
        return deleted ? NoContent() : error == "Not found." ? NotFound() : Conflict(new { message = error });
    }
}
