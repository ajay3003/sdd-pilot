using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.PipelineReview;

// ── Pipeline Review: delivery-flow interpretation over Source Analysis CI/CD evidence ─────────────────────────────────────
// Pipeline Review never parses YAML: it reads the normalized CI/CD evidence of ONE Source Analysis snapshot (plus that snapshot's architecture,
// contracts and infrastructure evidence) and interprets how changes reach environments — sequence, gating, artifacts, coverage, gaps.
//   Pipeline defined ≠ pipeline executed · test step ≠ test passed · deployment stage ≠ deployment succeeded · approval configured ≠ approval
//   occurred · artifact name ≠ same artifact · pipeline resource declared ≠ downstream pipeline ran.
// Severity (how much a gap matters) and evidence state (how sure the reading is) are separate. There is no pipeline score.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineNodeKind { Pipeline, Stage, Job, Artifact, Environment, TestGroup, Template }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineEdgeKind { Triggers, DependsOn, Publishes, ConsumesArtifactFrom, LikelyFollows, DeploysTo, Validates, IncludesTemplate }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineFindingCategory
{
    TriggerCoverageGap, TestGatingGap, PostDeployValidationGap, EnvironmentProgressionGap, ArtifactLineageGap, CrossPipelineDependencyGap, InfrastructureSequenceGap,
    TemplateResolutionGap, ConditionCoverageGap, SecurityValidationGap, DependencyValidationGap, ContractValidationGap, RollbackGap, VerificationGap, UnresolvedFlow,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineFindingSeverity { High, Medium, Low, Info }

/// <summary>What a validation step checks, by command/task/name evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationCategory { Build, Unit, Integration, Api, E2E, Frontend, Accessibility, Performance, Security, DependencyScan, StaticAnalysis, Contract, Smoke, HealthCheck, InfrastructurePlan, Test }

/// <summary>How a validation relates to a deployment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateState
{
    /// <summary>On the dependency path before the deployment; its failure stops the deployment.</summary>
    Gates,
    /// <summary>Gates through a promoted artifact: it ran in the pipeline that produced the artifact (cross-pipeline).</summary>
    Inherited,
    /// <summary>On the path, but only runs under a condition; the deployment can proceed when it does not run.</summary>
    Conditional,
    /// <summary>On the path, but continueOnError or a tolerant condition lets the deployment proceed after a failure.</summary>
    SoftGate,
    /// <summary>Runs after the deployment (post-deployment validation).</summary>
    After,
    /// <summary>Defined in the pipeline but not on the deployment's dependency path.</summary>
    NotGating,
    /// <summary>Hidden behind an unresolved template or expression — not assessable.</summary>
    Unknown,
}

public sealed record PipelineEvidenceRef
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Pipeline { get; init; } = "";
    public string? Stage { get; init; }
    public string? Job { get; init; }
    public string? Step { get; init; }
    public string? Condition { get; init; }
    /// <summary>The template file the item comes from when it is not in the pipeline file itself.</summary>
    public string? TemplateOrigin { get; init; }
    public string? Note { get; init; }
}

public sealed record PipelineTriggerSummary(string Kind, string Text, List<string> Branches, List<string> PathsInclude, List<string> PathsExclude, string? Schedule);

public sealed record ReviewedPipeline
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string File { get; init; } = "";
    public PipelinePlatform Platform { get; init; }
    public bool IsTemplate { get; init; }
    /// <summary>"Pull-request validation", "Continuous integration", "Downstream (pipeline trigger)", "Scheduled", "Manual", "Template".</summary>
    public string Role { get; init; } = "";
    public List<PipelineTriggerSummary> Triggers { get; init; } = [];
    public bool ManualOnly { get; init; }
    public int Stages { get; init; }
    public int Jobs { get; init; }
    public List<string> Environments { get; init; } = [];
    public List<string> Artifacts { get; init; } = [];
    /// <summary>Azure DevOps definition name when the (optional) Azure DevOps metadata maps one to this file.</summary>
    public string? DefinitionName { get; init; }
}

public sealed record PipelineGraphNode(string Id, PipelineNodeKind Kind, string Label, string? PipelineId, PipelineEvidenceRef? Evidence);

