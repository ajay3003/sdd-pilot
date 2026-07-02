namespace BirkNext.Web.Models;

/// <summary>
/// Canonical semantic model for specifications.
/// Single source of truth for all specification review pages.
/// Built once by SpecExplorerService; consumed by all pages without reparsing.
/// </summary>
public sealed class SpecificationSemanticModel
{
    // ── Metadata ────────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string? Feature { get; init; }
    public string? Branch { get; init; }
    public string? Status { get; init; }
    public string? Created { get; init; }
    public List<string> Inputs { get; init; } = [];

    // ── Core Semantic Elements ───────────────────────────────────────────────
    public List<SemanticUserStory> UserStories { get; init; } = [];
    public List<SemanticRequirement> Requirements { get; init; } = [];
    public List<SemanticSuccessCriterion> SuccessCriteria { get; init; } = [];
    public List<SemanticEntity> KeyEntities { get; init; } = [];
    public List<SemanticClarification> Clarifications { get; init; } = [];
    public List<SemanticEdgeCase> EdgeCases { get; init; } = [];
    public List<SemanticAssumption> Assumptions { get; init; } = [];
    public List<SemanticSecurity> SecurityConsiderations { get; init; } = [];
    public List<SemanticAcceptanceScenario> AcceptanceScenarios { get; init; } = [];

    // ── Aggregates ───────────────────────────────────────────────────────────
    public int TotalUserStories => UserStories.Count;
    public int TotalRequirements => Requirements.Count;
    public int TotalSuccessCriteria => SuccessCriteria.Count;
    public int TotalClarifications => Clarifications.Count;
    public int TotalEdgeCases => EdgeCases.Count;
    public int TotalAssumptions => Assumptions.Count;
    public int TotalSecurityConsiderations => SecurityConsiderations.Count;
    public int TotalAcceptanceScenarios => AcceptanceScenarios.Count;
    public int TotalEntities => KeyEntities.Count;

    // ── Coverage Metrics (derived from semantic links) ──────────────────────
    public int RequirementsWithSuccessCriteria => Requirements.Count(r => r.LinkedSuccessCriteria.Count > 0);
    public int RequirementsWithTests => Requirements.Count(r => r.LinkedAcceptanceScenarios.Count > 0 || r.LinkedUserStories.Any(us => us.LinkedAcceptanceScenarios.Count > 0));
    public int UserStoriesWithTests => UserStories.Count(us => us.LinkedAcceptanceScenarios.Count > 0 || us.IndependentTest != null);
    public int UserStoriesWithRequirements => UserStories.Count(us => us.LinkedRequirements.Count > 0);
    public int ClarificationsLinked => Clarifications.Count(c => c.AffectedElements.Count > 0);
}

/// <summary>
/// User Story with full semantic details and relationships.
/// </summary>
public sealed class SemanticUserStory
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Priority { get; init; }
    public string? Description { get; init; }
    public string? Why { get; init; }
    public string? IndependentTest { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<SemanticRequirement> LinkedRequirements { get; init; } = [];
    public List<SemanticAcceptanceScenario> LinkedAcceptanceScenarios { get; init; } = [];
    public List<SemanticSuccessCriterion> LinkedSuccessCriteria { get; init; } = [];
}

/// <summary>
/// Functional Requirement with semantic relationships.
/// </summary>
public sealed class SemanticRequirement
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? Category { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<SemanticSuccessCriterion> LinkedSuccessCriteria { get; init; } = [];
    public List<SemanticUserStory> LinkedUserStories { get; init; } = [];
    public List<SemanticAcceptanceScenario> LinkedAcceptanceScenarios { get; init; } = [];
    public List<SemanticEdgeCase> LinkedEdgeCases { get; init; } = [];
    public List<SemanticSecurity> LinkedSecurityConsiderations { get; init; } = [];

    // ── Cross-artifact links ────────────────────────────────────────────────
    public List<string> LinkedConstitutionRules { get; init; } = [];
    public List<string> LinkedTasks { get; init; } = [];
    public List<string> LinkedArchitectureDecisions { get; init; } = [];
    public List<string> LinkedDataEntities { get; init; } = [];
}

/// <summary>
/// Success Criterion linked to requirements.
/// </summary>
public sealed class SemanticSuccessCriterion
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<SemanticRequirement> LinkedRequirements { get; init; } = [];
    public List<SemanticUserStory> LinkedUserStories { get; init; } = [];

    // ── Cross-artifact links ────────────────────────────────────────────────
    public List<string> LinkedTasks { get; init; } = [];
}

/// <summary>
/// Acceptance Scenario (BDD-style test case).
/// </summary>
public sealed class SemanticAcceptanceScenario
{
    public string? Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Given { get; init; }
    public string? When { get; init; }
    public string? Then { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<SemanticUserStory> LinkedUserStories { get; init; } = [];
    public List<SemanticRequirement> LinkedRequirements { get; init; } = [];
}

/// <summary>
/// Key Entity / Domain Model element.
/// </summary>
public sealed class SemanticEntity
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> Attributes { get; init; } = [];
}

/// <summary>
/// Clarification / Q&A item.
/// </summary>
public sealed class SemanticClarification
{
    public string? Id { get; init; }
    public string Question { get; init; } = string.Empty;
    public string? Answer { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    /// <summary>
    /// Elements affected by this clarification (US ID, FR ID, SC ID, etc.)
    /// </summary>
    public List<string> AffectedElements { get; init; } = [];
}

/// <summary>
/// Edge Case scenario.
/// </summary>
public sealed class SemanticEdgeCase
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<string> RelatedRequirementIds { get; init; } = [];
}

/// <summary>
/// Assumption documented in the specification.
/// </summary>
public sealed class SemanticAssumption
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
}

/// <summary>
/// Security consideration / requirement.
/// </summary>
public sealed class SemanticSecurity
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<string> AffectedRequirementIds { get; init; } = [];
}
