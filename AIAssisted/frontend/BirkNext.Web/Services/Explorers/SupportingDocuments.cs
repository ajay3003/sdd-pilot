using System.Text.RegularExpressions;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services.Explorers;

/// <summary>
/// Deterministic rules for authored documentation that supports an Artifact Pack without being one of its roles (quickstart, contracts,
/// decisions, checklists …). Discovery only: which documents are kept, what kind they are and which other documents explicitly reference them.
/// Strong evidence (location in the pack folder, an explicit path reference) decides; a file name alone never makes a document relevant.
/// </summary>
public static partial class SupportingDocuments
{
    public const int MaxBytes = 512 * 1024;

    /// <summary>
    /// The documents of a discovery that are kept as supporting documentation: readable Markdown that no role claimed (unclassified or needing
    /// review), not generated documentation, and either inside a feature pack folder or explicitly referenced by a document in one.
    /// Every other Markdown file of the project is left alone — supporting discovery never reads the whole repository.
    /// </summary>
    public static IReadOnlyList<DiscoveredDocument> Retained(IReadOnlyList<DiscoveredDocument> documents, Func<string, string?> text)
    {
        var candidates = documents.Where(d => d.Status is ArtifactDiscoveryStatus.Unclassified or ArtifactDiscoveryStatus.NeedsReview
            && IsMarkdown(d.RelativePath) && !IsGenerated(d.RelativePath) && text(d.RelativePath) is { } t && System.Text.Encoding.UTF8.GetByteCount(t) <= MaxBytes).ToList();
        if (candidates.Count == 0) return [];
        var paths = candidates.Select(c => c.RelativePath).ToList();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in documents.Where(d => ArtifactModules.FeatureOf(d.RelativePath) is not null))
            if (text(source.RelativePath) is { } content)
                referenced.UnionWith(References(source.RelativePath, content, paths));
        return [.. candidates.Where(c => ArtifactModules.FeatureOf(c.RelativePath) is not null || referenced.Contains(c.RelativePath))];
    }

    /// <summary>Generated documentation by location (an <c>autodoc/</c> or <c>generated-docs/</c> folder). Kept apart from authored documents.</summary>
    public static bool IsGenerated(string? path) => DataModelLayers.IsGenerated(path is null ? null : "/" + path.Replace('\\', '/').TrimStart('/'));

    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>What kind of supporting document a path is, from its folder or file name: a display label, never authority.</summary>
    public static string KindOf(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        var name = p[(p.LastIndexOf('/') + 1)..];
        if (p.Contains("/contracts/") || name.StartsWith("contract")) return "Contract";
        if (p.Contains("/decisions/") || p.Contains("/adr/") || p.Contains("/adrs/") || AdrName().IsMatch(name)) return "Decision (ADR)";
        if (p.Contains("/checklists/") || name.Contains("checklist")) return "Checklist";
        if (name.StartsWith("quickstart")) return "Quickstart";
        if (name.StartsWith("research")) return "Research";
        if (name.Contains("security")) return "Security";
        if (name.Contains("privacy") || name.Contains("gdpr")) return "Privacy";
        if (name.Contains("architecture")) return "Architecture";
        return "Supporting documentation";
    }

    /// <summary>
    /// The candidate paths a document explicitly references: Markdown links and path mentions (<c>[x](contracts/events.md)</c>,
    /// <c>`specs/001-x/spec.md`</c>, a project-structure tree line) resolved against the document's folder, the module root, or — for a bare file
    /// name in a structure listing — the one candidate with that name in the document's own folder tree. Ambiguous names resolve to nothing.
    /// </summary>
    public static IReadOnlyList<string> References(string fromPath, string text, IEnumerable<string> candidatePaths)
    {
        var candidates = candidatePaths.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        var from = Normalize(fromPath);
        var folder = from.Contains('/') ? from[..from.LastIndexOf('/')] : "";
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in MarkdownPath().Matches(text))
        {
            var token = match.Value.Trim().TrimStart('.', '/').Split('#')[0];
            if (match.Value.StartsWith("../", StringComparison.Ordinal)) token = match.Value.Split('#')[0];
            if (token.Length == 0) continue;
            var resolved = Resolve(folder, token, candidates, from);
            if (resolved is not null && resolved != from) found.Add(resolved);
        }
        return [.. found.Order(StringComparer.Ordinal)];
    }

    private static string? Resolve(string folder, string token, IReadOnlyList<string> candidates, string from)
    {
        var relative = Combine(folder, token);
        if (relative is not null && candidates.Contains(relative, StringComparer.Ordinal)) return relative;
        if (token.Contains('/'))
        {
            // A path written from the module or project root ("specs/001-x/spec.md"): the one candidate ending with it.
            var suffix = candidates.Where(c => c.EndsWith("/" + token, StringComparison.Ordinal) || c == token).ToList();
            return suffix.Count == 1 ? suffix[0] : null;
        }
        // A bare file name ("events.md" in a structure tree): the one candidate with that name in the document's own folder tree.
        var named = candidates.Where(c => (folder.Length == 0 || c.StartsWith(folder + "/", StringComparison.Ordinal))
            && c[(c.LastIndexOf('/') + 1)..].Equals(token, StringComparison.Ordinal) && c != from).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static string? Combine(string folder, string token)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in token.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    [GeneratedRegex(@"(?<![\w/.-])(?:\.{1,2}/)*[\w][\w./-]*\.(?:md|markdown)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownPath();

    [GeneratedRegex(@"^(adr[-_]?\d+|\d{3,4}[-_].*decision)", RegexOptions.CultureInvariant)]
    private static partial Regex AdrName();
}
