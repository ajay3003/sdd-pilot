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
        CancellationToken cancellationToken = default);
}

public sealed class ScenarioExtractionService : IScenarioExtractionService
{
    private readonly IExtractionConfiguration _config;
    private readonly IExtractionRuleEngine _ruleEngine;
    private readonly ILogger<ScenarioExtractionService> _logger;

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
        CancellationToken cancellationToken = default)
        => Task.FromResult(RunPipeline(specificationText));

    private ExtractionPipelineResult RunPipeline(string rawInput)
    {
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
            return ExtractionPipelineResult.NonSuccess(PipelineStatus.EmptyInput, inputLengthChars, 0, 0);
        }

        if (inputLengthChars > _config.MaxInputLengthChars)
        {
            sw.Stop();
            // no raw text — counts only
            _logger.LogInformation(
                "ExtractionEmpty: inputLengthChars={InputLengthChars}, reason={Reason}",
                inputLengthChars, "input_too_large");
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
        var blocks = PartitionBlocks(lines);                    // Stage 3
        var filtered = FilterBlocksWithEngine(blocks, summary); // Stage 4
        var contents = ExtractContent(filtered);                 // Stage 5
        var classified = ClassifyContent(contents, summary);     // Stage 6
        var deduplicated = Deduplicate(classified);              // Stage 7

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
                PipelineStatus.NoResults, inputLengthChars, inputLineCount, durationMs);
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
            "ExtractionCompleted: candidateCount={CandidateCount}, requirementCount={RequirementCount}, testCount={TestCount}, needsClarificationCount={NeedsClarificationCount}, durationMs={DurationMs}, rulesEvaluatedCount={RulesEvaluatedCount}",
            candidates.Count, reqCount, testCount, ncCount, durationMs, summary.TotalRulesEvaluated);

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

    private List<TextBlock> FilterBlocksWithEngine(IReadOnlyList<TextBlock> blocks, RuleExecutionSummary summary)
    {
        var result = new List<TextBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            var evalResult = _ruleEngine.Evaluate(block, string.Empty);
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
        ClassificationSignal Signal,
        ScenarioKind Kind);
    private List<ClassifiedItem> ClassifyContent(List<ContentItem> items, RuleExecutionSummary summary)
    {
        var result = new List<ClassifiedItem>(items.Count);
        foreach (var item in items)
        {
            var block = new TextBlock(item.PlainText, item.SourceBlockType, 0, item.ContextHeading);
            var evalResult = _ruleEngine.Evaluate(block, item.PlainText);
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
