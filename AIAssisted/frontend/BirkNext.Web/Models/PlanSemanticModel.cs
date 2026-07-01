namespace BirkNext.Web.Models;

/// <summary>
/// Canonical semantic model for Plan documents.
/// Single source of truth for all Plan review pages.
/// </summary>
public sealed class PlanSemanticModel
{
    // ── Metadata ────────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string? FeatureName { get; init; }
    public string? Branch { get; init; }
    public string? Status { get; init; }
    public string? CreatedDate { get; init; }
    public string? LastUpdated { get; init; }
    public string? Author { get; init; }

    // ── Core Elements ───────────────────────────────────────────────────────
    public string? Summary { get; init; }
    public List<SemanticPlanArchitectureDecision> ArchitectureDecisions { get; init; } = [];
    public List<SemanticPlanRiskItem> Risks { get; init; } = [];
    public List<SemanticPlanConstraint> Constraints { get; init; } = [];
    public List<SemanticPlanComplexityFactor> ComplexityFactors { get; init; } = [];
    public List<SemanticPlanDependency> Dependencies { get; init; } = [];
    public List<SemanticPlanPhase> Phases { get; init; } = [];
    public List<SemanticPlanMilestone> Milestones { get; init; } = [];
    public List<SemanticPlanTestingStrategy> TestingStrategies { get; init; } = [];
    public List<SemanticPlanConstitutionGate> ConstitutionGates { get; init; } = [];

    // ── Aggregates ──────────────────────────────────────────────────────────
    public int TotalArchitectureDecisions => ArchitectureDecisions.Count;
    public int TotalRisks => Risks.Count;
    public int TotalConstraints => Constraints.Count;
    public int TotalComplexityFactors => ComplexityFactors.Count;
    public int TotalDependencies => Dependencies.Count;
    public int TotalPhases => Phases.Count;
    public int TotalMilestones => Milestones.Count;
    public int TotalTestingStrategies => TestingStrategies.Count;

    // ── Coverage Metrics ────────────────────────────────────────────────────
    public int CriticalRisks => Risks.Count(r => r.Severity == "Critical");
    public int HighRisks => Risks.Count(r => r.Severity == "High");
    public int HighComplexityAreas => ComplexityFactors.Count(c => c.Level is "High" or "VeryHigh");
    public int ExternalDependencies => Dependencies.Count(d => d.IsExternal);
    public int PassedGates => ConstitutionGates.Count(g => g.Status == "Pass");
    public int FailedGates => ConstitutionGates.Count(g => g.Status == "Fail");
    public int WarningGates => ConstitutionGates.Count(g => g.Status == "Warning");

    // ── Health Indicators ───────────────────────────────────────────────────
    public bool HasTechnicalContext { get; init; }
    public bool HasProjectStructure { get; init; }
    public bool HasTestingStrategy { get; init; }
    public bool HasArchitectureDecisions { get; init; }
    public bool HasImplementationPhases { get; init; }
    public bool HasRiskAssessment { get; init; }

    // ── Relationships ───────────────────────────────────────────────────────
    public Dictionary<string, List<string>> DecisionToRequirements { get; init; } = [];
    public Dictionary<string, List<string>> RiskToRequirements { get; init; } = [];
    public Dictionary<string, List<string>> PhaseToTasks { get; init; } = [];
}

/// <summary>
/// Architecture Decision Record (ADR) (semantic model).
/// </summary>
public sealed class SemanticPlanArchitectureDecision
{
    public string Id { get; init; } = string.Empty;  // e.g., ADR-001
    public string Title { get; init; } = string.Empty;
    public string? Context { get; init; }
    public string? Decision { get; init; }
    public string? Rationale { get; init; }
    public List<string> Consequences { get; init; } = [];
    public List<string> RelatedRequirementIds { get; init; } = [];
}

/// <summary>
/// Risk identified in the plan (semantic model).
/// </summary>
public sealed class SemanticPlanRiskItem
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Severity { get; init; } = "Medium";  // Critical, High, Medium, Low
    public string? Mitigation { get; init; }
    public string? Area { get; init; }
}

/// <summary>
/// Constraint or performance goal (semantic model).
/// </summary>
public sealed class SemanticPlanConstraint
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Type { get; init; } = "Constraint";  // Constraint, PerformanceGoal, ScaleScope, NoViolation
}

/// <summary>
/// Complexity factor affecting implementation (semantic model).
/// </summary>
public sealed class SemanticPlanComplexityFactor
{
    public string Area { get; init; } = string.Empty;
    public string Level { get; init; } = "Medium";  // Low, Medium, High, VeryHigh
    public string? Notes { get; init; }
    public List<string> Factors { get; init; } = [];
}

/// <summary>
/// External or internal dependency (semantic model).
/// </summary>
public sealed class SemanticPlanDependency
{
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool IsExternal { get; init; }
}

/// <summary>
/// Implementation phase (semantic model).
/// </summary>
public sealed class SemanticPlanPhase
{
    public int PhaseNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> TaskIds { get; init; } = [];
    public List<string> Checks { get; init; } = [];
}

/// <summary>
/// Project milestone (semantic model).
/// </summary>
public sealed class SemanticPlanMilestone
{
    public string Title { get; init; } = string.Empty;
    public string? TargetDate { get; init; }
    public string? Description { get; init; }
    public List<string> Deliverables { get; init; } = [];
}

/// <summary>
/// Testing strategy or approach (semantic model).
/// </summary>
public sealed class SemanticPlanTestingStrategy
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> Frameworks { get; init; } = [];
    public List<string> TestFolders { get; init; } = [];
    public List<string> TestClasses { get; init; } = [];
    public List<string> LinkedRuleIds { get; init; } = [];
}

/// <summary>
/// Constitution gate status from plan (semantic model).
/// </summary>
public sealed class SemanticPlanConstitutionGate
{
    public string Gate { get; init; } = string.Empty;
    public string RuleId { get; init; } = string.Empty;
    public string Principle { get; init; } = string.Empty;
    public string Status { get; init; } = "NotApplicable";  // Pass, Warning, Fail, NotApplicable
    public string? Evidence { get; init; }
    public string? Notes { get; init; }
}
