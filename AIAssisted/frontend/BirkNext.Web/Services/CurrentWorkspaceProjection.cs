using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>Whether there is a current workspace. Loading is not a state here: a page that has no snapshot yet is loading.</summary>
public enum CurrentWorkspaceState
{
    /// <summary>No project is selected, no artifact is in the workspace and no saved workspace was resumed.</summary>
    NoWorkspace,
    Loaded,
    /// <summary>The workspace exists but its artifacts could not be read. Never shown as "no workspace".</summary>
    Error,
}

/// <summary>Whether the current workspace has an artifact of a role. Unknown when the project's documents could not be read.</summary>
public enum ArtifactRoleAvailability { Available, Missing, Unknown }

/// <summary>Which artifact of the role reviews read. Selection is never availability and never authority.</summary>
public enum ArtifactRoleSelection
{
    /// <summary>No artifact of the role, so nothing to select.</summary>
    None,
    Selected,
    /// <summary>Several artifacts of the role and nothing decides between them.</summary>
    SelectionRequired,
}

/// <summary>
/// One artifact role of the current workspace: how many artifacts it has, which one is selected, and the fingerprint of the
/// selected artifact's content (<see cref="ArtifactFingerprint"/>) — the revision a review decision on the role is about.
/// </summary>
public sealed record ArtifactRoleStatus(
    WorkspaceArtifactType Role,
    ArtifactRoleAvailability Availability,
    int ArtifactCount,
    ArtifactRoleSelection Selection,
    ExplorerArtifact? SelectedArtifact,
    string? Error,
    string? Fingerprint = null)
{
    public bool IsAvailable => Availability == ArtifactRoleAvailability.Available;
    public string Label => ArtifactExplorerRoles.Label(Role);
    /// <summary>The selected artifact's repository authority (Approved, Baseline, …), or null when none is selected.</summary>
    public string? Authority => SelectedArtifact?.Authority;

    /// <summary>The selected revision as review decisions name it, or null when no single readable artifact is selected.</summary>
    public ArtifactRevisionRef? Revision => IsAvailable && SelectedArtifact is { } selected && !string.IsNullOrEmpty(Fingerprint)
        ? new ArtifactRevisionRef { Role = Role.ToString(), ArtifactId = selected.Id, Fingerprint = Fingerprint, FileName = selected.FileName }
        : null;
}

/// <summary>
/// The current workspace as every page sees it. Built from the workspace repository (selected project, imported artifact
/// revisions, explorer selections) and the selected Sample Project's discovered documents — the same role view the explorers
/// read — plus save state from workspace persistence. Pages derive their own domain statuses from it; none decides on its own
/// whether a workspace, project or artifact exists.
/// </summary>
public sealed record CurrentWorkspaceSnapshot(
    CurrentWorkspaceState State,
    Guid? WorkspaceId,
    string WorkspaceName,
    string? ProjectSlug,
    string? ProjectName,
    bool ProjectInCatalog,
    IReadOnlyList<ArtifactRoleStatus> Roles,
    string SaveStatus,
    DateTimeOffset? LastSavedAt,
    long Version,
    string? Error = null)
{
    /// <summary>The document roles the SDD workflow uses. Any of them may be absent: a missing role never makes a workspace invalid.</summary>
    public static readonly IReadOnlyList<WorkspaceArtifactType> WorkflowRoles =
    [
        WorkspaceArtifactType.Constitution,
        WorkspaceArtifactType.Specification,
        WorkspaceArtifactType.Plan,
        WorkspaceArtifactType.Tasks,
        WorkspaceArtifactType.DataModel,
    ];

    public static CurrentWorkspaceSnapshot None(long version = 0) =>
        new(CurrentWorkspaceState.NoWorkspace, null, "No workspace loaded", null, null, false,
            WorkflowRoles.Select(r => new ArtifactRoleStatus(r, ArtifactRoleAvailability.Missing, 0, ArtifactRoleSelection.None, null, null)).ToList(),
            "NotSaved", null, version);

    public bool WorkspaceLoaded => State == CurrentWorkspaceState.Loaded;

    /// <summary>The workspace has a project identity (a selected Sample Project). A manual workspace has none.</summary>
    public bool ProjectLoaded => ProjectSlug is not null;

    /// <summary>Roles with at least one artifact. A count of roles, not of files.</summary>
    public int AvailableRoleCount => Roles.Count(r => r.IsAvailable);

    /// <summary>Artifacts across all roles. Larger than <see cref="AvailableRoleCount"/> when a role has several artifacts.</summary>
    public int ArtifactCount => Roles.Where(r => r.IsAvailable).Sum(r => r.ArtifactCount);

    public IEnumerable<ArtifactRoleStatus> AvailableRoles => Roles.Where(r => r.IsAvailable);

    public ArtifactRoleStatus Role(WorkspaceArtifactType role) =>
        Roles.FirstOrDefault(r => r.Role == role)
        ?? new ArtifactRoleStatus(role, ArtifactRoleAvailability.Missing, 0, ArtifactRoleSelection.None, null, null);

    public bool Has(WorkspaceArtifactType role) => Role(role).IsAvailable;

    /// <summary>
    /// The selected revision of every available role that has one: what review decisions are bound to. The artifact id is
    /// scoped by project ("person-module/sample:constitution.md"): Sample Project paths repeat across projects, and the
    /// auto-saved workspace follows the selected project, so without the scope one project's decision would reach another's.
    /// </summary>
    public IReadOnlyList<ArtifactRevisionRef> ArtifactRevisions => Roles.Select(r => r.Revision).OfType<ArtifactRevisionRef>()
        .Select(r => new ArtifactRevisionRef { Role = r.Role, ArtifactId = $"{ProjectSlug ?? "manual-workspace"}/{r.ArtifactId}", Fingerprint = r.Fingerprint, FileName = r.FileName })
        .ToList();

    public string ProjectDisplay => ProjectName ?? ProjectSlug ?? "Not assigned";

    /// <summary>"5 artifact roles available", plus the artifact count when it differs ("· 7 artifacts").</summary>
    public string RoleSummary
    {
        get
        {
            var roles = AvailableRoleCount;
            var text = $"{roles} artifact role{(roles == 1 ? "" : "s")} available";
            return ArtifactCount != roles ? $"{text} · {ArtifactCount} artifact{(ArtifactCount == 1 ? "" : "s")}" : text;
        }
    }

    public string SaveStatusLabel => SaveStatus switch
    {
        "Saved" => "Saved",
        "AutoSaved" => "Auto-saved",
        "UnsavedChanges" => "Unsaved changes",
        _ => "Not saved",
    };
}

