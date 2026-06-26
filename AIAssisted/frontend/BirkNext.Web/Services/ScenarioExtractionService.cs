using System.Diagnostics;
using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Services;

public interface IScenarioExtractionService
{
    Task<ExtractionPipelineResult> ExtractAsync(
        string specificationText,
        ExtractionProfile profile = ExtractionProfile.Default,
        CancellationToken cancellationToken = default);
}

public sealed class ScenarioExtractionService : IScenarioExtractionService
{
    private readonly IExtractionConfiguration _config;
    private readonly IExtractionRuleEngine _ruleEngine;
    private readonly ILogger<ScenarioExtractionService> _logger;
    // Lazily-initialized Speckit engine — created on first Speckit extraction.
    private IExtractionRuleEngine? _speckitRuleEngine;

    // Internal constructor used by the DI factory in Program.cs.
    // IExtractionRuleEngine is internal; a public constructor cannot accept it (CS0051).
    internal ScenarioExtractionService(
        IExtractionConfiguration config,
        IExtractionRuleEngine ruleEngine,
        ILogger<ScenarioExtractionService> logger)
    {
        _config = config;
        _ruleEngine = ruleEngine;
        _logger = logger;
    }

    // Public convenience constructor for direct instantiation (tests and backward compatibility).
    // Builds the default rule engine from ExtractionRuleSet.Default(); logging is a no-op.
    public ScenarioExtractionService(IExtractionConfiguration config)
        : this(config, new ExtractionRuleEngine(ExtractionRuleSet.Default(), config),
               NullLogger<ScenarioExtractionService>.Instance)
    {
    }

    public Task<ExtractionPipelineResult> ExtractAsync(
        string specificationText,
        ExtractionProfile profile = ExtractionProfile.Default,
        CancellationToken cancellationToken = default)
        => Task.FromResult(RunPipeline(specificationText, profile));

    private IExtractionRuleEngine GetEngineForProfile(ExtractionProfile profile) =>
        profile == ExtractionProfile.Speckit
            ? (_speckitRuleEngine ??= new ExtractionRuleEngine(ExtractionRuleSet.Speckit(), _config))
            : _ruleEngine;

    private ExtractionPipelineResult RunPipeline(string rawInput, ExtractionProfile profile)
    {
        var engine = GetEngineForProfile(profile);
        var sw = Stopwatch.StartNew();
        var summary = new RuleExecutionSummary();

        // -------------------------------------------------------------------------
        // Stage 1: Input Validation Gate
        // Capture raw metrics BEFORE normalization.
        // -------------------------------------------------------------------------
        var inputLengthChars = rawInput?.Length ?? 0;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            // no raw text — counts only
            _logger.LogInformation(
                "ExtractionEmpty: inputLengthChars={InputLengthChars}, reason={Reason}",
                inputLengthChars, "empty_input");
            return ExtractionPipelineResult.NonSuccess(PipelineStatus.EmptyInput, inputLengthChars, 0, 0, profile);
        }

        if (inputLengthChars > _config.MaxInputLengthChars)
        {
            sw.Stop();
            // no raw text — counts only
            _logger.LogInformation(
                "ExtractionEmpty: inputLengthChars={InputLengthChars}, reason={Reason}",
                inputLengthChars, "input_too_large");
            return ExtractionPipelineResult.NonSuccess(
                PipelineStatus.InputTooLarge, inputLengthChars, CountLines(rawInput), sw.ElapsedMilliseconds, profile);
        }

        // -------------------------------------------------------------------------
        // Stage 2: Normalization
        // -------------------------------------------------------------------------
        if (rawInput.StartsWith('﻿'))
            rawInput = rawInput[1..];

        var normalized = rawInput.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        var inputLineCount = lines.Length;

