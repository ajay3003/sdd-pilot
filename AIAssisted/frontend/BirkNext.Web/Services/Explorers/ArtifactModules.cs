namespace BirkNext.Web.Services.Explorers;

/// <summary>
/// Module (sub-project) identity of project documents — the one canonical resolver every consumer uses (Explorers, Import Project, Documentation
/// Drift, the workspace projection and Recommended Workflow). From folder structure only, never a guess from file names or document text, and
/// never a list of project-specific or folder names.
/// <para>
/// <b>Strong</b> module evidence is Spec-Kit structure: a folder that holds <c>.specify/</c>, or a <c>specs/&lt;feature&gt;/</c> folder holding a
/// canonical Spec-Kit document (<c>spec.md</c>, <c>plan.md</c>, <c>tasks.md</c> …). That folder is a module root. <b>Weak</b> structure — a
/// document lying loose in some <c>specs/</c> folder (dated design notes, a documentation tree's own <c>specs/</c>) — never creates a module: the
/// document belongs to the deepest strong root it lives under (<c>Frontend/docs/…/specs/x-design.md</c> → <c>Frontend</c>), else to the
/// project level. A nested folder is a module of its own only with its own strong evidence. A root at the project root, or a document under no
/// root, is <b>project-level</b> (key <see cref="ProjectLevel"/>).
/// </para>
/// </summary>
public static class ArtifactModules
{
    /// <summary>Key of the project-level group (documents outside any module root, or a root at the project root).</summary>
    public const string ProjectLevel = "";
    public const string ProjectLevelLabel = "Project-level";

    /// <summary>Spec-Kit's own document names inside a feature folder: the strong evidence that a <c>specs/</c> folder is a Spec-Kit one.</summary>
    private static readonly HashSet<string> SpecKitDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "spec", "specification", "plan", "tasks", "data-model", "research", "quickstart",
    };

    /// <summary>The module roots evidenced by <paramref name="paths"/> (relative, '/' or '\' separated): strong Spec-Kit structure only. The
    /// project root is "".</summary>
    public static IReadOnlySet<string> RootsOf(IEnumerable<string?> paths)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (StrongRoot(path) is { } root) roots.Add(root);
        return roots;
    }

    /// <summary>The module root this one path proves on its own (strong evidence), or null.</summary>
    public static string? StrongRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = Segments(path);
        var folders = segments[..^1];
        var specify = Array.FindIndex(folders, f => f.Equals(".specify", StringComparison.OrdinalIgnoreCase));
        if (specify >= 0) return string.Join('/', folders[..specify]);
        // specs/<feature>/<Spec-Kit document>: the file sits in a feature folder directly under specs/.
        var specs = Array.FindLastIndex(folders, f => f.Equals("specs", StringComparison.OrdinalIgnoreCase));
        if (specs >= 0 && specs == folders.Length - 2 && SpecKitDocuments.Contains(System.IO.Path.GetFileNameWithoutExtension(segments[^1])))
            return string.Join('/', folders[..specs]);
        return null;
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

    /// <summary>
    /// The feature folder of a document within its module's own <c>specs/</c> folder (<c>&lt;root&gt;/specs/&lt;feature&gt;/…</c>), or null — a
    /// documentation tree's <c>specs/</c> folder below the module never forms an Artifact Pack.
    /// </summary>
    public static string? FeatureOf(string? path, IReadOnlySet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var module = ModuleOf(path, roots);
        var segments = Segments(path);
        var skip = module.Length == 0 ? 0 : Segments(module).Length;
        var rest = segments[skip..^1];
        return rest.Length >= 2 && rest[0].Equals("specs", StringComparison.OrdinalIgnoreCase) ? rest[1] : null;
    }

    /// <summary>
    /// A document lying loose in a <c>specs/</c> folder (no feature folder, no Spec-Kit name): supporting/design documentation, not a canonical
    /// Spec-Kit artifact. Structural only; it never names a folder.
    /// </summary>
    public static bool IsLooseSpecsDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = Segments(path);
        var folders = segments[..^1];
        return folders.Length > 0 && folders[^1].Equals("specs", StringComparison.OrdinalIgnoreCase)
            && !SpecKitDocuments.Contains(System.IO.Path.GetFileNameWithoutExtension(segments[^1]));
    }

    /// <summary>
    /// A loose <c>specs/</c> document inside a nested documentation tree (the <c>specs/</c> folder is at least two folders deep, e.g.
    /// <c>&lt;module&gt;/docs/design/specs/x-design.md</c> or <c>docs/design/specs/x.md</c>): supporting/design documentation rather than a canonical
    /// review artifact. A project's or module's own top-level <c>specs/</c> folder is not nested, so its loose specifications keep their role.
    /// </summary>
    public static bool IsNestedSupportingDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = Segments(path);
        var folders = segments[..^1];
        // The same structural rule for a nested documentation tree's plans/ and tasks/ folders (dated plan notes are not a module's Plan).
        return folders.Length >= 3 && RoleFolders.Contains(folders[^1]) && !SpecKitDocuments.Contains(System.IO.Path.GetFileNameWithoutExtension(segments[^1]));
    }

    private static readonly HashSet<string> RoleFolders = new(StringComparer.OrdinalIgnoreCase) { "specs", "plans", "tasks" };

    /// <summary>Why a document belongs where it does (advanced diagnostics): "Frontend · Spec-Kit root (specs/001-x/spec.md)" / "Frontend · supporting
    /// document under the module" / "Project-level · no module structure".</summary>
    public static string Explain(string? path, IReadOnlySet<string> roots)
    {
        var module = ModuleOf(path, roots);
        if (StrongRoot(path) is { } own && own == module && module.Length > 0) return $"{Label(module)} · Spec-Kit module root";
        if (module.Length > 0) return $"{Label(module)} · supporting document under the module";
        return IsLooseSpecsDocument(path) ? "Project-level · supporting document (no module structure)" : "Project-level · shared / project-wide";
    }

    public static string Label(string? module) => string.IsNullOrEmpty(module) ? ProjectLevelLabel : module;

    private static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
