namespace BirkNext.Web.Services;

public enum WorkspaceArtifactKind
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    Research
}

public sealed record WorkspaceArtifact(
    string Text,
    DateTime LoadedAt,
    string? FileName = null,
    string? SourcePath = null,
    DateTime? LastModified = null);

public interface IWorkspaceSessionService : IWorkspaceArtifactRepository
{
    WorkspaceArtifact? Constitution { get; }
    WorkspaceArtifact? Specification { get; }
    WorkspaceArtifact? Plan { get; }
    WorkspaceArtifact? Tasks { get; }
    WorkspaceArtifact? DataModel { get; }

    void Set(WorkspaceArtifactKind kind, string text,
             string? fileName = null, string? sourcePath = null, DateTime? lastModified = null);
    WorkspaceArtifact? Get(WorkspaceArtifactKind kind);
    bool Has(WorkspaceArtifactKind kind);
    void Clear(WorkspaceArtifactKind kind);

    /// <summary>
    /// Clear all workspace state: project identity and all artifacts.
    /// Called by ApplicationRuntimeResetService after backend database reset.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Notify that artifacts have changed and ReviewContext needs rebuild.
    /// </summary>
    void NotifyArtifactsChanged();

    /// <summary>
    /// Raised when artifacts change and ReviewContext rebuild is needed.
    /// </summary>
    event EventHandler? ReviewContextRebuildNeeded;
}