        // -------------------------------------------------------------------------
        // Stages 3–7
        // -------------------------------------------------------------------------
        var blocks = PartitionBlocks(lines);                           // Stage 3
        var filtered = FilterBlocksWithEngine(blocks, summary, engine); // Stage 4
        var contents = ExtractContent(filtered);                        // Stage 5
        contents = GroupBddSteps(contents);                             // Stage 5.3

        // Stage 5.5 — IgnorePrefixes filter (US4)
        // No-op when IgnorePrefixes is empty (default configuration).
        var ignorePrefixes = engine.IgnorePrefixes;
        if (ignorePrefixes.Count > 0)
            contents = contents
                .Where(item => !ignorePrefixes.Any(p =>
                    item.PlainText.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var classified = ClassifyContent(contents, summary, engine);    // Stage 6

        // Stage 6.5 — NarrativeContext suppression
        // Drop blocks classified as narrative/documentation context (business rationale,
        // user story prose, section labels) before deduplication.
        var narrativeDropped = classified.Count(c => c.Signal == ClassificationSignal.NarrativeContext);
        if (narrativeDropped > 0)
            classified = classified.Where(c => c.Signal != ClassificationSignal.NarrativeContext).ToList();

        var deduplicated = Deduplicate(classified, summary);            // Stage 7

        sw.Stop();
        var durationMs = sw.ElapsedMilliseconds;

        // -------------------------------------------------------------------------
        // Stage 8: Result Assembly
        // -------------------------------------------------------------------------
        if (deduplicated.Count == 0)
        {
            // no raw text — counts only
            _logger.LogInformation(
                "ExtractionEmpty: inputLengthChars={InputLengthChars}, reason={Reason}",
                inputLengthChars, "no_candidates_found");
            return ExtractionPipelineResult.NonSuccess(
                PipelineStatus.NoResults, inputLengthChars, inputLineCount, durationMs, profile);
        }

        var candidates = new List<ExtractionCandidate>(deduplicated.Count);
        int reqCount = 0, testCount = 0, ncCount = 0;

        foreach (var item in deduplicated)
        {
            candidates.Add(new ExtractionCandidate
            {
                Title = item.PlainText,
                Classification = item.Kind,
                ClassificationSignal = item.Signal,
                ContextHeading = item.ContextHeading,
                SourceBlockType = item.SourceBlockType,
            });
            switch (item.Kind)
            {
                case ScenarioKind.Requirement: reqCount++; break;
                case ScenarioKind.Test: testCount++; break;
                default: ncCount++; break;
            }
        }

        // no raw text — counts only
        _logger.LogInformation(
            "ExtractionCompleted: candidateCount={CandidateCount}, requirementCount={RequirementCount}, testCount={TestCount}, needsClarificationCount={NeedsClarificationCount}, durationMs={DurationMs}, rulesEvaluatedCount={RulesEvaluatedCount}, duplicatesDroppedCount={DuplicatesDroppedCount}",
            candidates.Count, reqCount, testCount, ncCount, durationMs, summary.TotalRulesEvaluated, summary.DuplicatesDropped);

        return ExtractionPipelineResult.Success(
            candidates, inputLengthChars, inputLineCount, durationMs,
            reqCount, testCount, ncCount, profile);
    }

    // =============================================================================
    // Stage 3: Block Partitioning
    // =============================================================================

    private static IReadOnlyList<TextBlock> PartitionBlocks(string[] lines)
    {
        var blocks = new List<TextBlock>(lines.Length);
        string? currentHeading = null;
        bool inFencedCode = false;
        bool seenNonEmptyLine = false;

        // Consecutive paragraph lines (not separated by a blank line or structural block) are
        // accumulated here and flushed as a single ParagraphLine block. This prevents every
        // hard-wrapped sentence in a multi-sentence paragraph from becoming its own candidate.
        var paragraphBuf = new List<string>();
        string? paragraphHeading = null;

        void FlushParagraph()
        {
            if (paragraphBuf.Count == 0) return;
            blocks.Add(new TextBlock(string.Join(" ", paragraphBuf), BlockType.ParagraphLine, 0, paragraphHeading));
            paragraphBuf.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // YAML front matter: only possible before any non-empty content.
            if (!seenNonEmptyLine && !inFencedCode && trimmed == "---")
            {
                FlushParagraph();
                blocks.Add(new TextBlock(line, BlockType.YamlFrontMatter, 0, currentHeading));
                i++;
                while (i < lines.Length)
                {
                    var fmLine = lines[i];
                    var fmTrimmed = fmLine.Trim();
                    blocks.Add(new TextBlock(fmLine, BlockType.YamlFrontMatter, 0, currentHeading));
                    i++;
                    if (fmTrimmed == "---" || fmTrimmed == "...") break;
                }
                seenNonEmptyLine = true;
                i--; // compensate for the for-loop increment
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
                seenNonEmptyLine = true;

            // Fenced code block toggle (``` ... ```)
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                inFencedCode = !inFencedCode;
                blocks.Add(new TextBlock(line, BlockType.FencedCodeBlock, 0, currentHeading));
                continue;
            }

            if (inFencedCode)
            {
                blocks.Add(new TextBlock(line, BlockType.FencedCodeBlock, 0, currentHeading));
                continue;
            }

            // Empty line — paragraph boundary: flush any accumulated paragraph lines.
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                blocks.Add(new TextBlock(line, BlockType.Empty, 0, currentHeading));
                continue;
            }

            // HTML comment
            if (trimmed.StartsWith("<!--"))
            {
                FlushParagraph();
                blocks.Add(new TextBlock(line, BlockType.HtmlComment, 0, currentHeading));
                continue;
            }

            // ATX heading (#, ##, ###, ...)
            if (trimmed.StartsWith("#"))
            {
                FlushParagraph();
                var headingText = trimmed.TrimStart('#').Trim();
                blocks.Add(new TextBlock(line, BlockType.Heading, 0, currentHeading));
                currentHeading = headingText; // update AFTER the heading block uses the old value
                continue;
            }

            // Horizontal rule: 3+ identical chars (-, *, _) optionally with spaces
            if (IsHorizontalRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new TextBlock(line, BlockType.HorizontalRule, 0, currentHeading));
                continue;
            }

            // Blockquote
            if (trimmed.StartsWith(">"))
            {
                FlushParagraph();
                blocks.Add(new TextBlock(line, BlockType.Blockquote, 0, currentHeading));
                continue;
            }

            // Unordered list item (-, *, + followed by space)
            if (IsUnorderedListItem(trimmed))
            {
                FlushParagraph();
                int indent = (line.Length - trimmed.Length) / 2;
                blocks.Add(new TextBlock(line, BlockType.UnorderedListItem, indent, currentHeading));
                continue;
            }

            // Ordered list item (N. followed by space)
            if (IsOrderedListItem(trimmed))
            {
                FlushParagraph();
                int indent = (line.Length - trimmed.Length) / 2;
                blocks.Add(new TextBlock(line, BlockType.OrderedListItem, indent, currentHeading));
                continue;
            }

            // Table rows — use look-ahead to detect header vs body
            if (trimmed.StartsWith("|"))
            {
                FlushParagraph();
                BlockType tableType;
                if (IsTableSeparator(trimmed))
                {
                    tableType = BlockType.TableSeparatorRow;
                }
                else if (i + 1 < lines.Length && IsTableSeparator(lines[i + 1].TrimStart()))
                {
                    tableType = BlockType.TableHeaderRow;
                }
                else
                {
                    tableType = BlockType.TableBodyRow;
                }
                blocks.Add(new TextBlock(line, tableType, 0, currentHeading));
                continue;
            }

            // Paragraph line (catch-all) — accumulate consecutive lines into a single block.
            // The buffer is flushed by blank lines, headings, list items, and any structural block.
            if (paragraphBuf.Count == 0)
                paragraphHeading = currentHeading;
            paragraphBuf.Add(trimmed);
        }

