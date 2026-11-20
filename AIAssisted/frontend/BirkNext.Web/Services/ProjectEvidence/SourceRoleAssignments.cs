using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>One evidence role a source holds for this Target Application (a binding in this Target Environment's Project evidence).</summary>
public sealed record AssignedRole(EvidenceRoleRow Row, ProjectEvidenceSourceReference Reference, bool Primary)
{
    public string Label => Primary ? $"{Row.Role.Label} (primary)" : Row.Role.Label;
}

/// <summary>A role a source could be given here: supported by what the source contains, or not (with the reason; never offered then).</summary>
public sealed record RoleOption(EvidenceRoleRow Row, bool Supported, bool Assigned, string? Reason);

/// <summary>
/// One source of the Target Application with its assigned roles. Four separate facts, never one "Ready": <see cref="Detected"/> capabilities (what
/// the source contains), <see cref="Roles"/> (what it contributes to THIS application — several allowed), <see cref="ReviewStatus"/> (who decided)
/// and <see cref="Completeness"/> (whether its contribution is complete).
/// </summary>
public sealed record SourceRoles(ProjectEvidencePackage Package, IReadOnlyList<AssignedRole> Roles, PackageAssociation? Association)
{
    public IReadOnlyList<PackageCapability> Detected => Package.Capabilities;

    /// <summary>Who decided the roles: "Confirmed by you", "Associated automatically", "Partly confirmed" (some roles confirmed, some automatic).</summary>
    public string ReviewStatus
    {
        get
        {
            if (Roles.Count == 0) return Association?.State == EvidenceAssociationState.NeedsReview ? "Needs review" : "No role assigned";
            var confirmed = Roles.Count(r => r.Reference.BoundBy is "User" or "ProjectSelection" or "Iqr");
            return confirmed == Roles.Count ? "Confirmed by you" : confirmed == 0 ? "Associated automatically" : "Partly confirmed";
        }
    }

    public string ReviewTone => ReviewStatus switch { "Confirmed by you" => "available", "Needs review" => "partial", _ => "neutral" };

    /// <summary>Whether this source's contribution is complete, judged per assigned role on that role's capability only: "Complete", or
    /// "Partial — contracts incomplete" (the role is still valid: a confirmed role with partial evidence is not a wrong role).</summary>
    public (string Label, string Tone) Completeness
    {
        get
        {
            if (Roles.Count == 0) return ("—", "neutral");
            var incomplete = Roles.Select(r => (r.Row.Role, Capability: Package.Capability(r.Row.Role.Capability == ProjectEvidenceCapability.SupportingSource
                    ? Package.Has(ProjectEvidenceCapability.SupportingSource) ? ProjectEvidenceCapability.SupportingSource : ProjectEvidenceCapability.ApplicationSource
                    : r.Row.Role.Capability)))
                .Where(x => x.Capability is null || x.Capability.Status != PackageCapabilityStatus.Ready)
                .Select(x => x.Role.Label.ToLowerInvariant()).Distinct().ToList();
            return incomplete.Count == 0 ? ("Complete", "available") : ($"Partial — {string.Join(", ", incomplete)} incomplete", "partial");
        }
    }
}

/// <summary>
/// Project Evidence per source (2026-11-20): the roles each source has for THIS Target Environment's Target Application, read from its bindings —
/// the per-target decision. A source's global classification (Import Project / Source Management) only suggests roles; it never decides them here,
/// so changing a role for one Target Application never changes another's. Pure; no I/O.
/// </summary>
public static class SourceRoleAssignments
{
    /// <summary>Every source that holds a role here, and every source associated or awaiting review without one, in that order.</summary>
    public static IReadOnlyList<SourceRoles> Sources(ProjectEvidenceView view)
    {
        var bound = view.Packages.Select(p => new SourceRoles(p, RolesOf(view, p.ImportId), view.Associations.FirstOrDefault(a => a.Package.ImportId == p.ImportId)))
            .Where(s => s.Roles.Count > 0 || s.Association?.State == EvidenceAssociationState.Associated).ToList();
        return [.. bound.OrderByDescending(s => s.Roles.Any(r => r.Primary)).ThenByDescending(s => s.Roles.Count).ThenBy(s => s.Package.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<AssignedRole> RolesOf(ProjectEvidenceView view, string importId) =>
        [.. view.Rows.SelectMany(row => row.Role.Sources.Where(s => s.ImportId == importId).Take(1)
            .Select(s => new AssignedRole(row, s, row.Role.RoleKey == ProjectEvidenceRoles.ApplicationSource && view.Project?.ProjectImportId == importId)))];

    /// <summary>The roles the editor offers for a source: every role of the project; a role whose capability the source does not contain is shown
    /// but cannot be chosen (BirkNext never assigns a capability a source does not have).</summary>
    public static IReadOnlyList<RoleOption> Options(ProjectEvidenceView view, ProjectEvidencePackage package) =>
        [.. view.Rows.Select(row =>
        {
            var supported = ProjectEvidencePackages.Satisfies(package, row.Role.Capability);
            var assigned = row.Role.Sources.Any(s => s.ImportId == package.ImportId);
            return new RoleOption(row, supported || assigned, assigned,
                supported ? null : $"Not detected in {package.DisplayName}: it contains no {ProjectEvidencePackages.CapabilityLabel(row.Role.Capability).ToLowerInvariant()}.");
        })];

    /// <summary>"Application source: M2LB (primary) + 1 contributing", or what is missing. Several application sources are allowed; one is primary.</summary>
    public static string ApplicationSourceSummary(ProjectEvidenceView view)
    {
        var row = view.Row(ProjectEvidenceRoles.ApplicationSource);
        if (row is null || row.Bound.Count == 0) return "Application source not selected";
        var primary = row.Bound.FirstOrDefault(b => b.ImportId == view.Project?.ProjectImportId);
        var others = row.Bound.Count - (primary is null ? 0 : 1);
        return primary is null
            ? $"Application source: {string.Join(", ", row.Bound.Select(b => b.DisplayName))} — choose the primary one"
            : $"Application source: {primary.DisplayName} (primary){(others > 0 ? $" + {others} contributing" : "")}";
    }
}
