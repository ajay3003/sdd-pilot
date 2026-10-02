using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;

namespace BirkNext.SourceDomains;

// ── Source Analysis → reusable source-evidence domains (Infrastructure, Configuration, CI/CD, Contracts, cross-domain links) ──────────
// Source Analysis owns SOURCE EVIDENCE; review features own DOMAIN INTERPRETATION. Every type here is a source-derived fact with provenance
// (snapshot, fingerprint, file, line, key/symbol, technology, analyzer version) — never a review result:
//   resource declared ≠ resource exists · role assignment declared ≠ effective permission · private endpoint declared ≠ connectivity
//   diagnostic setting declared ≠ logs observed · pipeline step defined ≠ step ran or passed · contract in source ≠ runtime compatibility.
// Generic by design: rules key on formats, providers, resource types, key shapes and commands — never on a project or repository name.
// Secret values are never stored: sensitive entries keep their key, kind and location only ("Connection string detected").
// Evidence states reuse the Source Analysis states (Confirmed … Conflict); domain status, finding severity, review status and runtime
// status stay separate axes.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEvidenceDomain { Infrastructure, Configuration, CiCd, Contracts, CrossDomain }

/// <summary>Analysis completeness of one domain. FailedAnalysis only when the analyzer itself failed; Unsupported ≠ FailedAnalysis;
/// NotDetected ≠ Unsupported (nothing of the domain is in the selected source, which says nothing about the project as a whole).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceDomainStatus { Complete, Partial, NotDetected, Unsupported, FailedAnalysis }

/// <summary>How far an analyzer can read a technology. Heuristic/regex readers are Partial — never labelled Supported.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainSupport { Supported, Partial, Unsupported }

/// <summary>A file's role, decided from path AND content (a YAML file may be a pipeline, a Kubernetes manifest, OpenAPI, Helm values or configuration).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceFileRole { SourceCode, InfrastructureAsCode, Configuration, Pipeline, Contract, ProjectMetadata, Documentation, Generated, Test, ToolConfiguration, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEnvironmentKind { Default, Development, Local, Test, QA, Staging, Production, Custom }

/// <summary>An environment as the source names it: the normalized kind plus the raw label (never assumed equal across formats without evidence).</summary>
public sealed record SourceEnvironmentLabel(SourceEnvironmentKind Kind, string Raw)
{
    public static readonly SourceEnvironmentLabel Default = new(SourceEnvironmentKind.Default, "");
}

/// <summary>Where one piece of evidence sits. A pattern label — never source text or a value.</summary>
public sealed record SourceEvidenceLocation(string File, int Line, string SymbolOrKey, string Technology, string Pattern);

public sealed record SourceDomainDiagnostic(string Kind, string Message, string? File = null, int Line = 0);

/// <summary>Registry metadata of one domain analyzer: what it reads, what it produces and which analyzers it depends on (explicit order).</summary>
public sealed record DomainAnalyzerInfo(SourceEvidenceDomain Domain, string Name, int Version, int Stage, List<string> Technologies, List<SourceEvidenceDomain> DependsOn, List<string> Produces);

public sealed record DomainCapability(SourceEvidenceDomain Domain, string Technology, DomainSupport Support, string Detail);

public sealed record SourceFileRoleSummary(SourceFileRole Role, int Files, List<string> Examples);

