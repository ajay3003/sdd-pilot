using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Test Plan Suggestions (api/test-plan-suggestions). Errors come back as a message, never as an exception. Read-only towards Azure DevOps:
/// nothing here talks to it.</summary>
public interface ITestPlanSuggestionsApiService
{
    Task<(TestPlanWorkspace? Workspace, string? Error)> WorkspaceAsync(string? guideKey, CancellationToken ct = default);
    /// <summary>Persisted facts only (drafts, snapshots, candidate count): what the Manual Testing focus panel shows.</summary>
    Task<(TestPlanWorkspace? Workspace, string? Error)> SummaryAsync(string? guideKey, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> DraftAsync(Guid id, CancellationToken ct = default);
    /// <summary>The plan's saved revisions, newest first (version history).</summary>
    Task<(List<TestPlanRevisionSummary>? Revisions, string? Error)> RevisionsAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<(List<TestPlanRevisionSummary>?, string?)>((null, "Version history is not available in this host."));
    Task<(TestPlanDraft? Draft, string? Error)> RevisionAsync(Guid id, int revision, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, "Version history is not available in this host."));
    /// <summary>Restores an earlier revision as the next revision.</summary>
    Task<(TestPlanDraft? Draft, string? Error)> RestoreRevisionAsync(Guid id, int baseRevision, int revision, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, "Version history is not available in this host."));
    Task<(TestPlanDraft? Draft, string? Error)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default);
    Task<(TestPlanDraft? Draft, string? Error)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default);
    Task<(SaveTestPlanSnapshotResult? Result, string? Error)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default);
    Task<(TestPlanSnapshot? Snapshot, string? Error)> SnapshotAsync(Guid id, CancellationToken ct = default);
    Task<(DeliveryComparison? Comparison, string? Error)> CompareAsync(Guid from, Guid to, CancellationToken ct = default);
    Task<(TestPlanExport? Export, string? Error)> ExportAsync(Guid snapshotId, CancellationToken ct = default);

    /// <summary>MT-7 reviewed requirement coverage of one project scope (GET requirement-coverage). Read-only. Hosts without it report "not available".</summary>
    /// <param name="rows">False: counts and associations only (no row per requirement) — what Test Plan Suggestions and the focus panel need.</param>
    Task<(RequirementCoverageView? Coverage, string? Error)> RequirementCoverageAsync(string scope, CancellationToken ct = default, bool rows = true) =>
        Task.FromResult<(RequirementCoverageView?, string?)>((null, "Reviewed requirement coverage is not available in this host."));

    // ── Project-scoped discovery and plans (hosts without them report "not available") ────────────────────────

    /// <summary>The project's plans, snapshots, Test Data options and its environment's Critical E2E flows (GET project).</summary>
    Task<(TestPlanWorkspace? Workspace, string? Error)> ProjectAsync(string projectKey, string? environmentId, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanWorkspace?, string?)>((null, NotAvailable));
    /// <summary>Runs discovery for one project (POST discover). Explicit only.</summary>
    Task<(ScenarioDiscoveryResult? Result, string? Error)> DiscoverAsync(ScenarioDiscoveryRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ScenarioDiscoveryResult?, string?)>((null, NotAvailable));
    /// <summary>The last discovery result of the project (and snapshot) — no analyzer runs; (null, null) when there is none.</summary>
    Task<(ScenarioDiscoveryResult? Result, string? Error)> LastDiscoveryAsync(string projectKey, Guid? snapshotId, CancellationToken ct = default) =>
        Task.FromResult<(ScenarioDiscoveryResult?, string?)>((null, null));
    /// <summary>The plan compared with its application's current source basis (changed, removed, unavailable, new). Read-only.</summary>
    Task<(PlanImpactAnalysis? Impact, string? Error)> ImpactAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<(PlanImpactAnalysis?, string?)>((null, "Konsekvensanalyse er ikke tilgjengelig her."));
    /// <summary>Records which expected result applies to a case whose sources disagree.</summary>
    Task<(TestPlanDraft? Draft, string? Error)> ResolveConflictAsync(Guid id, ResolveConflictRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, "Avklaring er ikke tilgjengelig her."));
    Task<(TestPlanDraft? Draft, string? Error)> CreateProjectAsync(CreateProjectTestPlanRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, NotAvailable));
    Task<(TestPlanDraft? Draft, string? Error)> AddManualCaseAsync(Guid id, AddManualTestCaseRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, NotAvailable));
    /// <summary>Test-case variants the evidence of one scenario supports (proposals only).</summary>
    Task<(List<TestCaseVariantProposal>? Variants, string? Error)> VariantsAsync(Guid id, string scenarioKey, CancellationToken ct = default) =>
        Task.FromResult<(List<TestCaseVariantProposal>?, string?)>((null, NotAvailable));
    /// <summary>Whether a translation service is configured (nothing is sent to find out).</summary>
    Task<TranslationAvailability> TranslationAsync(CancellationToken ct = default) =>
        Task.FromResult(new TranslationAvailability(false, null, "Translation is not available in this host. You can write the Norwegian text by hand."));
    /// <summary>Translates one scenario's cases to Norwegian with the configured service.</summary>
    /// <summary>Excludes a whole scenario with one reason (every case under it; nothing deleted).</summary>
    Task<(TestPlanDraft? Draft, string? Error)> ExcludeScenarioAsync(Guid id, ExcludeScenarioRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraft?, string?)>((null, NotAvailable));
    /// <summary>Translate all: every scenario of the plan with its user story and linked requirement wording (one saved revision).</summary>
    Task<(TranslateScenarioResult? Result, string? Error)> TranslateAllAsync(Guid id, TranslatePlanRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TranslateScenarioResult?, string?)>((null, NotAvailable));
    Task<(TranslateScenarioResult? Result, string? Error)> TranslateAsync(Guid id, TranslateScenarioRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TranslateScenarioResult?, string?)>((null, NotAvailable));
    /// <summary>Finn og forstå's work queues for a discovery result (GET project/work-queues): queue membership, rank, reasons and saved test cases
    /// per scenario, computed on the server. Read-only.</summary>
    Task<(ScenarioWorkQueueView? View, string? Error)> WorkQueuesAsync(string projectKey, string? discoveryId, CancellationToken ct = default) =>
        Task.FromResult<(ScenarioWorkQueueView?, string?)>((null, NotAvailable));

    /// <summary>The user story grouping and requirement review decisions of one project and specification (GET story-grouping). Read-only.</summary>
    Task<(StoryGroupingDocument? Document, string? Error)> StoryGroupingAsync(string projectKey, string specificationPath, CancellationToken ct = default) =>
        Task.FromResult<(StoryGroupingDocument?, string?)>((null, NotAvailable));

    /// <summary>Applies grouping / link operations together (POST story-grouping) against the revision the page read; a stale change is refused.</summary>
    Task<(StoryGroupingDocument? Document, string? Error)> ApplyStoryGroupingAsync(string projectKey, string specificationPath, StoryGroupingChangeRequest request, CancellationToken ct = default) =>
        Task.FromResult<(StoryGroupingDocument?, string?)>((null, NotAvailable));

    /// <summary>The project's saved test cases traced to a specification's requirements (POST project/traceability). Read-only.</summary>
    Task<(PreparedCaseTraceView? Trace, string? Error)> PreparedCaseTraceAsync(string projectKey, PreparedCaseTraceRequest request, CancellationToken ct = default) =>
        Task.FromResult<(PreparedCaseTraceView?, string?)>((null, NotAvailable));

    /// <summary>Archives a plan at the revision the tester saw (read-only, listed apart).</summary>
    Task<(TestPlanDraftSummary? Plan, string? Error)> ArchiveAsync(Guid id, TestPlanLifecycleRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraftSummary?, string?)>((null, NotAvailable));
    /// <summary>Restores an archived plan to active work.</summary>
    Task<(TestPlanDraftSummary? Plan, string? Error)> UnarchiveAsync(Guid id, TestPlanLifecycleRequest request, CancellationToken ct = default) =>
        Task.FromResult<(TestPlanDraftSummary?, string?)>((null, NotAvailable));
    /// <summary>Deletes an archived plan (logical; needs its name). Null error = deleted.</summary>
    Task<string?> DeleteAsync(Guid id, DeleteTestPlanRequest request, CancellationToken ct = default) => Task.FromResult<string?>(NotAvailable);
    /// <summary>Scenario changes of a new import against the application's previous discovery (runs discovery).</summary>
    Task<(ScenarioChangeSummary? Changes, string? Error)> ChangesAsync(ScenarioDiscoveryRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ScenarioChangeSummary?, string?)>((null, NotAvailable));

    const string NotAvailable = "Project test planning is not available in this host.";
}

