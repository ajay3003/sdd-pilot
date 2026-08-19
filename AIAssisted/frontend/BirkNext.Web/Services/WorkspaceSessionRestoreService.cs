namespace BirkNext.Web.Services;

/// <summary>
/// Saved workspace type classification.
/// Used to determine safe artifact restoration policy.
/// </summary>
public enum SavedWorkspaceClassification
{
    /// <summary>
    /// ProjectName resolves to a Sample Project catalog entry.
    /// Artifacts are legacy copied Markdown files.
    /// Restore: identity only, skip persisted artifacts.
    /// </summary>
    SampleProject,

    /// <summary>
    /// ProjectName is either null/empty or does NOT resolve to Sample Project catalog.
    /// Artifacts are legitimate generic/manual workspace artifacts.
    /// Restore: project name and persisted artifacts normally.
    /// </summary>
    GenericWorkspace,

    /// <summary>
    /// Cannot safely determine workspace type (catalog lookup failed/unavailable).
    /// Unknown status means: do NOT restore persisted artifacts.
    /// Preserve identity but withhold artifact restoration.
    /// Log warning for diagnostic visibility.
    /// </summary>
    Unknown
}

/// <summary>
/// Handles restoring saved workspaces into the active session.
/// Bridges backend SavedWorkspace → frontend WorkspaceArtifactRepository → ReviewContext rebuild.
///
/// Responsibilities:
/// 1. Restore artifacts from saved workspace to in-memory repository
/// 2. Update project name in session
/// 3. Signal ReviewContext rebuild
/// 4. Track workspace metadata (id, name, etc)
/// </summary>
public interface IWorkspaceSessionRestoreService
{
    /// <summary>
    /// Restore a workspace DTO into the active session.
    /// Populates WorkspaceArtifactRepository with artifacts.
    /// Signals ReviewContext needs rebuild.
    /// </summary>
    Task RestoreWorkspaceAsync(SavedWorkspaceDto workspace);

    /// <summary>
    /// Clear the active workspace from session.
    /// Clears all artifacts and metadata.
    /// </summary>
    Task ClearWorkspaceAsync();

    /// <summary>
    /// Get the currently loaded workspace metadata (if any).
    /// Returns null if no workspace is loaded.
    /// </summary>
    Task<CurrentWorkspaceMetadata?> GetCurrentWorkspaceMetadataAsync();

    /// <summary>
    /// Check if a workspace is currently loaded.
    /// </summary>
    Task<bool> IsWorkspaceLoadedAsync();

    /// <summary>
    /// Notify that ReviewContext rebuild is needed.
    /// Raised after RestoreWorkspaceAsync completes.
    /// </summary>
    event EventHandler? ReviewContextRebuildNeeded;
}

public class CurrentWorkspaceMetadata
{
    public Guid WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public int ArtifactCount { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public string? ArtifactSetHash { get; set; }
    public bool AutoSaved { get; set; }
}

public class WorkspaceSessionRestoreService : IWorkspaceSessionRestoreService
{
    private readonly IWorkspaceArtifactRepository _artifactRepository;
    private readonly IWorkspaceStateManager _stateManager;
    private readonly IReviewContextProvider _reviewContextProvider;
    private readonly SampleProjectsApiService _sampleProjects;
    private readonly ILogger<WorkspaceSessionRestoreService> _logger;
    private CurrentWorkspaceMetadata? _currentMetadata;

    public event EventHandler? ReviewContextRebuildNeeded;

    public WorkspaceSessionRestoreService(
        IWorkspaceArtifactRepository artifactRepository,
        IWorkspaceStateManager stateManager,
        IReviewContextProvider reviewContextProvider,
        SampleProjectsApiService sampleProjects,
        ILogger<WorkspaceSessionRestoreService> logger)
    {
        _artifactRepository = artifactRepository;
        _stateManager = stateManager;
        _reviewContextProvider = reviewContextProvider;
        _sampleProjects = sampleProjects;
        _logger = logger;
    }