/// <summary>
/// The single read model of the current workspace. It owns no state: the workspace repository and the selected project's
/// documents are the truth, and this projection is recomputed from them whenever they raise a change (project selected or
/// switched, artifact imported, selection or authority changed, workspace resumed or cleared, local data reset).
/// </summary>
public interface ICurrentWorkspaceProjection
{
    /// <summary>The last computed snapshot, or null before the first one finishes (the page is loading).</summary>
    CurrentWorkspaceSnapshot? Current { get; }

    /// <summary>The current snapshot. Concurrent callers share one computation; a change invalidates it.</summary>
    Task<CurrentWorkspaceSnapshot> GetAsync();

    /// <summary>Recompute on the next read and notify. For changes the repository does not raise itself, such as a save.</summary>
    void Invalidate();

    /// <summary>Raised after the workspace changed. Consumers read <see cref="GetAsync"/> again.</summary>
    event Action? Changed;
}

public sealed class CurrentWorkspaceProjection : ICurrentWorkspaceProjection, IDisposable
{
    private readonly IArtifactExplorerContext _artifacts;
    private readonly IWorkspaceSessionRestoreService _restore;
    private readonly IWorkspacePersistenceApiService _persistence;
    private readonly ILogger<CurrentWorkspaceProjection> _logger;
    private readonly object _gate = new();
    private Task<CurrentWorkspaceSnapshot>? _pending;
    private long _version;

    public event Action? Changed;

    public CurrentWorkspaceSnapshot? Current { get; private set; }

    public CurrentWorkspaceProjection(
        IArtifactExplorerContext artifacts,
        IWorkspaceSessionRestoreService restore,
        IWorkspacePersistenceApiService persistence,
        ILogger<CurrentWorkspaceProjection> logger)
    {
        _artifacts = artifacts;
        _restore = restore;
        _persistence = persistence;
        _logger = logger;
        // The explorer context relays every repository change: artifacts, selection, project selection, resume/clear and reset.
        _artifacts.Changed += OnArtifactsChanged;
    }

    public Task<CurrentWorkspaceSnapshot> GetAsync()
    {
        lock (_gate)
        {
            return _pending ??= ComputeAsync(_version);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _pending = null;
        }
        Changed?.Invoke();
    }

    private void OnArtifactsChanged(object? sender, EventArgs e) => Invalidate();

