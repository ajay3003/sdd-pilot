using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.Explorers;

/// <summary>Whether Requirements Traceability can analyse the current workspace's documents.</summary>
public enum TraceabilityInputStatus
{
    /// <summary>The current workspace has no Constitution, Specification, Plan or Task artifact (for example a source-only import). Not a 0% result.</summary>
    NoDocuments,
    /// <summary>At least one role has several artifacts and none is chosen. Nothing is picked on the user's behalf.</summary>
    SelectionRequired,
    /// <summary>A selected artifact or the project's documents could not be read.</summary>
    Error,
    /// <summary>Only one traceability role is available: there is no link to assess.</summary>
    Insufficient,
    Ready,
}

/// <summary>Where the current workspace's artifacts come from, as Requirements Traceability names it.</summary>
public enum TraceabilityArtifactSource { ManualWorkspace, SampleProject, ProjectImport }

/// <summary>One role's input: the explorer state, and the fingerprint of the selected artifact's content.</summary>
public sealed record TraceabilityRoleInput(ArtifactExplorerState State, string? Fingerprint)
{
    public WorkspaceArtifactType Role => State.Role;
    public string Label => ArtifactExplorerRoles.Label(Role);
    public bool IsLoaded => State.Status == ExplorerArtifactStatus.Loaded && !string.IsNullOrEmpty(State.Content);
    public string Content => IsLoaded ? State.Content! : string.Empty;
    public bool NeedsSelection => State.Status == ExplorerArtifactStatus.SelectionRequired;
    public string ExplorerRoute => ProjectInputPresentation.ExplorerRoute(Role);

    public string StatusLabel => State.Status switch
    {
        ExplorerArtifactStatus.Loaded => "Selected",
        ExplorerArtifactStatus.SelectionRequired => "Needs selection",
        ExplorerArtifactStatus.Error or ExplorerArtifactStatus.ProjectUnavailable => "Unable to read",
        _ => "Not available",
    };

    /// <summary>Where the selected artifact came from. Never a storage path.</summary>
    public string? SourceLabel => State.Selected switch
    {
        null => null,
        { Source: ExplorerArtifactSource.SampleProject } => "Sample Project",
        { Origin: "ProjectImport" } => "Project Import",
        _ => "Workspace import",
    };
}

/// <summary>
/// The documents Requirements Traceability analyses, resolved from the current workspace through the same role authority the
/// explorers use (<see cref="IArtifactExplorerContext"/>): a selected Sample Project, the current imported project or the manual
/// workspace. A role with several candidates is never resolved here; the user chooses in its explorer. <see cref="Key"/> binds a
/// result to the exact scope and selected artifact revisions: when it changes, an earlier result is no longer current.
/// </summary>
public sealed record TraceabilityInputs(string? Scope, string? ProjectName, IReadOnlyList<TraceabilityRoleInput> Roles)
{
    /// <summary>The roles Requirements Traceability links. Data Model is not part of the document-reference chain.</summary>
    public static readonly IReadOnlyList<WorkspaceArtifactType> TraceabilityRoles =
    [
        WorkspaceArtifactType.Constitution,
        WorkspaceArtifactType.Specification,
        WorkspaceArtifactType.Plan,
        WorkspaceArtifactType.Tasks,
    ];

    public static async Task<TraceabilityInputs> ResolveAsync(IArtifactExplorerContext context, CancellationToken cancellationToken = default)
    {
        var scope = context.CurrentScope;
        var roles = new List<TraceabilityRoleInput>();
        foreach (var role in TraceabilityRoles)
        {
            var state = await context.GetStateAsync(role, cancellationToken);
            var fingerprint = state.Status == ExplorerArtifactStatus.Loaded && !string.IsNullOrEmpty(state.Content) ? ArtifactFingerprint.Compute(state.Content) : null;
            roles.Add(new TraceabilityRoleInput(state, fingerprint));
        }
        return new TraceabilityInputs(scope, roles.Select(r => r.State.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), roles);
    }

    public TraceabilityRoleInput Role(WorkspaceArtifactType role) => Roles.First(r => r.Role == role);

    public TraceabilityArtifactSource Source => ProjectImportScope.IsImport(Scope) ? TraceabilityArtifactSource.ProjectImport
        : Scope is null ? TraceabilityArtifactSource.ManualWorkspace : TraceabilityArtifactSource.SampleProject;

    public string SourceLabel => Source switch
    {
        TraceabilityArtifactSource.ProjectImport => "Project Import",
        TraceabilityArtifactSource.SampleProject => "Sample Project",
        _ => "Manual workspace",
    };

    /// <summary>The project name, or the scope when the name is unknown; null for the manual workspace.</summary>
    public string? ProjectDisplay => ProjectName ?? (ProjectImportScope.IsImport(Scope) ? null : Scope);

    public IReadOnlyList<TraceabilityRoleInput> SelectionRequired => Roles.Where(r => r.NeedsSelection).ToList();

    public int LoadedCount => Roles.Count(r => r.IsLoaded);

    public TraceabilityInputStatus Status =>
        Roles.Any(r => r.NeedsSelection) ? TraceabilityInputStatus.SelectionRequired
        : Roles.Any(r => r.State.Status is ExplorerArtifactStatus.Error or ExplorerArtifactStatus.ProjectUnavailable) ? TraceabilityInputStatus.Error
        : Roles.All(r => r.State.Artifacts.Count == 0) ? TraceabilityInputStatus.NoDocuments
        : LoadedCount < 2 ? TraceabilityInputStatus.Insufficient
        : TraceabilityInputStatus.Ready;

    /// <summary>The scope plus each role's selected artifact id and content fingerprint.</summary>
    public string Key => string.Join("|", new[] { Scope ?? "manual" }.Concat(Roles.Select(r => $"{r.Role}={r.State.Selected?.Id}@{r.Fingerprint}")));
}
