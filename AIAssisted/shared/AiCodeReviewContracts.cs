using System.Text.Json.Serialization;

namespace BirkNext.AiCodeReview;

// ── AI-Generated Code Review: deterministic risk review of generated or heavily AI-assisted source changes ──────────────────────
// The profile is chosen by the user; it never infers who or what wrote the code. It reads evidence BirkNext already owns (the Source
// Analysis snapshot and its code-risk observations, contract/configuration/architecture/dependency/test/generated-documentation evidence,
// and the workspace's SDD graph) and applies a small catalogue of deterministic rules (AIC-*). No language model is called; there is no
// score. "No indicators observed by the checks executed" never means the code is correct.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiCodeReviewCategory
{
    RequirementsAlignment, ArchitectureConstitution, UnresolvedReferences, DuplicateLogic, Dependencies, Security, ValidationErrorHandling,
    Tests, Placeholders, DeadCode, ContractDrift, ConfigurationDrift, GeneratedDocumentation, ChangeRisk,
}

/// <summary>How one rule ran. A rule that could not run is listed with its reason — skipped rules are never hidden.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiRuleExecutionState { Executed, NotApplicable, NotAssessed, Unsupported, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiCategoryStatus { NoIndicators, Findings, NotAssessed, Unsupported, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiFindingSeverity { Info, Low, Medium, High }

/// <summary>Where a finding's evidence comes from. Every finding names at least one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiEvidenceSource { Specification, Constitution, Source, Dependency, Contract, Configuration, Tests, Build, Runtime, GeneratedDocumentation, SnapshotDiff, Architecture }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiReviewMode { CurrentSnapshot, SnapshotChange }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiReviewScope { EntireSource, ChangedFiles }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiChangeKind { Introduced, Removed, Changed, Unchanged }

/// <summary>Derived only from the findings' severities and the change scope (documented rule), never from a model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiReviewAttention { Low, Moderate, High }

// ── Code-risk observations captured once at Source Analysis upload (stored on the snapshot) ─────────────────────────────────────

/// <summary>What kind of syntax observation a code-risk analyzer recorded. Observations are facts; rules decide what they mean.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeRiskObservationKind
{
    NotImplementedThrow, PlaceholderReturn, PlaceholderLiteral, CommentMarker, EmptyCatch, BroadCatchReturnsSuccess, ExceptionDetailReturned,
    UnresolvedNamespaceImport, UnusedPrivateMethod, DeveloperExceptionPageUnconditional, PermissiveCors, InputModelWithoutValidation,
    TestWithoutAssertion, EmptyTest,
}

/// <summary>One observation at a file location. <see cref="Detail"/> is a short, redacted description — never source text or values.</summary>
public sealed record CodeRiskObservation(CodeRiskObservationKind Kind, string File, int Line, string Symbol, string Detail, bool InTestCode);

/// <summary>An HTTP or GraphQL entry point and the authorization metadata its source declares (attributes/fluent calls only).</summary>
public sealed record CodeEndpointAuthorization(string Key, string Kind, string Display, string File, int Line, bool Authorized, bool AllowAnonymous, string Basis);

/// <summary>Two or more declarations with the same normalized structure (DTO shape or method body). Locations only.</summary>
public sealed record CodeDuplicateGroup(string Kind, string Fingerprint, string Description, List<CodeLocation> Locations);

public sealed record CodeLocation(string File, int Line, string Symbol);

/// <summary>Namespaces imported per project folder: the usage evidence an introduced-dependency rule checks a package against.</summary>
public sealed record CodeProjectUsage(string ProjectPath, List<string> ImportedNamespaces);

/// <summary>Per-language analyzer coverage: which files a code-risk analyzer read and which languages had none.</summary>
public sealed record CodeRiskLanguageCoverage(string Language, string AnalyzerId, bool Supported, int Files, string? Reason = null);

