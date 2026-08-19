using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Coordinates clearing of frontend runtime/session state that corresponds to backend database data.
///
/// Called ONLY after backend reset has succeeded.
/// Does NOT clear static configuration, feature visibility, or Sample Project catalog.
/// </summary>
public sealed class ApplicationRuntimeResetService
{
    private readonly IWorkspaceSessionService _workspace;
    private readonly IWorkspaceStateManager _stateManager;
    private readonly QualityReviewSessionService _qualitySession;
    private readonly IDashboardSnapshotService _dashboardSnapshot;
    private readonly RuntimeReviewSessionService _runtimeReviews;
    private readonly IExtractionSessionService _extractionSession;

    public ApplicationRuntimeResetService(
        IWorkspaceSessionService workspace,
        IWorkspaceStateManager stateManager,
        QualityReviewSessionService qualitySession,
        IDashboardSnapshotService dashboardSnapshot,
        RuntimeReviewSessionService runtimeReviews,
        IExtractionSessionService extractionSession)
    {
        _workspace = workspace;
        _stateManager = stateManager;
        _qualitySession = qualitySession;
        _dashboardSnapshot = dashboardSnapshot;
        _runtimeReviews = runtimeReviews;
        _extractionSession = extractionSession;
    }

    /// <summary>
    /// Clear all frontend runtime state that corresponds to deleted backend database data.
    ///
    /// This ensures:
    /// - CurrentProject is null
    /// - Workspace artifacts are empty
    /// - CurrentWorkspaceId reference is cleared
    /// - No active quality review result survives
    /// - No active analysis/runtime review state survives
    /// - No stale dashboard snapshot shows phantom state
    /// </summary>
    public async Task ClearFrontendRuntimeStateAsync()
    {
        // 0. Clear workspace state manager ID reference to deleted SavedWorkspace row
        // This fires WorkspaceChanged event so all cached state invalidates
        _stateManager.NotifyWorkspaceChanged(null);

        // 1. Clear workspace runtime state (project identity + artifacts)
        // Uses ClearAll() to clear both ProjectName and all artifacts, triggering NotifyArtifactsChanged
        _workspace.ClearAll();

        // 2. Clear quality review session
        _qualitySession.Clear();

        // 3. Clear dashboard snapshot cache (forces recompute from empty Workspace)
        _dashboardSnapshot.Clear();

        // 4. Clear runtime review sessions (AI reviews, analysis state)
        _runtimeReviews.ClearAll();

        // 5. Clear extraction session (spec/plan/tasks/etc. import state)
        await _extractionSession.ClearAsync();
    }
}