        // Flush any paragraph that reached the end of input without a trailing blank line.
        FlushParagraph();

        return blocks;
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (var ch in trimmed)
            if (ch != c && ch != ' ') return false;
        return true;
    }

    private static bool IsUnorderedListItem(string trimmed)
        => trimmed.Length >= 2
        && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+')
        && trimmed[1] == ' ';

    private static bool IsOrderedListItem(string trimmed)
    {
        int j = 0;
        while (j < trimmed.Length && char.IsDigit(trimmed[j])) j++;
        return j > 0 && j < trimmed.Length - 1 && trimmed[j] == '.' && trimmed[j + 1] == ' ';
    }

    private static bool IsTableSeparator(string trimmed)
    {
        if (!trimmed.StartsWith("|")) return false;
        foreach (var c in trimmed)
            if (c != '-' && c != ':' && c != ' ' && c != '|') return false;
        return trimmed.Length > 1;
    }

    // =============================================================================
    // Stage 4: Structure Filter
    // =============================================================================

    private static List<TextBlock> FilterBlocksWithEngine(
        IReadOnlyList<TextBlock> blocks, RuleExecutionSummary summary, IExtractionRuleEngine engine)
    {
        var result = new List<TextBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            var evalResult = engine.Evaluate(block, string.Empty);
            if (evalResult.IsFiltered)
            {
                summary.FilteredBlockCount++;
                summary.TotalRulesEvaluated += evalResult.EvaluatedRuleCount;
            }
            else
            {
                result.Add(block);
            }
        }
        return result;
    }

    // =============================================================================
    // Stage 5: Content Extraction
    // =============================================================================

    private readonly record struct ContentItem(
        string PlainText,
        string? ContextHeading,
        BlockType SourceBlockType);

    private List<ContentItem> ExtractContent(List<TextBlock> blocks)
    {
        var result = new List<ContentItem>(blocks.Count);
        foreach (var block in blocks)
        {
            var text = StripMarkdown(block.RawText, block.BlockType);
            if (text.Length < _config.MinCandidateLengthChars)
                continue;
            result.Add(new ContentItem(text, block.PrecedingHeading, block.BlockType));
        }
        return result;
    }

    private static string StripMarkdown(string rawText, BlockType blockType)
    {
        var text = rawText.TrimStart();

        // Strip list markers
        if (blockType == BlockType.UnorderedListItem)
        {
            if (text.Length >= 2 && (text[0] == '-' || text[0] == '*' || text[0] == '+') && text[1] == ' ')
                text = text[2..];
        }
        else if (blockType == BlockType.OrderedListItem)
        {
            int j = 0;
            while (j < text.Length && char.IsDigit(text[j])) j++;
            if (j > 0 && j < text.Length - 1 && text[j] == '.' && text[j + 1] == ' ')
                text = text[(j + 2)..];
        }
        else if (blockType == BlockType.TableBodyRow)
        {
            // Strip all pipe characters; the classification signal survives
            text = text.Replace("|", " ").Trim();
        }

        // Strip image syntax ![alt](url) entirely (before link syntax)
        text = ImagePattern.Replace(text, string.Empty);

        // Strip link syntax [text](url) → display text
        text = LinkPattern.Replace(text, "$1");

        // Strip inline code `text` → text (before bold/italic so backtick content is resolved first)
        text = CodePattern.Replace(text, "$1");

        // Strip bold **text** → text (must precede italic stripping to avoid partial matching of **)
        text = BoldAsteriskPattern.Replace(text, "$1");

        // Strip italic *text* → text
        text = ItalicAsteriskPattern.Replace(text, "$1");

        return text.Trim();
    }

    private static readonly Regex ImagePattern = new(
        @"!\[[^\]]*\]\([^)]*\)", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static readonly Regex LinkPattern = new(
        @"\[([^\]]+)\]\([^)]*\)", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static readonly Regex CodePattern = new(
        @"`([^`]*)`", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    // Bold markers **text** → text. Uses [^*\n]+ (one-or-more non-star, non-newline) so
    // empty-content and triple-asterisk edge cases are handled without greedy mismatch.
    private static readonly Regex BoldAsteriskPattern = new(
        @"\*{2}([^*\n]+)\*{2}", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    // Italic markers *text* → text. Applied after bold so **text** is already resolved.
    private static readonly Regex ItalicAsteriskPattern = new(
        @"\*([^*\n]+)\*", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    // =============================================================================
    // Stage 5.3: BDD Step Grouping
    // Merges adjacent BDD step lines (Given/When/Then/And/But) into single ContentItems
    // so that multi-line scenarios produce one TEST candidate instead of several.
    // Orphaned And/But lines (no preceding BDD step) are silently dropped.
    // =============================================================================

    private enum BddStepKind { None, Given, WhenOrThen, AndOrBut }

    private static BddStepKind GetBddStepKind(string text)
    {
        if (text.StartsWith("Given ", StringComparison.OrdinalIgnoreCase)) return BddStepKind.Given;
        if (text.StartsWith("When ",  StringComparison.OrdinalIgnoreCase)) return BddStepKind.WhenOrThen;
        if (text.StartsWith("Then ",  StringComparison.OrdinalIgnoreCase)) return BddStepKind.WhenOrThen;
        if (text.StartsWith("And ",   StringComparison.OrdinalIgnoreCase)) return BddStepKind.AndOrBut;
        if (text.StartsWith("But ",   StringComparison.OrdinalIgnoreCase)) return BddStepKind.AndOrBut;
        return BddStepKind.None;
    }

    private static List<ContentItem> GroupBddSteps(List<ContentItem> items)
    {
        var result = new List<ContentItem>(items.Count);
        var group  = new List<ContentItem>();

        foreach (var item in items)
        {
            switch (GetBddStepKind(item.PlainText))
            {
                case BddStepKind.Given:
                    FlushBddGroup(group, result);
                    group.Clear();
                    group.Add(item);
                    break;

                case BddStepKind.WhenOrThen:
                    group.Add(item);
                    break;

                case BddStepKind.AndOrBut:
                    if (group.Count > 0)
                        group.Add(item);
                    // else: orphaned continuation — silently drop
                    break;

                default:
                    FlushBddGroup(group, result);
                    group.Clear();
                    result.Add(item);
                    break;
            }
        }

        FlushBddGroup(group, result);
        return result;
    }

    private static void FlushBddGroup(List<ContentItem> group, List<ContentItem> result)
    {
        if (group.Count == 0) return;
        result.Add(group.Count == 1
            ? group[0]
            : new ContentItem(
                  string.Join(" ", group.Select(i => i.PlainText)),
                  group[0].ContextHeading,
                  group[0].SourceBlockType));
    }

    // =============================================================================
    // Stage 6: Classification
    // =============================================================================

    private readonly record struct ClassifiedItem(
        string PlainText,
        string? ContextHeading,
        BlockType SourceBlockType,
        ClassificationSignal Signal,
        ScenarioKind Kind);
    private static List<ClassifiedItem> ClassifyContent(
        List<ContentItem> items, RuleExecutionSummary summary, IExtractionRuleEngine engine)
    {
        var result = new List<ClassifiedItem>(items.Count);
        foreach (var item in items)
        {
            var block = new TextBlock(item.PlainText, item.SourceBlockType, 0, item.ContextHeading);
            var evalResult = engine.Evaluate(block, item.PlainText);
            summary.TotalRulesEvaluated += evalResult.EvaluatedRuleCount;
            if (evalResult.Signal == ClassificationSignal.Default)
                summary.DefaultFallbackCount++;
            result.Add(new ClassifiedItem(
                item.PlainText, item.ContextHeading, item.SourceBlockType,
                evalResult.Signal!.Value, evalResult.Classification!.Value));
        }
        return result;
    }

    // =============================================================================
    // Stage 7: Deduplication
    // Two-pass algorithm:
    //   Pass 1 — group items by normalized key; track the highest-quality candidate per group.
    //   Pass 2 — emit winners in original document order; count dropped items for diagnostics.
    // Normalization strips terminal punctuation, leading articles, and leading subject+modal
    // phrases so that "System must validate X" and "Application should validate X" reduce to
    // the same key. Word-order changes and mid-sentence modals are NOT normalized (determinism).
    // =============================================================================

    private static List<ClassifiedItem> Deduplicate(List<ClassifiedItem> items, RuleExecutionSummary summary)
    {
        if (items.Count == 0) return items;

        // Pass 1: for each normalized key, track the best candidate (index + quality score).
        // Strictly-greater guard preserves first-occurrence when scores are equal.
        var best = new Dictionary<string, (int Index, int Score)>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var key   = ComputeDedupKey(items[i].PlainText);
            var score = CandidateQualityScore(items[i]);
            if (!best.TryGetValue(key, out var existing) || score > existing.Score)
                best[key] = (i, score);
        }

        // Pass 2: emit winners in original document order.
        var winnerSet = new HashSet<int>(best.Values.Select(v => v.Index));
        var result    = new List<ClassifiedItem>(best.Count);
        for (int i = 0; i < items.Count; i++)
            if (winnerSet.Contains(i))
                result.Add(items[i]);

        summary.DuplicatesDropped = items.Count - result.Count;
        return result;
    }

    // Normalized deduplication key. Operates on PlainText that has already had markdown
    // formatting stripped by Stage 5 (StripMarkdown). The key is always lowercase.
    private static string ComputeDedupKey(string text)
    {
        var t = text.ToLowerInvariant();

        // Strip terminal punctuation
        t = t.TrimEnd('.', ',', ';', ':', '!', '?');

        // Collapse internal whitespace
        t = DedupWhitespacePattern.Replace(t.Trim(), " ");

        // Strip leading article
        if      (t.StartsWith("the ")) t = t[4..];
        else if (t.StartsWith("an "))  t = t[3..];
        else if (t.StartsWith("a "))   t = t[2..];

        // Strip leading subject + modal phrase:
        //   "system must", "application should", "app will", "service shall", etc.
        //   Requires the modal — does NOT strip subject-only ("system validates").
        t = DedupSubjectPhrasePattern.Replace(t, string.Empty);

        // Strip any remaining leading standalone modal:
        //   "should validate" → "validate", "must log" → "log"
        t = DedupLeadingModalPattern.Replace(t, string.Empty);

        return t.Trim();
    }

    // Quality score used to select the best candidate when multiple items share a key.
    // Higher word count = more complete sentence. Signal bonus rewards explicit markers.
    private static int CandidateQualityScore(ClassifiedItem item)
    {
        int words = item.PlainText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int bonus = item.Signal switch
        {
            ClassificationSignal.BddPattern          => 20,
            ClassificationSignal.Rfc2119Uppercase    => 15,
            ClassificationSignal.Rfc2119Lowercase    => 10,
            ClassificationSignal.FrPrefix            => 10,
            ClassificationSignal.ClarificationSignal => 5,
            _                                        => 0,
        };
        return words + bonus;
    }

    // Operates on already-lowercased text (ComputeDedupKey lowercases before applying).
    private static readonly Regex DedupWhitespacePattern = new(
        @"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DedupSubjectPhrasePattern = new(
        @"^(?:system|application|app|service)\s+(?:should|must|shall|will|can|may|is required to)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DedupLeadingModalPattern = new(
        @"^(?:should|must|shall|will|can|may)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // =============================================================================
    // Helpers
    // =============================================================================

    private static int CountLines(string text)
    {
        int count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        return count;
    }
}
