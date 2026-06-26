using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record ClassificationOutcome(ScenarioKind Kind, ClassificationSignal Signal);

public sealed class FilterRule
{
    public string Name { get; }
    public int Priority { get; }
    public FilterCondition Condition { get; }

    public FilterRule(string name, int priority, FilterCondition condition)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be null or empty.", nameof(name));
        if (priority <= 0)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority must be > 0 for filter rules.");
        ArgumentNullException.ThrowIfNull(condition);
        Name = name;
        Priority = priority;
        Condition = condition;
    }
}

public sealed class ClassificationRule
{
    public string Name { get; }
    public int Priority { get; }
    public ClassificationCondition Condition { get; }
    public ClassificationOutcome Outcome { get; }
    public BlockType[]? ApplicableBlockTypes { get; }

    public ClassificationRule(
        string name,
        int priority,
        ClassificationCondition condition,
        ClassificationOutcome outcome,
        BlockType[]? applicableBlockTypes = null)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be null or empty.", nameof(name));
        if (priority < 0)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority must be >= 0.");
        // Priority 0 is reserved exclusively for the unconditional Default fallback rule.
        if (priority == 0 && condition is not UnconditionalCondition)
            throw new ArgumentException(
                "Priority 0 is reserved exclusively for the unconditional Default rule.",
                nameof(priority));
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(outcome);
        Name = name;
        Priority = priority;
        Condition = condition;
        Outcome = outcome;
        ApplicableBlockTypes = applicableBlockTypes;
    }
}
