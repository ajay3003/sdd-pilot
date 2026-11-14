using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The structured view a generated document gets. Chosen from its generated kind and file name; never a forced parser.</summary>
public enum GeneratedDocumentRole { DataModel, Architecture, ModuleStructure, ContainerStructure, DataFlow, Messaging, EventRouting, Routes, Overview, ChangeLog, ApiContract, GraphQlSchema, Other }

/// <summary>Browsing states only — never Passed/Failed/Healthy: a document existing says nothing about its quality.</summary>
public enum GeneratedDocumentParseState { Parsed, PartiallyParsed, ContentUnavailable, UnsupportedStructuredFormat }

/// <summary>
/// One generated (implementation-derived) document as the Explorer lists it: a reference into the import's Generated Documentation Set
/// (module, path, kind, fingerprint, generator, source snapshot). Content is resolved by fingerprint from the existing generated-content store.
/// </summary>
public sealed record GeneratedExplorerDocument(DriftDocument Document, string Module, bool RepositoryLevel, GeneratedDocumentRole Role)
{
    public string Path => DocumentationDrift.StablePathOf(Document);
    public string RoleLabel => GeneratedDocumentationExplorer.RoleLabel(Role);
    public bool ContentAvailable => Document.ContentAvailable;
}

/// <summary>A section of a generated document: heading, level, its own lines grouped as list items, ordered steps, tables and code blocks.</summary>
public sealed record GeneratedSection(string Heading, int Level, int Line, IReadOnlyList<string> Items, IReadOnlyList<string> OrderedSteps,
    IReadOnlyList<GeneratedTable> Tables, IReadOnlyList<string> CodeBlocks, IReadOnlyList<string> Prose)
{
    public bool IsEmpty => Items.Count == 0 && OrderedSteps.Count == 0 && Tables.Count == 0 && CodeBlocks.Count == 0 && Prose.Count == 0;
}

public sealed record GeneratedTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A change-log version: heading, optional date, and its categorized entries (Added, Changed, Fixed, …).</summary>
public sealed record ChangeLogVersion(string Version, string? Date, IReadOnlyList<(string Category, IReadOnlyList<string> Entries)> Groups);

/// <summary>The structured projection of one generated document. Not persisted; recomputed from the content on demand.</summary>
public sealed record GeneratedDocumentView(
    GeneratedExplorerDocument Document, GeneratedDocumentParseState State, string? Content, IReadOnlyList<GeneratedSection> Sections,
    DataModelDocument? DataModel, IReadOnlyList<ChangeLogVersion> ChangeLog, int UnmappedLines)
{
    public int LineCount => Content?.Split('\n').Length ?? 0;
    public int ByteSize => Content is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Content);
    public string? Title => Sections.FirstOrDefault(s => s.Level == 1)?.Heading;
    public int HeadingCount => Sections.Count(s => s.Level > 0);

    public int StructuredItems => Document.Role switch
    {
        GeneratedDocumentRole.DataModel => DataModel?.Entities.Count ?? 0,
        GeneratedDocumentRole.ChangeLog => ChangeLog.Count,
        _ => Sections.Count(s => s.Level > 1),
    };

    public string StateLabel => State switch
    {
        GeneratedDocumentParseState.Parsed => "Parsed",
        GeneratedDocumentParseState.PartiallyParsed => "Partially parsed",
        GeneratedDocumentParseState.ContentUnavailable => "Content unavailable",
        _ => "Unsupported structured format",
    };
}

/// <summary>
/// Generated Documentation Explorer: structured, read-only browsing of the generated (implementation-derived) documentation an import
/// captured. It enumerates the existing Generated Documentation Sets through the Documentation Drift catalog and reads content by fingerprint
/// from the existing store — no Markdown reader, no content copy, no persisted view model. Data Model documents reuse the Data Model parser
/// and generated-document semantics; other roles use a small deterministic section normalizer over the shared Markdown tokenizer. It never
/// claims source, runtime or requirement truth: Generated Documentation Health owns health/freshness, Documentation Drift owns comparison.
/// </summary>
public static partial class GeneratedDocumentationExplorer
{
    public const string RepositoryLevel = "Repository level";
    public const string ContentUnavailableText = "The generated artifact was detected, but its content was not captured during Project Import (imports made before non-Markdown capture keep metadata only). Re-import the project to capture it; nothing is fabricated.";
    public const string Origin = "Generated · implementation-derived documentation";
    public const string PartialCoverage = "Some content could not be mapped to structured concepts. All source content remains available in Source view.";

