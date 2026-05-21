using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

internal sealed class ExtractionRuleEngine : IExtractionRuleEngine
{
    private readonly ExtractionRuleSet _ruleSet;
    private readonly IExtractionConfiguration _config;

    public IReadOnlyList<string> RuleNames { get; }
    public IReadOnlyList<string> IgnorePrefixes => _ruleSet.IgnorePrefixes;

    public ExtractionRuleEngine(ExtractionRuleSet ruleSet, IExtractionConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(config);
        ValidateRuleSet(ruleSet);
        _ruleSet = ruleSet;
        _config = config;
        RuleNames = [.. ruleSet.FilterRules.Select(r => r.Name),
                     .. ruleSet.ClassificationRules.Select(r => r.Name)];
    }

    public RuleEvaluationResult Evaluate(TextBlock block, string strippedText)
    {
        int evaluatedCount = 0;

        // Filter pass: rules are pre-sorted descending by priority.
        // Short-circuit on the first matching filter rule — a filtered block has no classification.
        foreach (var rule in _ruleSet.FilterRules)
        {
            evaluatedCount++;
            if (EvaluateFilterCondition(rule.Condition, block))
                return RuleEvaluationResult.Filtered(evaluatedCount);
        }

        // Classification pass: evaluate every applicable rule and track the highest-priority winner.
        // Rules are pre-sorted descending by priority; stable sort guarantees that ties preserve
        // first-registered order, so the simple "keep first, update only on strictly higher priority"
        // strategy correctly implements "highest-priority wins; first-registered breaks ties".
        ClassificationRule? winner = null;
        foreach (var rule in _ruleSet.ClassificationRules)
        {
            if (!IsApplicable(rule.ApplicableBlockTypes, block.BlockType))
                continue;

            evaluatedCount++;
            if (EvaluateClassificationCondition(rule.Condition, strippedText)
                && (winner is null || rule.Priority > winner.Priority))
            {
                winner = rule;
            }
        }

        // winner is never null: UnconditionalCondition (Classify:Default) always matches
        // and is guaranteed present by startup validation.
        return RuleEvaluationResult.Classified(
            winner!.Outcome.Kind, winner.Outcome.Signal, winner.Name, evaluatedCount);
    }

    private static bool IsApplicable(BlockType[]? applicableBlockTypes, BlockType blockType)
        => applicableBlockTypes is null || Array.IndexOf(applicableBlockTypes, blockType) >= 0;

    private static bool EvaluateFilterCondition(FilterCondition condition, TextBlock block) =>
        condition switch
        {
            BlockTypeMatchCondition btm => block.BlockType == btm.TargetBlockType,
            ContentLengthBelowCondition clb => block.RawText.Length < clb.ThresholdChars,
            _ => throw new InvalidOperationException(
                $"Unknown filter condition type: {condition.GetType().Name}")
        };

    // For PatternMatchCondition: bypass pattern matching entirely when stripped text exceeds the
    // per-line length cap. This mirrors Stage 6's "text.Length > MaxLineLengthForPatternMatching
    // → return Default" guard, ensuring over-limit lines always resolve to NeedsClarification/Default.
    // It also prevents ReDoS on adversarially crafted long inputs.
    // PrefixMatchCondition uses StartsWith — O(prefix_length), no ReDoS surface; no length cap needed.
    private bool EvaluateClassificationCondition(ClassificationCondition condition, string strippedText) =>
        condition switch
        {
            PatternMatchCondition pmc =>
                strippedText.Length <= _config.MaxLineLengthForPatternMatching
                && pmc.Pattern.IsMatch(strippedText),
            PrefixMatchCondition pmc =>
                strippedText.StartsWith(pmc.Prefix, StringComparison.OrdinalIgnoreCase),
            UnconditionalCondition => true,
            _ => throw new InvalidOperationException(
                $"Unknown classification condition type: {condition.GetType().Name}")
        };

    private static void ValidateRuleSet(ExtractionRuleSet ruleSet)
    {
        // (1) At least one classification rule must be present.
        if (ruleSet.ClassificationRules.Count == 0)
            throw new InvalidOperationException(
                "ExtractionRuleSet must contain at least one ClassificationRule.");

        // (2) Exactly one unconditional Default rule at priority 0 must exist.
        //     This guarantees every candidate-eligible block will always receive a classification.
        int defaultCount = ruleSet.ClassificationRules
            .Count(r => r.Condition is UnconditionalCondition && r.Priority == 0);
        if (defaultCount != 1)
            throw new InvalidOperationException(
                $"ExtractionRuleSet must contain exactly one unconditional Default rule at "
                + $"priority 0 (found {defaultCount}).");

        // (3) All rule names must be globally unique across both filter and classification lists.
        var allNames = ruleSet.FilterRules.Select(r => r.Name)
            .Concat(ruleSet.ClassificationRules.Select(r => r.Name))
            .ToList();
        var duplicates = allNames
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Rule names must be unique across all rules. Duplicates: {string.Join(", ", duplicates)}");

        // (4) Defence-in-depth: PatternMatchCondition patterns must not be null.
        //     PatternMatchCondition's constructor already enforces this; this is an extra guard.
        var nullPatternRules = ruleSet.ClassificationRules
            .Where(r => r.Condition is PatternMatchCondition { Pattern: null })
            .Select(r => r.Name)
            .ToList();
        if (nullPatternRules.Count > 0)
            throw new InvalidOperationException(
                $"PatternMatchCondition patterns must not be null. Affected rules: "
                + string.Join(", ", nullPatternRules));

        // (5) Defence-in-depth: no ClassificationRule at priority 0 other than the unconditional Default.
        //     ClassificationRule's constructor already enforces this; repeated here as a defence-in-depth check.
        var invalidZeroPriority = ruleSet.ClassificationRules
            .Where(r => r.Priority == 0 && r.Condition is not UnconditionalCondition)
            .Select(r => r.Name)
            .ToList();
        if (invalidZeroPriority.Count > 0)
            throw new InvalidOperationException(
                $"Only the unconditional Default rule may have Priority == 0. "
                + $"Violating rules: {string.Join(", ", invalidZeroPriority)}");
    }
}
