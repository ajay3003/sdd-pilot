using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Scopes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// The shared Scope Registry: project-scoped scopes and their immutable revisions. Reading and drafting are open to the local session;
/// approving and archiving are configuration decisions (backend policy SystemConfigurationWrite) recorded with a derived actor. Selecting
/// scopes is a filter for other modules, never an execution authorization. Every route is scoped by projectKey — no cross-project reads.
/// </summary>
[ApiController]
[Route("api/scopes")]
public sealed class ScopeRegistryController(ScopeRegistryService scopes, LocalTrustedExecution local) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScopeSummary>>> List([FromQuery] string projectKey, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "projectKey is required." }) : Ok(await scopes.ListAsync(projectKey, ct));

    [HttpGet("{scopeId:guid}")]
    public async Task<ActionResult<ScopeDetail>> Get(Guid scopeId, [FromQuery] string projectKey, CancellationToken ct) =>
        await scopes.GetAsync(projectKey, scopeId, ct) is { } detail ? Ok(detail) : NotFound();

    [HttpPost]
    public async Task<ActionResult<ScopeDetail>> Create([FromBody] CreateScopeRequest request, CancellationToken ct) =>
        Result(await scopes.CreateAsync(request, Actor(), ct));

    [HttpPut("{scopeId:guid}")]
    public async Task<ActionResult<ScopeDetail>> Save(Guid scopeId, [FromQuery] string projectKey, [FromBody] SaveScopeDraftRequest request, CancellationToken ct) =>
        Result(await scopes.SaveDraftAsync(projectKey, scopeId, request, Actor(), ct));

    [HttpPost("{scopeId:guid}/approve")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<ScopeDetail>> Approve(Guid scopeId, [FromQuery] string projectKey, [FromBody] ScopeDecisionRequest request, CancellationToken ct) =>
        Result(await scopes.ApproveAsync(projectKey, scopeId, request, Actor(), ct));

    [HttpPost("{scopeId:guid}/archive")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<ScopeDetail>> Archive(Guid scopeId, [FromQuery] string projectKey, [FromBody] ScopeDecisionRequest request, CancellationToken ct) =>
        Result(await scopes.ArchiveAsync(projectKey, scopeId, request, ct));

    [HttpGet("{scopeId:guid}/compare")]
    public async Task<ActionResult<ScopeComparison>> Compare(Guid scopeId, [FromQuery] string projectKey, [FromQuery] int from, [FromQuery] int to, CancellationToken ct) =>
        await scopes.CompareAsync(projectKey, scopeId, from, to, ct) is { } comparison ? Ok(comparison) : NotFound();

    /// <summary>The confirmed members of one or several scopes (approved revisions unless drafts=true), de-duplicated with attribution.</summary>
    [HttpGet("selection")]
    public async Task<ActionResult<ScopeSelection>> Selection([FromQuery] string projectKey, [FromQuery] Guid[] scope, [FromQuery] bool drafts, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "projectKey is required." }) : Ok(await scopes.SelectAsync(projectKey, scope, drafts, ct));

    private ActionResult<ScopeDetail> Result(ScopeWriteResult result) =>
        result.Scope is { } detail ? Ok(detail) : result.Conflict ? Conflict(new { message = result.Error }) : BadRequest(new { message = result.Error });

    private string Actor() => SystemConfigurationPermissions.Actor(User,
        local.EvaluateFor(SystemConfigurationPermissions.LocalSetting, SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed) ?? "not attributed";
}