public sealed record PipelineGraphEdge(string FromId, string ToId, PipelineEdgeKind Kind, ArchitectureEvidenceState State, string Basis);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowSection { Trigger, Build, Validation, Artifact, Deployment, PostDeployment, Promotion, Production }

/// <summary>One sentence of the delivery story, generated deterministically from the graph. Text uses `backticks` for names from source.</summary>
public sealed record FlowStoryStep(int Number, FlowSection Section, string Text, ArchitectureEvidenceState State, List<PipelineEvidenceRef> Evidence, string? Detail = null);

public sealed record ValidationGate(ValidationCategory Category, GateState State, string Label, string Pipeline, string? Stage, string? Job, string? Condition, PipelineEvidenceRef Evidence);

public sealed record ArtifactLineageItem
{
    public string Artifact { get; init; } = "";
    /// <summary>"current run of {pipeline}", "pipeline resource {alias} → {pipeline}", "unresolved".</summary>
    public string Source { get; init; } = "";
    public string? ProducerPipeline { get; init; }
    public string? ProducerStage { get; init; }
    public ArchitectureEvidenceState State { get; init; }
    public string Basis { get; init; } = "";
}

public sealed record DeploymentReview
{
    public string Id { get; init; } = "";
    public string Pipeline { get; init; } = "";
    public string PipelineName { get; init; } = "";
    public string? Stage { get; init; }
    public string Job { get; init; } = "";
    /// <summary>The environment as named in source (environment key, stage or job name), or "(unlabelled)".</summary>
    public string Environment { get; init; } = "";
    public SourceEnvironmentKind EnvironmentKind { get; init; }
    public string EnvironmentBasis { get; init; } = "";
    public ArchitectureEvidenceState EnvironmentState { get; init; }
    /// <summary>"Application", "Infrastructure", "Database".</summary>
    public List<string> DeploysWhat { get; init; } = [];
    public string HowReached { get; init; } = "";
    public string? Condition { get; init; }
    public List<ValidationGate> Before { get; init; } = [];
    public List<ValidationGate> After { get; init; } = [];
    public List<ArtifactLineageItem> Artifacts { get; init; } = [];
    public string Approval { get; init; } = "";
    public string Rollback { get; init; } = "";
    public List<string> Unresolved { get; init; } = [];
    public PipelineEvidenceRef Evidence { get; init; } = new();
}

public sealed record ReviewedTest
{
    public string Pipeline { get; init; } = "";
    public string PipelineName { get; init; } = "";
    public ValidationCategory Category { get; init; }
    public string Name { get; init; } = "";
    public string Tool { get; init; } = "";
    public List<string> Targets { get; init; } = [];
    public string? Stage { get; init; }
    public string? Job { get; init; }
    /// <summary>When it runs: the pipeline's triggers, plus its own condition.</summary>
    public string WhenItRuns { get; init; } = "";
    public string? Condition { get; init; }
    public bool ContinueOnError { get; init; }
    /// <summary>Deployments this test gates (directly, conditionally or softly) — deployment ids.</summary>
    public List<string> Gates { get; init; } = [];
    public List<string> RunsAfter { get; init; } = [];
    public PipelineEvidenceRef Evidence { get; init; } = new();
}

public sealed record TestMatrixRow(ValidationCategory Category, Dictionary<string, GateState?> Cells, string? Note);

public sealed record TestMatrix(List<string> Columns, List<TestMatrixRow> Rows, List<string> Notes);

public sealed record EnvironmentProgressionStep(string From, string To, ArchitectureEvidenceState State, string Basis);

public sealed record EnvironmentSummary
{
    public string Environment { get; init; } = "";
    public SourceEnvironmentKind Kind { get; init; }
    public List<string> Deployments { get; init; } = [];
    public List<string> Pipelines { get; init; } = [];
}

public sealed record PipelineDependency(string FromPipeline, string ToPipeline, PipelineEdgeKind Kind, ArchitectureEvidenceState State, string Basis, PipelineEvidenceRef Evidence);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerCoverage { Yes, Partial, No, Unknown }

public sealed record PathCoverageCell(string Pipeline, TriggerCoverage Triggers, string Basis, List<ValidationCategory> Validations);