public sealed class TestPlanSuggestionsApiService(HttpClient http, LastDiscoveryCache? lastDiscoveries = null) : ITestPlanSuggestionsApiService
{
    private const string Base = "api/test-plan-suggestions";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(TestPlanWorkspace?, string?)> WorkspaceAsync(string? guideKey, CancellationToken ct = default) =>
        Send<TestPlanWorkspace>(() => http.GetAsync(guideKey is null ? Base : $"{Base}?guideKey={Uri.EscapeDataString(guideKey)}", ct), ct);

    public Task<(TestPlanWorkspace?, string?)> SummaryAsync(string? guideKey, CancellationToken ct = default) =>
        Send<TestPlanWorkspace>(() => http.GetAsync(guideKey is null ? $"{Base}/summary" : $"{Base}/summary?guideKey={Uri.EscapeDataString(guideKey)}", ct), ct);

    public Task<(TestPlanDraft?, string?)> DraftAsync(Guid id, CancellationToken ct = default) => Send<TestPlanDraft>(() => http.GetAsync($"{Base}/drafts/{id}", ct), ct);
    public Task<(List<TestPlanRevisionSummary>?, string?)> RevisionsAsync(Guid id, CancellationToken ct = default) =>
        Send<List<TestPlanRevisionSummary>>(() => http.GetAsync($"{Base}/drafts/{id}/revisions", ct), ct);
    public Task<(TestPlanDraft?, string?)> RevisionAsync(Guid id, int revision, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.GetAsync($"{Base}/drafts/{id}/revisions/{revision}", ct), ct);
    public Task<(TestPlanDraft?, string?)> RestoreRevisionAsync(Guid id, int baseRevision, int revision, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/restore", new RestoreTestPlanRevisionRequest(baseRevision, revision), ct), ct);

    public Task<(TestPlanDraft?, string?)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PutAsJsonAsync($"{Base}/drafts/{id}", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/review", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsync($"{Base}/drafts/{id}/refresh?baseRevision={baseRevision}", null, ct), ct);

    public Task<(SaveTestPlanSnapshotResult?, string?)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default) =>
        Send<SaveTestPlanSnapshotResult>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/snapshots", request, Json, ct), ct);

    public Task<(TestPlanSnapshot?, string?)> SnapshotAsync(Guid id, CancellationToken ct = default) => Send<TestPlanSnapshot>(() => http.GetAsync($"{Base}/snapshots/{id}", ct), ct);

    public Task<(DeliveryComparison?, string?)> CompareAsync(Guid from, Guid to, CancellationToken ct = default) =>
        Send<DeliveryComparison>(() => http.GetAsync($"{Base}/compare?from={from}&to={to}", ct), ct);

    public Task<(TestPlanExport?, string?)> ExportAsync(Guid snapshotId, CancellationToken ct = default) =>
        Send<TestPlanExport>(() => http.GetAsync($"{Base}/snapshots/{snapshotId}/export", ct), ct);

    public Task<(RequirementCoverageView?, string?)> RequirementCoverageAsync(string scope, CancellationToken ct = default, bool rows = true) =>
        Send<RequirementCoverageView>(() => http.GetAsync($"{Base}/requirement-coverage?scope={Uri.EscapeDataString(scope)}{(rows ? "" : "&rows=false")}", ct), ct);

    public Task<(TestPlanWorkspace?, string?)> ProjectAsync(string projectKey, string? environmentId, CancellationToken ct = default) =>
        Send<TestPlanWorkspace>(() => http.GetAsync($"{Base}/project?projectKey={Uri.EscapeDataString(projectKey)}{(string.IsNullOrWhiteSpace(environmentId) ? "" : "&environmentId=" + Uri.EscapeDataString(environmentId))}", ct), ct);

    public Task<(ScenarioDiscoveryResult?, string?)> DiscoverAsync(ScenarioDiscoveryRequest request, CancellationToken ct = default) =>
        Send<ScenarioDiscoveryResult>(() => http.PostAsJsonAsync($"{Base}/discover", request, Json, ct), ct);

    public async Task<(ScenarioDiscoveryResult?, string?)> LastDiscoveryAsync(string projectKey, Guid? snapshotId, CancellationToken ct = default)
    {
        var key = LastDiscoveryCache.KeyOf(projectKey, snapshotId);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/discover/last?projectKey={Uri.EscapeDataString(projectKey)}{(snapshotId is { } id ? $"&snapshotId={id}" : "")}");
            // Revalidate the result already parsed in this session: 304 = byte-identical, reuse it (see LastDiscoveryCache).
            ScenarioDiscoveryResult? cached = null;
            if (lastDiscoveries is not null && lastDiscoveries.TryGet(key, out var etag, out var held))
            {
                cached = held;
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null) return (cached, null);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { lastDiscoveries?.Remove(key); return (null, null); }
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ScenarioDiscoveryResult>(Json, ct);
                if (lastDiscoveries is not null)
                {
                    if (result is not null && response.Headers.ETag?.Tag is { Length: > 0 } tag) lastDiscoveries.Set(key, tag, result);
                    else lastDiscoveries.Remove(key);
                }
                return (result, null);
            }
            lastDiscoveries?.Remove(key);
            return (null, $"The last discovery could not be read (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (null, "Test Plan Suggestions is not reachable.");
        }
    }

