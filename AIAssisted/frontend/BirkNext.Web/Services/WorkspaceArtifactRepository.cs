using System.Runtime.CompilerServices;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared in-memory repository for the active Studio session.
/// Cleared on browser refresh by design (no persistent storage required).
/// Registered as singleton for both IWorkspaceArtifactRepository and IWorkspaceSessionService.
/// </summary>
public sealed class WorkspaceArtifactRepository : IWorkspaceSessionService
{
    private readonly Dictionary<WorkspaceArtifactType, WorkspaceArtifact> _artifacts = new();

    public event EventHandler? ReviewContextRebuildNeeded;
    public event EventHandler? ProjectSelectionChanged;

    private string? _projectName;
    /// <summary>
    /// For Sample Projects: stores the CANONICAL LOWERCASE SLUG (e.g., "autorisasjon").
    /// NOT the display name ("Autorisasjon"). Used for identity-only persistence and restoration.
    /// Fires ProjectSelectionChanged to trigger auto-save when changed.
    /// </summary>
    public string? ProjectName
    {
        get => _projectName;
        set
        {
            if (_projectName != value)
            {
                _projectName = value;
                // Fire ProjectSelectionChanged so AutoSave persists the new project identity
                ProjectSelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Convenience alias for ProjectName. Represents the canonical project identifier (slug for Sample Projects).
    /// </summary>
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
        {
            var hash = RuntimeHelpers.GetHashCode(this);
            System.Diagnostics.Debug.WriteLine($"DIAG: [Repository] Set({type}) hash={hash}");
            _artifacts[type] = new WorkspaceArtifact(text, DateTime.UtcNow, fileName, sourcePath, lastModified);
        }
    }

    public WorkspaceArtifact? Get(WorkspaceArtifactType type)
        => _artifacts.TryGetValue(type, out var a) ? a : null;

    public bool Has(WorkspaceArtifactType type) => _artifacts.ContainsKey(type);

    public void Clear(WorkspaceArtifactType type) => _artifacts.Remove(type);

    public IEnumerable<(WorkspaceArtifactType Type, WorkspaceArtifact Artifact)> GetAllArtifacts()
    {
        var hash = RuntimeHelpers.GetHashCode(this);
        var count = _artifacts.Count;
        System.Diagnostics.Debug.WriteLine($"DIAG: [Repository] GetAllArtifacts() hash={hash}, count={count}");
        return _artifacts.Select(kvp => (kvp.Key, kvp.Value));
    }

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

    /// <summary>
    /// Clear all workspace state: project identity and all artifacts.
    /// Called by ApplicationRuntimeResetService after backend database reset.
    /// </summary>
    public void ClearAll()
    {
        ProjectName = null;
        _artifacts.Clear();
        NotifyArtifactsChanged();
    }

    public void NotifyArtifactsChanged()
    {
        ReviewContextRebuildNeeded?.Invoke(this, EventArgs.Empty);
    }
}
