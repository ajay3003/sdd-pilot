using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public abstract record FilterCondition;

public sealed record BlockTypeMatchCondition(BlockType TargetBlockType) : FilterCondition;

// Defined for extensibility; not used in ExtractionRuleSet.Default().
// FilterConditions operate on raw block structure before markdown stripping.
// Stage 5 minimum-length check operates on stripped text and is not replaced by this.
public sealed record ContentLengthBelowCondition(int ThresholdChars) : FilterCondition;

public abstract record ClassificationCondition;

public sealed record PatternMatchCondition : ClassificationCondition
{
    public Regex Pattern { get; }

    public PatternMatchCondition(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Pattern = pattern;
    }
}

// Always returns true; no state. Exactly one instance allowed per rule set (the Default rule).
public sealed record UnconditionalCondition : ClassificationCondition;