    private async Task<CurrentWorkspaceSnapshot> ComputeAsync(long version)
    {
        CurrentWorkspaceSnapshot snapshot;
        try
        {
            snapshot = await BuildAsync(version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Current workspace could not be read");
            snapshot = CurrentWorkspaceSnapshot.None(version) with
            {
                State = CurrentWorkspaceState.Error,
                WorkspaceName = "Unable to load workspace",
                ProjectSlug = _artifacts.CurrentScope,
                Error = ex.Message,
            };
        }

        lock (_gate)
        {
            // A change during the computation makes this snapshot stale: it is returned to its callers but never cached.
            if (version == _version) Current = snapshot;
        }
        return snapshot;
    }

    private async Task<CurrentWorkspaceSnapshot> BuildAsync(long version)
    {
        var scope = _artifacts.CurrentScope;
        var states = new List<ArtifactExplorerState>();
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles)
            states.Add(await _artifacts.GetStateAsync(role));

        var roles = states.Select(ToRoleStatus).ToList();
        var metadata = await _restore.GetCurrentWorkspaceMetadataAsync();
        // A resumed workspace describes this session only while its project is still the selected one (another project was chosen since).
        if (metadata is not null && !SameProject(metadata.ProjectName, scope)) metadata = null;
        var projectName = states.Select(s => s.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var projectInCatalog = scope is not null && states.All(s => s.Status != ExplorerArtifactStatus.ProjectUnavailable);

        // Exists = a project is selected, an artifact is in the workspace, or a saved workspace was resumed. Saving never decides it.
        var loaded = scope is not null || roles.Any(r => r.IsAvailable) || metadata is not null;
        if (!loaded) return CurrentWorkspaceSnapshot.None(version);

        // The documents of the selected project could not be read at all: the workspace exists, its artifacts are unknown.
        var unreadable = states.FirstOrDefault(s => s.Status == ExplorerArtifactStatus.Error && s.Artifacts.Count == 0);

        var persisted = await _persistence.GetCurrentStateAsync();
        // The backend's current workspace describes this session only when it holds the same project identity (slug, not display name).
        if (persisted?.CurrentWorkspaceId is null || !SameProject(persisted.ProjectName, scope)) persisted = null;

        // An explicit save names the workspace; an auto-saved one ("Auto_…") is named after its project.
        var explicitName = metadata is { AutoSaved: false } ? metadata.WorkspaceName
            : persisted is { Status: not "AutoSaved" } ? persisted.WorkspaceName
            : null;
        var workspaceName = FirstNonBlank(explicitName, projectName, scope, metadata?.WorkspaceName, persisted?.WorkspaceName) ?? "Unsaved workspace";
        var saveStatus = persisted?.Status ?? (metadata?.AutoSaved == true ? "AutoSaved" : "NotSaved");

        return new CurrentWorkspaceSnapshot(
            unreadable is null ? CurrentWorkspaceState.Loaded : CurrentWorkspaceState.Error,
            metadata?.WorkspaceId ?? persisted?.CurrentWorkspaceId,
            workspaceName,
            scope,
            projectName,
            projectInCatalog,
            roles,
            saveStatus,
            persisted?.LastSavedAt ?? metadata?.LoadedAt,
            version,
            unreadable?.Error);
    }

    private static ArtifactRoleStatus ToRoleStatus(ArtifactExplorerState state)
    {
        var count = state.Artifacts.Count;
        var availability = state.Status switch
        {
            ExplorerArtifactStatus.ProjectUnavailable => ArtifactRoleAvailability.Unknown,
            ExplorerArtifactStatus.Error when count == 0 => ArtifactRoleAvailability.Unknown,
            _ => count > 0 ? ArtifactRoleAvailability.Available : ArtifactRoleAvailability.Missing,
        };
        var selection = state.Status switch
        {
            ExplorerArtifactStatus.SelectionRequired => ArtifactRoleSelection.SelectionRequired,
            _ when state.Selected is not null => ArtifactRoleSelection.Selected,
            _ => ArtifactRoleSelection.None,
        };
        // The fingerprint of the content the explorers show for the role: the same function the repository uses for revisions.
        var fingerprint = state.Selected is not null && !string.IsNullOrEmpty(state.Content) ? ArtifactFingerprint.Compute(state.Content) : null;
        return new ArtifactRoleStatus(state.Role, availability, count, selection, state.Selected, state.Error, fingerprint);
    }

    private static bool SameProject(string? persisted, string? scope) =>
        string.Equals(string.IsNullOrWhiteSpace(persisted) ? null : persisted.Trim(), scope, StringComparison.Ordinal);

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public void Dispose() => _artifacts.Changed -= OnArtifactsChanged;
}
