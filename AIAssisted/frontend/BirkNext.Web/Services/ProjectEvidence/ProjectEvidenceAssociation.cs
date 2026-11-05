using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>How one imported package relates to the environment's Target Application.</summary>
public enum EvidenceAssociationState
{
    /// <summary>No Target Application yet: the package's role is shown as detected; nothing is assessed against an application.</summary>
    WaitingForTargetApplication,
    /// <summary>Evidence of the Target Application, associated automatically (deterministic role) or confirmed by the user.</summary>
    Associated,
    /// <summary>Its role or application is not certain (related system, conflict, not classified yet): the user decides.</summary>
    NeedsReview,
    /// <summary>Imported for another application, or deselected by the user.</summary>
    NotAssociated,
}

/// <summary>
/// How strongly a package that is not (yet) associated is suggested for the Target Application. Never applied automatically: a suggestion is a
/// review item the person confirms. Strong = supported by an explicit identity (the Target Application's own package) or by the same
/// application's confirmed association in another Target Environment. Possible = only the names are related (weak evidence).
/// </summary>
public enum AssociationSuggestion { None, Strong, Possible }

/// <summary>One package's association: state, role, and the reason in tester words.</summary>
public sealed record PackageAssociation(ProjectEvidencePackage Package, EvidenceAssociationState State, PackageEvidenceRole Role, string Detail, bool UserConfirmed = false,
    AssociationSuggestion Suggestion = AssociationSuggestion.None, bool RoleConfirmed = false)
{
    public string StateLabel => State switch
    {
        EvidenceAssociationState.WaitingForTargetApplication => "Detected",
        EvidenceAssociationState.Associated => ApplicationContexts.RoleLabel(Role),
        EvidenceAssociationState.NeedsReview => "Needs review",
        _ => "Not associated",
    };

    /// <summary>The Target Environment badge tone (tep-tone-*): never green for "needs review", never red for an unrelated package.</summary>
    public string Tone => State switch
    {
        EvidenceAssociationState.Associated => "available",
        EvidenceAssociationState.NeedsReview => "partial",
        _ => "neutral",
    };
}

/// <summary>
/// Deterministic association of imported evidence with the Target Application. Binding stays the persisted mechanism every consumer reads
/// (<see cref="EnvironmentProject.Evidence"/>: explorers' documentation scope, review source scopes, Test Pack satisfaction), so a manual Bind
/// is never required for an obvious source: strong matches are written as <c>BoundBy = "Detected"</c> and recomputed on every read, while
/// explicit decisions (a user's binding, a declined suggestion, removed evidence) are preserved and win. Precedence: explicit user choice &gt;
/// the application's own primary source named like the Target Application &gt; a deterministic role of a package imported as evidence for that
/// application &gt; unknown (Needs review). Archive names never decide. Associated means "evidence identified", never reviewed or ready.
/// </summary>
public static class ProjectEvidenceAssociation
{
    public const string Detected = "Detected";

