namespace BirkNext.Web.Services.Explorers;

/// <summary>
/// One role across the whole project, pack by pack: how many documents were found, in how many modules, which packs it applies to (its own
/// artifact, else the module's or project's shared one — never a sibling pack's), and where a pack has two candidates that nothing tells apart.
/// </summary>
public sealed record ProjectRoleCoverage(
    WorkspaceArtifactType Role,
    int Documents,
    int ModulesWithRole,
    int PacksWithRole,
    int SharedDocuments,
    IReadOnlyList<ArtifactPackSummary> PacksMissing,
    IReadOnlyList<ProjectAmbiguity> Ambiguities,
    IReadOnlyList<string> Paths)
{
    public bool Found => Documents > 0;
}

/// <summary>A genuine ambiguity: same module, same pack (or the module's shared documents), same role, several unresolved candidates.</summary>
public sealed record ProjectAmbiguity(WorkspaceArtifactType Role, string Module, string? Pack, string Where, IReadOnlyList<string> Candidates);

/// <summary>
/// The project's documents as Module → Artifact Packs → roles, across every module — not just the focused one. Built from the explorer
/// context's module overview (the same candidates and per-module resolution the explorers use), so availability is pack-aware: a pack that
/// lacks a Plan is reported even when a sibling pack has one, and a shared artifact satisfies every pack it applies to. Discovery only:
/// nothing here is a review result.
/// </summary>
public sealed record ProjectPackOverview(
    int Modules,
    bool HasModules,
    IReadOnlyList<ArtifactPackSummary> Packs,
    IReadOnlyList<ProjectRoleCoverage> Roles)
{
    /// <summary>Roles every pack needs to be complete for review (constitution and data model are optional and often shared).</summary>
    public static IReadOnlyList<WorkspaceArtifactType> CoreRoles => ArtifactPackReview.CoreRoles;

    public int PackCount => Packs.Count;

    public ProjectRoleCoverage Role(WorkspaceArtifactType role) =>
        Roles.FirstOrDefault(r => r.Role == role) ?? new ProjectRoleCoverage(role, 0, 0, 0, 0, [], [], []);

    public IEnumerable<ProjectAmbiguity> Ambiguities => Roles.SelectMany(r => r.Ambiguities);

    public static ProjectPackOverview Build(ArtifactModuleOverview overview)
    {
        var modules = overview.HasModules ? overview.Modules : [ArtifactModules.ProjectLevel];
        var all = overview.Roles.SelectMany(r => r.Candidates).ToList();
        bool In(ExplorerArtifact a, string module) => !overview.HasModules || a.Module == module;

        // Packs per module, from every role's documents there (the explorers' own grouping).
        var packsByModule = modules.ToDictionary(m => m, m => ArtifactPacks.PacksOf(all.Where(a => In(a, m)), m), StringComparer.Ordinal);
        var packs = packsByModule.Values.SelectMany(p => p).ToList();

        var roles = overview.Roles.Select(role =>
        {
            var candidates = role.Candidates;
            var withRole = 0;
            var missing = new List<ArtifactPackSummary>();
            foreach (var module in modules)
            {
                var own = candidates.Where(a => In(a, module)).ToList();
                // Resolution order PackLocal → ModuleShared → ProjectShared (a monorepo's root documents), as the explorers resolve a pack.
                var shared = own.Any(a => a.Feature is null)
                             || overview.HasModules && module != ArtifactModules.ProjectLevel
                                && candidates.Any(a => a.Module == ArtifactModules.ProjectLevel && a.Feature is null);
                foreach (var pack in packsByModule[module])
                {
                    if (shared || own.Any(a => a.Feature == pack.Pack)) withRole++;
                    else missing.Add(pack);
                }
            }

            // Ambiguity is the module's own resolution (it honours the user's stored choices); the candidates name where it is.
            var ambiguities = new List<ProjectAmbiguity>();
            foreach (var state in role.Modules.Where(m => m.NeedsSelection))
            {
                var own = candidates.Where(a => In(a, state.Module)).ToList();
                var groups = state.Packs > 1
                    ? own.Where(a => a.Feature is not null).GroupBy(a => a.Feature)
                        .Where(g => g.Count() > 1 && g.Count(a => ArtifactPacks.IsPackFile(a.SourcePath ?? a.FileName, role.Role)) != 1)
                    : own.GroupBy(_ => (string?)null);
                foreach (var group in groups)
                {
                    var pack = group.Key is null ? null : packsByModule[state.Module].FirstOrDefault(p => p.Pack == group.Key);
                    var where = pack is not null ? PackName(pack)
                        : !overview.HasModules ? "the project"
                        : state.Module == ArtifactModules.ProjectLevel ? "the project-level folder (outside any module)"
                        : $"module {ArtifactModules.Label(state.Module)}";
                    ambiguities.Add(new ProjectAmbiguity(role.Role, state.Module, group.Key, where, [.. group.Select(a => a.SourcePath ?? a.FileName)]));
                }
            }

            return new ProjectRoleCoverage(role.Role, candidates.Count, role.ModulesWithCandidates, withRole,
                candidates.Count(a => a.Feature is null), missing, ambiguities,
                [.. candidates.Select(a => a.SourcePath ?? a.FileName).OrderBy(p => p, StringComparer.Ordinal)]);
        }).ToList();

        var moduleCount = overview.HasModules ? overview.Modules.Count : 1;
        return new ProjectPackOverview(moduleCount, overview.HasModules, packs, roles);
    }

    /// <summary>"Revision System (001-revisjonssystem)" — the pack's title first, its folder only to tell packs apart.</summary>
    public static string PackName(ArtifactPackSummary pack) => pack.DisplayName == pack.Pack ? pack.Pack : $"{pack.DisplayName} ({pack.Pack})";
}