public sealed record CodeRiskSourceEvidence
{
    public int AnalyzerVersion { get; init; } = 1;
    public Guid SnapshotId { get; init; }
    public List<CodeRiskLanguageCoverage> Languages { get; init; } = [];
    public int ProductionFiles { get; init; }
    public int TestFiles { get; init; }
    public List<CodeRiskObservation> Observations { get; init; } = [];
    public List<CodeEndpointAuthorization> Endpoints { get; init; } = [];
    public List<CodeDuplicateGroup> Duplicates { get; init; } = [];
    public List<CodeProjectUsage> ProjectUsage { get; init; } = [];
    /// <summary>Configuration keys read by source as literal strings (key paths only; "may come from environment" is a limitation).</summary>
    public List<CodeLocation> ConfigurationKeyReads { get; init; } = [];
    public bool Truncated { get; init; }
    public List<string> Limitations { get; init; } = [];
}

// ── Review request / result ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Workspace evidence the browser owns (documents and the SDD graph live in the workspace): summarized client-side from the existing
/// requirement graph — never re-derived here, never a text-similarity link.
/// </summary>
public sealed record AiWorkspaceEvidence
{
    public string? ProjectName { get; init; }
    public bool SpecificationAvailable { get; init; }
    public bool ConstitutionAvailable { get; init; }
    public bool TasksAvailable { get; init; }
    public int RequirementCount { get; init; }
    public List<AiRequirementEvidence> Requirements { get; init; } = [];
    /// <summary>Completed task checkboxes whose referenced requirements have no implementation evidence (task id → requirement ids).</summary>
    public List<AiTaskEvidence> CompletedTasksWithoutEvidence { get; init; } = [];
    public int CompletedTasksWithoutRequirementReferences { get; init; }
    public AiTestExecutionSummary? TestExecution { get; init; }
}

public sealed record AiRequirementEvidence(string RequirementId, bool HasImplementationEvidence, List<string> ImplementationFiles, List<string> ImplementationSnapshotIds,
    bool HasDesignedTests, bool HasExecutedTests);

public sealed record AiTaskEvidence(string TaskId, List<string> RequirementIds);

/// <summary>Imported test-execution evidence (TRX). Absent = tests were not executed as far as BirkNext knows (NotVerified).</summary>
public sealed record AiTestExecutionSummary(int Runs, int Executed, int Passed, int Failed, int Skipped, string? LatestRun);

public sealed record AiCodeReviewRequest
{
    public Guid CurrentSnapshotId { get; init; }
    public Guid? BaselineSnapshotId { get; init; }
    public AiReviewScope Scope { get; init; } = AiReviewScope.EntireSource;
    public AiWorkspaceEvidence? Workspace { get; init; }
}

public sealed record AiSnapshotProvenance(Guid SnapshotId, string Repository, string ArchiveName, string Fingerprint, DateTimeOffset AnalyzedAt, bool HasCodeRiskEvidence);

public sealed record AiRuleExecution(string RuleId, AiCodeReviewCategory Category, string Title, AiRuleExecutionState State, string? Reason, int Findings,
    List<AiEvidenceSource> Sources);

public sealed record AiFindingLocation(string File, int Line, string? Symbol = null);

public sealed record AiCodeFinding
{
    /// <summary>Stable logical identity: the same issue seen through several evidence paths is one finding with several references.</summary>
    public string LogicalId { get; init; } = "";
    public string RuleId { get; init; } = "";
    public AiCodeReviewCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Rationale { get; init; } = "";
    public AiFindingSeverity Severity { get; init; }
    public List<AiEvidenceSource> EvidenceSources { get; init; } = [];
    public Guid SourceSnapshotId { get; init; }
    public Guid? BaselineSnapshotId { get; init; }
    public List<AiFindingLocation> Locations { get; init; } = [];
    public List<string> RelatedIds { get; init; } = [];
    public int EvidenceReferences { get; init; } = 1;
    public AiChangeKind? Change { get; init; }
    public string? Baseline { get; init; }
    public string? Current { get; init; }
    public string Limitation { get; init; } = "";
    public string Recommendation { get; init; } = "";
}