    public Task<(TestPlanDraftSummary?, string?)> ArchiveAsync(Guid id, TestPlanLifecycleRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraftSummary>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/archive", request, Json, ct), ct);

    public Task<(TestPlanDraftSummary?, string?)> UnarchiveAsync(Guid id, TestPlanLifecycleRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraftSummary>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/unarchive", request, Json, ct), ct);

    public async Task<string?> DeleteAsync(Guid id, DeleteTestPlanRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/drafts/{id}/delete", request, Json, ct);
            if (response.IsSuccessStatusCode) return null;
            if ((int)response.StatusCode is 401 or 403) return "Du har ikke tilgang til å slette planer.";
            try { return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return $"Slettingen feilet (HTTP {(int)response.StatusCode})."; }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "Test Plan Suggestions is not reachable."; }
    }

    public Task<(ScenarioChangeSummary?, string?)> ChangesAsync(ScenarioDiscoveryRequest request, CancellationToken ct = default) =>
        Send<ScenarioChangeSummary>(() => http.PostAsJsonAsync($"{Base}/discover/changes", request, Json, ct), ct);

    public Task<(PlanImpactAnalysis?, string?)> ImpactAsync(Guid id, CancellationToken ct = default) =>
        Send<PlanImpactAnalysis>(() => http.GetAsync($"{Base}/drafts/{id}/impact", ct), ct);

    public Task<(TestPlanDraft?, string?)> ResolveConflictAsync(Guid id, ResolveConflictRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/resolve-conflict", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> CreateProjectAsync(CreateProjectTestPlanRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/project-drafts", request, Json, ct), ct);

