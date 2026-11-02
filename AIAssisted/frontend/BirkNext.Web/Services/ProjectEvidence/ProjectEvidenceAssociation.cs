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

/// <summary>One package's association: state, role, and the reason in tester words.</summary>
public sealed record PackageAssociation(ProjectEvidencePackage Package, EvidenceAssociationState State, PackageEvidenceRole Role, string Detail, bool UserConfirmed = false)
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

    public static IReadOnlyList<PackageAssociation> Of(TargetApplicationIdentity? identity, EnvironmentProject? project, SddLifecycleState lifecycle,
        IReadOnlyList<ProjectEvidencePackage> packages, SourceSetupView? setup)
    {
        var application = ApplicationFor(identity, lifecycle);
        // Roles are judged within the package's own application (the one it was imported for); without a Target Application only detected.
        var roles = lifecycle.Applications.SelectMany(a => ApplicationContexts.Packages(lifecycle, a.Id, setup)).ToDictionary(p => p.ImportId, p => p.Role, StringComparer.Ordinal);
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
            if (identity is null)
                return new PackageAssociation(p, EvidenceAssociationState.WaitingForTargetApplication, role,
                    "Configure the Target Application to assess this evidence for it.");
            if (userBound.Contains(p.ImportId))
                return new PackageAssociation(p, EvidenceAssociationState.Associated, role, $"Confirmed for {name} by you.", UserConfirmed: true);
            var record = lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == p.ImportId);
            if (application is null || record?.ApplicationId != application.Id)
                return new PackageAssociation(p, EvidenceAssociationState.NotAssociated, role,
                    application is null ? $"No imported evidence is recorded for {name} yet." : "Imported as evidence for another application.");
            if (declined.Contains(p.ImportId) && !userBound.Contains(p.ImportId))
                return new PackageAssociation(p, EvidenceAssociationState.NotAssociated, role, "Removed from this application's evidence earlier.");
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