    public static IReadOnlyList<GeneratedExplorerDocument> Documents(IReadOnlyList<DriftDocument> catalog, string? importId) =>
        [.. catalog.Where(d => d.Origin == DriftOrigin.Generated && d.ProjectImportId == importId)
            .Select(d => new GeneratedExplorerDocument(d, d.Module is null or "(repository)" ? RepositoryLevel : d.Module, d.Module is null or "(repository)", RoleOf(d)))
            .OrderBy(d => d.RepositoryLevel).ThenBy(d => d.Module, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Role).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Modules with generated documents (each once), the repository level last and only when present.</summary>
    public static IReadOnlyList<string> Modules(IReadOnlyList<GeneratedExplorerDocument> documents) =>
        [.. documents.Where(d => !d.RepositoryLevel).Select(d => d.Module).Distinct(StringComparer.OrdinalIgnoreCase),
            .. documents.Any(d => d.RepositoryLevel) ? [RepositoryLevel] : Array.Empty<string>()];

    public static GeneratedDocumentRole RoleOf(DriftDocument d)
    {
        var file = System.IO.Path.GetFileNameWithoutExtension(d.Path).ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(d.Path).ToLowerInvariant();
        if (ext is ".yaml" or ".yml" || file.Contains("openapi") || file.Contains("swagger")) return GeneratedDocumentRole.ApiContract;
        if (ext == ".graphql" || file.Contains("schema.graphql")) return GeneratedDocumentRole.GraphQlSchema;
        return file switch
        {
            "data-model" or "entities" or "erd" => GeneratedDocumentRole.DataModel,
            "modules" => GeneratedDocumentRole.ModuleStructure,
            "containers" => GeneratedDocumentRole.ContainerStructure,
            "dataflow" or "data-flow" => GeneratedDocumentRole.DataFlow,
            "message-bus" or "messaging" => GeneratedDocumentRole.Messaging,
            "eventhub-routing" => GeneratedDocumentRole.EventRouting,
            "blazor-routes" or "routes" => GeneratedDocumentRole.Routes,
            "readme" or "overview" => GeneratedDocumentRole.Overview,
            "changelog" => GeneratedDocumentRole.ChangeLog,
            "architecture" => GeneratedDocumentRole.Architecture,
            _ => d.TypeLabel.Replace("Generated · ", "") switch
            {
                "DataModel" => GeneratedDocumentRole.DataModel,
                "Architecture" => GeneratedDocumentRole.Architecture,
                "DataFlow" => GeneratedDocumentRole.DataFlow,
                "Messaging" => GeneratedDocumentRole.Messaging,
                "Routes" => GeneratedDocumentRole.Routes,
                "Overview" => GeneratedDocumentRole.Overview,
                "Changelog" => GeneratedDocumentRole.ChangeLog,
                "ApiContract" => GeneratedDocumentRole.ApiContract,
                "GraphQlSchema" => GeneratedDocumentRole.GraphQlSchema,
                _ => GeneratedDocumentRole.Other,
            },
        };
    }

    public static string RoleLabel(GeneratedDocumentRole role) => role switch
    {
        GeneratedDocumentRole.DataModel => "Data Model",
        GeneratedDocumentRole.ModuleStructure => "Architecture / Module Structure",
        GeneratedDocumentRole.ContainerStructure => "Architecture / Container Structure",
        GeneratedDocumentRole.DataFlow => "Data Flow",
        GeneratedDocumentRole.EventRouting => "Messaging / Event Routing",
        GeneratedDocumentRole.ChangeLog => "Change Log",
        GeneratedDocumentRole.ApiContract => "API Contract",
        GeneratedDocumentRole.GraphQlSchema => "GraphQL Schema",
        GeneratedDocumentRole.Other => "Other Generated Documentation",
        _ => role.ToString(),
    };

    /// <summary>The Documentation Drift concern a role compares under (for "Compare in Documentation Drift"); null when Drift has no authored counterpart.</summary>
    public static DriftConcern? DriftConcernOf(GeneratedDocumentRole role) => role switch
    {
        GeneratedDocumentRole.DataModel => DriftConcern.DataModel,
        GeneratedDocumentRole.Architecture => DriftConcern.Architecture,
        _ => null,
    };

    /// <summary>Builds the structured view. Content comes from the existing generated-content store by fingerprint; absent content is a neutral state.</summary>
    public static GeneratedDocumentView View(GeneratedExplorerDocument document, string? content, IDataModelAnalysisService parser)
    {
        if (!document.ContentAvailable || content is null)
            return new(document, GeneratedDocumentParseState.ContentUnavailable, null, [], null, [], 0);
        var sections = Sections(content);
        var unmapped = sections.Sum(s => s.Prose.Count);
        if (document.Role == GeneratedDocumentRole.DataModel)
        {
            var model = DataModelGeneratedSemantics.Apply(parser.Parse(content));
            var state = model.Entities.Count == 0 ? GeneratedDocumentParseState.UnsupportedStructuredFormat
                : model.UnmappedSourceBlocks.Count > 0 ? GeneratedDocumentParseState.PartiallyParsed : GeneratedDocumentParseState.Parsed;
            return new(document, state, content, sections, model, [], model.UnmappedSourceBlocks.Count);
        }
        if (document.Role == GeneratedDocumentRole.ChangeLog)
        {
            var versions = ChangeLog(sections);
            return new(document, versions.Count > 0 ? GeneratedDocumentParseState.Parsed : GeneratedDocumentParseState.PartiallyParsed, content, sections, null, versions, unmapped);
        }
        if (document.Role is GeneratedDocumentRole.ApiContract or GeneratedDocumentRole.GraphQlSchema)
            return new(document, IsMarkdownContract(document) ? GeneratedDocumentParseState.UnsupportedStructuredFormat : GeneratedDocumentParseState.Parsed, content, [], null, [], 0);
        var headings = sections.Count(s => s.Level > 0);
        var state2 = headings == 0 ? GeneratedDocumentParseState.UnsupportedStructuredFormat
            : sections.Any(s => s.Level == 0 && s.Prose.Count > 0) ? GeneratedDocumentParseState.PartiallyParsed : GeneratedDocumentParseState.Parsed;
        return new(document, state2, content, sections, null, [], unmapped);
    }

    private static bool IsMarkdownContract(GeneratedExplorerDocument d) => DocumentationDrift.IsMarkdown(d.Path);

    /// <summary>Card state: what a tester can do with the artifact before opening it.</summary>
    public static string Availability(GeneratedExplorerDocument d) => d.ContentAvailable ? "Available" : "Detected — content unavailable";

    /// <summary>The raw-content language of an artifact (Source tab label).</summary>
    public static string SourceLanguage(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".yaml" or ".yml" => "YAML", ".json" => "JSON", ".graphql" or ".graphqls" or ".gql" => "GraphQL SDL", _ => "Markdown",
    };

    /// <summary>Deterministic section structure from the shared tokenizer: headings, bullets, ordered steps, tables, code and prose per section.</summary>
    public static IReadOnlyList<GeneratedSection> Sections(string markdown)
    {
        var sections = new List<GeneratedSection>();
        string heading = "(document start)"; int level = 0, line = 0;
        List<string> items = [], steps = [], code = [], prose = [];
        List<GeneratedTable> tables = [];
        List<string>? header = null; List<IReadOnlyList<string>> rows = [];
        var codeLines = new List<string>();
        void FlushTable() { if (header is not null) tables.Add(new(header, [.. rows])); header = null; rows = []; }
        void Flush()
        {
            FlushTable();
            var s = new GeneratedSection(heading, level, line, [.. items], [.. steps], [.. tables], [.. code], [.. prose]);
            if (!(s.IsEmpty && level == 0)) sections.Add(s);
            items = []; steps = []; code = []; prose = []; tables = [];
        }
        foreach (var t in MarkdownTokenizer.Tokenize(markdown))
        {
            switch (t.Kind)
            {
                case MarkdownTokenKind.Heading: Flush(); heading = MarkdownTokenizer.NormalizeInlineMarkup(t.Content); level = t.HeadingLevel; line = t.LineIndex + 1; break;
                case MarkdownTokenKind.BulletItem: FlushTable(); items.Add(MarkdownTokenizer.NormalizeInlineMarkup(t.Content)); break;
                case MarkdownTokenKind.OrderedItem: FlushTable(); steps.Add(MarkdownTokenizer.NormalizeInlineMarkup(t.Content)); break;
                case MarkdownTokenKind.TableRow when t.TableCells is { } cells:
                    if (header is null) header = [.. cells]; else rows.Add([.. cells]);
                    break;
                case MarkdownTokenKind.TableSeparator: break;
                case MarkdownTokenKind.FencedCodeStart: codeLines = []; break;
                case MarkdownTokenKind.FencedCodeLine: codeLines.Add(t.RawLine); break;
                case MarkdownTokenKind.FencedCodeEnd: code.Add(string.Join('\n', codeLines)); break;
                case MarkdownTokenKind.Blank: break;
                default: if (!string.IsNullOrWhiteSpace(t.Content)) prose.Add(t.Content.Trim()); break;
            }
        }
        Flush();
        return sections;
    }

    private static readonly string[] ChangeCategories = ["Added", "Changed", "Fixed", "Removed", "Deprecated", "Security", "Breaking changes", "Breaking"];

    /// <summary>Version headings ("## [1.2.0] - 2026-10-01", "## 1.2.0") with their category subsections; other formats fall back to sections.</summary>
    public static IReadOnlyList<ChangeLogVersion> ChangeLog(IReadOnlyList<GeneratedSection> sections)
    {
        var versions = new List<ChangeLogVersion>();
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Level != 2 || VersionHeading().Match(s.Heading) is not { Success: true } m) continue;
            var groups = new List<(string, IReadOnlyList<string>)>();
            if (s.Items.Count > 0) groups.Add(("Other", s.Items));
            for (var j = i + 1; j < sections.Count && sections[j].Level > 2; j++)
            {
                var category = ChangeCategories.FirstOrDefault(c => sections[j].Heading.StartsWith(c, StringComparison.OrdinalIgnoreCase)) ?? sections[j].Heading;
                groups.Add((category, [.. sections[j].Items, .. sections[j].Prose]));
            }
            versions.Add(new(m.Groups["v"].Value, m.Groups["d"].Success ? m.Groups["d"].Value : null, groups));
        }
        return versions;
    }

    [GeneratedRegex(@"^\[?(?<v>v?\d+(\.\d+){0,3}[^\]\s]*|Unreleased)\]?(\s*[-–]\s*(?<d>\d{4}-\d{2}-\d{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionHeading();

    /// <summary>Lines of the selected document that mention <paramref name="term"/> (headings, concept names, text) — never repository-wide.</summary>
    public static IReadOnlyList<(int Line, string Text)> Search(string? content, string term) =>
        string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(term) ? []
        : [.. content.Split('\n').Select((l, i) => (i + 1, l.TrimEnd())).Where(x => x.Item2.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)).Take(50)];

    /// <summary>The document views: Read (default), Outline (id "Structure", kept for existing links), Source, Provenance.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> ViewTabs = [("Read", "Read"), ("Structure", "Outline"), ("Source", "Source"), ("Provenance", "Provenance")];

    /// <summary>A view named in <c>?view=</c>; the former Overview (document metadata) is now part of Provenance. Null for anything else.</summary>
    public static string? ViewFromQuery(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "read" => "Read",
        "outline" or "structure" => "Structure",
        "source" => "Source",
        "provenance" or "overview" => "Provenance",
        _ => null,
    };

    /// <summary>The source's lines for the numbered view (the text itself is never altered).</summary>
    public static IReadOnlyList<string> SourceLines(string? content) =>
        string.IsNullOrEmpty(content) ? [] : content.Replace("\r\n", "\n").Split('\n');

    public static string ViewSlug(string tab) => tab == "Structure" ? "outline" : tab.ToLowerInvariant();

    /// <summary>
    /// The document in reading order: Markdown runs, and every fenced block that is a recognised diagram as its own segment (so it can be drawn).
    /// Every line of the source lands in exactly one segment, in order — nothing is dropped; other code fences stay in the Markdown.
    /// </summary>
    public static IReadOnlyList<GeneratedReadSegment> ReadSegments(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var segments = new List<GeneratedReadSegment>();
        var markdown = new List<string>();
        void FlushMarkdown() { if (markdown.Count > 0) { segments.Add(new(string.Join('\n', markdown), null)); markdown.Clear(); } }
        for (var i = 0; i < lines.Length; i++)
        {
            var fence = FenceOpen().Match(lines[i]);
            if (!fence.Success) { markdown.Add(lines[i]); continue; }
            var marker = fence.Groups["m"].Value;
            var end = i + 1;
            while (end < lines.Length && !lines[end].TrimStart().StartsWith(marker, StringComparison.Ordinal)) end++;
            var body = string.Join('\n', lines[(i + 1)..Math.Min(end, lines.Length)]);
            var closed = end < lines.Length;
            if (closed && GeneratedDiagrams.IsDiagram(body))
            {
                FlushMarkdown();
                segments.Add(new(null, body));
            }
            else
            {
                markdown.AddRange(lines[i..Math.Min(end + 1, lines.Length)]);
            }
            i = Math.Min(end, lines.Length - 1);
        }
        FlushMarkdown();
        return segments;
    }

    /// <summary>Headings of the Read view with the anchor ids it renders (per Markdown segment, in order).</summary>
    public static IReadOnlyList<GeneratedOutlineItem> Outline(IReadOnlyList<GeneratedReadSegment> segments) =>
        [.. segments.Select((s, index) => (s, index)).Where(x => x.s.Markdown is not null)
            .SelectMany(x => MarkdownRenderingService.UntrustedHeadings(x.s.Markdown!).Select(h => new GeneratedOutlineItem(h.Level, h.Text, AnchorId(x.index, h.Id))))];

    /// <summary>A heading's element id in the Read view: segment-scoped so headings in different segments never collide.</summary>
    public static string AnchorId(int segment, string headingId) => $"gde-s{segment}-{headingId}";

    /// <summary>Rendered HTML of one Markdown segment with its heading ids made segment-scoped (matching <see cref="AnchorId"/>).</summary>
    public static string RenderSegment(int segment, string markdown) =>
        HeadingId().Replace(MarkdownRenderingService.RenderUntrusted(markdown), m => $"{m.Groups["open"].Value}id=\"{AnchorId(segment, m.Groups["id"].Value)}\"");

    [GeneratedRegex(@"^\s{0,3}(?<m>```+|~~~+)")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"(?<open><h[1-6]\s+)id=""(?<id>[^""]*)""")]
    private static partial Regex HeadingId();
}

