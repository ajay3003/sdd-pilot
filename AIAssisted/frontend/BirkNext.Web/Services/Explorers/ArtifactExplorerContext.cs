using System.Text;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services.Explorers;

/// <summary>Where an explorer artifact comes from. Only metadata: explorers treat every source the same way.</summary>
public enum ExplorerArtifactSource
{
    /// <summary>A document of the selected Sample Project, found by generic discovery and classified by role.</summary>
    SampleProject,
    /// <summary>A document imported into the workspace (file, drop or paste, or a Project Import archive) and kept as an artifact revision.</summary>
    Workspace,
}

public enum ExplorerArtifactCurrentness { Current, Historical, Superseded }

/// <summary>One artifact of a role, as the explorer lists it. The file name is metadata, never the artifact's identity.</summary>
public sealed record ExplorerArtifact(
    string Id,
    WorkspaceArtifactType Role,
    string DisplayName,
    string FileName,
    string? SourcePath,
    ExplorerArtifactSource Source,
    string? Origin,
    int? Revision,
    int RevisionCount,
    string Authority,
    ExplorerArtifactCurrentness Currentness,
    DateTimeOffset? CapturedAt,
    string? DocumentIdentity = null,
    string Module = ArtifactModules.ProjectLevel,
    string? Feature = null)
{
    /// <summary>Approved or Baseline. Explicit repository authority only; never inferred from being latest or first.</summary>
    public bool IsAuthoritative => Authority is "Approved" or "Baseline";

    public string ModuleLabel => ArtifactModules.Label(Module);
}

/// <summary>One module's resolution of a role: how many candidates it has and which one is selected (and why), or none.</summary>
public sealed record ArtifactModuleRoleState(string Module, int Candidates, string? SelectedId, ExplorerSelectionReason? Reason)
{
    public string Label => ArtifactModules.Label(Module);
    public bool NeedsSelection => Candidates > 1 && SelectedId is null;
}

/// <summary>Every candidate of one role in the current scope, with each module's selection. Discovery is not selection.</summary>
public sealed record ArtifactRoleOverview(WorkspaceArtifactType Role, IReadOnlyList<ExplorerArtifact> Candidates, IReadOnlyList<ArtifactModuleRoleState> Modules)
{
    public int ModulesWithCandidates => Modules.Count(m => m.Candidates > 0);
    public int SelectedModules => Modules.Count(m => m.SelectedId is not null);
    public int ModulesNeedingSelection => Modules.Count(m => m.NeedsSelection);
}

/// <summary>
/// All artifact candidates of the current scope by role and module. <see cref="Modules"/> has one entry (project-level) when the scope has no
/// separate modules, so a simple project is summarized exactly as before.
/// </summary>
public sealed record ArtifactModuleOverview(
    string? Scope,
    string? ProjectName,
    string? Focus,
    IReadOnlyList<string> Modules,
    IReadOnlyList<ArtifactRoleOverview> Roles,
    string? Error = null)
{
    public bool HasModules => Modules.Count > 1;
    public ArtifactRoleOverview? Role(WorkspaceArtifactType role) => Roles.FirstOrDefault(r => r.Role == role);
    public static ArtifactModuleOverview Empty(string? scope) => new(scope, null, null, [ArtifactModules.ProjectLevel], []);
}

public enum ExplorerArtifactStatus
{
    /// <summary>No artifact with this role in the current workspace.</summary>
    Empty,
    /// <summary>Several artifacts with this role and nothing decides between them: the user chooses.</summary>
    SelectionRequired,
    Loaded,
    /// <summary>The selected project is no longer in the Sample Projects catalog.</summary>
    ProjectUnavailable,
    /// <summary>The workspace's documents could not be read. Not the same as "no artifact".</summary>
    Error,
}

