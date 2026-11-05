using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// Reads and changes Target Environment → Project. The binding is stored on the Target Environment profile (references only); packages come
/// from the workspace's Project Import records and their stored Source Analysis snapshots. Every change is an explicit user action, saved at once.
/// </summary>
public interface IProjectEvidenceService
{
    /// <summary>The environment's project evidence (default: the active Target Environment). Source setup is read once per environment and
    /// cached; the cache is re-read when it no longer covers the imported packages (a later import's snapshot) or after <see cref="Invalidate"/>.</summary>
    Task<ProjectEvidenceView> GetAsync(string? environmentId = null, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>The resolved view without a backend call: package capabilities from the workspace only (no Source Analysis areas).</summary>
    ProjectEvidenceView Peek(string? environmentId = null);

    /// <summary>Forget the cached Source Analysis setup of every environment (after an import or a re-analysis) and notify the views.</summary>
    void Invalidate() { }

    Task<bool> SelectProjectAsync(string environmentId, string importId);
    Task<IReadOnlyList<string>> ChangeProjectAsync(string environmentId, string importId);
    Task<bool> BindAsync(string environmentId, string roleKey, string importId);
    Task<bool> UnbindAsync(string environmentId, string roleKey, string importId);
    Task<bool> SetRequirementAsync(string environmentId, string roleKey, ProjectEvidenceRequirement requirement);
    Task<string?> AddRoleAsync(string environmentId, ProjectEvidenceCapability capability, string label);
    Task<bool> RemoveRoleAsync(string environmentId, string roleKey);
    Task<bool> SetTestPackAsync(string environmentId, string packId, bool active);
    Task<bool> ClearProjectAsync(string environmentId);

    /// <summary>Project names already used by Target Environments (detected or manually created) — the picker offers them across environments.</summary>
    IReadOnlyList<string> KnownProjectNames() => [];
    /// <summary>Registered Target Application names with the number of environments that use each.</summary>
    IReadOnlyList<(string Name, int Environments)> RegisteredApplications() => [];
    /// <summary>The stored Source Analysis snapshots the environment's last read returned (Source Management / imports), for application search.</summary>
    IReadOnlyList<BirkNext.Integrations.SourceSetupSnapshot> SourceSnapshots(string? environmentId) => [];

    /// <summary>
    /// Saves Project setup in one step: the logical project (by name; <paramref name="draft"/>'s main package when it is a detected one, none
    /// for a manual project), the checklist decisions applied to the canonical bindings, and expected evidence as named requirements. A
    /// different project replaces the environment's project; imports, snapshots and documents are never touched.
    /// </summary>
    Task<bool> SaveProjectAsync(string environmentId, ProjectSetupDraft draft) => Task.FromResult(false);

    /// <summary>Sets the environment's Target Application (the logical application/system). A different application archives the previous
    /// application's Project evidence (recoverable, never deleted). Never activates the environment.</summary>
    Task<bool> SetTargetApplicationAsync(string environmentId, TargetApplicationIdentity identity) => Task.FromResult(false);

    /// <summary>The environment profile's Target Application identity (legacy profiles: read from their Project).</summary>
    TargetApplicationIdentity? TargetApplicationOf(string? environmentId) => null;

    /// <summary>Which application the environment's evidence points to (imported primary sources + target detection) — the same inference
    /// <see cref="GetAsync"/> applies when it is unique and strong. Never writes.</summary>
    ApplicationInference InferApplication(string? environmentId) => ApplicationInference.None;

    /// <summary>"Not used for this application": a package that needs review is set aside for this environment (the import is untouched;
    /// re-evaluation never re-associates it). Reversible by binding it explicitly.</summary>
    Task<bool> IgnoreSourceAsync(string environmentId, string importId) => Task.FromResult(false);

    /// <summary>
    /// The single association editor's save: associates every chosen source with the Target Application under the evidence category of its
    /// role, in one transaction — all or nothing. Creates the Project evidence for the Target Application when there is none. An explicit
    /// choice reverses an earlier "Not used for this application". A role whose category the source's content does not support is refused
    /// (capability ≠ role), and nothing is saved.
    /// </summary>
    Task<AssociationSaveResult> AssociateAsync(string environmentId, IReadOnlyList<SourceAssociationChoice> choices) =>
        Task.FromResult(new AssociationSaveResult(false, ["Not supported in this view."]));

    /// <summary>Removes a source from the Target Application's evidence (every category) and remembers the decision, so re-evaluation never
    /// associates it again automatically. The import and its snapshots are untouched.</summary>
    Task<bool> RemoveAssociationAsync(string environmentId, string importId) => Task.FromResult(false);

    event Action? Changed;
}

/// <summary>One source and the role it has for the Target Application (several roles of one source are several choices).</summary>
public sealed record SourceAssociationChoice(string ImportId, PackageEvidenceRole Role);

/// <summary>The outcome of <see cref="IProjectEvidenceService.AssociateAsync"/>: saved, or why not (nothing was saved then).</summary>
public sealed record AssociationSaveResult(bool Saved, IReadOnlyList<string> Problems);

/// <summary>What Project setup saves: the logical project, its checklist decisions and new expected evidence (label + capability).</summary>
public sealed record ProjectSetupDraft(string Name, string? Description, string? MainImportId, IReadOnlyList<ProjectSetupPlanner.RoleDecision> Decisions,
    IReadOnlyList<(string Label, ProjectEvidenceCapability Capability)> Expected);

public sealed class ProjectEvidenceService : IProjectEvidenceService
{
    private readonly IFrontendAnalysisSettingsService _settings;
    private readonly IWorkspaceSessionService _workspace;
    private readonly IIntegrationJourneysApiService _journeys;
    private readonly IJSRuntime _js;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, SourceSetupView?> _setup = new(StringComparer.Ordinal);

