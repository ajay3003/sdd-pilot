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
    /// <summary>«Dependencies &amp; Open Items» rows as the source records them (status in the document, not the live state).</summary>
    public List<SemanticDependency> Dependencies { get; init; } = [];
    /// <summary>«Out of Scope» items: boundaries of this feature, never mandatory tests for it.</summary>
    public List<SemanticScopeItem> OutOfScope { get; init; } = [];

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
    /// <summary>The story number the document gives ("US6" from "User Story 6", "US-A1" from an id), else null (unnumbered).</summary>
    public string? AuthoredId { get; init; }
    /// <summary>Position among the document's story headings (1-based); 0 for an in-text reference (not a heading).</summary>
    public int Ordinal { get; init; }
    /// <summary>A real story heading ("User Story 6 …", "Brukerhistorie 1 …"). False for an in-text reference such as "[US-A3]" and for a
    /// "Scenario N:" heading the parser groups with stories: neither is ever a scenario's parent story.</summary>
    public bool IsStoryHeading { get; init; }
    /// <summary>Stable identity within the document: the authored number, else the position ("story-2"); never derived from the title.</summary>
    public string StableKey => AuthoredId ?? $"story-{Ordinal}";
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }

    // ── Semantic links ──────────────────────────────────────────────────────
    public List<SemanticRequirement> LinkedRequirements { get; init; } = [];
    /// <summary>Text-reference links and the acceptance scenarios written under this story heading (document structure).</summary>
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
    /// <summary>The functional area the source writes the requirement under (its FR section), else a heading-based category.</summary>
    public string? Category { get; set; }

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
    /// <summary>The story heading this scenario is written under (its <see cref="SemanticUserStory.StableKey"/>); null when the document
    /// structure names none (a scenario directly under the feature) — never guessed from nearby text.</summary>
    public string? ParentStoryKey { get; set; }
    /// <summary>The scenario's number within its story (1-based, document order); null without a parent story.</summary>
    public int? NumberInStory { get; set; }
    public int? LineStart { get; set; }

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
/// Clarification / Q&amp;A item.
/// </summary>
public sealed class SemanticClarification
{
    public string? Id { get; init; }
    public string Question { get; init; } = string.Empty;
    public string? Answer { get; init; }

    /// <summary>The dated session the clarification belongs to ("2026-10-06"), when the source groups them so.</summary>
    public string? Session { get; set; }
    /// <summary>
    /// What the source itself says about the answer (2026-11-25): <see cref="Current"/>, <see cref="Superseded"/> ("*Superseded 2026-10-06: …*": the
    /// answer is historical, the note states what replaced it) or <see cref="Amended"/> ("*Amended …*": the note refines the answer). Read from the
    /// source's own markers only; a later position in the document never decides precedence.
    /// </summary>
    public string Status { get; set; } = Current;
    /// <summary>The source's supersession / amendment note, verbatim (without the asterisks).</summary>
    public string? StatusNote { get; set; }
    public const string Current = "Current", Superseded = "Superseded", Amended = "Amended";

    // ── Semantic links ──────────────────────────────────────────────────────
    /// <summary>
    /// Elements affected by this clarification (US ID, FR ID, SC ID, etc.)
    /// </summary>
    public List<string> AffectedElements { get; init; } = [];
}

/// <summary>A row of «Dependencies &amp; Open Items». <see cref="Closed"/> and <see cref="Withdrawn"/> are the document's own record (bold «Closed»,
/// struck-through row); the live implementation state is not known from the specification.</summary>
public sealed class SemanticDependency
{
    public string Id { get; init; } = string.Empty;
    public string Statement { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public string? Blocks { get; init; }
    public bool Closed { get; init; }
    public bool Withdrawn { get; init; }
    public int Line { get; init; }
    public List<string> RequirementIds { get; init; } = [];
}

/// <summary>An «Out of Scope» item with the references it cites.</summary>
public sealed class SemanticScopeItem
{
    public string Text { get; init; } = string.Empty;
    public int Line { get; init; }
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