public sealed record AiCategoryResult(AiCodeReviewCategory Category, AiCategoryStatus Status, int Findings, string AssessedScope, int UnsupportedRules, string? Explanation);

/// <summary>Introduced/removed/changed/unchanged counts for one evidence area of a two-snapshot review.</summary>
public sealed record AiChangeArea(string Area, int Introduced, int Removed, int Changed, int Unchanged, string? Note = null);

public sealed record AiReadinessItem(string Key, string Label, bool Available, string Detail, string? Blocks = null);

public sealed record AiCodeReviewResult
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public AiReviewMode Mode { get; init; }
    public AiReviewScope Scope { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public int EngineVersion { get; init; } = 1;
    public string? ProjectName { get; init; }
    public AiSnapshotProvenance Current { get; init; } = new(Guid.Empty, "", "", "", default, false);
    public AiSnapshotProvenance? Baseline { get; init; }
    public List<AiReadinessItem> Readiness { get; init; } = [];
    public List<AiCategoryResult> Categories { get; init; } = [];
    public List<AiRuleExecution> Rules { get; init; } = [];
    public List<AiCodeFinding> Findings { get; init; } = [];
    public List<AiChangeArea> Changes { get; init; } = [];
    public int ChangedFiles { get; init; }
    public AiReviewAttention? Attention { get; init; }
    public string? AttentionBasis { get; init; }
    public List<string> Limitations { get; init; } = [];
    public string Disclaimer { get; init; } = AiCodeReviewText.Disclaimer;
}

public sealed record AiCodeReviewRunSummary(Guid RunId, DateTimeOffset CompletedAt, AiReviewMode Mode, Guid CurrentSnapshotId, Guid? BaselineSnapshotId, string Label, int Findings);

public static class AiCodeReviewText
{
    public const string Title = "AI-Generated Code Review";
    public const string Hero = "Review source changes for risks commonly seen in generated or heavily AI-assisted code. BirkNext evaluates deterministic project evidence; it does not attempt to determine who or what wrote the code.";
    public const string Disclaimer = "This review does not detect whether code was written by AI. It applies deterministic checks to project evidence; no language model is used and there is no score.";
    public const string NoIndicators = "No indicators observed by the checks executed.";

    public static string Label(AiCodeReviewCategory category) => category switch
    {
        AiCodeReviewCategory.RequirementsAlignment => "Requirements alignment",
        AiCodeReviewCategory.ArchitectureConstitution => "Architecture / constitution",
        AiCodeReviewCategory.UnresolvedReferences => "Unresolved references",
        AiCodeReviewCategory.DuplicateLogic => "Duplicate / redundant logic",
        AiCodeReviewCategory.Dependencies => "Dependencies",
        AiCodeReviewCategory.Security => "Security / configuration",
        AiCodeReviewCategory.ValidationErrorHandling => "Validation / error handling",
        AiCodeReviewCategory.Tests => "Tests",
        AiCodeReviewCategory.Placeholders => "Incomplete / placeholder code",
        AiCodeReviewCategory.DeadCode => "Dead / unused code",
        AiCodeReviewCategory.ContractDrift => "Contract drift",
        AiCodeReviewCategory.ConfigurationDrift => "Configuration drift",
        AiCodeReviewCategory.GeneratedDocumentation => "Generated documentation",
        _ => "Snapshot change risk",
    };

    public static string Label(AiCategoryStatus status) => status switch
    {
        AiCategoryStatus.NoIndicators => "No indicators observed",
        AiCategoryStatus.Findings => "Findings",
        AiCategoryStatus.NotAssessed => "Not assessed",
        AiCategoryStatus.Unsupported => "Unsupported",
        _ => "Not applicable",
    };

    public static string Label(AiRuleExecutionState state) => state switch
    {
        AiRuleExecutionState.NotApplicable => "Not applicable",
        AiRuleExecutionState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };
}
