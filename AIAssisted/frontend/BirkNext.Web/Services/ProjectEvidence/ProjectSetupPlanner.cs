using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>How strongly an imported package is evidence for the chosen project. Strong is selected by default; Possible never is.</summary>
public enum EvidenceMatchStrength { Strong, Possible }

/// <summary>A logical project the picker offers: a detected application/system, a documentation project, or a project named elsewhere.</summary>
public sealed record ProjectOption(string Name, string Group, ProjectEvidencePackage? Package, string Reason);

/// <summary>One package for one evidence role in the Project evidence checklist, with why it is (or is not) suggested.</summary>
public sealed record ChecklistItem(string RoleKey, ProjectEvidencePackage Package, EvidenceMatchStrength Strength, bool Selected, string Reason, bool Bound, bool Declined)
{
    public string Badge => Bound ? "Bound" : Declined ? "Deselected earlier" : Strength == EvidenceMatchStrength.Strong ? "Strong match" : "Possible match";
}

/// <summary>One evidence role in the checklist: the suggested packages and the other compatible imported packages that can be added.</summary>
public sealed record ChecklistGroup(EnvironmentProjectEvidenceBinding Role, IReadOnlyList<ChecklistItem> Items, IReadOnlyList<ProjectEvidencePackage> Addable);

/// <summary>
/// Project setup as a checklist: pick the logical project (a system — never an infrastructure, contract, shared-library or generated-docs
/// package), then confirm the evidence packages per role. Suggestions are deterministic and explained; only strong evidence is preselected:
/// the chosen project's own package, an existing binding, or an import recorded under the project's name. A package whose name merely
/// resembles the project is a Possible match, never preselected. Explicit deselections are remembered and never silently undone. The result
/// is applied to the one canonical binding model (<see cref="EnvironmentProject.Evidence"/>) — no second configuration.
/// </summary>
public static class ProjectSetupPlanner
{
    public const string SuggestedGroup = "Suggested projects", OtherGroup = "Other detected applications/systems", KnownGroup = "Projects named in Target Environments";

    /// <summary>A logical project candidate: application source, or a documentation project without source. Infrastructure-, contract-,
    /// supporting-source- and generated-docs-only packages are evidence for a project, never the project — an infrastructure repository that
    /// also holds a few documents is still infrastructure evidence.</summary>
    public static bool IsLogicalProjectCandidate(ProjectEvidencePackage package) =>
        package.IsApplicationCandidate
        || package.Has(ProjectEvidenceCapability.Documentation) && !package.Has(ProjectEvidenceCapability.SupportingSource) && !package.Has(ProjectEvidenceCapability.Infrastructure);

