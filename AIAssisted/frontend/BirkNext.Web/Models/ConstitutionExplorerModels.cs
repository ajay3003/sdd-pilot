namespace BirkNext.Web.Models;

public enum ConstitutionType
{
    Generic,
    Module,
    Platform,
    Frontend,
    Service,
}

public enum ConstitutionSectionType
{
    CorePrinciples,
    PlatformStandards,
    ModuleConstraints,
    DevelopmentStandards,
    SecurityCompliance,
    Governance,
    Changelog,
    Other,
}

public enum GovernanceItemType
{
    AmendmentProcess,
    ComplianceRules,
    VersioningPolicy,
    Other,
}

public enum ConstitutionRuleType
{
    Principle,
    Standard,
    Guideline,
    Constraint,
    Governance,
}

public enum HealthIndicatorLevel
{
    Good,
    Warning,
    Error,
}

// ── Core document ─────────────────────────────────────────────────────────────

public sealed class ConstitutionDocument
{
    public string Title { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? RatifiedDate { get; init; }
    public string? LastAmendedDate { get; init; }
    public ConstitutionType Type { get; init; } = ConstitutionType.Generic;
    public string? Scope { get; init; }

    // Section-level lists (existing tabs)
    public List<ConstitutionPrinciple> Principles { get; init; } = [];
    public List<ConstitutionStandard> Standards { get; init; } = [];
    public List<ConstitutionConstraint> Constraints { get; init; } = [];
    public List<ConstitutionGovernanceItem> GovernanceItems { get; init; } = [];
    public List<ConstitutionVersion> Changelog { get; init; } = [];

    // Unified rule catalog — populated after parsing all sections
    public List<ConstitutionRule> RuleCatalog { get; init; } = [];

    public ConstitutionHealth Health { get; init; } = new();
}

// ── Unified rule model ────────────────────────────────────────────────────────

public sealed class ConstitutionRule
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string RuleId { get; init; } = string.Empty;     // PP-01, PS-07, GL-24, etc.
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ConstitutionRuleType RuleType { get; init; }

    // Additional IDs found in the rule's heading (e.g. "(PP-02, PP-04)" → primary PP-02, alias PP-04)
    public List<string> Aliases { get; init; } = [];

    // Forward references: IDs this rule explicitly references in its text
    public List<string> References { get; init; } = [];

    // Reverse references: IDs of rules that reference this rule (populated post-parse)
    public List<string> ReferencedBy { get; init; } = [];
}

// ── Section-specific models ───────────────────────────────────────────────────

public sealed class ConstitutionPrinciple
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Guidelines { get; init; } = [];
    public List<string> ReferencedStandards { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}

public sealed class ConstitutionStandard
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Rules { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}

public sealed class ConstitutionConstraint
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Rules { get; init; } = [];
    public bool IsPlatformWide { get; init; }
    public string RawText { get; init; } = string.Empty;
}

public sealed class ConstitutionGovernanceItem
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public GovernanceItemType Type { get; init; } = GovernanceItemType.Other;
    public List<string> Points { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}

public sealed class ConstitutionVersion
{
    public string Version { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public List<string> Changes { get; init; } = [];
}

// ── Health & indicators ───────────────────────────────────────────────────────

public sealed class ConstitutionHealthIndicator
{
    public string Icon { get; init; } = "✓";
    public string Message { get; init; } = string.Empty;
    public HealthIndicatorLevel Level { get; init; } = HealthIndicatorLevel.Good;
}

public sealed class ConstitutionHealth
{
    // Section counts (existing)
    public int TotalPrinciples { get; init; }
    public int TotalStandards { get; init; }
    public int TotalConstraints { get; init; }
    public int TotalGovernanceItems { get; init; }
    public int TotalVersions { get; init; }
    public int PlatformWideConstraints { get; init; }
    public int ModuleConstraints { get; init; }

    // Rule catalog metrics (new)
    public int TotalRules { get; init; }
    public int TotalReferences { get; init; }
    public int OrphanRules { get; init; }
    public int RulesWithoutReferences { get; init; }
    public int BrokenReferences { get; init; }

    public string HealthSummary { get; init; } = string.Empty;
    public List<ConstitutionHealthIndicator> Indicators { get; init; } = [];
}

// ── Map tree node ─────────────────────────────────────────────────────────────

public sealed class ConstitutionMapNode
{
    public string NodeId { get; } = Guid.NewGuid().ToString("N")[..10];
    public ConstitutionRule Rule { get; init; } = new();
    public List<ConstitutionMapNode> Children { get; init; } = [];
    public int Depth { get; init; }
}
