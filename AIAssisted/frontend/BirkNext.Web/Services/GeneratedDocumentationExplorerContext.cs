using BirkNext.GeneratedDocumentation;

namespace BirkNext.Web.Services;

/// <summary>One line of a document that contains the search term, split around the first match (plain text; Blazor escapes every part).</summary>
public sealed record GeneratedSearchSnippet(int Line, string Before, string Match, string After);

/// <summary>A document of the selected import that contains the search term: its match count and the first few lines that contain it.</summary>
public sealed record GeneratedSearchResult(GeneratedExplorerDocument Document, int Matches, IReadOnlyList<GeneratedSearchSnippet> Snippets);

/// <summary>Cross-document search over the selected import's generated documents (bounded; the import is the boundary).</summary>
public sealed record GeneratedSearchResults(string Term, IReadOnlyList<GeneratedSearchResult> Results, int TotalMatches, bool Truncated);

/// <summary>
/// Cross-document search and contextual health for the Generated Documentation Explorer. Pure: the caller passes the documents of ONE import
/// (with their already-loaded content) and the existing Generated Documentation Health run; nothing is fetched, stored or re-analysed here.
/// </summary>
public static class GeneratedDocumentationExplorerContext
{
    public const int MinTermLength = 2;
    public const int MaxDocuments = 50;
    public const int MaxSnippetsPerDocument = 3;
    public const int MaxMatchesCounted = 999;
    private const int SnippetContext = 60;

    /// <summary>
    /// Plain, case-insensitive substring search over the raw text lines of each document. Bounded: at most <see cref="MaxDocuments"/> documents,
    /// <see cref="MaxSnippetsPerDocument"/> snippets each, matches counted up to <see cref="MaxMatchesCounted"/> per document.
    /// Documents without captured content are skipped (never guessed).
    /// </summary>
    public static GeneratedSearchResults Search(IEnumerable<(GeneratedExplorerDocument Document, string? Content)> documents, string? term)
    {
        var needle = term?.Trim() ?? "";
        if (needle.Length < MinTermLength) return new(needle, [], 0, false);
        var results = new List<GeneratedSearchResult>();
        var total = 0;
        var truncated = false;
        foreach (var (document, content) in documents)
        {
            if (string.IsNullOrEmpty(content)) continue;
            var count = 0;
            var snippets = new List<GeneratedSearchSnippet>();
            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length && count < MaxMatchesCounted; i++)
            {
                var line = lines[i];
                var at = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                if (snippets.Count < MaxSnippetsPerDocument) snippets.Add(Snippet(i + 1, line, at, needle.Length));
                while (at >= 0 && count < MaxMatchesCounted)
                {
                    count++;
                    at = line.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
            if (count == 0) continue;
            if (results.Count == MaxDocuments) { truncated = true; break; }
            results.Add(new(document, count, snippets));
            total += count;
        }
        return new(needle, [.. results.OrderByDescending(r => r.Matches).ThenBy(r => r.Document.Path, StringComparer.OrdinalIgnoreCase)], total, truncated);
    }

    private static GeneratedSearchSnippet Snippet(int number, string line, int at, int length)
    {
        var start = Math.Max(0, at - SnippetContext);
        var end = Math.Min(line.Length, at + length + SnippetContext);
        var before = (start > 0 ? "…" : "") + line[start..at].TrimStart();
        var after = line[(at + length)..end].TrimEnd() + (end < line.Length ? "…" : "");
        return new(number, before, line.Substring(at, length), after);
    }

    // ── contextual health ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Health module of a document: by the module root path (Health links name the repository level "(repository)", the Explorer
    /// "Repository level"), else by module name. Null when the run does not hold the module (never "healthy").</summary>
    public static GeneratedDocumentationHealthResult? ModuleOf(GeneratedDocumentationDiagnosticRun run, string module, bool repositoryLevel)
    {
        if (repositoryLevel) return run.Modules.FirstOrDefault(m => Norm(m.RootPath).Length == 0);
        var wanted = Norm(module);
        return run.Modules.FirstOrDefault(m => Norm(m.RootPath) is { Length: > 0 } root && SamePath(root, wanted))
            ?? run.Modules.FirstOrDefault(m => string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase) || string.Equals(m.ModuleId, module, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Findings of the existing health run that concern this document: its module's missing required documents, the parse problems of THIS
    /// document, and the module's source discrepancies / cross-artifact drift — where a candidate names a generated document of the module,
    /// only when it names this one. Null when the run holds no such module.
    /// </summary>
    public static GeneratedDocumentHealthFindings? For(GeneratedDocumentationDiagnosticRun run, string module, bool repositoryLevel, string path)
    {
        if (ModuleOf(run, module, repositoryLevel) is not { } m) return null;
        var evidence = m.Evidence.FirstOrDefault(e => SamePath(e.SafeRelativePath, path));
        IReadOnlyList<string> parse = evidence is { StructurallyInvalid: true }
            ? (evidence.Warnings.Count > 0 ? evidence.Warnings : ["The generated document could not be parsed structurally."])
            : [];
        var moduleDocs = m.Evidence.Select(e => e.SafeRelativePath).Where(p => p.Length > 0).ToList();
        bool Relevant(CrossArtifactDriftCandidate c)
        {
            var named = c.LeftEvidence.Concat(c.RightEvidence).Select(r => r.Path).Where(p => !string.IsNullOrWhiteSpace(p) && moduleDocs.Any(d => SamePath(d, p))).ToList();
            return named.Count == 0 || named.Any(p => SamePath(p, path));
        }
        var source = run.Drift.Where(d => m.SourceDiscrepancies.Contains(d.Id) && Relevant(d)).ToList();
        var cross = run.Drift.Where(d => m.CrossArtifactDrift.Contains(d.Id) && !m.SourceDiscrepancies.Contains(d.Id) && Relevant(d)).ToList();
        return new(m, evidence, parse, m.MissingExpectedDocs, source, cross);
    }

    /// <summary>Same document path regardless of the archive's top-level folder or separators (one is a '/'-bounded suffix of the other).</summary>
    public static bool SamePath(string? a, string? b)
    {
        var (x, y) = (Norm(a), Norm(b));
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        return x.EndsWith("/" + y, StringComparison.OrdinalIgnoreCase) || y.EndsWith("/" + x, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string? path) => (path ?? "").Replace('\\', '/').Trim().Trim('/');
}

/// <summary>Health findings relevant to one open generated document (a presentation of the existing run; no second analyzer).</summary>
public sealed record GeneratedDocumentHealthFindings(
    GeneratedDocumentationHealthResult Module, GeneratedDocumentationEvidence? Evidence, IReadOnlyList<string> ParseErrors, IReadOnlyList<string> MissingExpected,
    IReadOnlyList<CrossArtifactDriftCandidate> SourceDiscrepancies, IReadOnlyList<CrossArtifactDriftCandidate> CrossArtifactDrift)
{
    public int Total => ParseErrors.Count + MissingExpected.Count + SourceDiscrepancies.Count + CrossArtifactDrift.Count;

    /// <summary>The document's own freshness when the run has evidence for it, else the module's.</summary>
    public string Freshness => GeneratedDocumentationText.Label(Evidence?.FreshnessStatus ?? Module.Freshness);
    public string FreshnessReason => Evidence is { FreshnessReason.Length: > 0 } e ? e.FreshnessReason : Module.FreshnessReason;
}