/// <summary>The common envelope every domain result carries: snapshot binding, analyzer version, completeness, technologies and limitations.</summary>
public abstract record SourceDomainResult
{
    public SourceEvidenceDomain Domain { get; init; }
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset ExtractedAt { get; init; }
    public SourceDomainStatus Status { get; init; } = SourceDomainStatus.NotDetected;
    /// <summary>Why the status is what it is (e.g. "No supported Infrastructure as Code files detected in the selected source").</summary>
    public string? StatusReason { get; init; }
    public List<string> Technologies { get; init; } = [];
    public List<SourceDomainDiagnostic> Diagnostics { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

// ── Infrastructure as Code ───────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureFormat { Terraform, Bicep, Arm, Kubernetes, Helm }

/// <summary>Provider-neutral category. Azure/AWS/GCP/Kubernetes adapters map resource types onto it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureCategory { Compute, Database, Messaging, Storage, Cache, SecretStore, Identity, AccessControl, Networking, Observability, ApiGateway, ContainerRegistry, ResourceContainer, Dns, Configuration, Other }

/// <summary>A source-visible setting of a resource. Value is a safe literal (bool, number, short token) or a state label
/// ("(expression)", "(variable)", "[sensitive]") — never a secret.</summary>
public sealed record InfrastructureSetting(string Key, string Value, string Area, bool Resolved);

public sealed record InfrastructureResource
{
    /// <summary>Stable key: "{module path or root}/{type}.{name}" (Terraform), "{file}#{symbol}" (Bicep), "{file}#{kind}/{name}" (Kubernetes).</summary>
    public string Id { get; init; } = "";
    public InfrastructureFormat Format { get; init; }
    /// <summary>"resource" or "data" (a data source reads existing infrastructure; it declares nothing).</summary>
    public string Kind { get; init; } = "resource";
    public string Provider { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string LogicalName { get; init; } = "";
    /// <summary>The resource's own name attribute when it is a safe literal; null when computed or unresolved.</summary>
    public string? DeclaredName { get; init; }
    public bool DeclaredNameResolved { get; init; }
    public InfrastructureCategory Category { get; init; } = InfrastructureCategory.Other;
    /// <summary>Provider adapter label, e.g. "Service Bus topic", "S3 bucket", "Deployment".</summary>
    public string CategoryDetail { get; init; } = "";
    public string? ModulePath { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    public SourceEnvironmentLabel? Environment { get; init; }
    public List<InfrastructureSetting> Settings { get; init; } = [];
    public List<string> TagKeys { get; init; } = [];
    public ArchitectureEvidenceState EvidenceState { get; init; } = ArchitectureEvidenceState.Confirmed;
    /// <summary>Always "Declared in source; existence not verified": declaration is not deployment.</summary>
    public string RuntimeState { get; init; } = SourceDomainText.DeclaredNotVerified;
    /// <summary>The declared name per environment when an environment file (tfvars/parameters) changes the variables the name is built from
    /// (analyzer v2+). Empty when the name does not vary or cannot be resolved per environment.</summary>
    public List<InfrastructureEnvironmentName> EnvironmentNames { get; init; } = [];
}

/// <summary>A resource's name in one environment, statically resolved from that environment's variable file. Basis names the file.</summary>
public sealed record InfrastructureEnvironmentName(SourceEnvironmentLabel Environment, string Name, string Basis);

/// <summary>A static relationship between declarations (depends_on, attribute reference, module wiring) — never runtime connectivity.</summary>
public sealed record InfrastructureDependency(string FromId, string ToId, string Kind, ArchitectureEvidenceState State, string File, int Line);

public sealed record InfrastructureModule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Safe source label: a local relative path, or a registry/git address with credentials and query stripped.</summary>
    public string Source { get; init; } = "";
    public bool Local { get; init; }
    public string? ResolvedPath { get; init; }
    public bool Analyzed { get; init; }
    public string? Version { get; init; }
    public List<string> Inputs { get; init; } = [];
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string? Note { get; init; }
}

/// <summary>A value a variable gets in one environment file (tfvars, parameters). Literal values only when safe.</summary>
public sealed record InfrastructureVariableValue(SourceEnvironmentLabel Environment, string File, int Line, string State, string? SafeValue);

public sealed record InfrastructureVariable
{
    public string Name { get; init; } = "";
    public string? Type { get; init; }
    public bool Sensitive { get; init; }
    /// <summary>"none", "literal", "redacted", "expression".</summary>
    public string DefaultState { get; init; } = "none";
    public string? SafeDefault { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    public List<InfrastructureVariableValue> Values { get; init; } = [];
}

public sealed record InfrastructureOutput(string Name, bool Sensitive, List<string> References, string File, int Line);
public sealed record InfrastructureProvider(string Name, string? Source, string? Version, bool HasCredentialSettings, string File, int Line);
/// <summary>State backend metadata: type and setting NAMES only (access keys and SAS tokens are never read out).</summary>
public sealed record InfrastructureBackend(string Type, List<string> SettingNames, string File, int Line);

/// <summary>A role assignment / policy binding as declared. Declared ≠ effective permission.</summary>
public sealed record AccessAssignment
{
    public string ResourceId { get; init; } = "";
    public string? Role { get; init; }
    public string? ScopeReference { get; init; }
    public string? PrincipalReference { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public string RuntimeState { get; init; } = "Effective permission not verified";
}

public sealed record InfrastructureEnvironment(SourceEnvironmentLabel Label, List<string> Files, string Basis);

public sealed record InfrastructureEvidence : SourceDomainResult
{
    public List<InfrastructureFormat> Formats { get; init; } = [];
    public List<InfrastructureProvider> Providers { get; init; } = [];
    public List<InfrastructureModule> Modules { get; init; } = [];
    public List<InfrastructureResource> Resources { get; init; } = [];
    public List<InfrastructureVariable> Variables { get; init; } = [];
    public List<string> Locals { get; init; } = [];
    public List<InfrastructureOutput> Outputs { get; init; } = [];
    public List<InfrastructureDependency> Dependencies { get; init; } = [];
    public List<InfrastructureBackend> Backends { get; init; } = [];
    public List<InfrastructureEnvironment> Environments { get; init; } = [];
    public List<AccessAssignment> AccessAssignments { get; init; } = [];
    /// <summary>References that could not be resolved statically (unknown resource, computed address).</summary>
    public List<string> UnresolvedReferences { get; init; } = [];
}

// ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfigurationCategory { Authentication, Authorization, Api, Database, Messaging, Storage, Observability, Networking, FeatureFlags, Performance, Security, ExternalSystems, RuntimeEnvironment, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfigurationValueKind { Empty, Boolean, Number, Text, Url, Identifier, EntityName, Collection, ConnectionString, Secret, SecretReference, Placeholder, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfigurationSensitivity { None, Sensitive, SecretReference }

public sealed record ConfigurationFile
{
    public string Path { get; init; } = "";
    /// <summary>"json", "yaml", "properties", "env", "xml".</summary>
    public string Format { get; init; } = "";
    /// <summary>"ASP.NET Core appsettings", "launchSettings", "Docker Compose", "Helm values", "Kubernetes ConfigMap", "Terraform variables" …</summary>
    public string Technology { get; init; } = "";
    public SourceEnvironmentLabel Environment { get; init; } = SourceEnvironmentLabel.Default;
    /// <summary>How the environment was decided ("file-name suffix", "launch profile variable", "none") — a suffix is not a deployed environment.</summary>
    public string EnvironmentBasis { get; init; } = "none";
    public string? Component { get; init; }
    public int Entries { get; init; }
    public bool Parsed { get; init; } = true;
    public string? Note { get; init; }
}

public sealed record ConfigurationEntry
{
    public string Id { get; init; } = "";
    public string Key { get; init; } = "";
    /// <summary>Lower-case, ':'-separated ("__" and "." folded) so the same key matches across formats.</summary>
    public string NormalizedKey { get; init; } = "";
    public ConfigurationCategory Category { get; init; } = ConfigurationCategory.Unknown;
    public ConfigurationValueKind ValueKind { get; init; } = ConfigurationValueKind.Unknown;
    /// <summary>Only for non-sensitive values that are safe to show (booleans, numbers, endpoint scheme+host+path, entity names, public ids).</summary>
    public string? ValuePreviewSafe { get; init; }
    public ConfigurationSensitivity Sensitivity { get; init; }
    public SourceEnvironmentLabel Environment { get; init; } = SourceEnvironmentLabel.Default;
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Technology { get; init; } = "";
    public string? Component { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; } = ArchitectureEvidenceState.Confirmed;
    /// <summary>Normalized identities the value refers to ("host:orders.servicebus.windows.net", "entity:orders") — used for cross-domain links.</summary>
    public List<string> References { get; init; } = [];
}

/// <summary>Same environment, same normalized key, materially different values in two files. Values are never shown; different
/// environments are variants, not conflicts.</summary>
public sealed record ConfigurationConflict(string NormalizedKey, SourceEnvironmentLabel Environment, List<string> Files, string Detail);

public sealed record ConfigurationEvidence : SourceDomainResult
{
    public List<ConfigurationFile> Files { get; init; } = [];
    public List<ConfigurationEntry> Entries { get; init; } = [];
    public List<SourceEnvironmentLabel> Environments { get; init; } = [];
    public List<ConfigurationConflict> Conflicts { get; init; } = [];
    public bool Truncated { get; init; }
}

// ── CI/CD ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelinePlatform { AzurePipelines, GitHubActions, GitLabCi, Jenkins }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineStepKind
{
    Restore, Build, UnitTest, IntegrationTest, FrontendTest, E2ETest, AccessibilityTest, Test, Coverage, StaticAnalysis, DependencyScan, SecurityScan, Sbom,
    ContainerBuild, ContainerPush, Publish, InfrastructureDeploy, InfrastructurePlan, ApplicationDeploy, DatabaseMigration, SecretRetrieval, Template, Other,
    // Analyzer v2: finer validation kinds and delivery mechanics (appended; earlier values keep their meaning).
    SmokeTest, ApiTest, ContractTest, PerformanceTest, HealthCheck, ArtifactDownload, Rollback, ManualApproval,
}

/// <summary>A trigger as written. Path filters show which source changes start the pipeline — not that it ran.</summary>
public sealed record PipelineTrigger
{
    /// <summary>"push", "pull-request", "schedule", "manual", "pipeline-resource", "none".</summary>
    public string Type { get; init; } = "";
    public List<string> BranchesInclude { get; init; } = [];
    public List<string> BranchesExclude { get; init; } = [];
    public List<string> PathsInclude { get; init; } = [];
    public List<string> PathsExclude { get; init; } = [];
    public string? Schedule { get; init; }
    public int Line { get; init; }
    /// <summary>Analyzer v2: for Type "none", which trigger key is switched off ("push" for trigger: none, "pull-request" for pr: none).</summary>
    public string? Disables { get; init; }
}

public sealed record PipelineStage(string Name, string? DisplayName, List<string> DependsOn, bool Deployment, string? Environment, int Line)
{
    /// <summary>False when the stage has no dependsOn key: Azure Pipelines then runs it after the previous stage. "dependsOn: []" is declared and empty (parallel).</summary>
    public bool DependsOnDeclared { get; init; }
    /// <summary>The condition as written (safe expression text), null for the default succeeded().</summary>
    public string? Condition { get; init; }
    /// <summary>Position among the stages of this file (0-based), the order Azure uses for implicit dependencies.</summary>
    public int Order { get; init; }
}

/// <summary>A job as written: its stage, explicit dependencies (jobs in a stage run in parallel unless dependsOn says otherwise), condition and
/// continueOnError. Deployment jobs name an environment. Analyzer v2.</summary>
public sealed record PipelineJob
{
    public string Name { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Stage { get; init; }
    public List<string> DependsOn { get; init; } = [];
    public bool DependsOnDeclared { get; init; }
    public string? Condition { get; init; }
    public bool Deployment { get; init; }
    public string? Environment { get; init; }
    public string? Strategy { get; init; }
    public bool ContinueOnError { get; init; }
    public int Order { get; init; }
    public int Line { get; init; }
}

/// <summary>An artifact a step consumes: Source "current" (this run), a pipeline-resource alias, or "pipeline:{name}" (a specific pipeline). Analyzer v2.</summary>
public sealed record PipelineArtifactUse(string Artifact, string Source);

/// <summary>A pipeline or repository resource as declared. A pipeline resource with a trigger starts this pipeline when that pipeline completes. Analyzer v2.</summary>
public sealed record PipelineResource
{
    /// <summary>"pipeline" or "repository".</summary>
    public string Kind { get; init; } = "";
    public string Alias { get; init; } = "";
    /// <summary>The CI system's pipeline name (pipeline resources) or repository name (repository resources) as written.</summary>
    public string Source { get; init; } = "";
    public string? Project { get; init; }
    public string? RepositoryType { get; init; }
    public string? Ref { get; init; }
    public bool TriggerDeclared { get; init; }
    public List<string> TriggerBranches { get; init; } = [];
    public int Line { get; init; }
}

/// <summary>Where a template is used: level (stages/jobs/steps/extends/variables), the including stage/job, whether it resolved to a file in the
/// snapshot, the repository alias for "file@alias", and literal parameter values (safe scalars only). Analyzer v2.</summary>
public sealed record PipelineTemplateUse
{
    public string Template { get; init; } = "";
    public string? ResolvedPath { get; init; }
    public string? RepositoryAlias { get; init; }
    public string Level { get; init; } = "";
    public string? Stage { get; init; }
    public string? Job { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = [];
    public int Line { get; init; }
    public int Order { get; init; }
    public bool Resolved => ResolvedPath is not null;
}

public sealed record PipelineStep
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public PipelineStepKind Kind { get; init; }
    /// <summary>The tool/command family detected ("dotnet test", "terraform apply", "AzureCLI@2") — never the script text.</summary>
    public string Tool { get; init; } = "";
    public string? Stage { get; init; }
    public string? Job { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Environment { get; init; }
    /// <summary>Safe targets the step names (IaC directory, var-file, chart path, test project path).</summary>
    public List<string> Targets { get; init; } = [];
    public int Line { get; init; }
    public string ExecutionState { get; init; } = SourceDomainText.StepNotExecuted;
    /// <summary>Analyzer v2: the step's condition (safe expression text), continueOnError, deployment-strategy phase, artifacts it publishes and consumes.</summary>
    public string? Condition { get; init; }
    public bool ContinueOnError { get; init; }
    public string? StrategyPhase { get; init; }
    public List<string> ArtifactsPublished { get; init; } = [];
    public List<PipelineArtifactUse> ArtifactsConsumed { get; init; } = [];
    /// <summary>Position among the steps of its job (0-based): steps of a job run in order.</summary>
    public int Order { get; init; }
}

public sealed record PipelineDefinition
{
    public string Id { get; init; } = "";
    public PipelinePlatform Platform { get; init; }
    public string File { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>True for a template file included by other pipelines (not runnable on its own).</summary>
    public bool IsTemplate { get; init; }
    public List<PipelineTrigger> Triggers { get; init; } = [];
    public List<PipelineStage> Stages { get; init; } = [];
    public List<string> Jobs { get; init; } = [];
    public List<PipelineStep> Steps { get; init; } = [];
    public List<string> Environments { get; init; } = [];
    public List<string> Artifacts { get; init; } = [];
    public List<string> Templates { get; init; } = [];
    /// <summary>Names only: variable groups, secret variables, Key Vault tasks, secrets.X references. Values are never read.</summary>
    public List<string> SecretReferences { get; init; } = [];
    public List<string> VariableNames { get; init; } = [];
    public bool UsesFederatedCredentials { get; init; }
    public bool ApprovalsDeclared { get; init; }
    /// <summary>Analyzer v2: jobs with dependencies/conditions, pipeline and repository resources, and every template use.</summary>
    public List<PipelineJob> JobDetails { get; init; } = [];
    public List<PipelineResource> Resources { get; init; } = [];
    public List<PipelineTemplateUse> TemplateUses { get; init; } = [];
    /// <summary>Template parameter names this file declares (template files).</summary>
    public List<string> Parameters { get; init; } = [];
    /// <summary>Literal (safe) default values of declared template parameters.</summary>
    public Dictionary<string, string> ParameterDefaults { get; init; } = [];
}

public sealed record PipelineEvidence : SourceDomainResult
{
    public List<PipelineDefinition> Pipelines { get; init; } = [];
    /// <summary>Dependency-update automation configuration found beside the pipelines (Renovate, Dependabot) — file names only.</summary>
    public List<string> DependencyAutomation { get; init; } = [];
}

// ── Contracts / schemas ─────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceContractType { OpenApi, GraphQlSchema, GraphQlOperations, AsyncApi, JsonSchema, Protobuf, MessageContract, GeneratedClient }

public sealed record ContractField(string Name, string Type, bool Required);
public sealed record ContractTypeShape(string Name, string Kind, List<ContractField> Fields);
/// <summary>"GET /orders/{id}", "query orders", "rpc GetOrder", "publish orders.created".</summary>
public sealed record ContractOperation(string Name, string Kind, string? Path, List<string> Parameters);

public sealed record SourceContract
{
    public string Id { get; init; } = "";
    public SourceContractType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Format { get; init; } = "";
    /// <summary>The component whose project holds the contract, when evidence supports it; never assumed for a shared folder.</summary>
    public string? Producer { get; init; }
    public string? ProducerBasis { get; init; }
    public List<string> ConsumerHints { get; init; } = [];
    public List<ContractOperation> Operations { get; init; } = [];
    public List<ContractTypeShape> Types { get; init; } = [];
    public ArchitectureEvidenceState EvidenceState { get; init; } = ArchitectureEvidenceState.Confirmed;
    public DomainSupport ParseSupport { get; init; } = DomainSupport.Supported;
    public List<string> Limitations { get; init; } = [];
    public string RuntimeState { get; init; } = SourceDomainText.ContractNotVerified;
}

public sealed record ContractEvidence : SourceDomainResult
{
    public List<SourceContract> Contracts { get; init; } = [];
}

// ── Cross-domain relationships ──────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEvidenceLinkType
{
    ApplicationUsesInfrastructureResource, ApplicationUsesDatastore, ConfigurationReferencesInfrastructureResource, PipelineDeploysInfrastructure,
    PipelineRunsTests, ContractProducedByComponent, ContractConsumedByComponent, TelemetryConfiguredForComponent,
}

/// <summary>A typed, source-derived relationship between evidence of two domains. Unresolved = no matching declaration found in the
/// selected source (it may be managed elsewhere) — neutral, never "missing".</summary>
public sealed record SourceEvidenceLink
{
    public string Id { get; init; } = "";
    public SourceEvidenceLinkType Type { get; init; }
    public string FromId { get; init; } = "";
    public string FromLabel { get; init; } = "";
    public string? ToId { get; init; }
    public string ToLabel { get; init; } = "";
    public ArchitectureEvidenceState State { get; init; }
    public string Basis { get; init; } = "";
    public bool Resolved => ToId is not null;
}

/// <summary>One environment as several formats name it. Confirmed only when a file explicitly links them (e.g. a pipeline passes the var-file).</summary>
public sealed record SourceEnvironmentMapping
{
    public SourceEnvironmentKind Kind { get; init; }
    public List<string> RawLabels { get; init; } = [];
    public List<string> ConfigurationFiles { get; init; } = [];
    public List<string> InfrastructureFiles { get; init; } = [];
    public List<string> PipelineEnvironments { get; init; } = [];
    public ArchitectureEvidenceState State { get; init; }
    public string Basis { get; init; } = "";
}

/// <summary>One layer of source observability evidence. Runtime telemetry is always its own layer and never assessed here.</summary>
public sealed record SourceObservabilityLayer(string Layer, string State, string Detail, int Items);

public sealed record CrossDomainEvidence : SourceDomainResult
{
    public List<SourceEvidenceLink> Links { get; init; } = [];
    public List<SourceEnvironmentMapping> EnvironmentMappings { get; init; } = [];
    public List<SourceObservabilityLayer> ObservabilityLayers { get; init; } = [];
}

/// <summary>All reusable source-evidence domains of ONE immutable source snapshot (null on snapshots analyzed before they existed).</summary>
public sealed record SourceEvidenceDomainsSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int Version { get; init; } = 1;
    public DateTimeOffset ExtractedAt { get; init; }
    public List<DomainAnalyzerInfo> Analyzers { get; init; } = [];
    public List<DomainCapability> Capabilities { get; init; } = [];
    public List<SourceFileRoleSummary> FileRoles { get; init; } = [];
    public InfrastructureEvidence Infrastructure { get; init; } = new() { Domain = SourceEvidenceDomain.Infrastructure };
    public ConfigurationEvidence Configuration { get; init; } = new() { Domain = SourceEvidenceDomain.Configuration };
    public PipelineEvidence CiCd { get; init; } = new() { Domain = SourceEvidenceDomain.CiCd };
    public ContractEvidence Contracts { get; init; } = new() { Domain = SourceEvidenceDomain.Contracts };
    public CrossDomainEvidence CrossDomain { get; init; } = new() { Domain = SourceEvidenceDomain.CrossDomain };

    [JsonIgnore] public IEnumerable<SourceDomainResult> Domains => [Infrastructure, Configuration, CiCd, Contracts, CrossDomain];
}

// ── Source evidence change (snapshot A vs B) ────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEvidenceChangeKind { Added, Removed, Changed }

/// <summary>Contract-specific change classes; only a compatibility rule may call one breaking.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractChangeClass { None, OperationAdded, OperationRemoved, FieldAddedOptional, FieldAddedRequired, FieldRemoved, TypeChanged, RequirednessChanged, TypeAdded, TypeRemoved }

public sealed record SourceEvidenceChange(SourceEvidenceDomain Domain, SourceEvidenceChangeKind Kind, string Area, string Key, string Name, string Detail,
    ContractChangeClass ContractChange = ContractChangeClass.None);

/// <summary>
/// Compares the evidence domains of two snapshots by stable keys. A difference is a SOURCE change ("Infrastructure source change") —
/// never Terraform/runtime drift, which compares source with deployed state. Nothing of the older snapshot is merged into the newer one.
/// </summary>
public static class SourceEvidenceDiff
{
    public const string Label = "Source evidence change — the two source snapshots differ. This is not runtime or deployment drift.";

    public static List<SourceEvidenceChange> Compare(SourceEvidenceDomainsSnapshot previous, SourceEvidenceDomainsSnapshot current)
    {
        var changes = new List<SourceEvidenceChange>();
        void Diff<T>(SourceEvidenceDomain domain, string area, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> key, Func<T, string> name, Func<T, string> signature)
        {
            var b = before.GroupBy(key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var a = after.GroupBy(key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var (k, item) in a.Where(x => !b.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)) changes.Add(new(domain, SourceEvidenceChangeKind.Added, area, k, name(item), signature(item)));
            foreach (var (k, item) in b.Where(x => !a.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)) changes.Add(new(domain, SourceEvidenceChangeKind.Removed, area, k, name(item), signature(item)));
            foreach (var (k, item) in a.Where(x => b.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
                if (signature(item) != signature(b[k])) changes.Add(new(domain, SourceEvidenceChangeKind.Changed, area, k, name(item), $"{signature(b[k])} → {signature(item)}"));
        }
        static string Settings(InfrastructureResource r) => string.Join(", ", r.Settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));
        Diff(SourceEvidenceDomain.Infrastructure, "Resource", previous.Infrastructure.Resources, current.Infrastructure.Resources, r => r.Id, r => r.LogicalName,
            r => $"{r.ResourceType} · {r.DeclaredName ?? "(name not resolved)"} · {Settings(r)}");
        Diff(SourceEvidenceDomain.Infrastructure, "Module", previous.Infrastructure.Modules, current.Infrastructure.Modules, m => m.Id, m => m.Name, m => $"{m.Source} · {m.Version}");
        Diff(SourceEvidenceDomain.Configuration, "Key", previous.Configuration.Entries, current.Configuration.Entries, e => $"{e.File}|{e.NormalizedKey}", e => e.Key,
            e => $"{e.ValueKind} · {(e.Sensitivity == ConfigurationSensitivity.None ? e.ValuePreviewSafe ?? "(value not shown)" : "(sensitive — value not shown)")}");
        Diff(SourceEvidenceDomain.CiCd, "Pipeline", previous.CiCd.Pipelines, current.CiCd.Pipelines, p => p.Id, p => p.Name,
            p => $"triggers {string.Join("; ", p.Triggers.Select(t => $"{t.Type} {string.Join(",", t.BranchesInclude)} {string.Join(",", t.PathsInclude)}".Trim()))} · steps {string.Join(", ", p.Steps.Select(s => s.Kind).Distinct().Order())}");
        foreach (var contract in current.Contracts.Contracts)
        {
            if (previous.Contracts.Contracts.FirstOrDefault(c => c.Id == contract.Id) is not { } before)
            { changes.Add(new(SourceEvidenceDomain.Contracts, SourceEvidenceChangeKind.Added, "Contract", contract.Id, contract.Name, $"{contract.Type} · {contract.Operations.Count} operation(s)")); continue; }
            changes.AddRange(ContractChanges(before, contract));
        }
        foreach (var removed in previous.Contracts.Contracts.Where(p => current.Contracts.Contracts.All(c => c.Id != p.Id)))
            changes.Add(new(SourceEvidenceDomain.Contracts, SourceEvidenceChangeKind.Removed, "Contract", removed.Id, removed.Name, $"{removed.Type} · {removed.Operations.Count} operation(s)"));
        return changes;
    }

    /// <summary>Operation, type and field changes of one contract, each with its change class. Compatibility rules are the consumer's.</summary>
    public static List<SourceEvidenceChange> ContractChanges(SourceContract before, SourceContract after)
    {
        var changes = new List<SourceEvidenceChange>();
        void Add(SourceEvidenceChangeKind kind, string area, string key, string detail, ContractChangeClass cls) =>
            changes.Add(new(SourceEvidenceDomain.Contracts, kind, area, $"{after.Id}|{key}", after.Name, detail, cls));
        var ops = before.Operations.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var op in after.Operations.Where(o => !ops.Contains(o.Name))) Add(SourceEvidenceChangeKind.Added, "Operation", op.Name, op.Name, ContractChangeClass.OperationAdded);
        foreach (var op in before.Operations.Where(o => after.Operations.All(a => a.Name != o.Name))) Add(SourceEvidenceChangeKind.Removed, "Operation", op.Name, op.Name, ContractChangeClass.OperationRemoved);
        var types = before.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var type in after.Types)
        {
            if (!types.TryGetValue(type.Name, out var old)) { Add(SourceEvidenceChangeKind.Added, "Type", type.Name, type.Name, ContractChangeClass.TypeAdded); continue; }
            var fields = old.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            foreach (var field in type.Fields)
            {
                if (!fields.TryGetValue(field.Name, out var was))
                    Add(SourceEvidenceChangeKind.Added, "Field", $"{type.Name}.{field.Name}", $"{type.Name}.{field.Name} ({(field.Required ? "required" : "optional")})",
                        field.Required ? ContractChangeClass.FieldAddedRequired : ContractChangeClass.FieldAddedOptional);
                else if (was.Type != field.Type) Add(SourceEvidenceChangeKind.Changed, "Field", $"{type.Name}.{field.Name}", $"{type.Name}.{field.Name}: {was.Type} → {field.Type}", ContractChangeClass.TypeChanged);
                else if (was.Required != field.Required) Add(SourceEvidenceChangeKind.Changed, "Field", $"{type.Name}.{field.Name}",
                    $"{type.Name}.{field.Name}: {(was.Required ? "required" : "optional")} → {(field.Required ? "required" : "optional")}", ContractChangeClass.RequirednessChanged);
            }
            foreach (var gone in old.Fields.Where(f => type.Fields.All(n => n.Name != f.Name))) Add(SourceEvidenceChangeKind.Removed, "Field", $"{type.Name}.{gone.Name}", $"{type.Name}.{gone.Name}", ContractChangeClass.FieldRemoved);
        }
        foreach (var gone in before.Types.Where(t => after.Types.All(a => a.Name != t.Name))) Add(SourceEvidenceChangeKind.Removed, "Type", gone.Name, gone.Name, ContractChangeClass.TypeRemoved);
        return changes;
    }
}

public static class SourceDomainText
{
    public const string SourceBoundary = "Source-derived evidence only. Deployed/runtime state is not verified here.";
    public const string DeclaredNotVerified = "Declared in source; existence not verified";
    public const string StepNotExecuted = "Defined in pipeline; execution not assessed";
    public const string ContractNotVerified = "Source contract; runtime compatibility not verified";
    public const string RuntimeNotAssessed = "Not assessed in Source Analysis";

    public static string Label(SourceDomainStatus status) => status switch
    {
        SourceDomainStatus.NotDetected => "Not detected",
        SourceDomainStatus.FailedAnalysis => "Analysis failed",
        _ => status.ToString(),
    };

    public static string Label(SourceEvidenceDomain domain) => domain switch
    {
        SourceEvidenceDomain.CiCd => "CI/CD",
        SourceEvidenceDomain.CrossDomain => "Cross-source relationships",
        _ => domain.ToString(),
    };

    public static string Label(SourceEnvironmentLabel env) => env.Kind == SourceEnvironmentKind.Default ? "Default" : string.IsNullOrEmpty(env.Raw) ? env.Kind.ToString() : env.Raw;

    public static string Label(ArchitectureEvidenceState state) => state == ArchitectureEvidenceState.StronglySupported ? "Strongly supported" : state.ToString();

    public static string Label(InfrastructureCategory category) => category switch
    {
        InfrastructureCategory.SecretStore => "Secret store",
        InfrastructureCategory.AccessControl => "Access control",
        InfrastructureCategory.ApiGateway => "API gateway",
        InfrastructureCategory.ContainerRegistry => "Container registry",
        InfrastructureCategory.ResourceContainer => "Resource container",
        _ => category.ToString(),
    };

    public static string Label(ConfigurationCategory category) => category switch
    {
        ConfigurationCategory.FeatureFlags => "Feature flags",
        ConfigurationCategory.ExternalSystems => "External systems",
        ConfigurationCategory.RuntimeEnvironment => "Runtime environment",
        _ => category.ToString(),
    };

    public static string Label(ConfigurationValueKind kind) => kind switch
    {
        ConfigurationValueKind.ConnectionString => "Connection string detected",
        ConfigurationValueKind.Secret => "Secret value detected (not shown)",
        ConfigurationValueKind.SecretReference => "Secret reference",
        ConfigurationValueKind.EntityName => "Entity name",
        _ => kind.ToString(),
    };

    public static string Label(PipelinePlatform platform) => platform switch
    {
        PipelinePlatform.AzurePipelines => "Azure Pipelines",
        PipelinePlatform.GitHubActions => "GitHub Actions",
        PipelinePlatform.GitLabCi => "GitLab CI",
        _ => platform.ToString(),
    };

    public static string Label(PipelineStepKind kind) => kind switch
    {
        PipelineStepKind.UnitTest => "Unit tests",
        PipelineStepKind.IntegrationTest => "Integration tests",
        PipelineStepKind.FrontendTest => "Frontend tests",
        PipelineStepKind.E2ETest => "E2E tests",
        PipelineStepKind.AccessibilityTest => "Accessibility tests",
        PipelineStepKind.Test => "Tests",
        PipelineStepKind.StaticAnalysis => "Static analysis",
        PipelineStepKind.DependencyScan => "Dependency scan",
        PipelineStepKind.SecurityScan => "Security scan",
        PipelineStepKind.Sbom => "SBOM",
        PipelineStepKind.ContainerBuild => "Container build",
        PipelineStepKind.ContainerPush => "Container push",
        PipelineStepKind.InfrastructureDeploy => "Infrastructure deploy",
        PipelineStepKind.InfrastructurePlan => "Infrastructure plan",
        PipelineStepKind.ApplicationDeploy => "Application deploy",
        PipelineStepKind.DatabaseMigration => "Database migration",
        PipelineStepKind.SecretRetrieval => "Secret retrieval",
        _ => kind.ToString(),
    };

    public static string Label(SourceContractType type) => type switch
    {
        SourceContractType.OpenApi => "OpenAPI",
        SourceContractType.GraphQlSchema => "GraphQL schema",
        SourceContractType.GraphQlOperations => "GraphQL operations",
        SourceContractType.AsyncApi => "AsyncAPI",
        SourceContractType.JsonSchema => "JSON Schema",
        SourceContractType.MessageContract => "Message contract",
        SourceContractType.GeneratedClient => "Generated client",
        _ => type.ToString(),
    };

    public static string Label(SourceEvidenceLinkType type) => type switch
    {
        SourceEvidenceLinkType.ApplicationUsesInfrastructureResource => "Application ↔ Infrastructure",
        SourceEvidenceLinkType.ApplicationUsesDatastore => "Application ↔ Database",
        SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource => "Configuration ↔ Infrastructure",
        SourceEvidenceLinkType.PipelineDeploysInfrastructure => "Pipeline ↔ Infrastructure",
        SourceEvidenceLinkType.PipelineRunsTests => "Pipeline ↔ Tests",
        SourceEvidenceLinkType.ContractProducedByComponent => "Component ↔ Contract (producer)",
        SourceEvidenceLinkType.ContractConsumedByComponent => "Component ↔ Contract (consumer)",
        _ => "Component ↔ Telemetry",
    };
}
