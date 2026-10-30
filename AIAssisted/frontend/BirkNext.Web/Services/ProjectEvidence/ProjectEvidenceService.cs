using BirkNext.Integrations;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// Reads and changes Target Environment → Project. The binding is stored on the Target Environment profile (references only); packages come
/// from the workspace's Project Import records and their stored Source Analysis snapshots. Every change is an explicit user action, saved at once.
/// </summary>
public interface IProjectEvidenceService
{
    /// <summary>The environment's project evidence (default: the active Target Environment). Source setup is read once per environment and cached.</summary>
    Task<ProjectEvidenceView> GetAsync(string? environmentId = null, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>The resolved view without a backend call: package capabilities from the workspace only (no Source Analysis areas).</summary>
    ProjectEvidenceView Peek(string? environmentId = null);

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

    event Action? Changed;
}

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
        if (refresh || !_setup.TryGetValue(key, out var setup))
        {
            try { setup = (await _journeys.SourceSetupAsync(profile?.Id, cancellationToken)).Value; }
            catch (Exception ex) when (ex is not OperationCanceledException) { setup = null; }
            _setup[key] = setup;
        }
        return Build(profile, setup);
    }

    public ProjectEvidenceView Peek(string? environmentId = null)
    {
        var profile = Profile(environmentId);
        return Build(profile, _setup.GetValueOrDefault(profile?.Id ?? ""));
    }

    private ProjectEvidenceView Build(FrontendAnalysisProfile? profile, SourceSetupView? setup) =>
        ProjectEvidenceResolver.Resolve(profile?.Id, profile?.Name, profile?.Project, ProjectEvidencePackages.All(_workspace.SddLifecycle, setup), setup);

    private ProjectEvidencePackage? Package(string environmentId, string importId)
    {
        var record = _workspace.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId);
        return record is null ? null : ProjectEvidencePackages.Of(record, _workspace.SddLifecycle, _setup.GetValueOrDefault(environmentId));
    }

    private async Task<bool> MutateAsync(string environmentId, Func<FrontendAnalysisProfile, bool> change)
    {
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
        profile.Project is { } project && ProjectEvidenceResolver.Unbind(project, roleKey, importId));

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
