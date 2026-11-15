using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Plan Suggestions: reviewable Test Plan / Suite / Case drafts from existing scenario candidates, immutable snapshots, delivery comparison
/// and a safe export. Reading needs only the local session like every API. Creating or changing a draft, recording a review and saving a
/// snapshot need the configuration-write permission (<see cref="BirkNextPermissions.IntegrationConfigurationWritePolicy"/>), the same as
/// editing the system guide the scenarios come from. Nothing here writes to Azure DevOps or executes an imported step.
/// </summary>
[ApiController]
[Route("api/test-plan-suggestions")]
public sealed class TestPlanSuggestionsController(TestPlanSuggestionService service, IAuthorizationService authorization) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    private async Task<bool> CanWriteAsync() =>
        (await authorization.AuthorizeAsync(User, null, BirkNextPermissions.IntegrationConfigurationWritePolicy)).Succeeded;

    [HttpGet]
    public Task<ActionResult> Workspace([FromQuery] string? guideKey, CancellationToken ct) => Run(async () => Ok(await service.WorkspaceAsync(guideKey, await CanWriteAsync(), ct)));

    /// <summary>Persisted facts for the Manual Testing focus panel (Dashboard, Recommended Workflow): drafts, snapshots, candidate count.</summary>
    [HttpGet("summary")]
    public Task<ActionResult> Summary([FromQuery] string? guideKey, CancellationToken ct) => Run(async () => Ok(await service.SummaryAsync(guideKey, ct)));

    [HttpGet("drafts/{id:guid}")]
    public Task<ActionResult> Draft(Guid id, CancellationToken ct) => Run(async () => Ok(await service.DraftAsync(id, ct)));

    [HttpPost("drafts")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Create([FromBody] CreateTestPlanDraftRequest request, CancellationToken ct) => Run(async () => Ok(await service.CreateAsync(request, Actor, ct)));

    [HttpPut("drafts/{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Save(Guid id, [FromBody] SaveTestPlanDraftRequest request, CancellationToken ct) => Run(async () => Ok(await service.SaveAsync(id, request, Actor, ct)));

    [HttpPost("drafts/{id:guid}/review")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Review(Guid id, [FromBody] ReviewTestCaseRequest request, CancellationToken ct) => Run(async () => Ok(await service.ReviewAsync(id, request, Actor, ct)));

    [HttpPost("drafts/{id:guid}/refresh")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Refresh(Guid id, [FromQuery] int baseRevision, CancellationToken ct) => Run(async () => Ok(await service.RefreshAsync(id, baseRevision, Actor, ct)));

    [HttpPost("drafts/{id:guid}/snapshots")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> SaveSnapshot(Guid id, [FromBody] SaveTestPlanSnapshotRequest request, CancellationToken ct) => Run(async () => Ok(await service.SaveSnapshotAsync(id, request, Actor, ct)));

    [HttpGet("snapshots/{id:guid}")]
    public Task<ActionResult> Snapshot(Guid id, CancellationToken ct) => Run(async () => Ok(await service.SnapshotAsync(id, ct)));

    [HttpGet("compare")]
    public Task<ActionResult> Compare([FromQuery] Guid from, [FromQuery] Guid to, CancellationToken ct) => Run(async () => Ok(await service.CompareAsync(from, to, ct)));

    /// <summary>The structured export of one saved snapshot (BirkNext JSON + copyable text). Read-only; not an Azure DevOps write or import.</summary>
    [HttpGet("snapshots/{id:guid}/export")]
    public Task<ActionResult> Export(Guid id, CancellationToken ct) => Run(async () => Ok(await service.ExportAsync(id, ct)));

    // ── Project-scoped discovery and plans ─────────────────────────────────────────────────────────────────────

    /// <summary>The selected project's plans and snapshots (only this project's), Test Data options and its environment's Critical E2E flows.</summary>
    [HttpGet("project")]
    public Task<ActionResult> Project([FromQuery] string projectKey, [FromQuery] string? environmentId, CancellationToken ct) =>
        Run(async () => Ok(await service.ProjectWorkspaceAsync(projectKey, environmentId, await CanWriteAsync(), ct)));

    /// <summary>The project's saved test cases traced to the given specification's requirements (Requirements Traceability). Read-only; counts
    /// a requirement only from the case's own specification evidence; a prepared case is never an executed test.</summary>
    [HttpPost("project/traceability")]
    public Task<ActionResult> ProjectTraceability([FromQuery] string projectKey, [FromBody] PreparedCaseTraceRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.PreparedCaseTraceAsync(projectKey, request.SpecificationPaths ?? [], request.RequirementIds, ct)));

    /// <summary>Runs the registered discovery capabilities for one project (explicit; never on navigation). Read-only towards the evidence.</summary>
    [HttpPost("discover")]
    public Task<ActionResult> Discover([FromBody] ScenarioDiscoveryRequest request, [FromServices] TestScenarioDiscoveryService discovery, CancellationToken ct) =>
        Run(async () => Ok(await discovery.DiscoverAsync(request, ct)));

    /// <summary>The last discovery result of the project (and snapshot) since BirkNext started; 204 when there is none. Runs no analyzer.</summary>
    [HttpGet("discover/last")]
    public Task<ActionResult> LastDiscovery([FromQuery] string projectKey, [FromQuery] Guid? snapshotId, [FromServices] TestScenarioDiscoveryService discovery) =>
        Run(() => Task.FromResult<ActionResult>(discovery.Last(projectKey, snapshotId) is { } result ? Ok(result) : NoContent()));

    [HttpPost("project-drafts")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> CreateProject([FromBody] CreateProjectTestPlanRequest request, CancellationToken ct) => Run(async () => Ok(await service.CreateProjectAsync(request, Actor, ct)));

    [HttpPost("drafts/{id:guid}/manual-cases")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> AddManualCase(Guid id, [FromBody] AddManualTestCaseRequest request, CancellationToken ct) => Run(async () => Ok(await service.AddManualCaseAsync(id, request, Actor, ct)));

    /// <summary>
    /// MT-7 reviewed requirement coverage of one project scope: specification requirements of the latest relationship interpretation, the
    /// scenario → requirement associations (decided in Relationship Review) and the tester reviews of those scenarios. Read-only.
    /// </summary>
    [HttpGet("requirement-coverage")]
    public Task<ActionResult> RequirementCoverage([FromQuery] string scope, [FromServices] RequirementCoverageService coverage, CancellationToken ct, [FromQuery] bool rows = true) =>
        Run(async () => Ok(await coverage.CoverageAsync(scope, ct, rows)));

    private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (TestPlanException e) { return StatusCode(e.Status, new { code = e.Code, message = e.Message }); }
        catch (SystemGuideException e) { return StatusCode(e.Status, new { code = "guide", message = e.Message }); }
        catch (BirkNext.Api.Services.CanonicalInterpretation.CanonicalRequestException e) { return BadRequest(new { code = "scope", message = e.Message }); }
    }
}
