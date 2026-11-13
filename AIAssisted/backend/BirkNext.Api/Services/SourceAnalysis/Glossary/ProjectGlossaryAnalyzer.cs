using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Glossary;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Api.Services.SourceAnalysis.Glossary;

/// <summary>
/// Detects and parses the project's own glossary documents in the Markdown a Source Analysis snapshot already read (no archive re-read, the
/// shared <see cref="MarkdownTokenizer"/> — no second Markdown parser). Authority comes from document evidence only:
/// <list type="bullet">
/// <item>Proposed — the document names itself a proposal / pull-request item list (file name or title).</item>
/// <item>Authoritative — it states its authority ("single list for the whole platform", "source of truth", "required by …"), or at least two
/// other project documents refer to it by path. A file name alone is never enough.</item>
/// <item>Conflict — more than one document is authoritative: each is marked, none wins by name or order.</item>
/// <item>Unknown — glossary-like, without either kind of evidence.</item>
/// </list>
/// Rows are taken as the source states them (term = first column; preferred form, meaning and decision only when the table has such columns
/// or markers); categories are the document's own sections.
/// </summary>
public static partial class ProjectGlossaryAnalyzer
{
    /// <summary>v2: rows are classified by their table's term-column header (structural tables are not terms).</summary>
    public const int Version = 2;
    private const int MaxEntries = 5_000;

