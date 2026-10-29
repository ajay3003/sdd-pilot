using System.Text;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
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
    string? Feature = null,
    string? EvidenceSource = null,
    string? EvidenceImportId = null,
    DateTimeOffset? EvidenceImportedAt = null)
{
    /// <summary>Approved or Baseline. Explicit repository authority only; never inferred from being latest or first.</summary>
    public bool IsAuthoritative => Authority is "Approved" or "Baseline";

    public string ModuleLabel => ArtifactModules.Label(Module);
}

/// <summary>One module's resolution of a role: how many candidates it has and which one is selected (and why), or none.</summary>
public sealed record ArtifactModuleRoleState(string Module, int Candidates, string? SelectedId, ExplorerSelectionReason? Reason,
    int Packs = 0, bool AmbiguousInPack = false)
{
    public string Label => ArtifactModules.Label(Module);
    /// <summary>Several artifacts that nothing tells apart. Artifacts organised in separate packs are never a selection problem: only
    /// two artifacts of the role in the same pack are.</summary>
    public bool NeedsSelection => Packs > 1 ? AmbiguousInPack : Candidates > 1 && SelectedId is null;

    /// <summary>Every pack (or the module itself) resolves to one artifact of the role: selected, the only one, or one per pack.</summary>
    public bool IsResolved => Packs > 1 ? !AmbiguousInPack : SelectedId is not null;
}

/// <summary>Every candidate of one role in the current scope, with each module's selection. Discovery is not selection.</summary>
public sealed record ArtifactRoleOverview(WorkspaceArtifactType Role, IReadOnlyList<ExplorerArtifact> Candidates, IReadOnlyList<ArtifactModuleRoleState> Modules)
{
    public int ModulesWithCandidates => Modules.Count(m => m.Candidates > 0);
    public int SelectedModules => Modules.Count(m => m.Candidates > 0 && m.IsResolved);
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
    /// <summary>The viewed pack's own artifact of the role (one per role per pack).</summary>
    PackArtifact,
    /// <summary>The viewed pack has no artifact of the role; the module's (or project's) shared artifact applies.</summary>
    SharedArtifact,
    /// <summary>Several candidates; the role's Spec-Kit file (e.g. <c>specs/002-x/spec.md</c>, <c>.specify/memory/constitution.md</c>) is the artifact.</summary>
    PackFile,
}

/// <summary>One imported package whose documents the explorers read (provenance; the import id is the canonical identity).</summary>
public sealed record ExplorerEvidenceSource(string ImportId, string Name, string ArchiveFileName, DateTimeOffset ImportedAt, bool IsMainProject);