    public event Action? Changed;

    public ProjectEvidenceService(IFrontendAnalysisSettingsService settings, IWorkspaceSessionService workspace, IIntegrationJourneysApiService journeys,
        IJSRuntime js, TimeProvider? time = null)
    {
        _settings = settings;
        _workspace = workspace;
        _journeys = journeys;
        _js = js;
        _time = time ?? TimeProvider.System;
    }

    private FrontendAnalysisProfile? Profile(string? environmentId) => environmentId is { Length: > 0 } id
        ? _settings.Settings.Profiles.FirstOrDefault(p => p.Id == id)
        : _settings.ActiveProfile;

    public async Task<ProjectEvidenceView> GetAsync(string? environmentId = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var profile = Profile(environmentId);
        var key = profile?.Id ?? "";
        if (refresh || !_setup.TryGetValue(key, out var setup) || Stale(key, setup))
        {
            try { setup = (await _journeys.SourceSetupAsync(profile?.Id, cancellationToken)).Value; }
            catch (Exception ex) when (ex is not OperationCanceledException) { setup = null; }
            _setup[key] = setup;
            _missingAfterRead[key] = Missing(setup);
        }
        // One guided workflow: (1) the Target Application is inferred from imported primary sources + target detection and applied when the
        // match is unique and strong (an existing same-named application is confirmed, never duplicated; an explicit different choice wins);
        // (2) deterministic association: strong matches are associated without a manual step (recomputed on every read; explicit choices win).
        // Only with the backend's classification — an unreachable backend never removes or guesses associations or applications.
        if (profile is not null && setup is not null)
        {
            await EnsureDetectionSnapshotsAsync();
            var inferred = TargetApplicationInference.Apply(profile, Inference(profile, setup), _time.GetUtcNow());
            var associated = ProjectEvidenceAssociation.Apply(profile, Associations(profile, setup), _time.GetUtcNow());
            if (inferred || associated)
            {
                await _settings.MaterializeAsync(_js, profile.Id);
                await _settings.SaveAsync(_js);
            }
        }
        return Build(profile, setup);
    }

    private bool _snapshotsLoaded;

    /// <summary>Target detection results are persisted separately (public discovery evidence only); read them once per session.</summary>
    private async Task EnsureDetectionSnapshotsAsync()
    {
        if (_snapshotsLoaded) return;
        _snapshotsLoaded = true;
        try { await _settings.LoadDetectionSnapshotsAsync(_js); } catch (Exception ex) when (ex is InvalidOperationException or JSException) { }
    }