    public async Task RestoreWorkspaceAsync(SavedWorkspaceDto workspace)
    {
        if (workspace?.Artifacts == null || workspace.Artifacts.Count == 0)
        {
            _logger.LogWarning("Cannot restore workspace {WorkspaceId}: no artifacts", workspace?.Id);
            return;
        }

        try
        {
            // Clear existing artifacts
            _artifactRepository.Clear(WorkspaceArtifactType.Constitution);
            _artifactRepository.Clear(WorkspaceArtifactType.Specification);
            _artifactRepository.Clear(WorkspaceArtifactType.Plan);
            _artifactRepository.Clear(WorkspaceArtifactType.Tasks);
            _artifactRepository.Clear(WorkspaceArtifactType.DataModel);
            _artifactRepository.Clear(WorkspaceArtifactType.Research);

            // Classify the workspace to determine safe restoration policy
            var classification = await ClassifyWorkspaceAsync(workspace.ProjectName);

            var restoredCount = 0;

            // Apply classification-specific restoration policy
            switch (classification)
            {
                case SavedWorkspaceClassification.SampleProject:
                    // Sample Project: restore identity only
                    // Do NOT restore persisted Markdown artifact copies
                    _logger.LogInformation(
                        "Workspace {WorkspaceId} ({ProjectName}) classified as Sample Project: restoring identity only, skipping persisted artifact copies",
                        workspace.Id, workspace.ProjectName);
                    break;

                case SavedWorkspaceClassification.GenericWorkspace:
                    // Generic/manual workspace: restore artifacts normally
                    foreach (var artifact in workspace.Artifacts)
                    {
                        if (!Enum.TryParse<WorkspaceArtifactType>(artifact.ArtifactType, out var type))
                        {
                            _logger.LogWarning("Unsupported artifact type: {ArtifactType}", artifact.ArtifactType);
                            continue;
                        }

                        _artifactRepository.Set(
                            type,
                            artifact.Content,
                            artifact.FileName,
                            artifact.OriginalPath,
                            DateTime.UtcNow);

                        restoredCount++;
                    }

                    _logger.LogInformation(
                        "Workspace {WorkspaceId} ({ProjectName}) classified as GenericWorkspace: restored {ArtifactCount} artifacts",
                        workspace.Id, workspace.ProjectName, restoredCount);
                    break;

                case SavedWorkspaceClassification.Unknown:
                    // Catalog lookup failed: preserve identity but withhold artifact restoration
                    // This prevents re-introducing phantom artifacts if catalog is temporarily unavailable
                    _logger.LogWarning(
                        "Workspace {WorkspaceId} ({ProjectName}) classification unknown (catalog unavailable): restoring identity only, withholding persisted artifacts for safety",
                        workspace.Id, workspace.ProjectName);
                    break;
            }

            // Update project name (always restored regardless of classification)
            _artifactRepository.ProjectName = workspace.ProjectName;

            // Track metadata
            _currentMetadata = new CurrentWorkspaceMetadata
            {
                WorkspaceId = workspace.Id,
                WorkspaceName = workspace.Name,
                ProjectName = workspace.ProjectName,
                ArtifactCount = restoredCount,
                LoadedAt = DateTimeOffset.UtcNow,
                ArtifactSetHash = workspace.ArtifactSetHash,
                AutoSaved = workspace.AutoSaved
            };

            _logger.LogInformation(
                "Restored workspace {WorkspaceId} ({WorkspaceName}) with {ArtifactCount} artifacts",
                workspace.Id, workspace.Name, restoredCount);

            // Notify root state manager: workspace changed
            _stateManager.NotifyWorkspaceChanged(workspace.Id);

            // Rebuild ReviewContext from restored artifacts
            await _reviewContextProvider.RebuildAsync();

            // Signal ReviewContext rebuild event (for backward compatibility)
            OnReviewContextRebuildNeeded();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring workspace {WorkspaceId}", workspace.Id);
            throw;
        }
    }

    public async Task ClearWorkspaceAsync()
    {
        _artifactRepository.Clear(WorkspaceArtifactType.Constitution);
        _artifactRepository.Clear(WorkspaceArtifactType.Specification);
        _artifactRepository.Clear(WorkspaceArtifactType.Plan);
        _artifactRepository.Clear(WorkspaceArtifactType.Tasks);
        _artifactRepository.Clear(WorkspaceArtifactType.DataModel);
        _artifactRepository.Clear(WorkspaceArtifactType.Research);

        _artifactRepository.ProjectName = null;
        _currentMetadata = null;

        // Notify root state manager: workspace cleared
        _stateManager.NotifyWorkspaceChanged(null);

        _logger.LogInformation("Cleared workspace from session");
        await Task.CompletedTask;
    }

    public async Task<CurrentWorkspaceMetadata?> GetCurrentWorkspaceMetadataAsync()
    {
        return await Task.FromResult(_currentMetadata);
    }

    public async Task<bool> IsWorkspaceLoadedAsync()
    {
        return await Task.FromResult(_currentMetadata != null);
    }

    public void NotifyArtifactsChanged()
    {
        OnReviewContextRebuildNeeded();
    }

    protected virtual void OnReviewContextRebuildNeeded()
    {
        ReviewContextRebuildNeeded?.Invoke(this, EventArgs.Empty);
    }

    private async Task<SavedWorkspaceClassification> ClassifyWorkspaceAsync(string? projectName)
    {
        // Empty or null project name → generic workspace
        if (string.IsNullOrWhiteSpace(projectName))
            return SavedWorkspaceClassification.GenericWorkspace;

        try
        {
            // Attempt to load Sample Project catalog
            var projects = await _sampleProjects.GetProjectsAsync();

            // If project name matches a Sample Project slug (case-sensitive, canonical)
            // then this is a legacy Sample Project workspace
            if (projects.Any(p => p.Slug == projectName))
            {
                return SavedWorkspaceClassification.SampleProject;
            }

            // Project name exists but doesn't match any Sample Project
            // Treat as generic/manual workspace
            return SavedWorkspaceClassification.GenericWorkspace;
        }
        catch (Exception ex)
        {
            // Catalog lookup failed — cannot safely determine type
            // Return Unknown to trigger safe (identity-only) policy
            _logger.LogWarning(
                ex,
                "Failed to classify workspace {ProjectName}: catalog lookup unavailable",
                projectName);
            return SavedWorkspaceClassification.Unknown;
        }
    }
}
