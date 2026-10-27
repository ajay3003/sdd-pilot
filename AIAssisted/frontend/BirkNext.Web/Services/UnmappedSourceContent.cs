using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// ── Source content not structured ───────────────────────────────────────────────────────────────────────────────────────
//
// Every Explorer keeps the source blocks it did not map to a structured field (MarkdownSourceNote, by exact line). This is the
// one shared, deterministic presentation of those blocks: each block gets exactly one reviewer-facing category, consecutive
// blocks of the same category and section are grouped, and only blocks that strongly resemble a structured fact of the active
// Explorer — and are not already represented in its structured model — become a "Potential mapping gap".
//
// Presentation only: nothing here is persisted, nothing changes the structured model, and a gap is a review observation —
// never a requirement, relationship, task or score. Grouping keeps every block (id, lines, raw text) untouched.

/// <summary>The Explorer whose structured model the source blocks are compared against (<c>None</c>: generic categories only).</summary>
public enum SourceContentExplorer { None, Specification, Constitution, Plan, Tasks, DataModel }

/// <summary>Exactly one reviewer-facing category per source block.</summary>
public enum SourceContentCategory { PotentialMappingGap, Informational, ChangeHistory, Reference, Structural }

/// <summary>One preserved block with its category. <see cref="PotentialKind"/> names the structured kind it resembles (gaps, or
/// blocks already represented); <see cref="AlreadyRepresented"/> means the structured model already holds the same fact.</summary>
public sealed record ClassifiedSourceBlock(MarkdownSourceNote Block, SourceContentCategory Category, string Reason,
    string? PotentialKind = null, bool AlreadyRepresented = false)
{
    public string? Section => Block.Section;
}

/// <summary>Consecutive blocks of one category under one authored heading. The blocks keep their own provenance.</summary>
public sealed record SourceContentGroup(string Id, SourceContentCategory Category, string? Section, IReadOnlyList<ClassifiedSourceBlock> Blocks)
{
    public int StartLine => Blocks.Min(b => b.Block.StartLine);
    public int EndLine => Blocks.Max(b => b.Block.EndLine);
    public string Title => Section is { Length: > 0 } s ? $"{UnmappedSourceContent.GroupLabel(Category)} · {s}" : UnmappedSourceContent.GroupLabel(Category);

    /// <summary>A readable preview: the blocks' text without Markdown markers, shortened.</summary>
    public string Preview => UnmappedSourceContent.Shorten(string.Join(" ", Blocks.Select(b => UnmappedSourceContent.Readable(b.Block.Text)).Where(t => t.Length > 0)), 220);
}

/// <summary>What the Explorer compares against: the artifact kind and its structured model (read only).</summary>
public sealed record UnmappedSourceContext(SourceContentExplorer Artifact, object? Model)
{
    public static UnmappedSourceContext For(SpecTree? tree) => new(SourceContentExplorer.Specification, tree);
    public static UnmappedSourceContext For(ConstitutionDocument? document) => new(SourceContentExplorer.Constitution, document);
    public static UnmappedSourceContext For(PlanDocument? plan) => new(SourceContentExplorer.Plan, plan);
    public static UnmappedSourceContext For(TaskTree? tree) => new(SourceContentExplorer.Tasks, tree);
    public static UnmappedSourceContext For(DataModelDocument? document) => new(SourceContentExplorer.DataModel, document);
}

/// <summary>The classified blocks of one document, in source order, with derived counts and groups.</summary>
public sealed record UnmappedSourceContent(IReadOnlyList<ClassifiedSourceBlock> Blocks)
{
    public int Total => Blocks.Count;
    public int Count(SourceContentCategory category) => Blocks.Count(b => b.Category == category);
    public IReadOnlyList<ClassifiedSourceBlock> Gaps => Blocks.Where(b => b.Category == SourceContentCategory.PotentialMappingGap).ToList();

    /// <summary>Non-empty categories in display order (gaps first), with counts. Counts always add up to <see cref="Total"/>.</summary>
    public IReadOnlyList<(SourceContentCategory Category, int Count)> Counts =>
        Enum.GetValues<SourceContentCategory>().Select(c => (c, Count(c))).Where(x => x.Item2 > 0).ToList();

    /// <summary>Groups of consecutive blocks with the same category and section (gaps and structural blocks are not grouped here).</summary>
    public IReadOnlyList<SourceContentGroup> GroupsOf(SourceContentCategory category)
    {
        var groups = new List<SourceContentGroup>();
        List<ClassifiedSourceBlock>? current = null;
        ClassifiedSourceBlock? previous = null;
        foreach (var block in Blocks)
        {
            var continues = previous is not null && previous.Category == category && block.Category == category
                && string.Equals(previous.Section, block.Section, StringComparison.Ordinal);
            if (block.Category == category)
            {
                if (!continues || current is null) groups.Add(new SourceContentGroup($"{category}-{block.Block.StartLine}", category, block.Section, current = []));
                current.Add(block);
            }
            previous = block;
        }
        return groups;
    }

