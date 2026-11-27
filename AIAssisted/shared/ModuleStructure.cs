namespace BirkNext.TestPlanning;

/// <summary>
/// The canonical module rule (moved here 2026-11-27 so the backend's Testoversikt and the browser's explorers use ONE implementation; the
/// explorers' <c>ArtifactModules</c> delegates to it). From folder structure only, never from file names or document text.
/// <para><b>Strong</b> module evidence is Spec-Kit structure: a folder that holds <c>.specify/</c>, or a <c>specs/&lt;feature&gt;/</c> folder holding a
/// canonical Spec-Kit document. That folder is a module root; a document belongs to the deepest root it lies under. A root at the project root, or
/// a document under no root, is project level (key <see cref="ProjectLevel"/>).</para>
/// </summary>
public static class ModuleStructure
{
    /// <summary>Key of the project level (documents outside every module root, or a root at the project root).</summary>
    public const string ProjectLevel = "";

    /// <summary>Testoversikt's name for the project's own Spec-Kit specifications (<c>specs/&lt;feature&gt;/…</c> at the project root): explicitly
    /// project-wide features, not a module.</summary>
    public const string ProjectWide = "Prosjektnivå";

    /// <summary>Testoversikt's name for documents outside every module root that are not the project's own Spec-Kit specifications (e.g.
    /// <c>docs/analysis/…</c>): no module association in the source structure.</summary>
    public const string Unassigned = "Uten modultilknytning";

    private static readonly HashSet<string> SpecKitDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "spec", "specification", "plan", "tasks", "data-model", "research", "quickstart",
    };

    public static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The module root this one path proves on its own (strong evidence), or null. The project root is "".</summary>
    public static string? StrongRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = Segments(path);
        var folders = segments[..^1];
        var specify = Array.FindIndex(folders, f => f.Equals(".specify", StringComparison.OrdinalIgnoreCase));
        if (specify >= 0) return string.Join('/', folders[..specify]);
        var specs = Array.FindLastIndex(folders, f => f.Equals("specs", StringComparison.OrdinalIgnoreCase));
        if (specs >= 0 && specs == folders.Length - 2 && SpecKitDocuments.Contains(System.IO.Path.GetFileNameWithoutExtension(segments[^1])))
            return string.Join('/', folders[..specs]);
        return null;
    }

    /// <summary>The module roots evidenced by <paramref name="paths"/> (strong Spec-Kit structure only). The project root is "".</summary>
    public static IReadOnlySet<string> RootsOf(IEnumerable<string?> paths)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (StrongRoot(path) is { } root) roots.Add(root);
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

    /// <summary>
    /// Testoversikt's module of a document: its module root (<c>HendelseService/docs/…Testspesifikasjon.md</c> → <c>HendelseService</c>), else
    /// <see cref="ProjectWide"/> for the project root's own <c>specs/</c> folder, else <see cref="Unassigned"/>.
    /// </summary>
    public static string OverviewModule(string? path, IReadOnlySet<string> roots)
    {
        var module = ModuleOf(path, roots);
        if (module.Length > 0) return module;
        var segments = string.IsNullOrWhiteSpace(path) ? [] : Segments(path);
        return segments.Length >= 2 && segments[0].Equals("specs", StringComparison.OrdinalIgnoreCase) ? ProjectWide : Unassigned;
    }

    /// <summary>Why a document is in its Testoversikt module, in the tester's words.</summary>
    public static string WhyOverviewModule(string? path, IReadOnlySet<string> roots) => OverviewModule(path, roots) switch
    {
        ProjectWide => "Prosjektets egne spesifikasjoner i specs/ på rotnivå: gjelder hele prosjektet, ikke én modul.",
        Unassigned => "Dokumentet ligger utenfor alle modulmapper (ingen Spec-Kit-modul over det).",
        var m => StrongRoot(path) == m ? $"Spesifikasjon i modulen {m}." : $"Dokument under modulmappen {m}.",
    };
}
