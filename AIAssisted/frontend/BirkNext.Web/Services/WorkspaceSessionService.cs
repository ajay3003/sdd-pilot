namespace BirkNext.Web.Services;

/// <summary>
/// In-memory workspace shared across all explorer pages for the lifetime of the browser tab.
/// Cleared on browser refresh by design (no persistent storage required at this stage).
/// </summary>
public sealed class WorkspaceSessionService : IWorkspaceSessionService
{
    private readonly Dictionary<WorkspaceArtifactKind, WorkspaceArtifact> _artifacts = new();

    public string? ProjectName { get; set; }

    public WorkspaceArtifact? Constitution => Get(WorkspaceArtifactKind.Constitution);
    public WorkspaceArtifact? Specification => Get(WorkspaceArtifactKind.Specification);
    public WorkspaceArtifact? Plan => Get(WorkspaceArtifactKind.Plan);
    public WorkspaceArtifact? Tasks => Get(WorkspaceArtifactKind.Tasks);
    public WorkspaceArtifact? DataModel => Get(WorkspaceArtifactKind.DataModel);

    public void Set(WorkspaceArtifactKind kind, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _artifacts[kind] = new WorkspaceArtifact(text, DateTime.UtcNow);
    }

    public WorkspaceArtifact? Get(WorkspaceArtifactKind kind)
        => _artifacts.TryGetValue(kind, out var artifact) ? artifact : null;

    public bool Has(WorkspaceArtifactKind kind)
        => _artifacts.ContainsKey(kind);
}