/// <summary>One part of a document in reading order: Markdown, or a diagram's source (exactly one is set).</summary>
public sealed record GeneratedReadSegment(string? Markdown, string? Diagram);

/// <summary>Presentation helpers for the reader: titles, in-document search highlighting, system mentions, outline comparison. Pure.</summary>
public static partial class GeneratedDocumentationReader
{
    /// <summary>The document's own title: its first level-1 heading (else the first heading); null when it has none (the caller falls back to the type).</summary>
    public static string? Title(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        string? first = null;
        foreach (var raw in content.Split('\n').Take(400))
        {
            var line = raw.TrimEnd('\r');
            var m = HeadingRe().Match(line);
            if (!m.Success) continue;
            var text = m.Groups[2].Value.Trim().Trim('#').Trim();
            if (text.Length == 0) continue;
            if (m.Groups[1].Value.Length == 1) return text;
            first ??= text;
        }
        return first;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial System.Text.RegularExpressions.Regex HeadingRe();

    [System.Text.RegularExpressions.GeneratedRegex(@"(<[^>]*>)|([^<]+)")]
    private static partial System.Text.RegularExpressions.Regex HtmlTokenRe();

    /// <summary>
    /// Marks every case-insensitive occurrence of <paramref name="term"/> in the TEXT of already-sanitized HTML (tags and attributes are never
    /// touched; the term is matched against HTML-encoded text, so markup cannot be injected). Ids continue from <paramref name="start"/>.
    /// </summary>
    public static string Highlight(string html, string term, int start, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2) return html;
        var needle = System.Net.WebUtility.HtmlEncode(term.Trim());
        var sb = new System.Text.StringBuilder(html.Length + 64);
        var n = start;
        var inCode = 0;
        foreach (System.Text.RegularExpressions.Match token in HtmlTokenRe().Matches(html))
        {
            if (token.Groups[1].Success)
            {
                var tag = token.Value;
                if (tag.StartsWith("<script", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("<style", StringComparison.OrdinalIgnoreCase)) inCode++;
                else if (tag.StartsWith("</script", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("</style", StringComparison.OrdinalIgnoreCase)) inCode--;
                sb.Append(tag);
                continue;
            }
            var text = token.Value;
            if (inCode > 0) { sb.Append(text); continue; }
            var i = 0;
            while (true)
            {
                var at = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, at - i);
                sb.Append($"<mark class=\"gde-hit\" id=\"gde-hit-{n}\" tabindex=\"-1\">").Append(text, at, needle.Length).Append("</mark>");
                n++; count++;
                i = at + needle.Length;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Registered systems and services the document names exactly (whole word, case-sensitive name or explicit/confirmed alias). A mention is a
    /// reference to an identity the shared registry already holds — never a relationship, and never a guess from a similar name.
    /// </summary>
    public static IReadOnlyList<(BirkNext.Scopes.SystemOverviewItem System, string Mention)> SystemMentions(string? content, IEnumerable<BirkNext.Scopes.SystemOverviewItem> systems)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var found = new List<(BirkNext.Scopes.SystemOverviewItem, string)>();
        foreach (var s in systems)
        {
            var names = s.Aliases.Where(a => a.State is BirkNext.Interpretation.AliasState.Explicit or BirkNext.Interpretation.AliasState.Confirmed).Select(a => a.Alias).Prepend(s.Name)
                .Where(n => n.Length >= 3).Distinct(StringComparer.Ordinal);
            foreach (var name in names)
                if (System.Text.RegularExpressions.Regex.IsMatch(content, $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(name)}(?![\w])"))
                { found.Add((s, name)); break; }
        }
        return [.. found.OrderBy(f => f.Item1.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Headings present in one revision and not the other (text, case-sensitive), and whether the diagram sources differ. Formatting-only
    /// changes are not reported as structural change; this is a reading aid, not a drift verdict (Documentation Drift owns comparison).</summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed, int DiagramsBefore, int DiagramsAfter, bool DiagramsChanged) CompareOutline(string? before, string? after)
    {
        static List<string> Headings(string? c) => [.. (c ?? "").Split('\n').Select(l => HeadingRe().Match(l.TrimEnd('\r'))).Where(m => m.Success).Select(m => m.Groups[2].Value.Trim())];
        static List<string> Diagrams(string? c) => [.. GeneratedDocumentationExplorer.ReadSegments(c).Where(s => s.Diagram is not null).Select(s => string.Join('\n', s.Diagram!.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)))];
        var (hb, ha, db, da) = (Headings(before), Headings(after), Diagrams(before), Diagrams(after));
        return ([.. ha.Except(hb, StringComparer.Ordinal)], [.. hb.Except(ha, StringComparer.Ordinal)], db.Count, da.Count, !db.SequenceEqual(da, StringComparer.Ordinal));
    }
}

/// <summary>One heading of the Read view and the id of its element.</summary>
public sealed record GeneratedOutlineItem(int Level, string Text, string AnchorId);