public enum ExplorerSelectionReason
{
    /// <summary>Chosen by the user: in an explorer, on Sample Projects, or by importing it.</summary>
    Explicit,
    /// <summary>The only Approved/Baseline artifact of the role.</summary>
    Authoritative,
    /// <summary>The only artifact of the role.</summary>
    OnlyArtifact,
}

public sealed record ArtifactExplorerState(
    WorkspaceArtifactType Role,
    ExplorerArtifactStatus Status,
    string? ProjectSlug,
    string? ProjectName,
    IReadOnlyList<ExplorerArtifact> Artifacts,
    ExplorerArtifact? Selected = null,
    ExplorerSelectionReason? Reason = null,
    string? Content = null,
    string? Error = null,
    IReadOnlyList<ArtifactModuleRoleState>? Modules = null,
    string? Module = null)
{
    public bool HasProject => ProjectSlug is not null;

    /// <summary>The scope has several modules (Spec-Kit sub-projects): this role resolves per module, never across the whole project.</summary>
    public bool HasModules => Modules is { Count: > 1 };

    /// <summary>Several modules and none chosen to focus on: the user chooses a module first. Nothing is picked automatically.</summary>
    public bool ModuleRequired => HasModules && Module is null;

    public string? ModuleLabel => HasModules && Module is not null ? ArtifactModules.Label(Module) : null;
}

public sealed record ArtifactImportRequest(WorkspaceArtifactType Role, string Content, string? FileName, string Origin);

/// <summary>Result of an import. <see cref="SuggestedRole"/> is a warning only: the role the user chose is kept.</summary>
public sealed record ArtifactImportResult(string? ArtifactId, string? Error, WorkspaceArtifactType? SuggestedRole)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// What every document explorer reads: the artifacts of one ROLE in the current workspace, from whichever source they came.
/// Explorers never look up files by name and do not depend on a Sample Project being selected.
/// </summary>
public interface IArtifactExplorerContext
{
    /// <summary>Raised when artifacts, selection, authority or the selected project change, and after a reset.</summary>
    event EventHandler? Changed;

    /// <summary>The workspace scope: the selected Sample Project slug, the current imported project (<c>import:{id}</c>), or null for the manual workspace.</summary>
    string? CurrentScope { get; }

    Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chooses which artifact the explorers show for the role in <paramref name="module"/> (default: the focused module, or project-level).
    /// Not authority: approval and baselines do not change. A selection for one module never changes another module's selection.
    /// </summary>
    void Select(WorkspaceArtifactType role, string artifactId, string? module = null);

    /// <summary>The module that explorers and reviews focus on when the scope has several modules; null clears the focus.</summary>
    Task SelectModuleAsync(string? module, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Every candidate of every role, grouped by module, with each module's selection.</summary>
    Task<ArtifactModuleOverview> GetModuleOverviewAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ArtifactModuleOverview.Empty(CurrentScope));

    /// <summary>Adds a file, dropped file or pasted text to the workspace as an artifact revision of the role, and shows it.</summary>
    ArtifactImportResult Import(ArtifactImportRequest request);
}

/// <summary>
/// The workspace has two kinds of artifacts. A selected Sample Project's documents are found by generic discovery and resolved
/// on demand (they are never copied). Imported documents are artifact revisions in the workspace repository's lifecycle,
/// scoped to the project they were imported into, or to the manual workspace when no project is selected. Both are listed
/// together by role. Another project's imports never appear.
///
/// The artifact shown for a role is chosen in this order:
/// 1. explicit: the explorer selection, the Sample Projects choice, or the repository's current selection;
/// 2. the only authoritative (Approved/Baseline) artifact;
/// 3. the only artifact.
/// Otherwise the user must choose. It is never the first file, the latest file or a merge of several.
/// </summary>
public sealed class ArtifactExplorerContext : IArtifactExplorerContext, IDisposable
{
    /// <summary>Same limit as the file importer.</summary>
    public const int MaxImportBytes = 1_048_576;
    public const string SamplePrefix = "sample:";
    public const string WorkspacePrefix = "workspace:";