    private ApplicationInference Inference(FrontendAnalysisProfile profile, SourceSetupView? setup)
    {
        var lifecycle = _workspace.SddLifecycle;
        // The same classification Import Project and the association use: primary application sources only (a related component is not one).
        var primary = lifecycle.Applications.SelectMany(a => ApplicationContexts.Packages(lifecycle, a.Id, setup))
            .Where(p => p.Role == PackageEvidenceRole.PrimaryApplicationSource).Select(p => p.ImportId).ToHashSet(StringComparer.Ordinal);
        return TargetApplicationInference.Infer(ProjectEvidencePackages.All(lifecycle, setup), profile.TargetUrl,
            _settings.GetDetectionSnapshot(profile.Id)?.Result, KnownProjectNames(), primary);
    }

    public ApplicationInference InferApplication(string? environmentId) =>
        Profile(environmentId) is { } profile ? Inference(profile, _setup.GetValueOrDefault(profile.Id)) : ApplicationInference.None;

    public Task<bool> IgnoreSourceAsync(string environmentId, string importId) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project is not { } project || project.Declined.Any(d => d.ImportId == importId && d.RoleKey == IgnoredRole)) return false;
        project.Declined.Add(new ProjectEvidenceDecline { RoleKey = IgnoredRole, ImportId = importId, At = _time.GetUtcNow() });
        foreach (var role in project.Evidence) role.Sources.RemoveAll(s => s.ImportId == importId && s.BoundBy == ProjectEvidenceAssociation.Detected);
        return true;
    });

    /// <summary>The decline marker of "Not used for this application" (every role).</summary>
    public const string IgnoredRole = "*";

    public async Task<AssociationSaveResult> AssociateAsync(string environmentId, IReadOnlyList<SourceAssociationChoice> choices)
    {
        var problems = new List<string>();
        if (choices.Count == 0) return new(false, ["Choose at least one source."]);
        var saved = await MutateAsync(environmentId, profile =>
        {
            if (TargetApplications.Of(profile) is not { } identity) { problems.Add("Choose the Target Application first."); return false; }
            var now = _time.GetUtcNow();
            // All or nothing: the choices are applied to a copy and the copy replaces the evidence only when every choice applied.
            var draft = profile.Project is { } current
                ? System.Text.Json.JsonSerializer.Deserialize<EnvironmentProject>(System.Text.Json.JsonSerializer.Serialize(current))!
                : new EnvironmentProject { ProjectName = identity.Name, SelectedAt = now, Manual = true, Evidence = [.. ProjectEvidenceRoles.Defaults()] };
            foreach (var choice in choices)
            {
                if (Package(environmentId, choice.ImportId) is not { } package) { problems.Add($"{choice.ImportId} is no longer imported."); continue; }
                var capability = SourceRoleVocabulary.CategoryOf(choice.Role);
                if (!ProjectEvidencePackages.Satisfies(package, capability))
                {
                    problems.Add($"{package.DisplayName}: its content does not show {ProjectEvidencePackages.CapabilityLabel(capability)}, so it cannot be {ApplicationContexts.RoleLabel(choice.Role)}.");
                    continue;
                }
                var role = draft.Evidence.FirstOrDefault(r => r.Capability == capability);
                if (role is null)
                {
                    role = ProjectEvidenceRoles.Role($"user-{capability.ToString().ToLowerInvariant()}", ProjectEvidencePackages.CapabilityLabel(capability), capability,
                        ProjectEvidenceRequirement.Optional, ProjectEvidenceCardinality.Multiple, "Associated in Project Evidence", ProjectEvidenceRoleOrigin.UserAdded);
                    draft.Evidence.Add(role);
                }
                draft.Declined.RemoveAll(d => d.ImportId == package.ImportId && (d.RoleKey == IgnoredRole || d.RoleKey == role.RoleKey));
                if (!ProjectEvidenceResolver.Bind(draft, role.RoleKey, package, now)) { problems.Add($"{package.DisplayName} could not be associated as {role.Label}."); continue; }
                if (capability == ProjectEvidenceCapability.ApplicationSource) { draft.ProjectImportId = package.ImportId; draft.ArchiveSha256 = package.ArchiveSha256; draft.Manual = false; }
            }
            if (problems.Count > 0) return false;
            profile.Project = draft;
            return true;
        });
        return new(saved, saved ? [] : problems.Count > 0 ? problems : ["The association could not be saved."]);
    }

    public Task<bool> RemoveAssociationAsync(string environmentId, string importId) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project is not { } project) return false;
        var removed = project.Evidence.Sum(role => role.Sources.RemoveAll(s => s.ImportId == importId)) > 0;
        if (!project.Declined.Any(d => d.ImportId == importId && d.RoleKey == IgnoredRole))
            project.Declined.Add(new ProjectEvidenceDecline { RoleKey = IgnoredRole, ImportId = importId, At = _time.GetUtcNow() });
        if (project.ProjectImportId == importId) { project.ProjectImportId = null; project.ArchiveSha256 = null; project.Manual = true; }
        return true;
    });

    private IReadOnlyList<PackageAssociation> Associations(FrontendAnalysisProfile? profile, SourceSetupView? setup) =>
        ProjectEvidenceAssociation.Of(TargetApplications.Of(profile), profile?.Project, _workspace.SddLifecycle, ProjectEvidencePackages.All(_workspace.SddLifecycle, setup), setup,
            ConfirmedElsewhere(profile));

    /// <summary>Packages the person bound (not "Detected") for the same Target Application in other Target Environments — reused as suggestions.</summary>
    private HashSet<string> ConfirmedElsewhere(FrontendAnalysisProfile? profile)
    {
        if (TargetApplications.Of(profile)?.Name is not { Length: > 0 } name) return [];
        return _settings.Settings.Profiles
            .Where(p => p.Id != profile!.Id && string.Equals(TargetApplications.Of(p)?.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Project?.Evidence.SelectMany(r => r.Sources) ?? [])
            .Where(s => s.BoundBy != ProjectEvidenceAssociation.Detected).Select(s => s.ImportId).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The cached setup no longer describes the imported packages: an import names a Source Analysis snapshot the cache does not hold (it was
    /// imported, re-analyzed or retried after the cache was read). Without this, a later package kept its snapshot-less capabilities — no
    /// contracts, infrastructure or shared-library classification — and was not offered for those roles.
    /// </summary>
    /// <para>A snapshot the backend itself no longer has (deleted) is not re-read on every call: only a change of the missing set triggers a read.</para>
    private bool Stale(string key, SourceSetupView? setup) =>
        !Missing(setup).SetEquals(_missingAfterRead.GetValueOrDefault(key) ?? []);

    private HashSet<Guid> Missing(SourceSetupView? setup) =>
        [.. _workspace.SddLifecycle.ProjectImports.Select(i => i.SourceSnapshotId).OfType<Guid>().Where(id => setup is null || setup.Snapshots.All(s => s.SnapshotId != id))];

    private readonly Dictionary<string, HashSet<Guid>> _missingAfterRead = new(StringComparer.Ordinal);

    public void Invalidate()
    {
        _setup.Clear();
        _missingAfterRead.Clear();
        Changed?.Invoke();
    }

    public ProjectEvidenceView Peek(string? environmentId = null)
    {
        var profile = Profile(environmentId);
        return Build(profile, _setup.GetValueOrDefault(profile?.Id ?? ""));
    }

    private ProjectEvidenceView Build(FrontendAnalysisProfile? profile, SourceSetupView? setup) =>
        ProjectEvidenceResolver.Resolve(profile?.Id, profile?.Name, profile?.Project, ProjectEvidencePackages.All(_workspace.SddLifecycle, setup), setup)
            with { Associations = Associations(profile, setup), TargetApplication = TargetApplications.Of(profile), Classified = setup is not null };

    private ProjectEvidencePackage? Package(string environmentId, string importId)
    {
        var record = _workspace.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId);
        return record is null ? null : ProjectEvidencePackages.Of(record, _workspace.SddLifecycle, _setup.GetValueOrDefault(environmentId));
    }

    private async Task<bool> MutateAsync(string environmentId, Func<FrontendAnalysisProfile, bool> change)
    {
        // Every change judges packages on current Source Analysis facts: without this, a project created (or a role bound) before the setup was
        // read — or with a stale read — saw the main package without its snapshot (no contracts) and left Contracts unbound ("Suggested").
        if (Profile(environmentId) is null) return false;
        await GetAsync(environmentId);
        if (Profile(environmentId) is not { } profile || !change(profile)) return false;
        await _settings.MaterializeAsync(_js, profile.Id);
        await _settings.SaveAsync(_js);
        Changed?.Invoke();
        return true;
    }

    public Task<bool> SelectProjectAsync(string environmentId, string importId) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project is not null || Package(environmentId, importId) is not { } main) return false;
        profile.Project = ProjectEvidenceResolver.CreateProject(main, _time.GetUtcNow());
        profile.TargetApplication ??= TargetApplications.Of(profile) is { } identity ? new TargetApplicationIdentity
            { Name = identity.Name, ImportId = identity.ImportId, ArchiveSha256 = identity.ArchiveSha256, SetAt = _time.GetUtcNow() } : null;
        return true;
    });

    public async Task<IReadOnlyList<string>> ChangeProjectAsync(string environmentId, string importId)
    {
        IReadOnlyList<string> impact = [];
        await MutateAsync(environmentId, profile =>
        {
            if (Package(environmentId, importId) is not { } main) return false;
            if (profile.Project is null) { profile.Project = ProjectEvidenceResolver.CreateProject(main, _time.GetUtcNow()); return true; }
            impact = ProjectEvidenceResolver.ChangeProject(profile.Project, main, ProjectEvidencePackages.All(_workspace.SddLifecycle, _setup.GetValueOrDefault(environmentId)), _time.GetUtcNow());
            return true;
        });
        return impact;
    }

    public Task<bool> BindAsync(string environmentId, string roleKey, string importId) => MutateAsync(environmentId, profile =>
        profile.Project is { } project && Package(environmentId, importId) is { } package && ProjectEvidenceResolver.Bind(project, roleKey, package, _time.GetUtcNow()));

    public Task<bool> UnbindAsync(string environmentId, string roleKey, string importId) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project is not { } project) return false;
        // Removing an automatic association is the user's decision: remembered, so the next read does not associate it again.
        var automatic = project.Evidence.FirstOrDefault(r => r.RoleKey == roleKey)?.Sources.Any(s => s.ImportId == importId && s.BoundBy == ProjectEvidenceAssociation.Detected) == true;
        if (!ProjectEvidenceResolver.Unbind(project, roleKey, importId)) return false;
        if (automatic && !project.Declined.Any(d => d.RoleKey == roleKey && d.ImportId == importId))
            project.Declined.Add(new ProjectEvidenceDecline { RoleKey = roleKey, ImportId = importId, At = _time.GetUtcNow() });
        return true;
    });

    public Task<bool> SetRequirementAsync(string environmentId, string roleKey, ProjectEvidenceRequirement requirement) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project?.Evidence.FirstOrDefault(r => r.RoleKey == roleKey) is not { } role || role.Requirement == requirement) return false;
        role.Requirement = requirement;
        return true;
    });

    public async Task<string?> AddRoleAsync(string environmentId, ProjectEvidenceCapability capability, string label)
    {
        string? key = null;
        await MutateAsync(environmentId, profile =>
        {
            if (profile.Project is not { } project || string.IsNullOrWhiteSpace(label)) return false;
            var slug = new string(label.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            key = "user-" + (slug.Length == 0 ? "role" : slug);
            for (var n = 2; project.Evidence.Any(r => r.RoleKey == key); n++) key = $"user-{slug}-{n}";
            project.Evidence.Add(ProjectEvidenceRoles.Role(key, label.Trim(), capability, ProjectEvidenceRequirement.Optional,
                capability is ProjectEvidenceCapability.ApplicationSource ? ProjectEvidenceCardinality.Single : ProjectEvidenceCardinality.Multiple,
                "Added in Project setup", ProjectEvidenceRoleOrigin.UserAdded));
            return true;
        });
        return key;
    }

    public Task<bool> RemoveRoleAsync(string environmentId, string roleKey) => MutateAsync(environmentId, profile =>
        profile.Project is { } project && project.Evidence.RemoveAll(r => r.RoleKey == roleKey && r.Origin is ProjectEvidenceRoleOrigin.UserAdded or ProjectEvidenceRoleOrigin.Expected) > 0);

    public Task<bool> SetTestPackAsync(string environmentId, string packId, bool active) => MutateAsync(environmentId, profile =>
    {
        if (profile.Project is not { } project) return false;
        if (active) { if (project.TestPacks.Contains(packId, StringComparer.Ordinal)) return false; project.TestPacks.Add(packId); return true; }
        return project.TestPacks.RemoveAll(p => p == packId) > 0;
    });

    public Task<bool> SetTargetApplicationAsync(string environmentId, TargetApplicationIdentity identity) =>
        MutateAsync(environmentId, profile => TargetApplications.Set(profile, identity, _time.GetUtcNow()));

    public TargetApplicationIdentity? TargetApplicationOf(string? environmentId) => TargetApplications.Of(Profile(environmentId));

    public IReadOnlyList<(string Name, int Environments)> RegisteredApplications() =>
        [.. _settings.Settings.Profiles.Select(p => TargetApplications.Of(p)?.Name).OfType<string>().Where(n => n.Trim().Length > 0)
            .GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count()))];

    public IReadOnlyList<BirkNext.Integrations.SourceSetupSnapshot> SourceSnapshots(string? environmentId) =>
        _setup.GetValueOrDefault(Profile(environmentId)?.Id ?? "")?.Snapshots ?? [];

    public IReadOnlyList<string> KnownProjectNames() =>
        [.. _settings.Settings.Profiles.Select(p => TargetApplications.Of(p)?.Name).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    public Task<bool> SaveProjectAsync(string environmentId, ProjectSetupDraft draft) => MutateAsync(environmentId, profile =>
    {
        var name = draft.Name.Trim();
        if (name.Length is 0 or > 120) return false;
        var packages = ProjectEvidencePackages.All(_workspace.SddLifecycle, _setup.GetValueOrDefault(environmentId));
        var main = draft.MainImportId is { } id ? packages.FirstOrDefault(p => p.ImportId == id) : null;
        var now = _time.GetUtcNow();
        // Project evidence is for the Target Application: the identity is set (or kept) first; a legacy profile's identity is materialized.
        if (TargetApplications.Of(profile) is not { } identity || !TargetApplications.SameApplication(identity, name))
            TargetApplications.Set(profile, new TargetApplicationIdentity { Name = name, ImportId = main?.ImportId, ArchiveSha256 = main?.ArchiveSha256, Manual = main is null, SetAt = now }, now);
        else if (profile.TargetApplication is null)
            profile.TargetApplication = new TargetApplicationIdentity { Name = identity.Name, ImportId = identity.ImportId, ArchiveSha256 = identity.ArchiveSha256, Manual = identity.Manual, Description = identity.Description, SetAt = now };
        if (profile.Project is not { } project || !string.Equals(project.ProjectName, name, StringComparison.OrdinalIgnoreCase))
        {
            // A new (or different) logical project for this environment. Its main package's own evidence is decided by the checklist below.
            project = new EnvironmentProject
            {
                ProjectName = name, ProjectImportId = main?.ImportId, ArchiveSha256 = main?.ArchiveSha256, SelectedAt = now, Manual = main is null,
                Evidence = [.. ProjectEvidenceRoles.Defaults()],
            };
            profile.Project = project;
        }
        project.Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        foreach (var (label, capability) in draft.Expected)
        {
            var text = label.Trim();
            if (text.Length == 0 || project.Evidence.Any(r => r.Origin == ProjectEvidenceRoleOrigin.Expected && string.Equals(r.Label, text, StringComparison.OrdinalIgnoreCase))) continue;
            var slug = new string([.. text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
            var key = "expected-" + (slug.Length == 0 ? "evidence" : slug);
            for (var n = 2; project.Evidence.Any(r => r.RoleKey == key); n++) key = $"expected-{slug}-{n}";
            project.Evidence.Add(ProjectEvidenceRoles.Role(key, text, capability, ProjectEvidenceRequirement.Required,
                capability is ProjectEvidenceCapability.ApplicationSource ? ProjectEvidenceCardinality.Single : ProjectEvidenceCardinality.Multiple,
                "Expected evidence (Project setup)", ProjectEvidenceRoleOrigin.Expected));
        }
        ProjectSetupPlanner.Apply(project, packages, draft.Decisions, now);
        return true;
    });

    public Task<bool> ClearProjectAsync(string environmentId) => MutateAsync(environmentId, profile =>
    {
        // Removes the evidence bindings from the environment; they are archived (recoverable), and imports/snapshots are never touched.
        if (profile.Project is not { } project) return false;
        if (profile.TargetApplication is null && TargetApplications.Of(profile) is { } identity)
            profile.TargetApplication = new TargetApplicationIdentity { Name = identity.Name, ImportId = identity.ImportId, ArchiveSha256 = identity.ArchiveSha256, Manual = identity.Manual, SetAt = _time.GetUtcNow() };
        project.ArchivedAt = _time.GetUtcNow();
        (profile.ArchivedProjects ??= []).Add(project);
        profile.Project = null;
        return true;
    });
}