    public Task<(TestPlanDraft?, string?)> AddManualCaseAsync(Guid id, AddManualTestCaseRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/manual-cases", request, Json, ct), ct);

    public Task<(List<TestCaseVariantProposal>?, string?)> VariantsAsync(Guid id, string scenarioKey, CancellationToken ct = default) =>
        Send<List<TestCaseVariantProposal>>(() => http.GetAsync($"{Base}/drafts/{id}/variants?scenario={Uri.EscapeDataString(scenarioKey)}", ct), ct);

    public async Task<TranslationAvailability> TranslationAsync(CancellationToken ct = default)
    {
        var (availability, error) = await Send<TranslationAvailability>(() => http.GetAsync($"{Base}/translation", ct), ct);
        return availability ?? new TranslationAvailability(false, null, error ?? "Whether translation is available could not be read. You can write the Norwegian text by hand.");
    }

    public Task<(TestPlanDraft?, string?)> ExcludeScenarioAsync(Guid id, ExcludeScenarioRequest request, CancellationToken ct = default) =>
        Send<TestPlanDraft>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/exclude-scenario", request, Json, ct), ct);

    public Task<(TranslateScenarioResult?, string?)> TranslateAllAsync(Guid id, TranslatePlanRequest request, CancellationToken ct = default) =>
        Send<TranslateScenarioResult>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/translate-all", request, Json, ct), ct);

    public Task<(TranslateScenarioResult?, string?)> TranslateAsync(Guid id, TranslateScenarioRequest request, CancellationToken ct = default) =>
        Send<TranslateScenarioResult>(() => http.PostAsJsonAsync($"{Base}/drafts/{id}/translate", request, Json, ct), ct);

    public Task<(ScenarioWorkQueueView?, string?)> WorkQueuesAsync(string projectKey, string? discoveryId, CancellationToken ct = default) =>
        Send<ScenarioWorkQueueView>(() => http.GetAsync($"{Base}/project/work-queues?projectKey={Uri.EscapeDataString(projectKey)}{(discoveryId is { Length: > 0 } d ? $"&discoveryId={Uri.EscapeDataString(d)}" : "")}", ct), ct);

    public Task<(StoryGroupingDocument?, string?)> StoryGroupingAsync(string projectKey, string specificationPath, CancellationToken ct = default) =>
        Send<StoryGroupingDocument>(() => http.GetAsync($"{Base}/story-grouping?projectKey={Uri.EscapeDataString(projectKey)}&specificationPath={Uri.EscapeDataString(specificationPath)}", ct), ct);

    public Task<(StoryGroupingDocument?, string?)> ApplyStoryGroupingAsync(string projectKey, string specificationPath, StoryGroupingChangeRequest request, CancellationToken ct = default) =>
        Send<StoryGroupingDocument>(() => http.PostAsJsonAsync($"{Base}/story-grouping?projectKey={Uri.EscapeDataString(projectKey)}&specificationPath={Uri.EscapeDataString(specificationPath)}", request, Json, ct), ct);

    public Task<(PreparedCaseTraceView?, string?)> PreparedCaseTraceAsync(string projectKey, PreparedCaseTraceRequest request, CancellationToken ct = default) =>
        Send<PreparedCaseTraceView>(() => http.PostAsJsonAsync($"{Base}/project/traceability?projectKey={Uri.EscapeDataString(projectKey)}", request, Json, ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to change Test Plan proposals.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Test Plan Suggestions is not reachable.");
        }
    }
}