    // ── Labels ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Label(SourceContentCategory category) => category switch
    {
        SourceContentCategory.PotentialMappingGap => "Potential mapping gap",
        SourceContentCategory.Informational => "Informational",
        SourceContentCategory.ChangeHistory => "Change / history",
        SourceContentCategory.Reference => "Reference",
        _ => "Structural",
    };

    public static string PluralLabel(SourceContentCategory category, int count) => category switch
    {
        SourceContentCategory.PotentialMappingGap => count == 1 ? "Potential mapping gap" : "Potential mapping gaps",
        SourceContentCategory.Informational => "Informational",
        SourceContentCategory.ChangeHistory => "Change / history",
        SourceContentCategory.Reference => count == 1 ? "Reference" : "References",
        _ => "Structural / formatting",
    };

    public static string GroupLabel(SourceContentCategory category) => category switch
    {
        SourceContentCategory.Informational => "Explanatory prose",
        SourceContentCategory.ChangeHistory => "Change / history",
        SourceContentCategory.Reference => "References",
        SourceContentCategory.Structural => "Structural / formatting",
        _ => "Potential mapping gap",
    };

    // ── Text helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The block text without leading Markdown markers (heading hashes, bullets, quote marks, table pipes).</summary>
    public static string Readable(string raw)
    {
        var text = raw.Trim();
        text = Regex.Replace(text, @"^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)", "");
        if (text.StartsWith('|')) text = string.Join(" · ", text.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
        return text.Replace("**", "").Replace("`", "").Trim();
    }

    public static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // ── Classification ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classifies the blocks deterministically for the given Explorer. The structured model is read, never changed.</summary>
    public static UnmappedSourceContent Classify(IReadOnlyList<MarkdownSourceNote>? blocks, UnmappedSourceContext context)
    {
        if (blocks is not { Count: > 0 }) return new([]);
        var structured = new StructuredFacts(context);
        var result = new List<ClassifiedSourceBlock>(blocks.Count);
        var inComment = false;
        var commentIsHistory = false;
        foreach (var block in blocks)
        {
            var text = block.Text.Trim();
            // HTML comments are never rendered authored content: boundaries are scaffolding, the inside is history or context.
            if (!inComment && text.StartsWith("<!--", StringComparison.Ordinal))
            {
                inComment = !text.Contains("-->", StringComparison.Ordinal);
                commentIsHistory = HistoryText.IsMatch(text);
                var inner = text.Replace("<!--", "").Replace("-->", "").Trim();
                result.Add(inner.Length == 0
                    ? new(block, SourceContentCategory.Structural, "HTML comment boundary.")
                    : new(block, commentIsHistory ? SourceContentCategory.ChangeHistory : SourceContentCategory.Informational, "Inside an HTML comment (not rendered document content)."));
                continue;
            }
            if (inComment)
            {
                if (text.Contains("-->", StringComparison.Ordinal)) inComment = false;
                commentIsHistory |= HistoryText.IsMatch(text);
                var inner = text.Replace("-->", "").Trim();
                result.Add(inner.Length == 0 || IsScaffolding(inner)
                    ? new(block, SourceContentCategory.Structural, "HTML comment boundary.")
                    : new(block, commentIsHistory ? SourceContentCategory.ChangeHistory : SourceContentCategory.Informational,
                        commentIsHistory ? "Change report inside an HTML comment." : "Inside an HTML comment (not rendered document content)."));
                continue;
            }
            result.Add(ClassifyBlock(block, context.Artifact, structured));
        }
        return new(result);
    }

    private static ClassifiedSourceBlock ClassifyBlock(MarkdownSourceNote block, SourceContentExplorer artifact, StructuredFacts structured)
    {
        var text = block.Text.Trim();
        var readable = Readable(block.Text);

        // Structural / formatting scaffolding.
        if (block.BlockType is nameof(MarkdownTokenKind.FencedCodeStart) or nameof(MarkdownTokenKind.FencedCodeEnd))
            return new(block, SourceContentCategory.Structural, "Code fence delimiter.");
        if (readable.Length == 0 || IsScaffolding(text))
            return new(block, SourceContentCategory.Structural, "Formatting only (separator, connector or empty line).");

        // Change / history, then references: never structured domain facts of the current artifact.
        if (HistorySection.IsMatch(block.Section ?? "") || HistoryText.IsMatch(text))
            return new(block, SourceContentCategory.ChangeHistory, "Version, change or provenance history.");
        if (block.BlockType == nameof(MarkdownTokenKind.Heading))
            return new(block, SourceContentCategory.Structural, "Section heading used for document structure.");

        // Examples and templates illustrate; they never assert facts about this artifact.
        var illustrative = ExampleSection.IsMatch(block.Section ?? "") || ExampleText.IsMatch(readable);

        if (!illustrative && ExplorerRules.Match(artifact, block, readable, structured) is { } match)
            return match.Represented
                ? new(block, match.WhenRepresented, $"Already represented in the structured view ({match.Kind}).", match.Kind, AlreadyRepresented: true)
                : new(block, SourceContentCategory.PotentialMappingGap, match.Reason, match.Kind);

        if (IsReference(readable, block.Section))
            return new(block, SourceContentCategory.Reference, "Reference to a document, file or source.");
        return new(block, SourceContentCategory.Informational, illustrative ? "Example or template content." : "Explanatory content kept for context.");
    }

    /// <summary>Separator rows, lone pipes, fence/rule markers and box-drawing/arrow-only diagram connector rows.</summary>
    internal static bool IsScaffolding(string text) =>
        Scaffolding.IsMatch(text) || text.Trim() is "<!--" or "-->";

    private static bool IsReference(string readable, string? section)
    {
        if (ReferenceSection.IsMatch(section ?? "")) return true;
        if (Regex.IsMatch(readable, @"^(Source|Sources|See|Ref|Reference|References|Related|Input|Link|Links|Spec|Specification|Plan|Docs?)\s*:", RegexOptions.IgnoreCase))
            return !Regex.IsMatch(readable, @"\b(MUST|SHALL)\b");
        // A short line that is essentially a link or a file path.
        var withoutLinks = Regex.Replace(readable, @"\[[^\]]*\]\([^)]*\)|https?://\S+|\b[\w./\\-]+\.(md|cs|json|ya?ml|sql|ts|razor|txt|xml|xsd|csproj|sln)\b", "", RegexOptions.IgnoreCase);
        return withoutLinks.Length < readable.Length && Regex.Replace(withoutLinks, @"[\s,;:()·\-–—]+", "").Length <= 12;
    }

    private static readonly Regex Scaffolding = new(@"^[\s|│┃┆┊┌┐└┘├┤┬┴┼─━═╔╗╚╝╠╣╦╩╬▼▲►◄▶◀↓↑→←↔⇒⇐⇓⇑v^+:=*_~`#\-.]*$", RegexOptions.Compiled);
    private static readonly Regex HistoryText = new(@"(sync impact report|version change|previous version|version history|change ?log|changelog|\bratified\b|last amended|modified (principles|sections)|(added|removed) sections|templates? (requiring updates|reviewed)|follow-up todos|\b\d+\.\d+\.\d+\s*(→|->)\s*\d+\.\d+\.\d+|\(new\)\s*(→|->))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HistorySection = new(@"(history|changelog|change log|sync impact|revisions?\b|previous versions?)",RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReferenceSection = new(@"^(references?|related (documents?|docs|links)|source documents?|sources|see also|links)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExampleSection = new(@"\b(example|examples|template|sample|format|legend|conventions?|notation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExampleText = new(@"^(example|e\.g\.|for example|template)\b\s*[:,]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>A match of an Explorer rule: the structured kind, why it was flagged, and whether the model already holds it.</summary>
internal sealed record ExplorerRuleMatch(string Kind, string Reason, bool Represented,
    SourceContentCategory WhenRepresented = SourceContentCategory.Structural);

/// <summary>Deterministic, Explorer-specific "this resembles a structured fact" rules. Only strong patterns match.</summary>
internal static class ExplorerRules
{
    public static ExplorerRuleMatch? Match(SourceContentExplorer artifact, MarkdownSourceNote block, string text, StructuredFacts facts) => artifact switch
    {
        SourceContentExplorer.Specification => Specification(text, facts),
        SourceContentExplorer.Constitution => Constitution(text, facts),
        SourceContentExplorer.Plan => Sequencing(text, facts, "Plan", block.BlockType),
        SourceContentExplorer.Tasks => Tasks(text, facts, block.BlockType),
        SourceContentExplorer.DataModel => DataModel(text, facts),
        _ => null,
    };

    private static ExplorerRuleMatch Gap(string kind, string reason, bool represented) => new(kind, reason, represented);

    // ── Specification ──
    private static ExplorerRuleMatch? Specification(string text, StructuredFacts facts)
    {
        if (Regex.Match(text, @"^\**(FR|NFR|REQ)-\d+", RegexOptions.IgnoreCase) is { Success: true } fr)
            return Gap("Requirement", "Starts with a requirement ID but is not a structured requirement.", facts.HasId(fr.Value.Trim('*')));
        if (Regex.Match(text, @"^\**SC-\d+", RegexOptions.IgnoreCase) is { Success: true } sc)
            return Gap("Success criterion", "Starts with a success-criterion ID but is not a structured success criterion.", facts.HasId(sc.Value.Trim('*')));
        if (Regex.IsMatch(text, @"\bGiven\b.+\bwhen\b.+\bthen\b", RegexOptions.IgnoreCase))
            return Gap("Scenario", "Given/When/Then statement not represented as a structured scenario.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"^(Q|Question)\s*:", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"^\[?NEEDS CLARIFICATION", RegexOptions.IgnoreCase))
            return Gap("Clarification", "Question or open clarification not represented as a structured clarification.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"\b(MUST|SHALL)( NOT)?\b"))
            return Gap("Domain rule", "Normative MUST/SHALL statement not represented as a structured requirement.", facts.Mentions(text));
        return null;
    }

    // ── Constitution ──
    private static ExplorerRuleMatch? Constitution(string text, StructuredFacts facts)
    {
        var ids = Regex.Matches(text, @"\b[A-Z]{2,4}-\d{2,3}\b").Select(m => m.Value).Distinct().ToList();
        if (ids.Count > 0 && !Regex.IsMatch(text, @"\b(MUST|SHALL)( NOT)?\b"))
            // Mentions of rule IDs (e.g. "PP-01 through PP-09"): supporting reference when the rules exist, a gap otherwise.
            return new("Principle", "Refers to rule IDs that are not structured principles or rules.", ids.All(facts.HasId), SourceContentCategory.Reference);
        if (Regex.IsMatch(text, @"\b(MUST|SHALL)( NOT)?\b|\bNON-NEGOTIABLE\b"))
            return Gap("Obligation", "Normative MUST/SHALL statement not represented in a structured principle, standard or rule.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"^(gate|quality gate)\b.*:", RegexOptions.IgnoreCase))
            return Gap("Gate", "Gate statement not represented as a structured rule.", facts.Mentions(text));
        return null;
    }

    // ── Plan and Tasks: sequencing, parallelization, checkpoints, testing strategy ──
    private static ExplorerRuleMatch? Sequencing(string text, StructuredFacts facts, string model, string blockType)
    {
        // A short lead-in ("After Phase 2 completes:") introduces a list; it states nothing on its own.
        if (text.TrimEnd().EndsWith(':') && text.Length <= 40) return null;
        if (Regex.IsMatch(text, @"\b(STOP and VALIDATE|checkpoint)\b", RegexOptions.IgnoreCase))
            return Gap("Test checkpoint", $"Verification checkpoint not represented as structured {model} checkpoint evidence.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"\bBLOCKS\b|\bblocks (all|every|the)\b|\bdepends on\b|\bblocked by\b|\bmust (complete|finish) before\b|\bneeds \w+ from\b|\b(after|before) (Phase|Step) \w+ (completes?|is (complete|done))", RegexOptions.IgnoreCase)
            && Regex.IsMatch(text, @"\b(Phase|Step|US\d+|T\d{3}|user stor(y|ies)|tasks?)\b", RegexOptions.IgnoreCase))
            return Gap("Dependency", $"Execution dependency not represented in the structured {model} model.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"\b(in parallel|can run concurrently|parallelizable|parallelisable)\b", RegexOptions.IgnoreCase))
            return Gap("Parallelization", $"Parallel execution statement not represented in the structured {model} model.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"\b(tests? (are |is )?written (alongside|before|first)|test-first|TDD|red-green)\b", RegexOptions.IgnoreCase))
            return Gap("Testing strategy", $"Testing approach not represented in the structured {model} testing view.", facts.Mentions(text));
        return null;
    }

    private static ExplorerRuleMatch? Tasks(string text, StructuredFacts facts, string blockType)
    {
        // Readable() already removed the bullet and checkbox, so a task line starts with its ID.
        if (blockType is nameof(MarkdownTokenKind.BulletItem) or nameof(MarkdownTokenKind.OrderedItem)
            && Regex.Match(text, @"^(T\d{3,})\b") is { Success: true } task)
            return Gap("Task", "Task-like line with a task ID that is not a structured task.", facts.HasId(task.Groups[1].Value));
        return Sequencing(text, facts, "Task", blockType);
    }

    // ── Data Model ──
    private static ExplorerRuleMatch? DataModel(string text, StructuredFacts facts)
    {
        if (Regex.IsMatch(text, @"\b(seed(s|ed|ing)?|HasData)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(text, @"^\s*\|"))
            return Gap("Reference data", "Seed or reference-data rule; the structured data model has no seed-data representation.", facts.Mentions(text));
        if (Regex.IsMatch(text, @"\bindex(es|ed)?\b", RegexOptions.IgnoreCase) && Regex.Match(text, @"\b([A-Z]\w+)\.([A-Z]\w+)\b") is { Success: true } column)
            return Gap("Index", "Index statement not represented in the structured indexes.", facts.HasIndex(column.Groups[1].Value, column.Groups[2].Value));
        var entities = facts.EntitiesIn(text);
        if (entities.Count >= 2 && Regex.IsMatch(text, @"→|->|─>|─►|►|▶|—\(|\(1:[1N*]\)|\b1:N\b|\bN:1\b|\b1:1\b|\bFK\b|foreign key|references\b", RegexOptions.IgnoreCase))
            return Gap("Relationship", "Relationship statement between known structures not represented in the structured relationships.",
                entities.Zip(entities.Skip(1)).All(pair => facts.HasRelationship(pair.First, pair.Second)));
        if (Regex.IsMatch(text, @"\bmigration(s|Builder)?\b", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"\b(add(s|ed)?|creates?|drops?|renames?|alters?|must|applies|via)\b", RegexOptions.IgnoreCase))
            return Gap("Migration rule", "Migration rule; the structured data model records declared structure, not migration steps.", facts.Mentions(text));
        return null;
    }
}

/// <summary>Read-only lookups over the active structured model: IDs, entities, relationships, indexes and normalized text.</summary>
internal sealed class StructuredFacts
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _corpus;
    private readonly DataModelDocument? _dataModel;
    private readonly List<string> _entityNames = [];

    public StructuredFacts(UnmappedSourceContext context)
    {
        var text = new StringBuilder();
        Collect(context.Model, text, new HashSet<object>(ReferenceEqualityComparer.Instance));
        _corpus = Normalize(text.ToString());
        foreach (Match id in Regex.Matches(text.ToString(), @"\b([A-Z]{1,4}-\d{2,4}|T\d{3,})\b")) _ids.Add(id.Value);
        if (context.Model is DataModelDocument dm)
        {
            _dataModel = dm;
            _entityNames.AddRange(dm.Entities.Select(e => e.Name).Where(n => n.Length > 1).OrderByDescending(n => n.Length));
        }
    }

    public bool HasId(string id) => _ids.Contains(id);

    /// <summary>The statement's words appear, normalized, in the structured model's own text.</summary>
    public bool Mentions(string text)
    {
        var needle = Normalize(text);
        return needle.Length >= 12 && _corpus.Contains(needle, StringComparison.Ordinal);
    }

    public IReadOnlyList<string> EntitiesIn(string text) => _entityNames
        .Select(name => (name, index: Regex.Match(text, $@"(?<![\w]){Regex.Escape(name)}(?![\w])")))
        .Where(x => x.index.Success)
        .GroupBy(x => x.index.Index).Select(g => g.First())   // longest name wins at one position
        .OrderBy(x => x.index.Index).Select(x => x.name).ToList();

    public bool HasRelationship(string a, string b) => _dataModel?.Relationships.Any(r =>
        (Same(r.SourceEntity, a) && Same(r.TargetEntity, b)) || (Same(r.SourceEntity, b) && Same(r.TargetEntity, a))) == true;

    public bool HasIndex(string entity, string column) => _dataModel?.Indexes.Any(i =>
        Same(i.EntityName, entity) && i.Columns.Any(c => Same(c, column))) == true;

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) =>
        Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[*`_#>|\[\]()]", " "), @"\s+", " ").Trim();

    private static void Collect(object? value, StringBuilder text, HashSet<object> visited)
    {
        if (value is null) return;
        if (value is string s) { text.Append(s).Append('\n'); return; }
        if (value is MarkdownSourceNote || value is ProjectionProvenance || !visited.Add(value)) return;
        if (value is System.Collections.IEnumerable sequence)
        {
            foreach (var item in sequence) Collect(item, text, visited);
            return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid)) return;
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "UnmappedSourceBlocks" or "NodeId" or "Id" or "Provenance") continue;
            try { Collect(property.GetValue(value), text, visited); }
            catch (System.Reflection.TargetInvocationException) { }
        }
    }
}
