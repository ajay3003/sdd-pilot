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

    /// <summary>The plan's saved revisions, newest first (version history).</summary>
    [HttpGet("drafts/{id:guid}/revisions")]
    public Task<ActionResult> Revisions(Guid id, CancellationToken ct) => Run(async () => Ok(await service.RevisionsAsync(id, ct)));

    [HttpGet("drafts/{id:guid}/revisions/{revision:int}")]
    public Task<ActionResult> Revision(Guid id, int revision, CancellationToken ct) => Run(async () => Ok(await service.RevisionAsync(id, revision, ct)));

    /// <summary>Restores an earlier revision as the next revision (history is kept).</summary>
    [HttpPost("drafts/{id:guid}/restore")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Restore(Guid id, [FromBody] RestoreTestPlanRevisionRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.RestoreRevisionAsync(id, request, Actor, ct)));

    /// <summary>Archives a plan (read-only, listed apart; content, revisions and snapshots kept).</summary>
    [HttpPost("drafts/{id:guid}/archive")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Archive(Guid id, [FromBody] TestPlanLifecycleRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.ArchiveAsync(id, request, Actor, ct)));
    /// <summary>Restores an archived plan to active work.</summary>
    [HttpPost("drafts/{id:guid}/unarchive")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Unarchive(Guid id, [FromBody] TestPlanLifecycleRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.RestoreFromArchiveAsync(id, request, Actor, ct)));
    /// <summary>Deletes an archived plan logically (hidden everywhere; content, revisions and snapshots kept for audit). Needs the plan's name.</summary>
    [HttpPost("drafts/{id:guid}/delete")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Delete(Guid id, [FromBody] DeleteTestPlanRequest request, CancellationToken ct) =>
        Run(async () => { await service.DeleteAsync(id, request, Actor, ct); return NoContent(); });
    [HttpPost("drafts/{id:guid}/review")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Review(Guid id, [FromBody] ReviewTestCaseRequest request, CancellationToken ct) => Run(async () => Ok(await service.ReviewAsync(id, request, Actor, ct)));

    [HttpPost("drafts/{id:guid}/refresh")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Refresh(Guid id, [FromQuery] int baseRevision, CancellationToken ct) => Run(async () => Ok(await service.RefreshAsync(id, baseRevision, Actor, ct)));

    /// <summary>A reviewable comparison of the plan with its application's current source basis (changed, removed, unavailable, new). Read-only.</summary>
    [HttpGet("drafts/{id:guid}/impact")]
    public Task<ActionResult> Impact(Guid id, CancellationToken ct) => Run(async () => Ok(await service.ImpactAsync(id, ct)));

    /// <summary>Records which expected result applies to a case whose sources disagree, with a rationale (the claims stay on the case).</summary>
    [HttpPost("drafts/{id:guid}/resolve-conflict")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> ResolveConflict(Guid id, [FromBody] ResolveConflictRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.ResolveConflictAsync(id, request, Actor, ct)));

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
        Run(async () => Ok(await service.PreparedCaseTraceAsync(projectKey, request.SpecificationPaths ?? [], request.RequirementIds, ct, request.RequirementTitles)));

    /// <summary>Runs the registered discovery capabilities for one project (explicit; never on navigation). Read-only towards the evidence.</summary>
    [HttpPost("discover")]
    public Task<ActionResult> Discover([FromBody] ScenarioDiscoveryRequest request, [FromServices] TestScenarioDiscoveryService discovery, CancellationToken ct) =>
        Run(async () => Ok(await discovery.DiscoverAsync(request, ct)));

    /// <summary>Scenario changes of a new import against the application's previous current discovery (runs discovery; only computed counts).</summary>
    [HttpPost("discover/changes")]
    public Task<ActionResult> Changes([FromBody] ScenarioDiscoveryRequest request, [FromServices] TestScenarioDiscoveryService discovery, CancellationToken ct) =>
        Run(async () => Ok(await discovery.ChangesAsync(request, ct)));
    /// <summary>The last discovery result of the project (and snapshot), also after a restart (stored); 204 when there is none. Runs no analyzer.</summary>
    /// <summary>Finn og forstå's work queues for a discovery result (default: the project's last): queue membership, rank, reasons and saved
    /// test cases per scenario, computed once over every plan of the application. Read-only.</summary>
    [HttpGet("project/work-queues")]
    public Task<ActionResult> WorkQueues([FromQuery] string projectKey, [FromQuery] string? discoveryId, CancellationToken ct) =>
        Run(async () => Ok(await service.WorkQueuesAsync(projectKey, discoveryId, ct)));

    /// <remarks>
    /// The result is large (all candidates with their evidence: 8 MB for 1,752 M2LB scenarios) and every Manual Testing page reads it. Its ETag is
    /// a hash of the exact JSON sent, so a client holding that version gets 304 Not Modified and reuses what it already parsed; any change to the
    /// result changes the bytes and therefore the ETag. Cache-Control no-cache: always revalidated, never served stale.
    /// </remarks>
    [HttpGet("discover/last")]
    public Task<ActionResult> LastDiscovery([FromQuery] string projectKey, [FromQuery] Guid? snapshotId, [FromQuery] string? scope, [FromServices] TestScenarioDiscoveryService discovery,
        [FromServices] Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json, CancellationToken ct) =>
        Run(async () =>
        {
            // With a scope (module + specification, DiscoveryScope): only a result computed for exactly that selection (2026-11-25).
            if (await discovery.LastAsync(projectKey, snapshotId, ct, scope) is not { } result) return NoContent();
            var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, json.Value.JsonSerializerOptions);
            var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body))}\"";
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";
            if (Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Any(t => t.Trim() == etag)))
                return StatusCode(StatusCodes.Status304NotModified);
            return File(body, "application/json; charset=utf-8");
        });

    [HttpPost("project-drafts")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> CreateProject([FromBody] CreateProjectTestPlanRequest request, CancellationToken ct) => Run(async () => Ok(await service.CreateProjectAsync(request, Actor, ct)));

    [HttpPost("drafts/{id:guid}/manual-cases")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> AddManualCase(Guid id, [FromBody] AddManualTestCaseRequest request, CancellationToken ct) => Run(async () => Ok(await service.AddManualCaseAsync(id, request, Actor, ct)));

    /// <summary>Test-case variants the evidence of one scenario of the plan supports (proposals only; nothing is added).</summary>
    [HttpGet("drafts/{id:guid}/variants")]
    public Task<ActionResult> Variants(Guid id, [FromQuery] string scenario, CancellationToken ct) => Run(async () => Ok(await service.VariantsAsync(id, scenario ?? "", ct)));

    /// <summary>Testoversikt (2026-11-25): the project's saved test work and its specifications' scenarios, read-only (no discovery, no write).</summary>
    [HttpGet("project/overview")]
    public Task<ActionResult> Overview([FromQuery] string projectKey, [FromServices] BirkNext.Api.Services.TestPlanning.UserStoryStatusService stories, CancellationToken ct) => Run(async () =>
    {
        var overview = await service.OverviewAsync(projectKey, ct);
        // User story task status from the shared engine (2026-11-26): the same results Finn og forstå shows on its story cards.
        var status = await stories.StatusAsync(projectKey, null, ct);
        return Ok(overview with
        {
            StoryStatusAvailable = status.TasksAvailable,
            Stories = [.. status.Stories.Select(s => new OverviewStoryStatus(s.StoryKey, s.StoryId, s.Title, s.SpecificationPath, s.Module, s.Status, s.Completed, s.Eligible,
                s.Open + s.Partial + s.UnknownState))],
        });
    });

    /// <summary>User story implementation status (2026-11-26): every story of the project, or of one specification, from tasks.md (read-only).</summary>
    [HttpGet("project/story-status")]
    public Task<ActionResult> StoryStatus([FromQuery] string projectKey, [FromQuery] string? specificationPath, [FromServices] BirkNext.Api.Services.TestPlanning.UserStoryStatusService stories,
        CancellationToken ct) => Run(async () => Ok(await stories.StatusAsync(projectKey, specificationPath, ct)));

    /// <summary>The confirmation history of one story (audit, newest first).</summary>
    [HttpGet("project/story-status/history")]
    public Task<ActionResult> StoryStatusHistory([FromQuery] string projectKey, [FromQuery] string storyKey, [FromServices] BirkNext.Api.Services.TestPlanning.UserStoryStatusService stories,
        CancellationToken ct) => Run(async () => Ok(await stories.HistoryAsync(projectKey, storyKey ?? "", ct)));

    /// <summary>A person confirms (or withdraws) a story's implementation for the task evidence shown now (write permission; recorded, never automatic).</summary>
    [HttpPost("project/story-status/confirm")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> ConfirmStory([FromBody] ConfirmUserStoryRequest request, [FromServices] BirkNext.Api.Services.TestPlanning.UserStoryStatusService stories,
        CancellationToken ct) => Run(async () => Ok(await stories.ConfirmAsync(request, Actor, ct)));

    /// <summary>System Settings → Manuell testing → Testoversikt: which dashboard parts are shown (readable by everyone who sees the dashboard).</summary>
    [HttpGet("dashboard-settings")]
    public ActionResult DashboardSettings([FromServices] BirkNext.Api.Services.TestPlanning.ManualTestingDashboardSettingsStore store) => Ok(store.Read());

    /// <summary>Saves the dashboard setup (settings write permission; unknown widget ids are refused; plans and cases are never touched).</summary>
    [HttpPut("dashboard-settings")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public ActionResult SaveDashboardSettings([FromBody] SaveManualTestingDashboardSettingsRequest request,
        [FromServices] BirkNext.Api.Services.TestPlanning.ManualTestingDashboardSettingsStore store)
    {
        try { return Ok(store.Save(request, Actor, DateTimeOffset.UtcNow)); }
        catch (ArgumentException e) { return BadRequest(new { code = "dashboard-widget-unknown", message = e.Message }); }
        catch (IOException) { return StatusCode(500, new { code = "dashboard-settings-not-saved", message = "Kunne ikke lagre oppsettet." }); }
    }

    /// <summary>Which translator the current settings choose and its languages (System Settings → Translation; no call is made to find out).</summary>
    [HttpGet("translation")]
    public ActionResult Translation([FromServices] BirkNext.Api.Services.TestPlanning.TestCaseTranslatorResolver translators)
    {
        var settings = translators.Settings();
        return Ok(translators.Current().Availability with { Source = settings.SourceLanguage, Targets = settings.TargetLanguages });
    }

    /// <summary>Translates the chosen scenario's test cases with the current translator (a person's explicit action).</summary>
    [HttpPost("drafts/{id:guid}/translate")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Translate(Guid id, [FromBody] TranslateScenarioRequest request, [FromServices] BirkNext.Api.Services.TestPlanning.TestCaseTranslatorResolver translators, CancellationToken ct) =>
        Run(async () => Ok(await service.TranslateScenarioAsync(id, request, translators.Current(), translators.Settings(), Actor, ct)));

    /// <summary>Excludes every test case of a scenario with one reason (nothing is deleted; history kept).</summary>
    [HttpPost("drafts/{id:guid}/exclude-scenario")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> ExcludeScenario(Guid id, [FromBody] ExcludeScenarioRequest request, CancellationToken ct) =>
        Run(async () => Ok(await service.ExcludeScenarioAsync(id, request, Actor, ct)));

    /// <summary>Translate all: every scenario of the plan with its user story and linked requirement wording, saved as one revision.</summary>
    [HttpPost("drafts/{id:guid}/translate-all")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> TranslateAll(Guid id, [FromBody] TranslatePlanRequest request, [FromServices] BirkNext.Api.Services.TestPlanning.TestCaseTranslatorResolver translators, CancellationToken ct) =>
        Run(async () => Ok(await service.TranslatePlanAsync(id, request, translators.Current(), translators.Settings(), Actor, ct)));

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