    /// <summary>The picker's options, grouped: detected applications first, then documentation projects, then project names already used by a
    /// Target Environment (including manually created ones). One option per name.</summary>
    public static IReadOnlyList<ProjectOption> Options(IReadOnlyList<ProjectEvidencePackage> packages, IEnumerable<string> knownProjectNames)
    {
        var options = new List<ProjectOption>();
        bool Has(string name) => options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var package in packages.Where(p => p.IsApplicationCandidate).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (!Has(package.DisplayName)) options.Add(new(package.DisplayName, SuggestedGroup, package, package.ClassificationReason));
        foreach (var package in packages.Where(p => !p.IsApplicationCandidate && IsLogicalProjectCandidate(p)).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (!Has(package.DisplayName)) options.Add(new(package.DisplayName, OtherGroup, package, $"Documentation project: {package.DocumentRoleSummary}."));
        foreach (var name in knownProjectNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            if (!Has(name)) options.Add(new(name, KnownGroup, null, "Already named by a Target Environment."));
        return options;
    }

    /// <summary>Options whose name contains the query (case-insensitive). Package file names are never searched — a Terraform archive is not a project.</summary>
    public static IReadOnlyList<ProjectOption> Search(IReadOnlyList<ProjectOption> options, string? query) =>
        string.IsNullOrWhiteSpace(query) ? options : [.. options.Where(o => o.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))];

    private static readonly string[] OwnEvidenceRoles =
        [ProjectEvidenceRoles.ApplicationSource, ProjectEvidenceRoles.Documentation, ProjectEvidenceRoles.Contracts, ProjectEvidenceRoles.GeneratedDocumentation];

    private static string Compact(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>The project name's distinctive tokens (letters/digits, at least three characters).</summary>
    private static IReadOnlyList<string> Tokens(string text) =>
        [.. System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(t => t.Length >= 3).Distinct()];

    /// <summary>The package's name or archive contains a distinctive token of <paramref name="name"/> — a resemblance only (Possible match).</summary>
    public static bool NameResembles(ProjectEvidencePackage package, string name)
    {
        var haystack = Compact(package.DisplayName) + " " + Compact(package.ArchiveFileName);
        return Tokens(name).Any(haystack.Contains);
    }

    /// <summary>A package names expected evidence ("SkoleAdapter source" ← SkoleAdapter.zip, or a recognized logical source named so).</summary>
    public static bool NamesExpected(ProjectEvidencePackage package, string expectedLabel)
    {
        var generic = new HashSet<string>(["source", "sources", "package", "documentation", "docs", "infrastructure", "contracts", "schema", "evidence"], StringComparer.OrdinalIgnoreCase);
        var tokens = Tokens(expectedLabel).Where(t => !generic.Contains(t)).ToList();
        if (tokens.Count == 0) return false;
        var haystack = Compact(package.DisplayName) + " " + Compact(package.ArchiveFileName) + " " + string.Join(" ", package.LogicalSources.Select(Compact));
        return tokens.All(haystack.Contains);
    }

    /// <summary>
    /// The checklist for <paramref name="projectName"/>: per evidence role (the existing project's roles, else the defaults), the suggested
    /// packages with strength, default selection and reason, plus the other compatible imported packages that can be added by hand.
    /// </summary>
    public static IReadOnlyList<ChecklistGroup> Checklist(string projectName, ProjectEvidencePackage? main, IReadOnlyList<ProjectEvidencePackage> packages, EnvironmentProject? existing)
    {
        var roles = existing?.Evidence ?? [.. ProjectEvidenceRoles.Defaults()];
        var declined = existing?.Declined ?? [];
        var groups = new List<ChecklistGroup>();
        foreach (var role in roles)
        {
            var items = new List<ChecklistItem>();
            // A bound package is always listed (and stays selected): saving the checklist must never drop an existing binding because the
            // package's capability is not detected at the moment (analysis pending or re-analyzed) — the user removes it explicitly.
            foreach (var package in packages.Where(p => ProjectEvidencePackages.Satisfies(p, role.Capability) || role.Sources.Any(s => s.ImportId == p.ImportId)))
            {
                var bound = role.Sources.Any(s => s.ImportId == package.ImportId);
                var isDeclined = declined.Any(d => d.RoleKey == role.RoleKey && d.ImportId == package.ImportId);
                (EvidenceMatchStrength Strength, string Reason)? match =
                    bound ? (EvidenceMatchStrength.Strong, EvidenceRoleRow.OriginLabel(role.Sources.First(s => s.ImportId == package.ImportId)))
                    : role.Origin == ProjectEvidenceRoleOrigin.Expected ? NamesExpected(package, role.Label) ? (EvidenceMatchStrength.Possible, $"Named like the expected evidence “{role.Label}” — review") : null
                    : main is not null && package.ImportId == main.ImportId && OwnEvidenceRoles.Contains(role.RoleKey, StringComparer.Ordinal)
                        ? (EvidenceMatchStrength.Strong, $"The project's own package provides it ({ProjectEvidencePackages.WhyMatches(package, role.Capability)})")
                    : main is null || package.ImportId != main.ImportId
                        ? string.Equals(package.DisplayName, projectName, StringComparison.OrdinalIgnoreCase) && role.Cardinality == ProjectEvidenceCardinality.Multiple
                            ? (EvidenceMatchStrength.Strong, $"Imported as project {projectName}")
                        : NameResembles(package, projectName) ? (EvidenceMatchStrength.Possible, "Possible match · name similarity only — review") : null
                    : null;
                if (match is not { } m) continue;
                // A single-source role offers one strong default; further strong candidates stay a decision.
                var selected = (bound || m.Strength == EvidenceMatchStrength.Strong && !isDeclined)
                    && !(role.Cardinality == ProjectEvidenceCardinality.Single && !bound && items.Any(i => i.Selected));
                items.Add(new(role.RoleKey, package, m.Strength, selected, isDeclined && !bound ? $"{m.Reason} · deselected earlier" : m.Reason, bound, isDeclined && !bound));
            }
            var listed = items.Select(i => i.Package.ImportId).ToHashSet(StringComparer.Ordinal);
            var addable = packages.Where(p => ProjectEvidencePackages.Satisfies(p, role.Capability) && !listed.Contains(p.ImportId)).ToList();
            groups.Add(new(role, [.. items.OrderByDescending(i => i.Selected).ThenBy(i => i.Strength).ThenBy(i => i.Package.DisplayName, StringComparer.OrdinalIgnoreCase)],
                [.. addable.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)]));
        }
        return groups;
    }

    /// <summary>The user's decision for one role: the packages selected. Suggested strong packages left unchecked become declines.</summary>
    public sealed record RoleDecision(string RoleKey, IReadOnlyList<string> SelectedImportIds, IReadOnlyList<string> DeclinedImportIds);

    /// <summary>
    /// Applies checklist decisions to <paramref name="project"/>'s canonical bindings: selected packages are bound (same references as the role
    /// table), deselected ones unbound, declined suggestions remembered. Returns false when nothing changed.
    /// </summary>
    public static bool Apply(EnvironmentProject project, IReadOnlyList<ProjectEvidencePackage> packages, IReadOnlyList<RoleDecision> decisions, DateTimeOffset now)
    {
        var changed = false;
        foreach (var decision in decisions)
        {
            if (project.Evidence.FirstOrDefault(r => r.RoleKey == decision.RoleKey) is not { } role) continue;
            foreach (var source in role.Sources.Where(s => s.ImportId is not null && !decision.SelectedImportIds.Contains(s.ImportId)).ToList())
                changed |= ProjectEvidenceResolver.Unbind(project, role.RoleKey, source.ImportId!);
            foreach (var importId in decision.SelectedImportIds)
                if (!role.Sources.Any(s => s.ImportId == importId) && packages.FirstOrDefault(p => p.ImportId == importId) is { } package)
                    changed |= ProjectEvidenceResolver.Bind(project, role.RoleKey, package, now, "ProjectSelection");
            foreach (var importId in decision.DeclinedImportIds)
                if (!project.Declined.Any(d => d.RoleKey == role.RoleKey && d.ImportId == importId))
                { project.Declined.Add(new ProjectEvidenceDecline { RoleKey = role.RoleKey, ImportId = importId, At = now }); changed = true; }
            changed |= project.Declined.RemoveAll(d => d.RoleKey == role.RoleKey && decision.SelectedImportIds.Contains(d.ImportId)) > 0;
        }
        return changed;
    }
}