    /// <summary>The workspace application the Target Application corresponds to: same name, else the one holding the identity's own package.</summary>
    public static SddApplicationContext? ApplicationFor(TargetApplicationIdentity? identity, SddLifecycleState lifecycle)
    {
        if (identity is null) return null;
        return lifecycle.Applications.FirstOrDefault(a => string.Equals(a.Name, identity.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? (identity.ImportId is { } own ? ApplicationContexts.Find(lifecycle, lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == own)?.ApplicationId) : null);
    }

    /// <param name="confirmedElsewhere">Import ids the person confirmed for the same Target Application in another Target Environment.</param>
    public static IReadOnlyList<PackageAssociation> Of(TargetApplicationIdentity? identity, EnvironmentProject? project, SddLifecycleState lifecycle,
        IReadOnlyList<ProjectEvidencePackage> packages, SourceSetupView? setup, IReadOnlySet<string>? confirmedElsewhere = null)
    {
        var application = ApplicationFor(identity, lifecycle);
        // Roles are judged within the package's own application (the one it was imported for); without a Target Application only detected. A
        // role confirmed in Source Management is the canonical role (ApplicationContexts.Packages).
        var classified = lifecycle.Applications.SelectMany(a => ApplicationContexts.Packages(lifecycle, a.Id, setup)).ToList();
        var roles = classified.ToDictionary(p => p.ImportId, p => p.Role, StringComparer.Ordinal);
        var confirmedRoles = classified.Where(p => p.RoleConfirmed).Select(p => p.ImportId).ToHashSet(StringComparer.Ordinal);
        PackageEvidenceRole RoleOf(ProjectEvidencePackage p) => roles.TryGetValue(p.ImportId, out var r) ? r
            : setup is null && p.SnapshotId is not null ? PackageEvidenceRole.NotClassified : ApplicationContexts.RoleOf(p);
        var userBound = project?.Evidence.SelectMany(r => r.Sources).Where(s => s.BoundBy != Detected).Select(s => s.ImportId).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var declined = project?.Declined.Select(d => d.ImportId).ToHashSet(StringComparer.Ordinal) ?? [];
        var name = identity?.Name.Trim();
        // Two application sources both named like the Target Application (two versions or two archives): a conflict, never a silent choice.
        var sameNamed = packages.Count(p => name is not null && RoleOf(p) is PackageEvidenceRole.PrimaryApplicationSource or PackageEvidenceRole.RelatedApplicationSource
            && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        return [.. packages.Select(p =>
        {
            var role = RoleOf(p);
            var roleConfirmed = confirmedRoles.Contains(p.ImportId);
            if (identity is null)
                return new PackageAssociation(p, EvidenceAssociationState.WaitingForTargetApplication, role,
                    "Configure the Target Application to assess this evidence for it.", RoleConfirmed: roleConfirmed);
            if (userBound.Contains(p.ImportId))
                return new PackageAssociation(p, EvidenceAssociationState.Associated, role, $"Confirmed for {name} by you.", UserConfirmed: true, RoleConfirmed: roleConfirmed);
            var record = lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == p.ImportId);
            if (application is null || record?.ApplicationId != application.Id)
            {
                // Not imported for this application: a suggestion when the evidence supports it, never an automatic association.
                if (project?.Declined.Any(d => d.ImportId == p.ImportId && d.RoleKey == ProjectEvidenceService.IgnoredRole) != true
                    && SuggestionFor(p, identity, application is null, confirmedElsewhere) is { } suggestion)
                    return new PackageAssociation(p, EvidenceAssociationState.NeedsReview, role, suggestion.Reason, Suggestion: suggestion.Strength, RoleConfirmed: roleConfirmed);
                return new PackageAssociation(p, EvidenceAssociationState.NotAssociated, role,
                    application is null ? $"No imported evidence is recorded for {name} yet." : "Imported as evidence for another application.", RoleConfirmed: roleConfirmed);
            }
            if (project?.Declined.Any(d => d.ImportId == p.ImportId && d.RoleKey == ProjectEvidenceService.IgnoredRole) == true)
                return new PackageAssociation(p, EvidenceAssociationState.NotAssociated, role, $"Not used for {name} (your decision). The import is kept.");
            if (declined.Contains(p.ImportId) && !userBound.Contains(p.ImportId))
                return new PackageAssociation(p, EvidenceAssociationState.NotAssociated, role, "Removed from this application's evidence earlier.");
            if (roleConfirmed && role is PackageEvidenceRole.RelatedApplicationSource)
                return new(p, EvidenceAssociationState.Associated, role, $"{ApplicationContexts.RoleLabel(role)} of {name} — role confirmed in Source Management.", RoleConfirmed: true);
            return role switch
            {
                PackageEvidenceRole.PrimaryApplicationSource when sameNamed > 1 => new(p, EvidenceAssociationState.NeedsReview, role,
                    $"Conflict: {sameNamed} imported application sources are named {name}. Choose which one is the application source."),
                PackageEvidenceRole.PrimaryApplicationSource when string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase) =>
                    new(p, EvidenceAssociationState.Associated, role, $"The application source of {name}, associated automatically."),
                PackageEvidenceRole.PrimaryApplicationSource => new(p, EvidenceAssociationState.NeedsReview, role,
                    $"Application source named {p.DisplayName}, not {name}: confirm that it is {name}'s source."),
                PackageEvidenceRole.RelatedApplicationSource => new(p, EvidenceAssociationState.NeedsReview, role,
                    "Detected application/contracts evidence may belong to another component or integration. Confirm its role."),
                PackageEvidenceRole.NotClassified => new(p, EvidenceAssociationState.NeedsReview, role,
                    "Its role is not classified yet (its Source Analysis is not available). Refresh sources or confirm its role."),
                _ => new(p, EvidenceAssociationState.Associated, role, $"{ApplicationContexts.RoleLabel(role)} of {name}, associated automatically."),
            };
        })];
    }

