namespace BirkNext.Api.Models;

/// <summary>A code link paired with its resolved Scenario.</summary>
public sealed class CodeLinkWithScenario
{
    public CodeLink Link { get; init; } = null!;
    public Scenario Scenario { get; init; } = null!;
}

/// <summary>
/// Full code impact for a single file: which requirements and tests are linked to it.
/// Computed on demand — not persisted.
/// </summary>
public sealed class CodeImpact
{
    public CodeFile File { get; init; } = null!;
    public IReadOnlyList<CodeLinkWithScenario> LinkedRequirements { get; init; } = [];
    public IReadOnlyList<CodeLinkWithScenario> LinkedTests { get; init; } = [];
}

/// <summary>Project-wide code traceability summary.</summary>
public sealed class CodeSummary
{
    public int TotalFiles { get; init; }
    public int LinkedRequirements { get; init; }
    public int LinkedTests { get; init; }
    /// <summary>Files with no code links at all.</summary>
    public int UnlinkedFiles { get; init; }
}
