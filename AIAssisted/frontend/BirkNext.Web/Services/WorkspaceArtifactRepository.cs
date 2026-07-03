namespace BirkNext.Web.Services;

/// <summary>
/// Shared in-memory repository for the active Studio session.
/// Cleared on browser refresh by design (no persistent storage required).
/// Registered as singleton for both IWorkspaceArtifactRepository and IWorkspaceSessionService.
/// </summary>
public sealed class WorkspaceArtifactRepository : IWorkspaceSessionService
{
    private readonly Dictionary<WorkspaceArtifactType, WorkspaceArtifact> _artifacts = new();

    public string? ProjectName { get; set; }
    public string? CurrentProject
    {
        get => ProjectName;
        set => ProjectName = value;
    }

    // ── IWorkspaceSessionService convenience properties ───────────────────────

    public WorkspaceArtifact? Constitution => Get(WorkspaceArtifactType.Constitution);
    public WorkspaceArtifact? Specification => Get(WorkspaceArtifactType.Specification);
    public WorkspaceArtifact? Plan         => Get(WorkspaceArtifactType.Plan);
    public WorkspaceArtifact? Tasks        => Get(WorkspaceArtifactType.Tasks);
    public WorkspaceArtifact? DataModel    => Get(WorkspaceArtifactType.DataModel);

    // ── IWorkspaceArtifactRepository (WorkspaceArtifactType) ─────────────────

    public void Set(WorkspaceArtifactType type, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _artifacts[type] = new WorkspaceArtifact(text, DateTime.UtcNow, fileName, sourcePath, lastModified);
    }

    public WorkspaceArtifact? Get(WorkspaceArtifactType type)
        => _artifacts.TryGetValue(type, out var a) ? a : null;

    public bool Has(WorkspaceArtifactType type) => _artifacts.ContainsKey(type);

    public void Clear(WorkspaceArtifactType type) => _artifacts.Remove(type);

    public IEnumerable<(WorkspaceArtifactType Type, WorkspaceArtifact Artifact)> GetAllArtifacts()
        => _artifacts.Select(kvp => (kvp.Key, kvp.Value));

    // ── IWorkspaceSessionService (WorkspaceArtifactKind) ─────────────────────
    // WorkspaceArtifactKind and WorkspaceArtifactType share identical integer values,
    // so the cast is safe for all defined members.

    public void Set(WorkspaceArtifactKind kind, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
        => Set((WorkspaceArtifactType)(int)kind, text, fileName, sourcePath, lastModified);

    public WorkspaceArtifact? Get(WorkspaceArtifactKind kind)
        => Get((WorkspaceArtifactType)(int)kind);

    public bool Has(WorkspaceArtifactKind kind) => Has((WorkspaceArtifactType)(int)kind);

    public void Clear(WorkspaceArtifactKind kind) => Clear((WorkspaceArtifactType)(int)kind);
}