    public static ProjectGlossarySnapshot Analyze(Guid snapshotId, string projectName, DateTimeOffset analyzedAt, IReadOnlyList<(string Path, string Content)> documents)
    {
        var markdown = documents.Where(d => d.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = markdown.Where(d => IsGlossaryLike(d.Path, d.Content)).ToList();
        var artifacts = new List<ProjectGlossaryArtifact>();
        var entries = new List<ProjectGlossaryEntry>();
        var limitations = new List<string>();

        foreach (var (path, content) in candidates.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var id = Normalize(path);
            var tokens = MarkdownTokenizer.Tokenize(content.Replace("\r\n", "\n"));
            var title = tokens.FirstOrDefault(t => t.Kind == MarkdownTokenKind.Heading && t.HeadingLevel == 1)?.Content.Trim() ?? System.IO.Path.GetFileNameWithoutExtension(path);
            var referencedBy = markdown.Where(m => !string.Equals(m.Path, path, StringComparison.Ordinal) && References(m.Content, path)).Select(m => Normalize(m.Path))
                .Order(StringComparer.Ordinal).ToList();
            var evidence = new List<string>();
            GlossaryAuthority authority;
            if (ProposalRe().IsMatch(System.IO.Path.GetFileNameWithoutExtension(path)) || ProposalRe().IsMatch(title))
            {
                authority = GlossaryAuthority.Proposed;
                evidence.Add($"Names itself a proposal / pull-request item list (\"{title}\"): its rows are proposals, not approved terms.");
            }
            else
            {
                if (AuthorityRe().Match(string.Join('\n', content.Split('\n').Take(60))) is { Success: true } statement)
                    evidence.Add($"States its authority: \"…{statement.Value.Trim()}…\"");
                if (referencedBy.Count >= 2) evidence.Add($"Referred to by {referencedBy.Count} project documents by its path.");
                authority = evidence.Count > 0 ? GlossaryAuthority.Authoritative : GlossaryAuthority.Unknown;
                if (authority == GlossaryAuthority.Unknown) evidence.Add("Glossary-like document without a statement of authority or references from other documents.");
            }

            var parsed = Entries(id, tokens, out var sections);
            if (entries.Count + parsed.Count > MaxEntries)
            {
                limitations.Add($"{path}: only the first {MaxEntries} glossary rows of the project are kept.");
                parsed = [.. parsed.Take(Math.Max(0, MaxEntries - entries.Count))];
            }
            entries.AddRange(parsed);
            artifacts.Add(new ProjectGlossaryArtifact
            {
                GlossaryId = id, SourcePath = path.Replace('\\', '/'), Title = title, Authority = authority, AuthorityEvidence = evidence,
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()[..16],
                Sections = sections, ReferencedBy = referencedBy, EntryCount = parsed.Count,
            });
        }

        // Several documents claiming authority: every one is a conflict to review — never the first, shortest or alphabetical one.
        var claimants = artifacts.Where(a => a.Authority == GlossaryAuthority.Authoritative).ToList();
        if (claimants.Count > 1)
            artifacts = [.. artifacts.Select(a => a.Authority != GlossaryAuthority.Authoritative ? a : a with
            {
                Authority = GlossaryAuthority.Conflict,
                AuthorityEvidence = [.. a.AuthorityEvidence, $"{claimants.Count} documents claim to be the project's glossary ({string.Join(", ", claimants.Select(c => c.SourcePath))}): review which one is authoritative."],
            })];

        return new ProjectGlossarySnapshot
        {
            SourceSnapshotId = snapshotId, ProjectName = projectName, AnalyzerVersion = Version, AnalyzedAt = analyzedAt,
            Glossaries = artifacts, Entries = entries, Limitations = limitations,
        };
    }

    /// <summary>A glossary-like document: its file name says glossary / word list, or its title IS a glossary title ("M2LB domain glossary",
    /// "Glossary: …") — a feature about terminology ("Reference Data Correction and Terminology Fix") is not a glossary.</summary>
    public static bool IsGlossaryLike(string path, string content)
    {
        if (GlossaryNameRe().IsMatch(System.IO.Path.GetFileNameWithoutExtension(path))) return true;
        var title = content.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
        return title is not null && GlossaryTitleRe().IsMatch(title[2..].Trim());
    }

    private static List<ProjectGlossaryEntry> Entries(string glossaryId, IReadOnlyList<MarkdownToken> tokens, out List<string> sections)
    {
        var result = new List<ProjectGlossaryEntry>();
        sections = [];
        string? h2 = null, h3 = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == MarkdownTokenKind.Heading)
            {
                if (t.HeadingLevel == 2) { h2 = t.Content.Trim(); h3 = null; sections.Add(h2); }
                else if (t.HeadingLevel >= 3) { h3 = t.Content.Trim(); sections.Add(h2 is null ? h3 : $"{h2} › {h3}"); }
                continue;
            }
            // A table: header row, separator, body rows.
            if (t.Kind != MarkdownTokenKind.TableRow || i + 1 >= tokens.Count || tokens[i + 1].Kind != MarkdownTokenKind.TableSeparator) continue;
            var headers = t.TableCells!.Select(ProjectTerminology.Clean).ToList();
            var termColumn = headers.FindIndex(h => h.Length > 0 && !NumberHeaderRe().IsMatch(h));
            if (headers.Count < 2 || termColumn < 0) continue;
            var preferred = headers.FindIndex(h => PreferredHeaderRe().IsMatch(h));
            var meaning = headers.FindIndex(h => MeaningHeaderRe().IsMatch(h));
            var status = headers.FindIndex(h => StatusHeaderRe().IsMatch(h));
            var kind = RowKind(headers[termColumn]);
            var category = h3 is null ? h2 ?? "" : h2 is null ? h3 : $"{h2} › {h3}";
            for (i += 2; i < tokens.Count && tokens[i].Kind == MarkdownTokenKind.TableRow; i++)
            {
                var cells = tokens[i].TableCells!;
                string Cell(int index) => index >= 0 && index < cells.Count ? cells[index].Trim() : "";
                var term = ProjectTerminology.Clean(Cell(termColumn));
                if (term.Length == 0) continue;
                var columns = headers.Select((h, index) => (Header: h.Length == 0 ? $"Column {index + 1}" : h, Value: Cell(index)))
                    .Where(c => c.Value.Length > 0).GroupBy(c => c.Header).ToDictionary(g => g.Key, g => g.First().Value);
                result.Add(new ProjectGlossaryEntry
                {
                    EntryId = $"{glossaryId}#{tokens[i].LineIndex + 1}", GlossaryId = glossaryId, Term = term,
                    PreferredForm = preferred >= 0 && preferred != termColumn && Cell(preferred) is { Length: > 0 } p ? p : null,
                    Meaning = meaning >= 0 && meaning != termColumn && Cell(meaning) is { Length: > 0 } m ? m : null,
                    Category = category, Decision = Decision(status >= 0 ? Cell(status) : null, cells), Columns = columns, Line = tokens[i].LineIndex + 1,
                    Kind = kind,
                });
            }
            i--;
        }
        return result;
    }

    /// <summary>
    /// What a table's rows are, from its term-column header only: "Kind" (naming conventions), "Owner" (pinned-name groups), "Issue" (known
    /// inconsistencies) and "Source" (the glossary's inputs) are structural; any other header ("Norwegian", "Word", "Term", …) is a term table.
    /// </summary>
    private static GlossaryRowKind RowKind(string termHeader) => termHeader.Trim().ToLowerInvariant() switch
    {
        "kind" or "convention" => GlossaryRowKind.NamingConvention,
        "owner" or "eier" => GlossaryRowKind.PinnedNames,
        "issue" or "inconsistency" or "avvik" => GlossaryRowKind.KnownInconsistency,
        "source" or "sources" or "kilde" => GlossaryRowKind.Source,
        _ => GlossaryRowKind.Term,
    };

    /// <summary>The row's own decision: its Status/Decision cell, else a leading marker (✅ approved, ⚠ caution, ❌ rejected) — never inferred.</summary>
    private static string? Decision(string? statusCell, IReadOnlyList<string> cells)
    {
        if (statusCell is { Length: > 0 }) return ProjectTerminology.Clean(statusCell);
        foreach (var cell in cells)
        {
            var c = cell.TrimStart();
            if (c.StartsWith("✅", StringComparison.Ordinal)) return "✅";
            if (c.StartsWith("⚠", StringComparison.Ordinal)) return "⚠";
            if (c.StartsWith("❌", StringComparison.Ordinal)) return "❌";
        }
        return null;
    }

    /// <summary>Another document refers to the glossary by its path (with or without the archive's top folder).</summary>
    private static bool References(string text, string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        var keys = new List<string> { normalized };
        // Never a generic tail such as "checklists/requirements.md": it names many documents of the project.
        if (parts.Length > 2) keys.Add(string.Join('/', parts[1..]));
        return keys.Distinct().Where(k => k.Contains('/')).Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    [GeneratedRegex(@"(?i)(glossar|ordliste|terminolog|termliste|begrepsliste)")]
    private static partial Regex GlossaryNameRe();

    /// <summary>A title that is a glossary title: it starts or ends with the glossary word.</summary>
    [GeneratedRegex(@"(?i)^(?:(?:glossary|ordliste|terminology|termliste|begrepsliste)\b.*|.*\b(?:glossary|ordliste|terminology|termliste|begrepsliste))$")]
    private static partial Regex GlossaryTitleRe();

    [GeneratedRegex(@"(?i)(pull[- ]?request|proposal|proposed|forslag|-items\b|\bitems\b)")]
    private static partial Regex ProposalRe();

    [GeneratedRegex(@"(?i)[^.\n]{0,60}\b(single (?:list|source)|source of truth|authoritative|the (?:official|canonical) (?:glossary|list)|required by|eneste (?:liste|kilde))\b[^.\n]{0,80}")]
    private static partial Regex AuthorityRe();

    [GeneratedRegex(@"^(#|no\.?|nr\.?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberHeaderRe();

    [GeneratedRegex(@"(?i)(code form|preferred|english|kodeform|engelsk)")]
    private static partial Regex PreferredHeaderRe();

    [GeneratedRegex(@"(?i)(meaning|betydning|definition|description|beskrivelse)")]
    private static partial Regex MeaningHeaderRe();

    [GeneratedRegex(@"(?i)^(status|decision|beslutning)$")]
    private static partial Regex StatusHeaderRe();
}
