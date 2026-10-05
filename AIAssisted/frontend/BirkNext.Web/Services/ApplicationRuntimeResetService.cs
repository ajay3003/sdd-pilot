using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The frontend half of a local data reset (the backend half is <c>LocalDataResetCoordinator</c>). Called ONLY after the backend reset
/// succeeded — or when the backend refuses a workspace write from before a reset (another tab reset local data). It clears every frontend
/// store that holds project/workspace state, so no page shows the previous project and nothing can save it again:
///   workspace artifacts + SDD lifecycle + current project/workspace id, review/dashboard/extraction sessions, ReviewContext, Target
///   Environments (back to the generic seed), endpoint discovery, API review history, integration target hints and mapping evidence,
///   the BirkNext-owned authenticated browser session, Browser Companion following, and every project localStorage key.
/// Preserved: feature visibility, sidebar section state (UI preference), the Sample Project catalog, provider capability.
/// See docs/local-data-reset.md for the full contract.
/// </summary>
public sealed class ApplicationRuntimeResetService
{
    /// <summary>Project-state localStorage keys (exact). UI preferences are not in localStorage (sidebar state is in memory).</summary>
    public static readonly IReadOnlyList<string> ProjectStorageKeys =
    [
        "birknext:frontend-analysis-settings", "birknext:detection-snapshots", "birknext:endpoint-discovery", "birknext:api-review",
        "birknext:integration-target-registry", "birknext:extraction:session",
        "ce-standalone-constitution", "pe-standalone-plan", "te-standalone-tasks", "dme-standalone-datamodel",
    ];

    /// <summary>Project-state localStorage key prefixes (diagram layouts keyed by source snapshot).</summary>
    public static readonly IReadOnlyList<string> ProjectStoragePrefixes = ["architecture-layout:", "database-layout:"];

    private readonly IWorkspaceSessionService _workspace;
    private readonly IWorkspaceStateManager _stateManager;
    private readonly QualityReviewSessionService _qualitySession;
    private readonly IDashboardSnapshotService _dashboardSnapshot;
    private readonly RuntimeReviewSessionService _runtimeReviews;
    private readonly IExtractionSessionService _extractionSession;
    private readonly IWorkspaceAutoSaveService? _autoSave;
    private readonly IServiceProvider? _services;
    private readonly Microsoft.JSInterop.IJSRuntime? _js;

    public ApplicationRuntimeResetService(
        IWorkspaceSessionService workspace,
        IWorkspaceStateManager stateManager,
        QualityReviewSessionService qualitySession,
        IDashboardSnapshotService dashboardSnapshot,
        RuntimeReviewSessionService runtimeReviews,
        IExtractionSessionService extractionSession,
        IWorkspaceAutoSaveService? autoSave = null,
        IServiceProvider? services = null,
        Microsoft.JSInterop.IJSRuntime? js = null)
    {
        _workspace = workspace;
        _stateManager = stateManager;
        _qualitySession = qualitySession;
        _dashboardSnapshot = dashboardSnapshot;
        _runtimeReviews = runtimeReviews;
        _extractionSession = extractionSession;
        _autoSave = autoSave;
        _services = services;
        _js = js;
    }

    public Task PauseAutoSaveForResetAsync() => _autoSave?.PauseForResetAsync() ?? Task.CompletedTask;

    public void ResumeAutoSaveAfterReset() => _autoSave?.ResumeAfterReset();

    /// <summary>What could not be cleared in the browser (empty on the normal path). A reload then finishes the job.</summary>
    public List<string> LastWarnings { get; } = [];

    /// <summary>Clears all frontend project/workspace state. Steps are independent: one failing never leaves the others undone.</summary>
    public async Task ClearFrontendRuntimeStateAsync()
    {
        LastWarnings.Clear();
        T? Get<T>() where T : class => _services?.GetService(typeof(T)) as T;

        // The owned authenticated browser session is cancelled while the Target Environment it belongs to still exists.
        await Step("authenticated browser session", async () => { if (Get<IAuthenticatedBrowserSessionService>() is { } auth) await auth.ClearSessionAsync(); });

        // Workspace: current workspace id, artifacts, project identity, SDD lifecycle, restore metadata.
        _stateManager.NotifyWorkspaceChanged(null);
        _workspace.ClearAll();
        await Step("workspace restore metadata", async () => { if (Get<IWorkspaceSessionRestoreService>() is { } restore) await restore.ClearWorkspaceAsync(); });

        // Review, dashboard and extraction sessions.
        _qualitySession.Clear();
        _dashboardSnapshot.Clear();
        _runtimeReviews.ClearAll();
        Get<TaskAlignmentSessionService>()?.Clear();
        Get<IntegrationMappingEvidenceSession>()?.Clear();
        await _extractionSession.ClearAsync();

        // Per-environment stores: in memory first, then their localStorage keys.
        Get<IEndpointDiscoveryService>()?.ResetForLocalDataReset();
        Get<IApiReviewHistoryService>()?.ResetForLocalDataReset();
        Get<IIntegrationTargetRegistryService>()?.ResetForLocalDataReset();
        await Step("Browser Companion", async () => { if (Get<BrowserCompanionRuntime>() is { } companion) await companion.FollowAsync(null); });
        // The backend stopped the project-bound proxy session; the observer forgets it, so the next page re-probes the certificate for the
        // then-selected environment instead of showing the deleted environment's session (or a blank "Unknown").
        Get<LocalHttpsProxyRuntime>()?.ResetForLocalDataReset();
        await Step("browser storage", async () =>
        {
            if (_js is not null) await _js.InvokeAsync<string[]>("birkNextStorage.removeProjectState", new object?[] { ProjectStorageKeys.ToArray(), ProjectStoragePrefixes.ToArray() });
        });
        // Target Environments go back to the generic seed (no project URL, integrations, thresholds, authentication or expectations).
        await Step("Target Environments", async () =>
        {
            if (_js is not null && Get<IFrontendAnalysisSettingsService>() is { } settings) await settings.ResetForLocalDataResetAsync(_js);
        });

        // Derived views: ReviewContext and the sidebar applicability badges re-evaluate from the empty state.
        await Step("review context", async () => { if (Get<IReviewContextProvider>() is { } context) await context.RebuildAsync(); });
        await Step("navigation status", async () => { if (Get<ProjectApplicabilityState>() is { } applicability) await applicability.RefreshAsync(); });
    }

    private async Task Step(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LastWarnings.Add($"{what}: {ex.Message}"); }
    }
}
