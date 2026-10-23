using System.Text.RegularExpressions;

namespace BirkNext.Web.Services.Explorers;

/// <summary>Where an artifact applies: its own feature pack, the whole module (shared by its packs), or the whole project.</summary>
public enum ArtifactScope { Pack, Module, Project }

/// <summary>
/// One feature pack of a module: the artifacts that belong together (a Spec-Kit <c>specs/NNN-name</c> folder with its spec, plan, tasks,
/// data model …). Identity is the module plus the pack's folder — never a title — so the same pack keeps its identity across revisions.
/// </summary>
public sealed record ArtifactPackSummary(string Module, string Pack, string? FeatureId, string DisplayName, IReadOnlyList<WorkspaceArtifactType> RolesPresent, int Artifacts)
{
    public string Key => ArtifactPacks.Key(Module, Pack);
}

/// <summary>
/// Module → Artifact Packs → Artifacts, from folder structure only (the same evidence as <see cref="ArtifactModules"/>): a document in a
/// feature folder belongs to that pack; a document of the module outside any feature folder is shared by the module's packs; a document
/// outside any module is project-level. One shared model for every explorer and review — never grouped per explorer, never by title.
/// </summary>
public static partial class ArtifactPacks
{
    public static string Key(string module, string pack) => $"{module}|{pack}";

    /// <summary>The leading feature number of a pack folder ("003-multi-entity" → "003"), or null.</summary>
    public static string? FeatureIdOf(string? pack) => pack is null ? null : FeatureNumber().Match(pack) is { Success: true } m ? m.Groups[1].Value : null;

    public static ArtifactScope ScopeOf(ExplorerArtifact artifact) =>
        artifact.Feature is not null ? ArtifactScope.Pack : artifact.Module.Length > 0 ? ArtifactScope.Module : ArtifactScope.Project;

    public static string ScopeLabel(ExplorerArtifact artifact) => ScopeOf(artifact) switch
    {
        ArtifactScope.Pack => $"Pack {artifact.Feature}",
        ArtifactScope.Module => "Shared module artifact",
        _ => "Shared project artifact",
    };

    /// <summary>The packs evidenced by <paramref name="artifacts"/> (one module's artifacts of every role), in folder order.</summary>
    public static IReadOnlyList<ArtifactPackSummary> PacksOf(IEnumerable<ExplorerArtifact> artifacts, string module) =>
        [.. artifacts.Where(a => a.Feature is not null).GroupBy(a => a.Feature!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ArtifactPackSummary(module, g.Key, FeatureIdOf(g.Key),
                g.FirstOrDefault(a => a.Role == WorkspaceArtifactType.Specification)?.DisplayName is { Length: > 0 } title && title != g.First(a => a.Role == WorkspaceArtifactType.Specification).FileName ? PackTitle(title) : g.Key,
                [.. g.Select(a => a.Role).Distinct().OrderBy(r => r)], g.Count()))];

    /// <summary>
    /// The pack key of a document path among <paramref name="roots"/>: module plus feature folder (empty for a shared document). Two documents
    /// of one role conflict only when they share this key — documents in sibling packs never compete.
    /// </summary>
    public static string PathKey(string? path, IReadOnlySet<string> roots) => Key(ArtifactModules.ModuleOf(path, roots), ArtifactModules.FeatureOf(path) ?? "");

    /// <summary>
    /// Whether two of <paramref name="paths"/> (one role's documents) compete in the same pack (or both shared in the same module). A pack whose
    /// candidates include exactly one Spec-Kit feature file for the role (<see cref="IsPackFile"/>) is not ambiguous: that file is the pack's.
    /// </summary>
    public static bool AmbiguousWithinPack(IEnumerable<string?> paths, IReadOnlySet<string> roots, WorkspaceArtifactType role) =>
        paths.GroupBy(p => PathKey(p, roots), StringComparer.Ordinal).Any(g => g.Count() > 1 && g.Count(p => IsSpecKitFile(p, role)) != 1);

    /// <summary>
    /// The role's Spec-Kit location: its feature file in a pack (<see cref="IsPackFile"/>), or for the constitution
    /// <c>.specify/memory/constitution.md</c> (of the project or a module). Other documents of the role are supporting material.
    /// </summary>
    public static bool IsSpecKitFile(string? path, WorkspaceArtifactType role) =>
        IsPackFile(path, role)
        || role == WorkspaceArtifactType.Constitution && path is not null
           && (path.Replace('\\', '/').Equals(".specify/memory/constitution.md", StringComparison.OrdinalIgnoreCase)
               || path.Replace('\\', '/').EndsWith("/.specify/memory/constitution.md", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Spec-Kit file of a role in a feature folder (<c>spec.md</c>, <c>plan.md</c>, <c>tasks.md</c>, <c>data-model.md</c>).</summary>
    public static string? PackFileName(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Specification => "spec.md",
        WorkspaceArtifactType.Plan => "plan.md",
        WorkspaceArtifactType.Tasks => "tasks.md",
        WorkspaceArtifactType.DataModel => "data-model.md",
        WorkspaceArtifactType.Constitution => "constitution.md",
        _ => null,
    };

    /// <summary>
    /// The document is the role's Spec-Kit file directly in its feature folder (<c>specs/002-x/spec.md</c>) — structure, not a guess from the
    /// title. Another document of the role in the same pack (a quickstart, a contract under <c>contracts/</c>) is supporting material.
    /// </summary>
    public static bool IsPackFile(string? path, WorkspaceArtifactType role)
    {
        if (path is null || PackFileName(role) is not { } file || ArtifactModules.FeatureOf(path) is not { } feature) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith($"/{feature}/{file}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Feature Specification: Cart" → "Cart": the spec title without the template's document-type prefix.</summary>
    public static string PackTitle(string title) => TitlePrefix().Replace(title, "").Trim() is { Length: > 0 } t ? t : title;

    [GeneratedRegex(@"^(?:feature\s+)?spec(?:ification)?\s*[:\-—–]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"^(\d{1,5})(?:[-_ ]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureNumber();
}
