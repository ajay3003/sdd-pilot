namespace BirkNext.Web.Services.Explorers;

/// <summary>
/// Module (sub-project) identity of project documents, from folder structure only. A folder that holds a Spec-Kit <c>.specify/</c> or
/// <c>specs/</c> folder is a Spec-Kit project root; a document belongs to the deepest such root it lives under. A root at the archive or
/// project root, or a document under no root, is <b>project-level</b> (key <see cref="ProjectLevel"/>) — never a guess from file names or
/// document text. Pure and deterministic: no project-specific names.
/// </summary>
public static class ArtifactModules
{
    /// <summary>Key of the project-level group (documents outside any module root, or a root at the project root).</summary>
    public const string ProjectLevel = "";
    public const string ProjectLevelLabel = "Project-level";

    private static readonly string[] RootMarkers = [".specify", "specs"];

    /// <summary>The Spec-Kit roots evidenced by <paramref name="paths"/> (relative, '/' or '\' separated). The project root is "".</summary>
    public static IReadOnlySet<string> RootsOf(IEnumerable<string?> paths)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var folders = Segments(path)[..^1];
            var marker = Array.FindIndex(folders, f => RootMarkers.Contains(f, StringComparer.OrdinalIgnoreCase));
            if (marker >= 0) roots.Add(string.Join('/', folders[..marker]));
        }
        return roots;
    }

    /// <summary>The module key of a document: its deepest non-project root, else <see cref="ProjectLevel"/>.</summary>
    public static string ModuleOf(string? path, IReadOnlySet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return ProjectLevel;
        var normalized = string.Join('/', Segments(path));
        return roots.Where(r => r.Length > 0 && normalized.StartsWith(r + "/", StringComparison.Ordinal))
            .OrderByDescending(r => r.Length).FirstOrDefault() ?? ProjectLevel;
    }

    /// <summary>The Spec-Kit feature folder of a document (<c>specs/001-name</c> → <c>001-name</c>), or null. Grouping metadata only, never authority.</summary>
    public static string? FeatureOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var folders = Segments(path)[..^1];
        var specs = Array.FindIndex(folders, f => f.Equals("specs", StringComparison.OrdinalIgnoreCase));
        return specs >= 0 && specs + 1 < folders.Length ? folders[specs + 1] : null;
    }

    public static string Label(string? module) => string.IsNullOrEmpty(module) ? ProjectLevelLabel : module;

    private static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
