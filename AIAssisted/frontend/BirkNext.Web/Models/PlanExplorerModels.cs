namespace BirkNext.Web.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum PlanSectionType
{
    TechnicalContext,
    Architecture,
    ProjectStructure,
    Risks,
    Complexity,
    Dependencies,
    Milestones,
    ConstitutionCheck,
    ImplementationPhases,
    Testing,
    Constraints,
    Other,
}

public enum RiskSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

public enum ComplexityLevel
{
    Low,
    Medium,
    High,
    VeryHigh,
}

public enum ConstitutionCheckStatus
{
    Compliant,
    NeedsReview,
    NonCompliant,
    NotApplicable,
}

public enum PlanGateStatus
{
    Pass,
    Warning,
    Fail,
    NotApplicable,
}

public enum ConstraintType
{
    Constraint,
    PerformanceGoal,
    ScaleScope,
    ComplexityJustification,
    NoViolation,
}

public enum PlanHealthLevel
{
    Good,
    Info,
    Warning,
    Error,
}

// ── Core document ──────────────────────────────────────────────────────────────

public sealed class PlanDocument
{
    public string Title { get; init; } = string.Empty;
    public string? FeatureName { get; init; }
    public string? Status { get; init; }
    public string? CreatedDate { get; init; }
    public string? LastUpdated { get; init; }
    public string? Author { get; init; }

    // Extended metadata
    public string? Branch { get; init; }
    public string? Date { get; init; }
    public string? SpecLink { get; init; }
    public string? InputSource { get; init; }

    // Summary (extracted from early narrative or dedicated Summary section)
    public string? Summary { get; init; }

    // Free-form sections (Technical Context, Project Structure, etc.)
    public List<PlanSection> Sections { get; init; } = [];

    // Structured extractions
    public List<PlanRisk> Risks { get; init; } = [];
    public List<PlanConstraint> Constraints { get; init; } = [];
    public List<PlanArchitectureDecision> ArchitectureDecisions { get; init; } = [];
    public List<PlanComplexityItem> ComplexityItems { get; init; } = [];
    public List<PlanDependency> Dependencies { get; init; } = [];
    public List<PlanMilestone> Milestones { get; init; } = [];
    public List<PlanConstitutionCheckItem> ConstitutionCheckItems { get; init; } = [];
    public List<PlanGate> Gates { get; init; } = [];
    public List<PlanImplementationPhase> Phases { get; init; } = [];
    public PlanTestingInfo? TestingInfo { get; init; }

    public PlanHealth Health { get; init; } = new();
}

// ── Free-form section ─────────────────────────────────────────────────────────

public sealed class PlanSection
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; init; } = string.Empty;
    public PlanSectionType SectionType { get; init; }
    public string RawContent { get; init; } = string.Empty;
    public List<PlanSectionBlock> Blocks { get; init; } = [];
}

public sealed class PlanSectionBlock
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string? SubHeading { get; init; }
    public string? Paragraph { get; init; }
    public List<string> BulletPoints { get; init; } = [];
    public string? CodeBlock { get; init; }
    public bool IsCodeBlock => !string.IsNullOrEmpty(CodeBlock);
    public bool HasContent => !string.IsNullOrEmpty(Paragraph)
                           || BulletPoints.Count > 0
                           || IsCodeBlock;
}

// ── Risk ──────────────────────────────────────────────────────────────────────

public sealed class PlanRisk
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public RiskSeverity Severity { get; init; } = RiskSeverity.Medium;
    public string? Mitigation { get; init; }
    public string? Area { get; init; }
    public string RawText { get; init; } = string.Empty;
}

// ── Constraint / Performance Goal ─────────────────────────────────────────────

public sealed class PlanConstraint
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ConstraintType ConstraintType { get; init; } = ConstraintType.Constraint;
    public string RawText { get; init; } = string.Empty;
}

// ── Architecture Decision (ADR) ───────────────────────────────────────────────

public sealed class PlanArchitectureDecision
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string? Rationale { get; init; }
    public List<string> Consequences { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}

// ── Complexity ────────────────────────────────────────────────────────────────

public sealed class PlanComplexityItem
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Area { get; init; } = string.Empty;
    public ComplexityLevel Level { get; init; } = ComplexityLevel.Medium;
    public string? Notes { get; init; }
    public List<string> Factors { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}

