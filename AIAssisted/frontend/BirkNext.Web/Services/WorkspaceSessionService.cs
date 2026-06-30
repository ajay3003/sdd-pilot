namespace BirkNext.Web.Services;

/// <summary>
/// Legacy implementation — superseded by WorkspaceArtifactRepository.
/// Kept for compile-time safety; no longer registered in DI.
/// </summary>
public sealed class WorkspaceSessionService : IWorkspaceSessionService
{
    private readonly Dictionary<WorkspaceArtifactKind, WorkspaceArtifact> _artifacts = new();

    public string? ProjectName { get; set; }

    public WorkspaceArtifact? Constitution => Get(WorkspaceArtifactKind.Constitution);
    public WorkspaceArtifact? Specification => Get(WorkspaceArtifactKind.Specification);
    public WorkspaceArtifact? Plan         => Get(WorkspaceArtifactKind.Plan);
    public WorkspaceArtifact? Tasks        => Get(WorkspaceArtifactKind.Tasks);
    public WorkspaceArtifact? DataModel    => Get(WorkspaceArtifactKind.DataModel);

    // IWorkspaceSessionService (WorkspaceArtifactKind)

    public void Set(WorkspaceArtifactKind kind, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _artifacts[kind] = new WorkspaceArtifact(text, DateTime.UtcNow, fileName, sourcePath, lastModified);
    }

    public WorkspaceArtifact? Get(WorkspaceArtifactKind kind)
        => _artifacts.TryGetValue(kind, out var a) ? a : null;

    public bool Has(WorkspaceArtifactKind kind) => _artifacts.ContainsKey(kind);

    public void Clear(WorkspaceArtifactKind kind) => _artifacts.Remove(kind);

    // IWorkspaceArtifactRepository (WorkspaceArtifactType) — bridge via cast

    public void Set(WorkspaceArtifactType type, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
        => Set((WorkspaceArtifactKind)(int)type, text, fileName, sourcePath, lastModified);

    public WorkspaceArtifact? Get(WorkspaceArtifactType type)
        => Get((WorkspaceArtifactKind)(int)type);

    public bool Has(WorkspaceArtifactType type) => Has((WorkspaceArtifactKind)(int)type);

    public void Clear(WorkspaceArtifactType type) => Clear((WorkspaceArtifactKind)(int)type);
}