    private readonly IWorkspaceSessionService _workspace;
    private readonly ISampleProjectDocumentResolver _resolver;
    private readonly ISampleProjectArtifactDiscovery _discovery;
    private readonly IWorkspaceUpdateCoordinator? _updates;
    private readonly IWorkspaceStateManager? _stateManager;
    // Title and content identity per document, computed once: every role is listed on every read (modules span roles), and hashing every
    // document of a large monorepo on each read stalls the WebAssembly UI thread. Keys include the content identity, so a change re-computes.
    private readonly Dictionary<(Guid RevisionId, string Fingerprint, int Length, string FileName), (string DisplayName, string Identity)> _revisionMeta = new();
    private readonly Dictionary<(string Slug, string Path), (string Content, string DisplayName, string Identity)> _sampleMeta = new();
    private readonly object _metaGate = new();
    /// <summary>While a module switch mirrors several roles, the repository's per-role events are coalesced into one Changed.</summary>
    private int _coalescing;

    public event EventHandler? Changed;

    public ArtifactExplorerContext(
        IWorkspaceSessionService workspace,
        ISampleProjectDocumentResolver resolver,
        ISampleProjectArtifactDiscovery discovery,
        IWorkspaceUpdateCoordinator? updates = null,
        IWorkspaceStateManager? stateManager = null)
    {
        _workspace = workspace;
        _resolver = resolver;
        _discovery = discovery;
        _updates = updates;
        _stateManager = stateManager;
        _workspace.ReviewContextRebuildNeeded += OnRepositoryChanged;
        if (_workspace is WorkspaceArtifactRepository repository) repository.ProjectSelectionChanged += OnRepositoryChanged;
        // A saved workspace resumed or cleared replaces the lifecycle without an artifact event.
        if (_stateManager is not null) _stateManager.WorkspaceChanged += OnWorkspaceChanged;
    }

    /// <summary>A selected Sample Project, else the current imported project, else the manual workspace (null). The two projects are mutually exclusive.</summary>
    public string? CurrentScope => _resolver.GetSelectedProject() is { Length: > 0 } slug && !string.IsNullOrWhiteSpace(slug)
        ? slug
        : ProjectImportScope.For(_workspace.SddLifecycle.CurrentProjectImportId);