    /// <summary>
    /// Why a package that was not imported for the Target Application's workspace application may still be its evidence. Strong: it is the
    /// identity's own package, or the person confirmed it for the same Target Application in another Target Environment (a reviewed association
    /// reused as a suggestion, not copied). Possible — only when no workspace application corresponds to the Target Application (the
    /// "imported, none associated" case): its name is related to the application's name. Names alone never associate anything.
    /// </summary>
    public static (AssociationSuggestion Strength, string Reason)? SuggestionFor(ProjectEvidencePackage package, TargetApplicationIdentity identity, bool noApplication,
        IReadOnlySet<string>? confirmedElsewhere)
    {
        if (identity.ImportId == package.ImportId)
            return (AssociationSuggestion.Strong, $"Strong suggestion: {package.DisplayName} is the package {identity.Name} was registered from. Confirm to associate it.");
        if (confirmedElsewhere?.Contains(package.ImportId) == true)
            return (AssociationSuggestion.Strong, $"Strong suggestion: confirmed for {identity.Name} in another Target Environment. Confirm to use it here too.");
        if (!noApplication) return null;
        var app = Compact(identity.Name);
        var candidate = Compact(package.DisplayName);
        if (app.Length >= 2 && (candidate.Contains(app, StringComparison.Ordinal) || ApplicationContexts.FamilyOf(package.DisplayName) is { } family && Compact(family) == app))
            return (AssociationSuggestion.Possible, $"Possible match: the name {package.DisplayName} is related to {identity.Name} — names alone are weak evidence. Review its content, then confirm or set it aside.");
        return null;
    }

    private static string Compact(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// Writes the automatic associations into the persisted Project evidence (creating it for the Target Application when there is evidence and
    /// the user has not removed it before): adds Detected references for every associated package to each role its content satisfies, removes
    /// Detected references that no longer hold, and never touches the user's own bindings or declines. True when something changed.
    /// </summary>
    public static bool Apply(FrontendAnalysisProfile profile, IReadOnlyList<PackageAssociation> associations, DateTimeOffset now)
    {
        if (TargetApplications.Of(profile) is not { } identity) return false;
        var associated = associations.Where(a => a.State == EvidenceAssociationState.Associated && !a.UserConfirmed).ToList();
        var changed = false;
        if (profile.Project is null)
        {
            // Evidence removed by the user ("Remove evidence") is never recreated behind their back.
            if (associated.Count == 0 || profile.ArchivedProjects?.Any(p => string.Equals(p.ProjectName, identity.Name, StringComparison.OrdinalIgnoreCase)) == true) return false;
            var main = associated.FirstOrDefault(a => a.Role == PackageEvidenceRole.PrimaryApplicationSource)?.Package;
            profile.Project = new EnvironmentProject
            {
                ProjectName = identity.Name, ProjectImportId = main?.ImportId, ArchiveSha256 = main?.ArchiveSha256, SelectedAt = now, Manual = main is null,
                Evidence = [.. ProjectEvidenceRoles.Defaults()],
            };
            changed = true;
        }
        var project = profile.Project;
        if (!string.Equals(project.ProjectName, identity.Name, StringComparison.OrdinalIgnoreCase)) return changed;
        if (project.ProjectImportId is null && associated.FirstOrDefault(a => a.Role == PackageEvidenceRole.PrimaryApplicationSource)?.Package is { } primary)
        {
            project.ProjectImportId = primary.ImportId; project.ArchiveSha256 = primary.ArchiveSha256; project.Manual = false;
            changed = true;
        }
        foreach (var role in project.Evidence.Where(r => r.Origin is not (ProjectEvidenceRoleOrigin.Expected or ProjectEvidenceRoleOrigin.UserAdded)))
        {
            var wanted = associated.Where(a => Fits(a, role)).Select(a => a.Package).ToList();
            // A single-source role the user already chose keeps their choice; several automatic candidates for it are a decision, not a guess.
            if (role.Cardinality == ProjectEvidenceCardinality.Single && (role.Sources.Any(s => s.BoundBy != Detected) || wanted.Count > 1)) wanted = [];
            changed |= role.Sources.RemoveAll(s => s.BoundBy == Detected && wanted.All(p => p.ImportId != s.ImportId)) > 0;
            foreach (var package in wanted)
            {
                if (role.Sources.Any(s => s.ImportId == package.ImportId) || project.Declined.Any(d => d.RoleKey == role.RoleKey && d.ImportId == package.ImportId)) continue;
                role.Sources.Add(ProjectEvidenceResolver.Reference(package, role.Capability, now, Detected));
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Whether an associated package fills a role: by its deterministic role for source roles, by capability for the rest.</summary>
    private static bool Fits(PackageAssociation association, EnvironmentProjectEvidenceBinding role) => role.Capability switch
    {
        ProjectEvidenceCapability.ApplicationSource => association.Role == PackageEvidenceRole.PrimaryApplicationSource,
        ProjectEvidenceCapability.SupportingSource => association.Role == PackageEvidenceRole.SupportingSource,
        ProjectEvidenceCapability.IntegrationSource => false,
        _ => association.Package.Has(role.Capability),
    };
}
