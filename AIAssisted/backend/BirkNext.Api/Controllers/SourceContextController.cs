using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.SourceBaselines;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// The shared source context (logical sources, their snapshots, the analysis baseline per source and the snapshot every module uses) and the
/// decisions that change it. Reading is open to the local session; choosing a baseline or declaring a branch label is a configuration decision
/// (backend policy SystemConfigurationWrite) and is recorded append-only with a derived actor.
/// </summary>
[ApiController]
[Route("api/source-context")]
public sealed class SourceContextController(SourceBaselineService baselines, LocalTrustedExecution local) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SourceContextView>> Get(CancellationToken ct) => Ok(await baselines.ContextAsync(ct));

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<SourceDecisionView>>> History([FromQuery] string repositoryKey, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(repositoryKey) ? BadRequest(new { message = "repositoryKey is required." }) : Ok(await baselines.HistoryAsync(repositoryKey, ct));

    [HttpPost("baseline")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<SourceDecisionResult>> SelectBaseline([FromBody] SourceBaselineRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryKey)) return BadRequest(new SourceDecisionResult(false, "repositoryKey is required.", null));
        var result = await baselines.SelectBaselineAsync(request.RepositoryKey, request.SnapshotId, Actor(), request.Reason, ct);
        return result.Decision is null && !result.Changed && !result.Message.StartsWith("Unchanged", StringComparison.Ordinal) ? BadRequest(result) : Ok(result);
    }

    [HttpPost("declared-branch")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<SourceDecisionResult>> DeclareBranch([FromBody] DeclaredBranchRequest request, CancellationToken ct)
    {
        var result = await baselines.DeclareBranchAsync(request.SnapshotId, request.Branch, Actor(), ct);
        return result.Decision is null && !result.Changed && result.Message != "Unchanged." ? BadRequest(result) : Ok(result);
    }

    private string Actor() => SystemConfigurationPermissions.Actor(User,
        local.EvaluateFor(SystemConfigurationPermissions.LocalSetting, SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed) ?? "not attributed";
}
