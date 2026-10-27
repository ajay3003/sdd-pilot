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
    public const string ContentUnavailableText = "The generated document was detected, but its Markdown content was not captured during Project Import.";
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
            return new(document, GeneratedDocumentParseState.UnsupportedStructuredFormat, content, [], null, [], 0);
        var headings = sections.Count(s => s.Level > 0);
        var state2 = headings == 0 ? GeneratedDocumentParseState.UnsupportedStructuredFormat
            : sections.Any(s => s.Level == 0 && s.Prose.Count > 0) ? GeneratedDocumentParseState.PartiallyParsed : GeneratedDocumentParseState.Parsed;
        return new(document, state2, content, sections, null, [], unmapped);
    }

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
}