    public async Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default)
    {
        var model = await LoadScopeAsync(cancellationToken);
        if (model.Failure is { } failure)
            return new ArtifactExplorerState(role, failure, model.Scope, model.ProjectName, [], Error: model.Error);

        var all = model.Roles[role];
        if (!model.HasModules)
            return Build(role, model, all, ArtifactModules.ProjectLevel, anyModule: true, modules: null, focus: null);

        // Several modules: a role resolves within one module, never across the whole project.
        var modules = model.Modules.Select(m => ModuleState(role, model, m)).ToList();
        var focus = FocusOf(model);
        if (focus is null)
        {
            var listed = all.Select(e => e.Artifact).ToList();
            return new ArtifactExplorerState(role, listed.Count == 0 ? ExplorerArtifactStatus.Empty : ExplorerArtifactStatus.SelectionRequired,
                model.Scope, model.ProjectName, listed, Modules: modules);
        }
        return Build(role, model, all.Where(e => e.Artifact.Module == focus).ToList(), focus, anyModule: false, modules, focus);
    }

    public async Task<ArtifactModuleOverview> GetModuleOverviewAsync(CancellationToken cancellationToken = default)
    {
        var model = await LoadScopeAsync(cancellationToken);
        if (model.Failure is not null)
            return ArtifactModuleOverview.Empty(model.Scope) with { ProjectName = model.ProjectName, Error = model.Error ?? model.Failure.ToString() };
        var modules = model.HasModules ? model.Modules : [ArtifactModules.ProjectLevel];
        var roles = SampleArtifactClassifier.RoleOrder.Select(role => new ArtifactRoleOverview(role,
            model.Roles[role].Select(e => e.Artifact).ToList(),
            modules.Select(m => ModuleState(role, model, m)).ToList())).ToList();
        return new ArtifactModuleOverview(model.Scope, model.ProjectName, model.HasModules ? FocusOf(model) : null, modules, roles);
    }

    public void Select(WorkspaceArtifactType role, string artifactId, string? module = null)
    {
        var scope = CurrentScope;
        var focus = StoredFocus(scope);
        var key = module ?? focus ?? ArtifactModules.ProjectLevel;
        var selections = _workspace.SddLifecycle.ExplorerSelections;
        selections.RemoveAll(s => s.WorkspaceScope == scope && s.Role == role.ToString() && (s.Module ?? ArtifactModules.ProjectLevel) == key);
        selections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = role.ToString(), ArtifactId = artifactId, Module = key, SelectedAt = DateTimeOffset.UtcNow });

        // Only the focused module (or a project without modules) is mirrored into the repository's current selection and the Sample
        // Project document choice, which older consumers read. Another module's selection is kept but changes nothing they see.
        if (module is null || module == focus)
        {
            Interlocked.Increment(ref _coalescing);
            try { MirrorSelection(scope, role, artifactId); }
            finally { Interlocked.Decrement(ref _coalescing); }
        }

        _updates?.NotifyMutation();
        OnRepositoryChanged(this, EventArgs.Empty);
    }

    public async Task SelectModuleAsync(string? module, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        var focus = _workspace.SddLifecycle.ModuleFocus;
        focus.RemoveAll(f => f.WorkspaceScope == scope);
        if (module is not null) focus.Add(new SddModuleFocus { WorkspaceScope = scope, Module = module });

        // Mirror the focused module's resolution into the repository's current selection (session artifacts) and the Sample Project
        // choice, so older consumers read the same module. A role the module does not resolve has no current artifact.
        Interlocked.Increment(ref _coalescing);
        try
        {
            foreach (var role in SampleArtifactClassifier.RoleOrder)
            {
                var state = await GetStateAsync(role, cancellationToken);
                if (state is { Status: ExplorerArtifactStatus.Loaded, Selected: { } selected }) MirrorSelection(scope, role, selected.Id);
                else if (ProjectImportScope.IsImport(scope)) _workspace.Clear(role);
                else if (scope is not null) _discovery.ChooseDocument(scope, role, null);
            }
        }
        finally { Interlocked.Decrement(ref _coalescing); }

        _updates?.NotifyMutation();
        OnRepositoryChanged(this, EventArgs.Empty);
    }

    private void MirrorSelection(string? scope, WorkspaceArtifactType role, string artifactId)
    {
        if (artifactId.StartsWith(SamplePrefix, StringComparison.Ordinal) && scope is not null)
        {
            // Keep the analysis pages that resolve Sample Project documents on the same document as the explorer.
            _discovery.ChooseDocument(scope, role, artifactId[SamplePrefix.Length..]);
        }
        else if (WorkspaceArtifacts(role, scope).FirstOrDefault(i => i.Artifact.Id == artifactId) is { } imported)
        {
            _workspace.SelectRevision(imported.Revision.RevisionId);
        }
    }

    private ArtifactExplorerState Build(WorkspaceArtifactType role, ScopeModel model, List<ScopeEntry> entries, string module, bool anyModule,
        IReadOnlyList<ArtifactModuleRoleState>? modules, string? focus)
    {
        var listed = entries.Select(e => e.Artifact).ToList();
        if (listed.Count == 0)
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty, model.Scope, model.ProjectName, listed, Modules: modules, Module: focus);

        var (selectedId, reason) = Resolve(role, model, entries, module, anyModule);
        if (selectedId is null)
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.SelectionRequired, model.Scope, model.ProjectName, listed, Modules: modules, Module: focus);

        var selected = entries.First(a => a.Artifact.Id == selectedId);
        var text = selected.Content();
        if (string.IsNullOrEmpty(text))
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.Error, model.Scope, model.ProjectName, listed, selected.Artifact, reason,
                Error: $"{selected.Artifact.SourcePath ?? selected.Artifact.FileName} could not be read.", Modules: modules, Module: focus);

        return new ArtifactExplorerState(role, ExplorerArtifactStatus.Loaded, model.Scope, model.ProjectName, listed, selected.Artifact, reason, text,
            Modules: modules, Module: focus);
    }

    private ArtifactModuleRoleState ModuleState(WorkspaceArtifactType role, ScopeModel model, string module)
    {
        var entries = model.HasModules ? model.Roles[role].Where(e => e.Artifact.Module == module).ToList() : model.Roles[role];
        if (entries.Count == 0) return new ArtifactModuleRoleState(module, 0, null, null);
        var (id, reason) = Resolve(role, model, entries, module, anyModule: !model.HasModules);
        return new ArtifactModuleRoleState(module, entries.Count, id, reason);
    }

    private string? StoredFocus(string? scope) => _workspace.SddLifecycle.ModuleFocus.LastOrDefault(f => f.WorkspaceScope == scope)?.Module;

    /// <summary>The focused module, if it still exists in the scope.</summary>
    private string? FocusOf(ScopeModel model) => StoredFocus(model.Scope) is { } focus && model.Modules.Contains(focus, StringComparer.Ordinal) ? focus : null;

    private sealed record ScopeEntry(ExplorerArtifact Artifact, Func<string?> Content, bool IsRepositoryCurrent);

    private sealed record ScopeModel(
        string? Scope,
        string? ProjectName,
        IReadOnlyDictionary<WorkspaceArtifactType, List<ScopeEntry>> Roles,
        IReadOnlyDictionary<WorkspaceArtifactType, string?> ChosenSamplePaths,
        IReadOnlyList<string> Modules,
        ExplorerArtifactStatus? Failure = null,
        string? Error = null)
    {
        /// <summary>Artifacts in more than one module group: roles resolve per module.</summary>
        public bool HasModules => Modules.Count > 1;
    }

    /// <summary>
    /// Every artifact of every role in the current scope, each with its module (from the Spec-Kit roots that the scope's document paths
    /// evidence) and feature folder. Module identity is structural only; a document under no root is project-level.
    /// </summary>
    private async Task<ScopeModel> LoadScopeAsync(CancellationToken cancellationToken)
    {
        var scope = CurrentScope;
        var roles = SampleArtifactClassifier.RoleOrder.ToDictionary(r => r, _ => new List<ScopeEntry>());
        var chosen = new Dictionary<WorkspaceArtifactType, string?>();
        var paths = new List<string?>();
        string? projectName = null;
        IReadOnlyDictionary<WorkspaceArtifactType, List<ScopeEntry>> NoRoles() => SampleArtifactClassifier.RoleOrder.ToDictionary(r => r, _ => new List<ScopeEntry>());

        if (ProjectImportScope.IsImport(scope))
        {
            // An imported project: its documents are revisions in its own scope (listed below); there is no catalog to discover.
            var importId = ProjectImportScope.ImportIdOf(scope);
            projectName = _workspace.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId)?.ProjectName ?? importId;
        }
        else if (scope is not null)
        {
            var namesTask = ProjectNameAsync(scope, cancellationToken);
            SampleProjectDiscoveryResult? discovery;
            try
            {
                discovery = await _discovery.DiscoverAsync(scope, cancellationToken);
            }
            catch (Exception ex)
            {
                return new ScopeModel(scope, await namesTask, NoRoles(), chosen, [], ExplorerArtifactStatus.Error,
                    $"The documents of project '{scope}' could not be read: {ex.Message}");
            }
            projectName = await namesTask;
            if (discovery is null)
                return new ScopeModel(scope, projectName, NoRoles(), chosen, [], ExplorerArtifactStatus.ProjectUnavailable);
            if (discovery.Error is not null && discovery.Documents.Count == 0)
                return new ScopeModel(scope, projectName, NoRoles(), chosen, [], ExplorerArtifactStatus.Error, discovery.Error);

            foreach (var role in SampleArtifactClassifier.RoleOrder)
            {
                var summary = discovery.Role(role);
                chosen[role] = summary.ChosenPath;
                foreach (var document in summary.Documents)
                {
                    var content = _discovery.GetContent(discovery.ProjectSlug, document.RelativePath);
                    var (displayName, identity) = SampleMeta(discovery.ProjectSlug, document.RelativePath, document.FileName, content);
                    roles[role].Add(new ScopeEntry(new ExplorerArtifact(
                        SamplePrefix + document.RelativePath, role, displayName, document.FileName,
                        document.RelativePath, ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown",
                        ExplorerArtifactCurrentness.Current, null, identity), () => content, false));
                }
            }
        }

        foreach (var role in SampleArtifactClassifier.RoleOrder)
            roles[role].AddRange(WorkspaceArtifacts(role, scope).Select(i => new ScopeEntry(i.Artifact, () => i.Revision.Content, i.Revision.IsCurrentSelection)));
        // Module roots come from the artifacts' own paths (every role, every revision of the scope) — the same evidence before and after an
        // import and for Demo Projects. A stray specs/ folder among unclassified documents does not create a module.
        paths.AddRange(roles.Values.SelectMany(l => l).Select(e => e.Artifact.SourcePath ?? e.Artifact.FileName));
        paths.AddRange(_workspace.SddLifecycle.Revisions.Where(r => r.WorkspaceScope == scope).Select(r => r.SourceReference ?? r.FileName));

        var roots = ArtifactModules.RootsOf(paths);
        foreach (var role in SampleArtifactClassifier.RoleOrder)
            roles[role] = roles[role].Select(e =>
            {
                var path = e.Artifact.SourcePath ?? e.Artifact.FileName;
                return e with { Artifact = e.Artifact with { Module = ArtifactModules.ModuleOf(path, roots), Feature = ArtifactModules.FeatureOf(path) } };
            }).ToList();

        // Named modules in path order; the project-level group (if any) last.
        var modules = roles.Values.SelectMany(l => l).Select(e => e.Artifact.Module).Distinct(StringComparer.Ordinal)
            .OrderBy(m => m.Length == 0 ? 1 : 0).ThenBy(m => m, StringComparer.Ordinal).ToList();
        return new ScopeModel(scope, projectName, roles, chosen, modules);
    }

    public ArtifactImportResult Import(ArtifactImportRequest request)
    {
        var content = Normalize(request.Content ?? "");
        if (string.IsNullOrWhiteSpace(content))
            return new ArtifactImportResult(null, "The document is empty. Add text before importing it.", null);
        if (Encoding.UTF8.GetByteCount(content) > MaxImportBytes)
            return new ArtifactImportResult(null, "The document is larger than 1 MB and cannot be imported.", null);
        if (content.Contains('\0'))
            return new ArtifactImportResult(null, "Binary content was detected. Only plain-text Markdown or text documents can be imported.", null);

        var scope = CurrentScope;
        var fileName = SafeFileName(request.FileName) ?? $"pasted-{ArtifactExplorerRoles.Label(request.Role).ToLowerInvariant().Replace(' ', '-')}.md";
        var revision = _workspace.AddArtifactRevision(request.Role, content, fileName, null, scope, request.Origin, select: true);
        if (revision is null)
            return new ArtifactImportResult(null, "The document could not be added to the workspace.", null);

        var artifactId = WorkspacePrefix + revision.FileName;
        var selections = _workspace.SddLifecycle.ExplorerSelections;
        selections.RemoveAll(s => s.WorkspaceScope == scope && s.Role == request.Role.ToString());
        selections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = request.Role.ToString(), ArtifactId = artifactId });

        // The chosen role is kept. A strong classification as another role is reported, never applied silently.
        var classification = SampleArtifactClassifier.Classify(fileName, content);
        WorkspaceArtifactType? suggested = classification.Status == ArtifactDiscoveryStatus.Detected && classification.Role is { } detected && detected != request.Role
            ? detected
            : null;

        _updates?.NotifyMutation();
        OnRepositoryChanged(this, EventArgs.Empty);
        return new ArtifactImportResult(artifactId, null, suggested);
    }

    public void Dispose()
    {
        _workspace.ReviewContextRebuildNeeded -= OnRepositoryChanged;
        if (_workspace is WorkspaceArtifactRepository repository) repository.ProjectSelectionChanged -= OnRepositoryChanged;
        if (_stateManager is not null) _stateManager.WorkspaceChanged -= OnWorkspaceChanged;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        if (sender != this && Volatile.Read(ref _coalescing) > 0) return; // raised once by the caller when it is done
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnWorkspaceChanged(Guid? workspaceId) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The artifact a role resolves to among <paramref name="entries"/> (one module's candidates, or all of a project without modules):
    /// explicit selection for the module → the Demo Project choice and the repository's current selection (projects without modules only;
    /// with modules they mirror the focused module) → the only authoritative artifact → the only artifact. Otherwise none.
    /// </summary>
    private (string? Id, ExplorerSelectionReason? Reason) Resolve(
        WorkspaceArtifactType role, ScopeModel model, IReadOnlyList<ScopeEntry> entries, string module, bool anyModule)
    {
        bool Exists(string? id) => id is not null && entries.Any(a => a.Artifact.Id == id);

        // Without modules every stored selection of the role counts (older selections have no module).
        var stored = _workspace.SddLifecycle.ExplorerSelections
            .LastOrDefault(s => s.WorkspaceScope == model.Scope && s.Role == role.ToString()
                && (anyModule || (s.Module ?? ArtifactModules.ProjectLevel) == module) && Exists(s.ArtifactId))?.ArtifactId;
        if (stored is not null) return (stored, ExplorerSelectionReason.Explicit);

        // The Demo Project document choice and the repository's current selection are project-wide. With modules they only mirror the
        // focused module's resolution, so they are not read back as a choice.
        if (anyModule)
        {
            var chosenSamplePath = model.ChosenSamplePaths.GetValueOrDefault(role);
            var chosenSample = chosenSamplePath is null ? null : SamplePrefix + chosenSamplePath;
            if (Exists(chosenSample)) return (chosenSample, ExplorerSelectionReason.Explicit);

            var repositoryCurrent = entries.Where(a => a.IsRepositoryCurrent).Select(a => a.Artifact.Id).ToList();
            if (repositoryCurrent.Count == 1) return (repositoryCurrent[0], ExplorerSelectionReason.Explicit);
        }

        var authoritative = entries.Where(a => a.Artifact.IsAuthoritative).Select(a => a.Artifact.Id).ToList();
        if (authoritative.Count == 1) return (authoritative[0], ExplorerSelectionReason.Authoritative);

        return entries.Count == 1 ? (entries[0].Artifact.Id, ExplorerSelectionReason.OnlyArtifact) : (null, null);
    }

    /// <summary>
    /// Imported artifacts of the role in this scope. Revisions with the same file name are one document's history: the
    /// document shows its current selection, otherwise its newest revision that is not superseded.
    /// </summary>
    private List<(ExplorerArtifact Artifact, SddArtifactRevision Revision)> WorkspaceArtifacts(WorkspaceArtifactType role, string? scope) =>
        _workspace.SddLifecycle.Revisions
            .Where(r => r.Role == role.ToString() && r.WorkspaceScope == scope && !string.IsNullOrWhiteSpace(r.Content))
            .GroupBy(r => r.FileName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var revision = g.FirstOrDefault(r => r.IsCurrentSelection)
                               ?? g.Where(r => CurrentnessOf(r) == ExplorerArtifactCurrentness.Current).MaxBy(r => r.Revision)
                               ?? g.MaxBy(r => r.Revision)!;
                var (displayName, identity) = RevisionMeta(revision, g.Key);
                var artifact = new ExplorerArtifact(
                    WorkspacePrefix + g.Key, role, displayName, g.Key, revision.SourceReference,
                    ExplorerArtifactSource.Workspace, revision.Origin, revision.Revision, g.Count(),
                    string.IsNullOrWhiteSpace(revision.Authority) ? "Unknown" : revision.Authority, CurrentnessOf(revision), revision.CapturedAt,
                    identity);
                return (artifact, revision);
            })
            .ToList();

    private (string DisplayName, string Identity) RevisionMeta(SddArtifactRevision revision, string fileName)
    {
        var key = (revision.RevisionId, revision.Fingerprint ?? "", revision.Content?.Length ?? 0, fileName);
        lock (_metaGate)
        {
            if (_revisionMeta.TryGetValue(key, out var cached)) return cached;
        }
        var meta = (DisplayNameOf(revision.Content, fileName), MarkdownTokenizer.DocumentFingerprint(revision.Content));
        lock (_metaGate) _revisionMeta[key] = meta;
        return meta;
    }

    private (string DisplayName, string? Identity) SampleMeta(string slug, string path, string fileName, string? content)
    {
        if (content is null) return (DisplayNameOf(null, fileName), null);
        lock (_metaGate)
        {
            if (_sampleMeta.TryGetValue((slug, path), out var cached) && ReferenceEquals(cached.Content, content)) return (cached.DisplayName, cached.Identity);
        }
        var meta = (Content: content, DisplayName: DisplayNameOf(content, fileName), Identity: MarkdownTokenizer.DocumentFingerprint(content));
        lock (_metaGate) _sampleMeta[(slug, path)] = meta;
        return (meta.DisplayName, meta.Identity);
    }

    private static ExplorerArtifactCurrentness CurrentnessOf(SddArtifactRevision revision) =>
        revision.Authority == "Superseded" || revision.SupersededByRevisionId is not null ? ExplorerArtifactCurrentness.Superseded
        : revision.Authority is "Historical" or "RejectedAlternative" ? ExplorerArtifactCurrentness.Historical
        : ExplorerArtifactCurrentness.Current;

    private async Task<string?> ProjectNameAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _resolver.GetAvailableProjectsAsync(cancellationToken);
            return projects.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase))?.Name ?? slug;
        }
        catch
        {
            return slug;
        }
    }

    /// <summary>
    /// The document's first level-1 heading, else the file name. Front matter, HTML comments (such as Spec Kit's sync
    /// impact report) and fenced code are skipped.
    /// </summary>
    public static string DisplayNameOf(string? content, string fileName)
    {
        if (!string.IsNullOrEmpty(content))
        {
            var inFrontMatter = false;
            var inComment = false;
            var inFence = false;
            var lineNumber = 0;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (lineNumber++ == 0 && line == "---") { inFrontMatter = true; continue; }
                if (inFrontMatter) { if (line == "---") inFrontMatter = false; continue; }
                if (inComment) { if (line.Contains("-->", StringComparison.Ordinal)) inComment = false; continue; }
                if (line.StartsWith("<!--", StringComparison.Ordinal)) { inComment = !line.Contains("-->", StringComparison.Ordinal); continue; }
                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { inFence = !inFence; continue; }
                if (inFence) continue;
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    var title = line[2..].Trim().Trim('#').Trim();
                    if (title.Length > 0) return title.Length > 120 ? title[..117] + "…" : title;
                }
            }
        }
        return fileName;
    }

    private static string Normalize(string text)
    {
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>A display file name only: the last path segment, without control or path characters.</summary>
    private static string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim();
        if (name.Length == 0) return null;
        return name.Length > 200 ? name[..200] : name;
    }
}