// ── Dependency ────────────────────────────────────────────────────────────────

public sealed class PlanDependency
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool IsExternal { get; init; }
}

// ── Milestone ─────────────────────────────────────────────────────────────────

public sealed class PlanMilestone
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; init; } = string.Empty;
    public string? TargetDate { get; init; }
    public string? Description { get; init; }
    public List<string> Deliverables { get; init; } = [];
}

// ── Constitution Check (heading-based) ───────────────────────────────────────

public sealed class PlanConstitutionCheckItem
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public ConstitutionCheckStatus Status { get; init; } = ConstitutionCheckStatus.NeedsReview;
    public string? Notes { get; init; }
    public string RawText { get; init; } = string.Empty;
}

// ── Constitution Gate (table-based) ──────────────────────────────────────────

public sealed class PlanGate
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Gate { get; init; } = string.Empty;      // full gate label
    public string RuleId { get; init; } = string.Empty;    // PP-01, GL-24, etc.
    public string Principle { get; init; } = string.Empty; // raw principle column text
    public PlanGateStatus Status { get; init; } = PlanGateStatus.NotApplicable;
    public string? Evidence { get; init; }
    public string? Notes { get; init; }
    public bool IsJustifiedDeviation { get; init; } = false; // true if status is Warning from "JUSTIFIED DEVIATION"
}

// ── Implementation Phase ──────────────────────────────────────────────────────

public sealed class PlanImplementationPhase
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public int PhaseNumber { get; init; }           // 0 = pre, 99 = post
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> Tasks { get; init; } = [];
    public List<string> Checks { get; init; } = [];
    public List<PlanSectionBlock> Blocks { get; init; } = [];
}

// ── Testing ───────────────────────────────────────────────────────────────────

public sealed class PlanTestingInfo
{
    public List<string> Frameworks { get; init; } = [];
    public List<string> TestFolders { get; init; } = [];
    public List<string> TestClasses { get; init; } = [];
    public List<string> GateRefs { get; init; } = [];       // rule IDs referenced in testing
    public List<PlanSectionBlock> Blocks { get; init; } = [];
}

// ── Health ────────────────────────────────────────────────────────────────────

public sealed class PlanHealthIndicator
{
    public string Icon { get; init; } = "✓";
    public string Message { get; init; } = string.Empty;
    public PlanHealthLevel Level { get; init; } = PlanHealthLevel.Good;
}

public sealed class PlanHealth
{
    // Existing risk counts
    public int TotalRisks { get; init; }
    public int CriticalRisks { get; init; }
    public int HighRisks { get; init; }
    public int MediumRisks { get; init; }
    public int LowRisks { get; init; }

    // Architecture
    public int TotalArchitectureDecisions { get; init; }

    // Complexity
    public int TotalComplexityItems { get; init; }
    public int HighComplexityItems { get; init; }

    // Dependencies
    public int TotalDependencies { get; init; }
    public int ExternalDependencies { get; init; }

    // Milestones
    public int TotalMilestones { get; init; }

    // Constitution check (heading-based)
    public int TotalConstitutionCheckItems { get; init; }
    public int CompliantItems { get; init; }
    public int NonCompliantItems { get; init; }
    public int NeedsReviewItems { get; init; }

    // Constitution gates (table-based) — new
    public int TotalConstitutionGates { get; init; }
    public int PassedGates { get; init; }
    public int WarningGates { get; init; }
    public int FailedGates { get; init; }

    // Phases — new
    public int TotalPhases { get; init; }

    // Constraints — new
    public int TotalConstraints { get; init; }
    public int TotalPerformanceGoals { get; init; }

    // Testing — new
    public int TotalTestReferences { get; init; }

    // Completeness flags — new
    public bool HasMetadata { get; init; }
    public bool HasSummary { get; init; }
    public bool HasTechnicalContext { get; init; }
    public bool HasConstitutionCheck { get; init; }
    public bool HasProjectStructure { get; init; }
    public bool HasImplementationPhases { get; init; }
    public bool HasTestingInfo { get; init; }
    public bool HasArchitecture { get; init; }

    // Special cases
    public bool IsFrontendOnly { get; init; }
    public bool IsStateless { get; init; }
    public bool HasNoStorage { get; init; }

    public string HealthSummary { get; init; } = string.Empty;
    public List<PlanHealthIndicator> Indicators { get; init; } = [];
}
