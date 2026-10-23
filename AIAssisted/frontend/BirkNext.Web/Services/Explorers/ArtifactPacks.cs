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

    /// <summary>Whether two of <paramref name="paths"/> (one role's documents) are in the same pack (or both shared in the same module).</summary>
    public static bool AmbiguousWithinPack(IEnumerable<string?> paths, IReadOnlySet<string> roots) =>
        paths.GroupBy(p => PathKey(p, roots), StringComparer.Ordinal).Any(g => g.Count() > 1);

    /// <summary>"Feature Specification: Cart" → "Cart": the spec title without the template's document-type prefix.</summary>
    public static string PackTitle(string title) => TitlePrefix().Replace(title, "").Trim() is { Length: > 0 } t ? t : title;

    [GeneratedRegex(@"^(?:feature\s+)?spec(?:ification)?\s*[:\-—–]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"^(\d{1,5})(?:[-_ ]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureNumber();
}
