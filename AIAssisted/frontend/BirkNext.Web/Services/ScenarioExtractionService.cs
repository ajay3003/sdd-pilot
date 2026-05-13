using System.Diagnostics;
using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IScenarioExtractionService
{
    Task<ExtractionPipelineResult> ExtractAsync(
        string specificationText,
        CancellationToken cancellationToken = default);
}

public sealed class ScenarioExtractionService : IScenarioExtractionService
{
    private readonly IExtractionConfiguration _config;

    public ScenarioExtractionService(IExtractionConfiguration config)
    {
        _config = config;
    }

    public Task<ExtractionPipelineResult> ExtractAsync(
        string specificationText,
        CancellationToken cancellationToken = default)
        => Task.FromResult(RunPipeline(specificationText));

    private ExtractionPipelineResult RunPipeline(string rawInput)
    {
        var sw = Stopwatch.StartNew();

        // -------------------------------------------------------------------------
        // Stage 1: Input Validation Gate
        // Capture raw metrics BEFORE normalization.
        // -------------------------------------------------------------------------
        var inputLengthChars = rawInput?.Length ?? 0;

        if (string.IsNullOrWhiteSpace(rawInput))
            return ExtractionPipelineResult.NonSuccess(PipelineStatus.EmptyInput, inputLengthChars, 0, 0);

        if (inputLengthChars > _config.MaxInputLengthChars)
        {
            sw.Stop();
            return ExtractionPipelineResult.NonSuccess(
                PipelineStatus.InputTooLarge, inputLengthChars, CountLines(rawInput), sw.ElapsedMilliseconds);
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
        var blocks    = PartitionBlocks(lines);           // Stage 3
        var filtered  = FilterBlocks(blocks);             // Stage 4
        var contents  = ExtractContent(filtered);         // Stage 5
        var classified = ClassifyContent(contents);       // Stage 6
        var deduplicated = Deduplicate(classified);       // Stage 7

        sw.Stop();
        var durationMs = sw.ElapsedMilliseconds;

        // -------------------------------------------------------------------------
        // Stage 8: Result Assembly
        // -------------------------------------------------------------------------
        if (deduplicated.Count == 0)
            return ExtractionPipelineResult.NonSuccess(
                PipelineStatus.NoResults, inputLengthChars, inputLineCount, durationMs);

        var candidates = new List<ExtractionCandidate>(deduplicated.Count);
        int reqCount = 0, testCount = 0, ncCount = 0;

        foreach (var item in deduplicated)
        {
            var kind = SignalToKind(item.Signal);
            candidates.Add(new ExtractionCandidate
            {
                Title = item.PlainText,
                Classification = kind,
                ClassificationSignal = item.Signal,
                ContextHeading = item.ContextHeading,
                SourceBlockType = item.SourceBlockType,
            });
            switch (kind)
            {
                case ScenarioKind.Requirement:        reqCount++;  break;
                case ScenarioKind.Test:               testCount++; break;
                default:                              ncCount++;   break;
            }
        }

        return ExtractionPipelineResult.Success(
            candidates, inputLengthChars, inputLineCount, durationMs,
            reqCount, testCount, ncCount);
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

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // YAML front matter: only possible before any non-empty content.
            if (!seenNonEmptyLine && !inFencedCode && trimmed == "---")
            {
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
                inFencedCode = !inFencedCode;
                blocks.Add(new TextBlock(line, BlockType.FencedCodeBlock, 0, currentHeading));
                continue;
            }

            if (inFencedCode)
            {
                blocks.Add(new TextBlock(line, BlockType.FencedCodeBlock, 0, currentHeading));
                continue;
            }

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
            {
                blocks.Add(new TextBlock(line, BlockType.Empty, 0, currentHeading));
                continue;
            }

            // HTML comment
            if (trimmed.StartsWith("<!--"))
            {
                blocks.Add(new TextBlock(line, BlockType.HtmlComment, 0, currentHeading));
                continue;
            }

            // ATX heading (#, ##, ###, ...)
            if (trimmed.StartsWith("#"))
            {
                var headingText = trimmed.TrimStart('#').Trim();
                blocks.Add(new TextBlock(line, BlockType.Heading, 0, currentHeading));
                currentHeading = headingText; // update AFTER the heading block uses the old value
                continue;
            }

            // Horizontal rule: 3+ identical chars (-, *, _) optionally with spaces
            if (IsHorizontalRule(trimmed))
            {
                blocks.Add(new TextBlock(line, BlockType.HorizontalRule, 0, currentHeading));
                continue;
            }

            // Blockquote
            if (trimmed.StartsWith(">"))
            {
                blocks.Add(new TextBlock(line, BlockType.Blockquote, 0, currentHeading));
                continue;
            }

            // Unordered list item (-, *, + followed by space)
            if (IsUnorderedListItem(trimmed))
            {
                int indent = (line.Length - trimmed.Length) / 2;
                blocks.Add(new TextBlock(line, BlockType.UnorderedListItem, indent, currentHeading));
                continue;
            }

            // Ordered list item (N. followed by space)
            if (IsOrderedListItem(trimmed))
            {
                int indent = (line.Length - trimmed.Length) / 2;
                blocks.Add(new TextBlock(line, BlockType.OrderedListItem, indent, currentHeading));
                continue;
            }

            // Table rows — use look-ahead to detect header vs body
            if (trimmed.StartsWith("|"))
            {
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

            // Paragraph line (catch-all)
            blocks.Add(new TextBlock(line, BlockType.ParagraphLine, 0, currentHeading));
        }

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

    private static readonly HashSet<BlockType> FilteredBlockTypes =
    [
        BlockType.Heading,
        BlockType.FencedCodeBlock,
        BlockType.Blockquote,
        BlockType.HorizontalRule,
        BlockType.HtmlComment,
        BlockType.YamlFrontMatter,
        BlockType.Empty,
        BlockType.TableHeaderRow,
        BlockType.TableSeparatorRow,
    ];

    private static List<TextBlock> FilterBlocks(IReadOnlyList<TextBlock> blocks)
        => blocks.Where(b => !FilteredBlockTypes.Contains(b.BlockType)).ToList();

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

        // Strip inline code `text` → text
        text = CodePattern.Replace(text, "$1");

        return text.Trim();
    }

    private static readonly Regex ImagePattern = new(
        @"!\[[^\]]*\]\([^)]*\)", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static readonly Regex LinkPattern = new(
        @"\[([^\]]+)\]\([^)]*\)", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    private static readonly Regex CodePattern = new(
        @"`([^`]*)`", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    // =============================================================================
    // Stage 6: Classification
    // =============================================================================

    private readonly record struct ClassifiedItem(
        string PlainText,
        string? ContextHeading,
        BlockType SourceBlockType,
        ClassificationSignal Signal);

    private List<ClassifiedItem> ClassifyContent(List<ContentItem> items)
    {
        var result = new List<ClassifiedItem>(items.Count);
        foreach (var item in items)
            result.Add(new ClassifiedItem(
                item.PlainText, item.ContextHeading, item.SourceBlockType,
                ClassifyText(item.PlainText)));
        return result;
    }

    private ClassificationSignal ClassifyText(string text)
    {
        // Lines exceeding the per-line cap skip pattern matching (ReDoS prevention)
        if (text.Length > _config.MaxLineLengthForPatternMatching)
            return ClassificationSignal.Default;

        // Priority 1: BDD pattern (near-zero false-positive)
        if (IsBddPattern(text))    return ClassificationSignal.BddPattern;

        // Priority 2: RFC 2119 uppercase modal verbs
        if (Rfc2119UpperPattern.IsMatch(text)) return ClassificationSignal.Rfc2119Uppercase;

        // Priority 3: RFC 2119 lowercase modal verbs / phrases
        if (Rfc2119LowerPattern.IsMatch(text)) return ClassificationSignal.Rfc2119Lowercase;

        // Priority 4: Functional requirement prefix (FR-NNN)
        if (FrPrefixPattern.IsMatch(text)) return ClassificationSignal.FrPrefix;

        // Priority 5: Question terminator
        if (text.TrimEnd().EndsWith('?')) return ClassificationSignal.QuestionTerminator;

        // Priority 6: Deferral marker
        if (DeferralPattern.IsMatch(text)) return ClassificationSignal.DeferralMarker;

        // Default fallback
        return ClassificationSignal.Default;
    }

    private static bool IsBddPattern(string text)
    {
        // Triple: Given ... When ... Then in document order on the same line
        int gi = text.IndexOf("Given ", StringComparison.OrdinalIgnoreCase);
        int wi = text.IndexOf("When ",  StringComparison.OrdinalIgnoreCase);
        int ti = text.IndexOf("Then ",  StringComparison.OrdinalIgnoreCase);
        if (gi >= 0 && wi > gi && ti > wi) return true;

        // Single BDD section opener at the start of the line
        return text.StartsWith("Given ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("When ",  StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Then ",  StringComparison.OrdinalIgnoreCase);
    }

    // Case-sensitive: MUST NOT / SHALL NOT must precede MUST / SHALL in alternation
    private static readonly Regex Rfc2119UpperPattern = new(
        @"\b(MUST NOT|SHALL NOT|MUST|SHALL|SHOULD|MAY)\b",
        RegexOptions.None,
        TimeSpan.FromMilliseconds(100));

    // Case-insensitive; longer phrases precede their component words
    private static readonly Regex Rfc2119LowerPattern = new(
        @"\b(must not|shall not|is required to|must|shall|required)\b",
        RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FrPrefixPattern = new(
        @"\bFR-\d+\b",
        RegexOptions.None,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DeferralPattern = new(
        @"\b(TBD|TODO|TBC|open question|to be defined|to be decided)\b",
        RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static ScenarioKind SignalToKind(ClassificationSignal signal) => signal switch
    {
        ClassificationSignal.BddPattern       => ScenarioKind.Test,
        ClassificationSignal.Rfc2119Uppercase => ScenarioKind.Requirement,
        ClassificationSignal.Rfc2119Lowercase => ScenarioKind.Requirement,
        ClassificationSignal.FrPrefix         => ScenarioKind.Requirement,
        _                                     => ScenarioKind.NeedsClarification,
    };

    // =============================================================================
    // Stage 7: Deduplication
    // =============================================================================

    private static List<ClassifiedItem> Deduplicate(List<ClassifiedItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ClassifiedItem>(items.Count);
        foreach (var item in items)
            if (seen.Add(item.PlainText.Trim()))
                result.Add(item);
        return result;
    }

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