/// <summary>A source area (component, shared library, contract or IaC folder) and whether a change there starts each relevant pipeline.</summary>
public sealed record PathCoverageRow(string Area, string Path, string AreaKind, List<PathCoverageCell> Pipelines, ArchitectureEvidenceState State, string Basis);

public sealed record PipelineReviewFinding
{
    public string Id { get; init; } = "";
    public PipelineFindingCategory Category { get; init; }
    public PipelineFindingSeverity Severity { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public string Title { get; init; } = "";
    public string WhyItMatters { get; init; } = "";
    public string Explanation { get; init; } = "";
    public List<string> Pipelines { get; init; } = [];
    public string? Environment { get; init; }
    public List<PipelineEvidenceRef> Evidence { get; init; } = [];
    public string SuggestedAction { get; init; } = "";
}

public sealed record UnresolvedTemplate(string Pipeline, string Template, string Level, string? Stage, string? Job, string Reason, PipelineEvidenceRef Evidence);

/// <summary>Optional Azure DevOps enrichment (definitions, environments, checks) — configuration metadata, never run history.</summary>
public sealed record PipelineMetadataSummary
{
    /// <summary>"NotConfigured", "Available", "NotAuthorized", "Failed".</summary>
    public string State { get; init; } = "NotConfigured";
    public string Detail { get; init; } = "";
    public List<PipelineDefinitionMetadata> Definitions { get; init; } = [];
    public List<EnvironmentChecksMetadata> Environments { get; init; } = [];
}

public sealed record PipelineDefinitionMetadata(string Id, string Name, string? Folder, string? YamlPath, string? Repository);
public sealed record EnvironmentChecksMetadata(string Name, List<string> Checks);

public sealed record PipelineReviewResult
{
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public DateTimeOffset SourceAnalyzedAt { get; init; }
    public int CiCdAnalyzerVersion { get; init; }
    public int RulesVersion { get; init; } = PipelineReviewText.RulesVersion;
    /// <summary>"Reviewed", "NoPipelines", "NeedsReanalysis" (snapshot predates CI/CD evidence v2).</summary>
    public string State { get; init; } = "Reviewed";
    public string StateReason { get; init; } = "";
    public List<ReviewedPipeline> Pipelines { get; init; } = [];
    public List<PipelineGraphNode> Nodes { get; init; } = [];
    public List<PipelineGraphEdge> Edges { get; init; } = [];
    public List<FlowStoryStep> Story { get; init; } = [];
    /// <summary>The apparent path ("PR Validation → Build → DEV → QA → Production"), only from evidence.</summary>
    public List<string> DeliveryPath { get; init; } = [];
    public List<DeploymentReview> Deployments { get; init; } = [];
    public List<ReviewedTest> Tests { get; init; } = [];
    public TestMatrix TestMatrix { get; init; } = new([], [], []);
    public List<EnvironmentSummary> Environments { get; init; } = [];
    public List<EnvironmentProgressionStep> Progression { get; init; } = [];
    public List<PipelineDependency> Dependencies { get; init; } = [];
    public List<PathCoverageRow> PathCoverage { get; init; } = [];
    public List<PipelineReviewFinding> Findings { get; init; } = [];
    public List<UnresolvedTemplate> UnresolvedTemplates { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public PipelineMetadataSummary Metadata { get; init; } = new();
    public string Boundary { get; init; } = PipelineReviewText.Boundary;
}

/// <summary>Which pipelines a change to one path starts, and what then validates it before each environment.</summary>
public sealed record PathProbeResult(string Path, List<PathProbePipeline> Pipelines, List<string> Notes);
public sealed record PathProbePipeline(string Pipeline, string PipelineName, TriggerCoverage Triggers, string Basis, List<PathProbeDeployment> Deployments);
public sealed record PathProbeDeployment(string Environment, List<ValidationGate> Before);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineChangeKind { PipelineAdded, PipelineRemoved, TriggerChanged, PathFilterChanged, ConditionChanged, TestAdded, TestRemoved, DeploymentAdded, DeploymentRemoved, GatingChanged, ArtifactFlowChanged, TemplateChanged }

public sealed record PipelineReviewChange(PipelineChangeKind Kind, string Pipeline, string Detail, string? Interpretation, PipelineFindingSeverity? Severity);

public sealed record PipelineReviewComparison(Guid PreviousSnapshotId, Guid CurrentSnapshotId, List<PipelineReviewChange> Changes, string Boundary);

public static class PipelineReviewText
{
    public const int RulesVersion = 1;
    public const string Boundary = "Read from pipeline definitions in the selected Source Analysis snapshot. A step in a definition means the pipeline intends to run it — " +
        "not that it ran, passed or deployed. Approvals and checks configured in Azure DevOps are not visible in YAML.";
    public const string ApprovalNotAssessable = "Approval checks are not assessable from YAML alone (Azure DevOps environment approvals and checks live outside the repository).";
    public const string ChangesBoundary = "Pipeline source changes between two snapshots — not pipeline runtime failures or deployment results.";

