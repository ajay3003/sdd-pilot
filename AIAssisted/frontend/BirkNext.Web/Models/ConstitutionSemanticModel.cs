namespace BirkNext.Web.Models;

/// <summary>
/// Canonical semantic model for Constitution documents.
/// Single source of truth for all Constitution review pages.
/// </summary>
public sealed class ConstitutionSemanticModel
{
    // ── Metadata ────────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Organization { get; init; }
    public string? CreatedDate { get; init; }
    public string? LastUpdated { get; init; }

    // ── Core Elements ───────────────────────────────────────────────────────
    public List<SemanticConstitutionPrinciple> Principles { get; init; } = [];
    public List<SemanticConstitutionRule> Rules { get; init; } = [];
    public List<SemanticConstitutionGate> Gates { get; init; } = [];
    public List<SemanticConstitutionComplianceCheckItem> ComplianceChecks { get; init; } = [];

    // ── Aggregates ──────────────────────────────────────────────────────────
    public int TotalPrinciples => Principles.Count;
    public int TotalRules => Rules.Count;
    public int TotalGates => Gates.Count;
    public int TotalComplianceChecks => ComplianceChecks.Count;

    // ── Coverage Metrics ────────────────────────────────────────────────────
    public int CompliantChecks => ComplianceChecks.Count(c => c.Status == "Compliant");
    public int NonCompliantChecks => ComplianceChecks.Count(c => c.Status == "NonCompliant");
    public int NeedsReviewChecks => ComplianceChecks.Count(c => c.Status == "NeedsReview");
    public int PassedGates => Gates.Count(g => g.Status == "Pass");
    public int FailedGates => Gates.Count(g => g.Status == "Fail");
    public int WarningGates => Gates.Count(g => g.Status == "Warning");

    public int CompliancePercentage => TotalComplianceChecks == 0 ? 0 : (CompliantChecks * 100) / TotalComplianceChecks;

    // ── Relationships ───────────────────────────────────────────────────────
    public Dictionary<string, List<string>> RuleToRequirements { get; init; } = [];
    public Dictionary<string, List<string>> GateToRequirements { get; init; } = [];
}

/// <summary>
/// Constitutional principle or guideline (semantic model).
/// </summary>
public sealed class SemanticConstitutionPrinciple
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> RelatedRuleIds { get; init; } = [];
}

/// <summary>
/// Constitutional rule with compliance requirements (semantic model).
/// </summary>
public sealed class SemanticConstitutionRule
{
    public string Id { get; init; } = string.Empty;  // e.g., PP-01, PS-01, GL-01
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = string.Empty;  // e.g., "Principles", "Standards"
    public List<string> RelatedPrincipleIds { get; init; } = [];
    public List<string> ApplicableRequirementIds { get; init; } = [];
}

/// <summary>
/// Constitutional gate (checkpoint for review/approval) (semantic model).
/// </summary>
public sealed class SemanticConstitutionGate
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "NotApplicable";  // Pass, Warning, Fail, NotApplicable
    public string? Evidence { get; init; }
    public string? Notes { get; init; }
    public List<string> LinkedRuleIds { get; init; } = [];
}

/// <summary>
/// Constitution compliance check item (semantic model).
/// </summary>
public sealed class SemanticConstitutionComplianceCheckItem
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleTitle { get; init; } = string.Empty;
    public string Status { get; init; } = "NeedsReview";  // Compliant, NonCompliant, NeedsReview
    public string? Notes { get; init; }
    public string? Evidence { get; init; }
    public string? ImplementationPath { get; init; }
}
