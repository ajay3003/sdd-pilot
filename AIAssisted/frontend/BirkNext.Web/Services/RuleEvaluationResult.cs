using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class RuleEvaluationResult
{
    public bool IsFiltered { get; }
    public ScenarioKind? Classification { get; }
    public ClassificationSignal? Signal { get; }
    public string? WinningRuleName { get; }
    public int EvaluatedRuleCount { get; }

    private RuleEvaluationResult(
        bool isFiltered,
        ScenarioKind? classification,
        ClassificationSignal? signal,
        string? winningRuleName,
        int evaluatedRuleCount)
    {
        IsFiltered = isFiltered;
        Classification = classification;
        Signal = signal;
        WinningRuleName = winningRuleName;
        EvaluatedRuleCount = evaluatedRuleCount;
    }

    public static RuleEvaluationResult Filtered(int evaluatedRuleCount)
    {
        if (evaluatedRuleCount < 1)
            throw new ArgumentOutOfRangeException(nameof(evaluatedRuleCount), "EvaluatedRuleCount must be >= 1.");
        return new RuleEvaluationResult(true, null, null, null, evaluatedRuleCount);
    }

    public static RuleEvaluationResult Classified(
        ScenarioKind kind,
        ClassificationSignal signal,
        string winningRuleName,
        int evaluatedRuleCount)
    {
        if (evaluatedRuleCount < 1)
            throw new ArgumentOutOfRangeException(nameof(evaluatedRuleCount), "EvaluatedRuleCount must be >= 1.");
        return new RuleEvaluationResult(false, kind, signal, winningRuleName, evaluatedRuleCount);
    }
}

public sealed class RuleExecutionSummary
{
    public int TotalRulesEvaluated { get; set; }
    public int FilteredBlockCount { get; set; }
    public int DefaultFallbackCount { get; set; }
}