    public static string Label(PipelineFindingCategory c) => c switch
    {
        PipelineFindingCategory.TriggerCoverageGap => "Trigger coverage",
        PipelineFindingCategory.TestGatingGap => "Test gating",
        PipelineFindingCategory.PostDeployValidationGap => "Post-deployment validation",
        PipelineFindingCategory.EnvironmentProgressionGap => "Environment progression",
        PipelineFindingCategory.ArtifactLineageGap => "Artifact lineage",
        PipelineFindingCategory.CrossPipelineDependencyGap => "Cross-pipeline dependency",
        PipelineFindingCategory.InfrastructureSequenceGap => "Infrastructure sequence",
        PipelineFindingCategory.TemplateResolutionGap => "Template resolution",
        PipelineFindingCategory.ConditionCoverageGap => "Conditions",
        PipelineFindingCategory.SecurityValidationGap => "Security validation",
        PipelineFindingCategory.DependencyValidationGap => "Dependency validation",
        PipelineFindingCategory.ContractValidationGap => "Contract validation",
        PipelineFindingCategory.RollbackGap => "Rollback",
        PipelineFindingCategory.VerificationGap => "Deployment verification",
        _ => "Unresolved flow",
    };

    public static string Label(ValidationCategory c) => c switch
    {
        ValidationCategory.Unit => "Unit tests",
        ValidationCategory.Integration => "Integration tests",
        ValidationCategory.Api => "API tests",
        ValidationCategory.E2E => "E2E / UI tests",
        ValidationCategory.Frontend => "Frontend tests",
        ValidationCategory.Accessibility => "Accessibility",
        ValidationCategory.Performance => "Performance tests",
        ValidationCategory.Security => "Security scan",
        ValidationCategory.DependencyScan => "Dependency scan",
        ValidationCategory.StaticAnalysis => "Static analysis",
        ValidationCategory.Contract => "Contract checks",
        ValidationCategory.Smoke => "Smoke tests",
        ValidationCategory.HealthCheck => "Health check",
        ValidationCategory.InfrastructurePlan => "Infrastructure plan/validate",
        ValidationCategory.Build => "Build",
        _ => "Tests (unclassified)",
    };

    public static string Label(GateState s) => s switch
    {
        GateState.Gates => "Gates",
        GateState.Inherited => "Gates via promoted artifact",
        GateState.Conditional => "Conditional",
        GateState.SoftGate => "May not block",
        GateState.After => "Runs after",
        GateState.NotGating => "Runs, does not gate",
        _ => "Not assessable",
    };

    public static string Label(ArchitectureEvidenceState s) => s switch
    {
        ArchitectureEvidenceState.Confirmed => "Confirmed",
        ArchitectureEvidenceState.StronglySupported => "Strongly supported",
        ArchitectureEvidenceState.Inferred => "Inferred",
        ArchitectureEvidenceState.Conflict => "Conflicting",
        _ => "Unresolved",
    };

    public static string Label(PipelineEdgeKind k) => k switch
    {
        PipelineEdgeKind.Triggers => "triggers",
        PipelineEdgeKind.DependsOn => "is followed by",
        PipelineEdgeKind.Publishes => "publishes",
        PipelineEdgeKind.ConsumesArtifactFrom => "provides artifacts to",
        PipelineEdgeKind.LikelyFollows => "is likely followed by",
        PipelineEdgeKind.DeploysTo => "deploys to",
        PipelineEdgeKind.Validates => "validates",
        _ => "includes template",
    };
}
