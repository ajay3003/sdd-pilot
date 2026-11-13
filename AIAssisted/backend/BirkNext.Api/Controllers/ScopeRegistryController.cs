using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.CanonicalInterpretation;
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

    /// <summary>The confirmed members of one kind in every scope of the project (newest revision), in one call.</summary>
    [HttpGet("membership")]
    public async Task<ActionResult<IReadOnlyList<ScopeMembershipSummary>>> Membership([FromQuery] string projectKey, [FromQuery] ScopeMemberKind kind, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "projectKey is required." }) : Ok(await scopes.MembershipAsync(projectKey, kind, ct));

    /// <summary>Scenarios linked to the given requirements by established canonical Verifies relations (never candidates).</summary>
    [HttpPost("verifying-scenarios")]
    public async Task<ActionResult<IReadOnlyList<ScopeScenarioLink>>> VerifyingScenarios([FromServices] CanonicalInterpretationService canonical,
        [FromBody] ScopeScenarioLinkRequest request, CancellationToken ct)
    {
        if (request.RequirementKeys is null || request.RequirementKeys.Count > ScopeRegistryRules.MaxMembers) return BadRequest(new { message = "Between 0 and 2000 requirements." });
        try { return Ok(await canonical.ScenariosVerifyingAsync(request.CatalogScope, request.RequirementKeys, ct) ?? []); }
        catch (CanonicalRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Marks the newest draft revision as ready for review (not an approval).</summary>
    [HttpPost("{scopeId:guid}/submit")]
    public async Task<ActionResult<ScopeDetail>> Submit(Guid scopeId, [FromQuery] string projectKey, [FromBody] ScopeDecisionRequest request, CancellationToken ct) =>
        Result(await scopes.SubmitAsync(projectKey, scopeId, request, ct));

    /// <summary>Each member of a revision checked against its catalog now: current, missing (stale), another context, or not checkable.</summary>
    [HttpGet("{scopeId:guid}/check")]
    public async Task<ActionResult<IReadOnlyList<ScopeMemberStatus>>> Check(Guid scopeId, [FromQuery] string projectKey, [FromQuery] int? revision,
        [FromQuery] string? catalogScope, CancellationToken ct) =>
        await scopes.CheckAsync(projectKey, scopeId, revision, catalogScope, ct) is { } statuses ? Ok(statuses) : NotFound();

    /// <summary>Checks members before they are added (the advanced by-id entry): only ids their catalog confirms can be saved.</summary>
    [HttpPost("check-members")]
    public async Task<ActionResult<IReadOnlyList<ScopeMemberStatus>>> CheckMembers([FromServices] IScopeMemberResolver resolver, [FromBody] ScopeMemberCheckRequest request,
        CancellationToken ct) =>
        request.Members is null || request.Members.Count > ScopeRegistryRules.MaxMembers ? BadRequest(new { message = "Between 0 and 2000 members can be checked." })
            : Ok(await resolver.CheckAsync(request.Members, request.CatalogScope, ct));

    /// <summary>A searchable page of what can be added to a scope (systems, requirements, scenarios, Test Data …), read from the owning stores.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<ScopeCatalogPage>> Catalog([FromServices] ScopeMemberCatalogService catalog, [FromQuery] ScopeMemberKind kind, [FromQuery] string? catalogScope,
        [FromQuery] string? search, [FromQuery] int skip = 0, [FromQuery] int take = 25, CancellationToken ct = default)
    {
        try { return Ok(await catalog.SearchAsync(kind, catalogScope, search, skip, take, ct)); }
        catch (CanonicalRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Systems, services and their connections (canonical interpretation + Integration Topology, offline) for one Target Environment.</summary>
    [HttpGet("systems")]
    public async Task<ActionResult<SystemsOverviewView>> Systems([FromServices] SystemsOverviewService systems, [FromQuery] string catalogScope, CancellationToken ct)
    {
        try { return Ok(await systems.OverviewAsync(catalogScope, ct)); }
        catch (CanonicalRequestException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>The confirmed members of one or several scopes (approved revisions unless drafts=true), de-duplicated with attribution.</summary>
    [HttpGet("selection")]
    public async Task<ActionResult<ScopeSelection>> Selection([FromQuery] string projectKey, [FromQuery] Guid[] scope, [FromQuery] bool drafts, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(projectKey) ? BadRequest(new { message = "projectKey is required." }) : Ok(await scopes.SelectAsync(projectKey, scope, drafts, ct));

    private ActionResult<ScopeDetail> Result(ScopeWriteResult result) =>
        result.Scope is { } detail ? Ok(detail) : result.Conflict ? Conflict(new { message = result.Error }) : BadRequest(new { message = result.Error });

    private string Actor() => SystemConfigurationPermissions.Actor(User,
        local.EvaluateFor(SystemConfigurationPermissions.LocalSetting, SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed) ?? "not attributed";
}