/// <summary>
/// Which project evidence the explorers read and on what basis. <see cref="FromEnvironment"/>: the active Target Environment's Project bindings
/// (one logical project, several packages). Otherwise the current project/import (legacy fallback; no environment binding is claimed).
/// <see cref="DocumentationUnresolved"/> = the project documentation role has no available binding: a source decision, not "no artifact".
/// </summary>
public sealed record ExplorerProjectEvidence(
    bool FromEnvironment,
    string? EnvironmentId,
    string? EnvironmentName,
    string ProjectName,
    IReadOnlyList<ExplorerEvidenceSource> Sources,
    bool DocumentationUnresolved,
    IReadOnlyList<string> UnboundDocumentPackages)
{
    public int SourceCount => Sources.Count;
    public string SourceNames => string.Join(", ", Sources.Select(s => s.ArchiveFileName));
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
    string? Module = null,
    IReadOnlyList<ArtifactPackSummary>? Packs = null,
    string? Pack = null,
    bool PackRequired = false,
    bool AmbiguousInPack = false,
    string? Resolution = null,
    ExplorerProjectEvidence? Evidence = null)
{
    public bool HasProject => ProjectSlug is not null;

    /// <summary>The project documentation source is not selected (Target Environment → Project): show the binding action, not "no artifact".</summary>
    public bool SourceUnresolved => Status == ExplorerArtifactStatus.Empty && Evidence is { DocumentationUnresolved: true };

    /// <summary>The module has several feature packs: explorers and reviews read one pack (plus shared artifacts), never siblings.</summary>
    public bool HasPacks => Packs is { Count: > 1 };

    /// <summary>The viewed pack when the module has several (a single pack is simply the module's documents).</summary>
    public ArtifactPackSummary? PackSummary => !HasPacks || Pack is null ? null : Packs!.FirstOrDefault(p => p.Pack == Pack);

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

    /// <summary>
    /// The role as <paramref name="pack"/> of the focused module resolves it (its own artifact, else the shared one), without changing what is
    /// viewed. Used to review every pack independently; never a sibling pack's artifact.
    /// </summary>
    Task<ArtifactExplorerState> GetPackStateAsync(WorkspaceArtifactType role, string pack, CancellationToken cancellationToken = default) => GetStateAsync(role, cancellationToken);

    /// <summary>
    /// The module-shared (else project-shared) artifact of the role that applies to the viewed pack, as if the pack had none of its own — the
    /// "shared base" under a pack-local artifact (e.g. a base data model). Null when no single shared artifact applies. Changes nothing viewed.
    /// </summary>
    Task<ArtifactExplorerState?> GetSharedBaseAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default) => Task.FromResult<ArtifactExplorerState?>(null);

    /// <summary>Views a feature pack of the module (default: the focused module). Navigation only; nothing becomes authoritative.</summary>
    Task SelectPackAsync(string? pack, string? module = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Every candidate of every role, grouped by module, with each module's selection.</summary>
    Task<ArtifactModuleOverview> GetModuleOverviewAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ArtifactModuleOverview.Empty(CurrentScope));

    /// <summary>Adds a file, dropped file or pasted text to the workspace as an artifact revision of the role, and shows it.</summary>
    ArtifactImportResult Import(ArtifactImportRequest request);

    /// <summary>
    /// The canonical document inventory of the viewed Artifact Pack (or of <paramref name="pack"/> of the focused module): primary, shared,
    /// supporting and generated documents with provenance and inclusion reasons, plus what was left out and why. Reads only; changes nothing viewed.
    /// </summary>
    Task<ArtifactPackInventory> GetPackInventoryAsync(string? pack = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(ArtifactPackInventory.Empty(CurrentScope));
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
        IWorkspaceStateManager? stateManager = null,
        IFrontendAnalysisSettingsService? environments = null)
    {
        _workspace = workspace;
        _resolver = resolver;
        _discovery = discovery;
        _updates = updates;
        _stateManager = stateManager;
        _environments = environments;
        _workspace.ReviewContextRebuildNeeded += OnRepositoryChanged;
        if (_workspace is WorkspaceArtifactRepository repository) repository.ProjectSelectionChanged += OnRepositoryChanged;
        // A saved workspace resumed or cleared replaces the lifecycle without an artifact event.
        if (_stateManager is not null) _stateManager.WorkspaceChanged += OnWorkspaceChanged;
        // A Target Environment saved or switched can change the project and its bound evidence.
        if (_environments is not null) _environments.Changed += OnEnvironmentsChanged;
    }

    private readonly IFrontendAnalysisSettingsService? _environments;

    /// <summary>The active Target Environment's Project, when its main import is recorded in this workspace; otherwise null (legacy fallback).</summary>
    private (FrontendAnalysisProfile Profile, EnvironmentProject Project, SddProjectImportRecord Main)? EnvironmentProject()
    {
        if (_environments?.ActiveProfile is not { Project: { } project } profile) return null;
        return ProjectEvidenceResolver.MainImport(project, _workspace.SddLifecycle) is { } main ? (profile, project, main) : null;
    }

    /// <summary>
    /// A selected Sample Project; else the active Target Environment's Project (its main import); else the current imported project; else the
    /// manual workspace (null). The environment's Project wins over the last imported/selected package, so a source-only current package never
    /// empties the explorers; it never changes the user's current project.
    /// </summary>
    public string? CurrentScope => _resolver.GetSelectedProject() is { Length: > 0 } slug && !string.IsNullOrWhiteSpace(slug)
        ? slug
        : EnvironmentProject() is { } bound ? ProjectImportScope.For(bound.Main.ImportId)
        : ProjectImportScope.For(_workspace.SddLifecycle.CurrentProjectImportId);

    /// <summary>One artifact-revision scope the explorers read, with its provenance. Only the primary scope's id format is unprefixed.</summary>
    private sealed record EvidenceScope(string? Scope, ExplorerEvidenceSource? Source, bool Primary, bool ManualOnly);

    /// <summary>
    /// The scopes whose revisions make up <paramref name="scope"/>'s documents. With a Target Environment Project: the documentation role's bound
    /// imports (aggregated, binding order), plus documents added by hand to the project scope. Otherwise just the scope itself.
    /// </summary>
    private IReadOnlyList<EvidenceScope> EvidenceScopes(string? scope)
    {
        if (!ProjectImportScope.IsImport(scope) || EnvironmentProject() is not { } bound || ProjectImportScope.For(bound.Main.ImportId) != scope)
            return [new EvidenceScope(scope, null, true, false)];
        var lifecycle = _workspace.SddLifecycle;
        var documentation = ProjectEvidenceResolver.DocumentationImports(bound.Project, lifecycle);
        var scopes = new List<EvidenceScope>
        {
            // The main project's own imported documents count only when bound as documentation; documents added in an explorer always do.
            new(scope, Source(bound.Main, true), true, !documentation.Contains(bound.Main.ImportId, StringComparer.Ordinal)),
        };
        foreach (var importId in documentation.Where(id => id != bound.Main.ImportId))
            if (lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is { } record)
                scopes.Add(new(ProjectImportScope.For(importId), Source(record, false), false, false));
        return scopes;
    }

    private static ExplorerEvidenceSource Source(SddProjectImportRecord record, bool main) =>
        new(record.ImportId, string.IsNullOrWhiteSpace(record.ProjectName) ? record.ArchiveFileName : record.ProjectName, record.ArchiveFileName, record.ImportedAt, main);

    /// <summary>The project-evidence basis the explorers read, or null for a Sample Project or the manual workspace.</summary>
    private ExplorerProjectEvidence? EvidenceOf(string? scope)
    {
        if (!ProjectImportScope.IsImport(scope)) return null;
        var lifecycle = _workspace.SddLifecycle;
        var scopes = EvidenceScopes(scope);
        var read = scopes.Where(s => !s.ManualOnly).Select(s => ProjectImportScope.ImportIdOf(s.Scope)).ToHashSet(StringComparer.Ordinal);
        // Imports outside the evidence set that hold authored documents: "available in another import" is never "not provided".
        var unbound = lifecycle.ProjectImports.Where(i => !read.Contains(i.ImportId)
                && lifecycle.Revisions.Any(r => r.WorkspaceScope == ProjectImportScope.For(i.ImportId) && ProjectEvidencePackages.DocumentRoleOrder.Any(role => r.Role == role.ToString())))
            .Select(i => i.ArchiveFileName).Distinct(StringComparer.Ordinal).ToList();
        if (EnvironmentProject() is { } bound && ProjectImportScope.For(bound.Main.ImportId) == scope)
        {
            var sources = scopes.Where(s => !s.ManualOnly && s.Source is not null).Select(s => s.Source!).ToList();
            var role = bound.Project.Evidence.FirstOrDefault(r => r.RoleKey == ProjectEvidenceRoles.Documentation);
            return new ExplorerProjectEvidence(true, bound.Profile.Id, bound.Profile.Name, bound.Project.ProjectName, sources,
                DocumentationUnresolved: role is not null && sources.Count == 0, unbound);
        }
        var importId = ProjectImportScope.ImportIdOf(scope);
        var record = lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId);
        return new ExplorerProjectEvidence(false, _environments?.ActiveProfile?.Id, _environments?.ActiveProfile?.Name, record?.ProjectName ?? importId ?? "",
            record is null ? [] : [Source(record, true)], false, unbound);
    }

    public Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default) =>
        StateAsync(role, null, cancellationToken);

    public Task<ArtifactExplorerState> GetPackStateAsync(WorkspaceArtifactType role, string pack, CancellationToken cancellationToken = default) =>
        StateAsync(role, pack, cancellationToken);

    public async Task<ArtifactExplorerState?> GetSharedBaseAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default)
    {
        var current = await StateAsync(role, null, cancellationToken);
        if (current is not { Status: ExplorerArtifactStatus.Loaded, Pack: { } pack }) return null;
        var shared = await StateAsync(role, pack, cancellationToken, sharedOnly: true);
        return shared is { Status: ExplorerArtifactStatus.Loaded, Selected: not null } && shared.Selected.Id != current.Selected?.Id ? shared : null;
    }

    private async Task<ArtifactExplorerState> StateAsync(WorkspaceArtifactType role, string? viewPack, CancellationToken cancellationToken, bool sharedOnly = false,
        ScopeModel? loaded = null)
    {
        var model = loaded ?? await LoadScopeAsync(cancellationToken);
        var state = StateOf(role, viewPack, model, sharedOnly);
        return model.Evidence is null ? state : state with { Evidence = model.Evidence };
    }

    private ArtifactExplorerState StateOf(WorkspaceArtifactType role, string? viewPack, ScopeModel model, bool sharedOnly)
    {
        if (model.Failure is { } failure)
            return new ArtifactExplorerState(role, failure, model.Scope, model.ProjectName, [], Error: model.Error);

        var all = model.Roles[role];
        if (!model.HasModules)
            return Build(role, model, all, ArtifactModules.ProjectLevel, anyModule: true, modules: null, focus: null, viewPack, sharedOnly);

        // Several modules: a role resolves within one module, never across the whole project.
        var modules = model.Modules.Select(m => ModuleState(role, model, m)).ToList();
        var focus = FocusOf(model);
        if (focus is null)
        {
            if (viewPack is null) SetActiveRequirementScope(null);
            var listed = all.Select(e => e.Artifact).ToList();
            return new ArtifactExplorerState(role, listed.Count == 0 ? ExplorerArtifactStatus.Empty : ExplorerArtifactStatus.SelectionRequired,
                model.Scope, model.ProjectName, listed, Modules: modules);
        }
        return Build(role, model, all.Where(e => e.Artifact.Module == focus).ToList(), focus, anyModule: false, modules, focus, viewPack, sharedOnly);
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
        var pack = FeatureOfArtifact(role, scope, artifactId);
        // One selection per role, module and pack: choosing in one pack never replaces another pack's choice.
        selections.RemoveAll(s => s.WorkspaceScope == scope && s.Role == role.ToString() && (s.Module ?? ArtifactModules.ProjectLevel) == key && s.Pack == pack);
        selections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = role.ToString(), ArtifactId = artifactId, Module = key, Pack = pack, SelectedAt = DateTimeOffset.UtcNow });
        // Choosing a pack's artifact means viewing that pack; the other roles then follow that pack too.
        var packChanged = pack is not null && _workspace.SddLifecycle.PackFocus.LastOrDefault(f => f.WorkspaceScope == scope && f.Module == key)?.Pack != pack;
        if (pack is not null) SetPackFocus(scope, key, pack);

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
        if (packChanged && (module is null || module == focus)) _ = MirrorFocusAsync(scope, CancellationToken.None);
    }

    public async Task SelectModuleAsync(string? module, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        var focus = _workspace.SddLifecycle.ModuleFocus;
        focus.RemoveAll(f => f.WorkspaceScope == scope);
        if (module is not null) focus.Add(new SddModuleFocus { WorkspaceScope = scope, Module = module });
        await MirrorFocusAsync(scope, cancellationToken);
    }

    public async Task SelectPackAsync(string? pack, string? module = null, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        var key = module ?? StoredFocus(scope) ?? ArtifactModules.ProjectLevel;
        if (pack is null) _workspace.SddLifecycle.PackFocus.RemoveAll(f => f.WorkspaceScope == scope && f.Module == key);
        else SetPackFocus(scope, key, pack);
        await MirrorFocusAsync(scope, cancellationToken);
    }

    private void SetActiveRequirementScope(string? scope)
    {
        if (!string.Equals(_workspace.SddLifecycle.ActiveRequirementScope, scope, StringComparison.Ordinal))
            _workspace.SddLifecycle.ActiveRequirementScope = scope;
    }

    private void SetPackFocus(string? scope, string module, string pack)
    {
        var packs = _workspace.SddLifecycle.PackFocus;
        packs.RemoveAll(f => f.WorkspaceScope == scope && f.Module == module);
        packs.Add(new SddPackFocus { WorkspaceScope = scope, Module = module, Pack = pack, ViewedAt = DateTimeOffset.UtcNow });
    }

    private string? FeatureOfArtifact(WorkspaceArtifactType role, string? scope, string artifactId)
    {
        var path = artifactId.StartsWith(SamplePrefix, StringComparison.Ordinal) ? artifactId[SamplePrefix.Length..]
            : WorkspaceArtifacts(role, scope).FirstOrDefault(i => i.Artifact.Id == artifactId).Revision?.SourceReference
              ?? (artifactId.StartsWith(WorkspacePrefix, StringComparison.Ordinal) ? artifactId[WorkspacePrefix.Length..] : null);
        return ArtifactModules.FeatureOf(path);
    }

    /// <summary>
    /// Mirrors the focused module's (and viewed pack's) resolution into the repository's current selection (session artifacts) and the Sample
    /// Project choice, so older consumers read the same module and pack. A role the module does not resolve has no current artifact.
    /// </summary>
    private async Task MirrorFocusAsync(string? scope, CancellationToken cancellationToken)
    {
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
        else if (WorkspaceArtifacts(role, scope).FirstOrDefault(i => i.Artifact.Id == artifactId) is { } imported && imported.Revision.WorkspaceScope == scope)
        {
            // Only the project's own scope is mirrored: another bound package's current selection is that package's, never changed from here.
            _workspace.SelectRevision(imported.Revision.RevisionId);
        }
    }

    private ArtifactExplorerState Build(WorkspaceArtifactType role, ScopeModel model, List<ScopeEntry> entries, string module, bool anyModule,
        IReadOnlyList<ArtifactModuleRoleState>? modules, string? focus, string? viewPack = null, bool sharedOnly = false)
    {
        var packs = ModulePacks(model, module);
        if (packs.Count > 0) return BuildInPack(role, model, entries, module, modules, focus, packs, viewPack: viewPack, sharedOnly: sharedOnly);
        if (viewPack is null) SetActiveRequirementScope(null);

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
        var packs = ModulePacks(model, module);
        if (packs.Count > 0)
        {
            var state = BuildInPack(role, model, entries, module, null, module, packs, readContent: false);
            var ambiguous = entries.Where(e => e.Artifact.Feature is not null).GroupBy(e => e.Artifact.Feature)
                .Any(g => g.Count() > 1 && !HasStoredChoice(role, model, module, g.Key, g) && g.Count(e => ArtifactPacks.IsPackFile(e.Artifact.SourcePath ?? e.Artifact.FileName, role)) != 1);
            return new ArtifactModuleRoleState(module, entries.Count, state.Selected?.Id, state.Reason, packs.Count, ambiguous);
        }
        var (id, reason) = Resolve(role, model, entries, module, anyModule: !model.HasModules);
        return new ArtifactModuleRoleState(module, entries.Count, id, reason);
    }

    /// <summary>The feature packs of a module (every role's artifacts there), in folder order; empty when the module has no feature folders.</summary>
    private static IReadOnlyList<ArtifactPackSummary> ModulePacks(ScopeModel model, string module) =>
        ArtifactPacks.PacksOf(model.Roles.Values.SelectMany(l => l).Select(e => e.Artifact).Where(a => !model.HasModules || a.Module == module), module);

    private bool HasStoredChoice(WorkspaceArtifactType role, ScopeModel model, string module, string? pack, IEnumerable<ScopeEntry> candidates) =>
        _workspace.SddLifecycle.ExplorerSelections.Any(s => s.WorkspaceScope == model.Scope && s.Role == role.ToString()
            && (s.Module ?? ArtifactModules.ProjectLevel) == module && candidates.Any(c => c.Artifact.Id == s.ArtifactId) && (s.Pack is null || s.Pack == pack));

    /// <summary>
    /// A module organised in feature packs: the role resolves within the viewed pack — that pack's own artifact, else the module's (or
    /// project's) shared artifact — and never borrows a sibling pack's artifact. The viewed pack is the stored pack focus, else the only pack,
    /// else the pack of an earlier explicit selection (migration of selections saved before packs). With several packs and none viewed, the
    /// user picks a pack to explore (navigation, not authority). Only two artifacts of the role in the same pack ask the user to choose.
    /// </summary>
    private ArtifactExplorerState BuildInPack(WorkspaceArtifactType role, ScopeModel model, List<ScopeEntry> entries, string module,
        IReadOnlyList<ArtifactModuleRoleState>? modules, string? focus, IReadOnlyList<ArtifactPackSummary> packs, bool readContent = true, string? viewPack = null, bool sharedOnly = false)
    {
        var names = packs.Select(p => p.Pack).ToHashSet(StringComparer.Ordinal);
        var viewing = (viewPack is not null && names.Contains(viewPack) ? viewPack : null) ?? _workspace.SddLifecycle.PackFocus.LastOrDefault(f => f.WorkspaceScope == model.Scope && f.Module == module && names.Contains(f.Pack))?.Pack
            ?? (packs.Count == 1 ? packs[0].Pack : null)
            ?? _workspace.SddLifecycle.ExplorerSelections.Where(s => s.WorkspaceScope == model.Scope && (s.Module ?? ArtifactModules.ProjectLevel) == module)
                .Select(s => s.Pack ?? model.Roles.Values.SelectMany(l => l).FirstOrDefault(e => e.Artifact.Id == s.ArtifactId)?.Artifact.Feature)
                .LastOrDefault(p => p is not null && names.Contains(p));
        if (readContent && viewPack is null)
        {
            // An earlier selection that resolved the viewed pack (saved before packs) becomes the pack last viewed, and the viewed pack is the
            // scope requirements, links and evidence are recorded and matched in. Reading the same state again changes nothing.
            if (viewing is not null && packs.Count > 1 && !_workspace.SddLifecycle.PackFocus.Any(f => f.WorkspaceScope == model.Scope && f.Module == module && f.Pack == viewing))
                SetPackFocus(model.Scope, module, viewing);
            // Every viewed pack scopes its requirements — also the only pack of a module — so FR-001 of one module never meets FR-001 of another.
            SetActiveRequirementScope(viewing is not null ? RequirementScopes.Of(model.Scope, module, viewing) : null);
        }
        ArtifactExplorerState State(ExplorerArtifactStatus status, IReadOnlyList<ExplorerArtifact> listed, ScopeEntry? selected = null,
            ExplorerSelectionReason? reason = null, bool packRequired = false, bool ambiguous = false)
        {
            var text = selected is not null && readContent ? selected.Content() : null;
            if (selected is not null && readContent && string.IsNullOrEmpty(text))
                return new ArtifactExplorerState(role, ExplorerArtifactStatus.Error, model.Scope, model.ProjectName, listed, selected.Artifact, reason,
                    Error: $"{selected.Artifact.SourcePath ?? selected.Artifact.FileName} could not be read.", Modules: modules, Module: focus, Packs: packs, Pack: viewing);
            var resolution = selected is null
                ? status == ExplorerArtifactStatus.Empty && viewing is not null ? $"No PackLocal, ModuleShared or ProjectShared {ArtifactExplorerRoles.Label(role)} applies." : null
                : ArtifactPacks.ScopeOf(selected.Artifact) switch
                {
                    ArtifactScope.Pack => "Exact pack-local match",
                    ArtifactScope.Module => "Module-shared artifact",
                    _ => "Project-shared artifact",
                };
            return new ArtifactExplorerState(role, status, model.Scope, model.ProjectName, listed, selected?.Artifact, reason, text, Modules: modules, Module: focus,
                Packs: packs, Pack: viewing, PackRequired: packRequired, AmbiguousInPack: ambiguous, Resolution: resolution);
        }

        if (viewing is null)
            return State(entries.Count == 0 ? ExplorerArtifactStatus.Empty : ExplorerArtifactStatus.SelectionRequired, entries.Select(e => e.Artifact).ToList(), packRequired: true);

        // Resolution order: PackLocal → ModuleShared → ProjectShared → missing. Never a sibling pack.
        var local = sharedOnly ? [] : entries.Where(e => e.Artifact.Feature == viewing).ToList();
        var shared = entries.Where(e => e.Artifact.Feature is null).ToList();
        if (shared.Count == 0 && model.HasModules && module != ArtifactModules.ProjectLevel)
            shared = model.Roles[role].Where(e => e.Artifact.Module == ArtifactModules.ProjectLevel && e.Artifact.Feature is null).ToList();
        var listed = local.Concat(shared).Select(e => e.Artifact).ToList();
        ScopeEntry? Stored(IEnumerable<ScopeEntry> candidates) => _workspace.SddLifecycle.ExplorerSelections
            .Where(s => s.WorkspaceScope == model.Scope && s.Role == role.ToString() && (s.Module ?? ArtifactModules.ProjectLevel) == module)
            .Select(s => candidates.FirstOrDefault(c => c.Artifact.Id == s.ArtifactId)).LastOrDefault(c => c is not null);

        if (local.Count == 1)
            return State(ExplorerArtifactStatus.Loaded, listed, Stored(local) ?? local[0], Stored(local) is not null ? ExplorerSelectionReason.Explicit
                : entries.Count == 1 ? ExplorerSelectionReason.OnlyArtifact : ExplorerSelectionReason.PackArtifact);
        if (local.Count > 1)
        {
            if (Stored(local) is { } chosen) return State(ExplorerArtifactStatus.Loaded, listed, chosen, ExplorerSelectionReason.Explicit);
            // Spec-Kit structure decides: the role's feature file (specs/<pack>/spec.md) is the pack's; other candidates stay selectable.
            var packFiles = local.Where(e => ArtifactPacks.IsPackFile(e.Artifact.SourcePath ?? e.Artifact.FileName, role)).ToList();
            return packFiles.Count == 1
                ? State(ExplorerArtifactStatus.Loaded, listed, packFiles[0], ExplorerSelectionReason.PackFile)
                : State(ExplorerArtifactStatus.SelectionRequired, local.Select(e => e.Artifact).ToList(), ambiguous: true);
        }
        if (shared.Count == 0) return State(ExplorerArtifactStatus.Empty, listed);
        if (Stored(shared) is { } chosenShared) return State(ExplorerArtifactStatus.Loaded, listed, chosenShared, ExplorerSelectionReason.Explicit);
        var authoritative = shared.Where(e => e.Artifact.IsAuthoritative).ToList();
        if (shared.Count == 1 || authoritative.Count == 1)
            return State(ExplorerArtifactStatus.Loaded, listed, shared.Count == 1 ? shared[0] : authoritative[0], ExplorerSelectionReason.SharedArtifact);
        var sharedSpecKit = shared.Where(e => ArtifactPacks.IsSpecKitFile(e.Artifact.SourcePath ?? e.Artifact.FileName, role)).ToList();
        if (sharedSpecKit.Count == 1) return State(ExplorerArtifactStatus.Loaded, listed, sharedSpecKit[0], ExplorerSelectionReason.SharedArtifact);
        return State(ExplorerArtifactStatus.SelectionRequired, shared.Select(e => e.Artifact).ToList(), ambiguous: true);
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
        string? Error = null,
        IReadOnlyList<ScopeEntry>? Supporting = null,
        ExplorerProjectEvidence? Evidence = null)
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
        var supporting = new List<ScopeEntry>();
        var paths = new List<string?>();
        string? projectName = null;
        IReadOnlyDictionary<WorkspaceArtifactType, List<ScopeEntry>> NoRoles() => SampleArtifactClassifier.RoleOrder.ToDictionary(r => r, _ => new List<ScopeEntry>());

        if (ProjectImportScope.IsImport(scope))
        {
            // An imported project: its documents are revisions in its own scope (listed below); there is no catalog to discover.
            var importId = ProjectImportScope.ImportIdOf(scope);
            projectName = EnvironmentProject() is { } bound && bound.Main.ImportId == importId ? bound.Project.ProjectName
                : _workspace.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId)?.ProjectName ?? importId;
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
            // Supporting documentation: documents no role claimed, kept only when in a feature pack or referenced by one (same rule as Project Import).
            foreach (var document in SupportingDocuments.Retained(discovery.Documents, p => _discovery.GetContent(discovery.ProjectSlug, p)))
            {
                var content = _discovery.GetContent(discovery.ProjectSlug, document.RelativePath);
                var (displayName, identity) = SampleMeta(discovery.ProjectSlug, document.RelativePath, document.FileName, content);
                supporting.Add(new ScopeEntry(new ExplorerArtifact(
                    SamplePrefix + document.RelativePath, WorkspaceArtifactType.SupportingDocument, displayName, document.FileName,
                    document.RelativePath, ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown",
                    ExplorerArtifactCurrentness.Current, null, identity), () => content, false));
            }
        }

        foreach (var role in SampleArtifactClassifier.RoleOrder)
            roles[role].AddRange(Distinct(WorkspaceArtifacts(role, scope)).Select(i => new ScopeEntry(i.Artifact, () => i.Revision.Content, i.Revision.IsCurrentSelection)));
        supporting.AddRange(Distinct(WorkspaceArtifacts(WorkspaceArtifactType.SupportingDocument, scope)).Select(i => new ScopeEntry(i.Artifact, () => i.Revision.Content, false)));
        // Module roots come from the artifacts' own paths (every role, every revision of the scope) — the same evidence before and after an
        // import and for Demo Projects. A stray specs/ folder among unclassified documents does not create a module.
        var evidenceScopes = EvidenceScopes(scope);
        paths.AddRange(roles.Values.SelectMany(l => l).Select(e => e.Artifact.SourcePath ?? e.Artifact.FileName));
        paths.AddRange(_workspace.SddLifecycle.Revisions.Where(r => evidenceScopes.Any(s => s.Scope == r.WorkspaceScope && (!s.ManualOnly || r.Origin != ProjectImportOrigin)))
            .Select(r => r.SourceReference ?? r.FileName));

        var roots = ArtifactModules.RootsOf(paths);
        foreach (var role in SampleArtifactClassifier.RoleOrder)
            roles[role] = roles[role].Select(e =>
            {
                var path = e.Artifact.SourcePath ?? e.Artifact.FileName;
                return e with { Artifact = e.Artifact with { Module = ArtifactModules.ModuleOf(path, roots), Feature = ArtifactModules.FeatureOf(path) } };
            }).ToList();
        supporting = supporting.Select(e =>
        {
            var path = e.Artifact.SourcePath ?? e.Artifact.FileName;
            return e with { Artifact = e.Artifact with { Module = ArtifactModules.ModuleOf(path, roots), Feature = ArtifactModules.FeatureOf(path) } };
        }).ToList();

        // Named modules in path order; the project-level group (if any) last.
        var modules = roles.Values.SelectMany(l => l).Select(e => e.Artifact.Module).Distinct(StringComparer.Ordinal)
            .OrderBy(m => m.Length == 0 ? 1 : 0).ThenBy(m => m, StringComparer.Ordinal).ToList();
        return new ScopeModel(scope, projectName, roles, chosen, modules, Supporting: supporting, Evidence: EvidenceOf(scope));
    }

    /// <summary>
    /// The same document bound twice (two packages carrying an identical file at the same path) is one artifact: kept from the first source in
    /// binding order. Different content at the same path stays two candidates — a decision, never "the newest".
    /// </summary>
    private static IEnumerable<(ExplorerArtifact Artifact, SddArtifactRevision Revision)> Distinct(IEnumerable<(ExplorerArtifact Artifact, SddArtifactRevision Revision)> items)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var item in items)
        {
            if (item.Artifact.DocumentIdentity is { Length: > 0 } identity && !seen.Add((item.Artifact.SourcePath ?? item.Artifact.FileName, identity))) continue;
            yield return item;
        }
    }

    public async Task<ArtifactPackInventory> GetPackInventoryAsync(string? pack = null, CancellationToken cancellationToken = default)
    {
        // The explorers' own resolution decides each role's primary document for the pack: one scope load, one resolution, never a second scanner.
        var model = await LoadScopeAsync(cancellationToken);
        if (model.Failure is not null) return ArtifactPackInventory.Empty(model.Scope);
        var primary = new Dictionary<WorkspaceArtifactType, string>();
        var viewed = pack;
        foreach (var role in SampleArtifactClassifier.RoleOrder.Where(r => r != WorkspaceArtifactType.Research))
        {
            var state = await StateAsync(role, pack, cancellationToken, loaded: model);
            if (state is { Status: ExplorerArtifactStatus.Loaded, Selected: { } selected }) primary[role] = selected.Id;
            viewed ??= state.Pack;
        }
        var module = model.HasModules ? FocusOf(model) : model.Modules.FirstOrDefault() ?? ArtifactModules.ProjectLevel;
        if (module is null)
            return ArtifactPackInventory.Empty(model.Scope) with { Limitations = ["Choose a module first: documents are inventoried per module and Artifact Pack."] };

        var candidates = model.Roles.Values.SelectMany(l => l).Concat(model.Supporting ?? [])
            .Select(e => new PackInventoryCandidate(e.Artifact, e.Content, Math.Max(0, e.Artifact.RevisionCount - 1))).ToList();
        var lifecycle = _workspace.SddLifecycle;
        var limitations = new List<string>();
        var generated = new List<PackInventoryGenerated>();
        PackDocument? snapshot = null;
        if (ProjectImportScope.IsImport(model.Scope))
        {
            var importId = ProjectImportScope.ImportIdOf(model.Scope);
            if (!lifecycle.Revisions.Any(r => r.WorkspaceScope == model.Scope && r.Role == nameof(WorkspaceArtifactType.SupportingDocument)))
                limitations.Add("No supporting documentation is stored for this import. Projects imported before supporting-document discovery keep only their role documents; import the project again to include quickstart, contract and decision documents.");
            if (lifecycle.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == importId) is { } set)
                generated.AddRange(set.Artifacts.Where(a => DocumentationDrift.IsMarkdown(a.RelativePath)).Select(a => new PackInventoryGenerated(
                    a.RelativePath, a.ModuleRoot ?? "", a.Kind, a.Fingerprint, a.Generator,
                    () => lifecycle.GeneratedDocumentContents.GetValueOrDefault(a.Fingerprint))));
            if (lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is { SourceSnapshotId: { } snapshotId } record)
                snapshot = new PackDocument("source:" + snapshotId, "Source Analysis snapshot", null, null, record.ArchiveFileName, record.ArchiveFileName,
                    module, null, PackDocumentOrigin.Authored, ArtifactScope.Project, null, snapshotId.ToString(), ExplorerArtifactCurrentness.Current,
                    PackDocumentClass.SourceSnapshotReference, ["Source snapshot of the current project import — implementation source, not documentation"], null);
        }
        return ArtifactPackInventoryBuilder.Build(model.Scope, module, viewed, candidates, primary, generated, snapshot, limitations);
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
        if (_environments is not null) _environments.Changed -= OnEnvironmentsChanged;
    }

    /// <summary>Settings are saved for many reasons; only a change of the active project or its bindings changes what the explorers read.</summary>
    private void OnEnvironmentsChanged()
    {
        var key = ProjectKey();
        if (string.Equals(key, _lastProjectKey, StringComparison.Ordinal)) return;
        _lastProjectKey = key;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string? _lastProjectKey;

    private string? ProjectKey() => _environments?.ActiveProfile is { Project: { } project } profile
        ? profile.Id + "|" + System.Text.Json.JsonSerializer.Serialize(project)
        : null;

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

        if (entries.Count == 1) return (entries[0].Artifact.Id, ExplorerSelectionReason.OnlyArtifact);
        // Spec-Kit structure decides between a role's own file (e.g. .specify/memory/constitution.md) and supporting documents.
        var specKit = entries.Where(e => ArtifactPacks.IsSpecKitFile(e.Artifact.SourcePath ?? e.Artifact.FileName, role)).ToList();
        return specKit.Count == 1 ? (specKit[0].Artifact.Id, ExplorerSelectionReason.PackFile) : (null, null);
    }

    /// <summary>
    /// Imported artifacts of the role in this scope. Revisions with the same file name are one document's history: the
    /// document shows its current selection, otherwise its newest revision that is not superseded.
    /// </summary>
    /// <remarks>
    /// With a Target Environment Project this lists every bound documentation import of the project, each artifact with its package as
    /// provenance. Artifacts of the primary scope keep their id (<c>workspace:{file}</c>); another package's are <c>workspace:@{importId}/{file}</c>.
    /// </remarks>
    private List<(ExplorerArtifact Artifact, SddArtifactRevision Revision)> WorkspaceArtifacts(WorkspaceArtifactType role, string? scope) =>
        EvidenceScopes(scope).SelectMany(evidence => _workspace.SddLifecycle.Revisions
            .Where(r => r.Role == role.ToString() && r.WorkspaceScope == evidence.Scope && !string.IsNullOrWhiteSpace(r.Content)
                && (!evidence.ManualOnly || r.Origin != ProjectImportOrigin))
            .GroupBy(r => r.FileName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var revision = g.FirstOrDefault(r => r.IsCurrentSelection)
                               ?? g.Where(r => CurrentnessOf(r) == ExplorerArtifactCurrentness.Current).MaxBy(r => r.Revision)
                               ?? g.MaxBy(r => r.Revision)!;
                var (displayName, identity) = RevisionMeta(revision, g.Key);
                var source = evidence.Source;
                var artifact = new ExplorerArtifact(
                    evidence.Primary ? WorkspacePrefix + g.Key : $"{WorkspacePrefix}@{source!.ImportId}/{g.Key}", role, displayName, g.Key, revision.SourceReference,
                    ExplorerArtifactSource.Workspace, revision.Origin, revision.Revision, g.Count(),
                    string.IsNullOrWhiteSpace(revision.Authority) ? "Unknown" : revision.Authority, CurrentnessOf(revision), revision.CapturedAt,
                    identity, EvidenceSource: source?.ArchiveFileName, EvidenceImportId: source?.ImportId,
                    EvidenceImportedAt: source?.ImportedAt);
                return (artifact, revision);
            }))
            .ToList();

    private const string ProjectImportOrigin = BirkNext.Web.Services.ProjectImport.ProjectImportActivation.Origin;

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
