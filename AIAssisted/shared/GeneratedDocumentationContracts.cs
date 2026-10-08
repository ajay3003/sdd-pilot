using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;

namespace BirkNext.GeneratedDocumentation;

// ── Generated documentation evidence ───────────────────────────────────────────────────────────────────────────────────
// Generated documentation (documentation a tool, build step, script or agent workflow writes from the code) is its OWN evidence type:
//   authored documentation (Constitution, Specification, Plan, Tasks, Data Model) ≠ generated documentation ≠ source evidence ≠ runtime evidence.
// Source is stronger implementation evidence than generated documentation. A discrepancy between them is evidence for review — never proof
// of a defect and never a decision about which side is wrong:
//   generated doc stale ≠ implementation wrong · spec/generated mismatch ≠ spec wrong · task marked done ≠ implementation verified ·
//   drift detected ≠ failed requirement.
// Everything here is derived from ONE immutable Source Analysis snapshot. No document text, no configuration value and no secret is stored:
// only paths, fingerprints, declared dates and bounded structured keys (route, resource, entity, key, component and technology names).
// Generic by design: detection keys on directory conventions, workflow declarations, formats and content markers — never on a project name.

/// <summary>What a generated document describes, classified from path, format AND content (the path is one signal, not the only one).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedDocumentKind { Overview, Architecture, DataFlow, DataModel, Messaging, ApiContract, GraphQlSchema, Routes, Changelog, OtherGeneratedDocumentation }

/// <summary>Where a document comes from. Not every docs/ file is generated, and not every file in a generated folder must be.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentationOrigin { Generated, Authored, Unknown }

/// <summary>What kind of mechanism produces or maintains generated documentation. Unknown when no evidence links one — never assumed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratorType { AgentWorkflow, BuildGenerator, DocumentationTool, Script, Unknown }

/// <summary>Freshness of generated documentation relative to the RELEVANT source evidence of its own scope. Stale is shown as
/// "Potentially stale": it needs evidence that relevant source changed after the document was generated. Unknown = the generation time
/// could not be established; NotEnoughEvidence = no trustworthy dated source evidence to compare with; NotApplicable = a history record
/// (changelog) or a document without a source scope.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedDocumentationFreshnessStatus { Current, Stale, Unknown, NotApplicable, NotEnoughEvidence }

/// <summary>Fail is reserved for deterministic broken conditions (a generated contract that does not parse). Semantic disagreement is at most Warning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedDocumentationHealthStatus { Pass, Partial, Warning, Fail }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrossArtifactDriftType { DocumentationDrift, SpecificationDrift, ConstitutionDrift, PlanDrift, DeliveryDrift }

/// <summary>Left is always the generated documentation side unless the drift type names an authored artifact (then left is the authored artifact).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftDifferenceKind { MissingOnLeft, MissingOnRight, ValueMismatch, NameMismatch, VersionMismatch, StructureMismatch, StaleEvidence, UnableToCompare }

/// <summary>Only "needs review" exists: BirkNext does not decide which artifact is wrong and has no drift review workflow.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReviewStatus { NeedsReview }

/// <summary>Result of one structured comparison family for one module, kept separate from freshness.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedComparisonState { Equivalent, PotentiallyDrifted, UnableToCompare, NotApplicable }

/// <summary>A file that defines, configures or triggers a documentation generator (agent skill, hook, documentation tool configuration,
/// build target or script). <see cref="GeneratorName"/> is set only when the file names the generator explicitly.</summary>
public sealed record GeneratorWorkflowEvidence
{
    public string Id { get; init; } = "";
    public GeneratorType Type { get; init; } = GeneratorType.Unknown;
    public string? GeneratorName { get; init; }
    public string Path { get; init; } = "";
    /// <summary>"Agent skill definition", "Agent hook registration", "Agent hook script", "Documentation tool configuration", "Build target", "Script".</summary>
    public string Role { get; init; } = "";
    /// <summary>Generated-documentation directories this file names (directory conventions or explicit paths), e.g. "autodoc/" or "docs/architecture/".</summary>
    public List<string> DeclaredOutputDirectories { get; init; } = [];
    /// <summary>File names this file declares as outputs of a declared directory, e.g. "openapi.yaml". Expected outputs come only from here.</summary>
    public List<string> DeclaredOutputFiles { get; init; } = [];
    /// <summary>Other workflow files this file references (e.g. a hook registration pointing at a hook script).</summary>
    public List<string> References { get; init; } = [];
    public string Basis { get; init; } = "";
    public string ContentFingerprint { get; init; } = "";
}

public sealed record GeneratedDocumentationProvenance
{
    public GeneratorType GeneratorType { get; init; } = GeneratorType.Unknown;
    /// <summary>Only when a workflow file names it explicitly (e.g. a skill's front-matter name). Never inferred from a tool convention.</summary>
    public string? GeneratorName { get; init; }
    public string? GeneratorConfigPath { get; init; }
    public string? GeneratorWorkflowPath { get; init; }
    /// <summary>The files and markers that establish provenance ("workflow declares autodoc/ (.claude/skills/x/SKILL.md)", "directory convention autodoc/").</summary>
    public List<string> DetectedFrom { get; init; } = [];
    /// <summary>Confirmed: a workflow names the output directory and the generator; StronglySupported: a workflow names the directory;
    /// Inferred: directory convention or in-file marker only; Unresolved: nothing links a generator.</summary>
    public ArchitectureEvidenceState Confidence { get; init; } = ArchitectureEvidenceState.Unresolved;
    public DateTimeOffset? LastGeneratorEvidenceModified { get; init; }
    /// <summary>"Generated by agent workflow", "Likely generated / Unknown provenance" …</summary>
    public string Label { get; init; } = "Likely generated / Unknown provenance";
}

/// <summary>A dated piece of evidence with what dates it and how precise it is. Never the current machine time.</summary>
public sealed record DatedEvidence(DateTimeOffset At, string Basis, string Path, string Precision);

/// <summary>Bounded structured keys a generated document states. Names only — never values, prose or secrets.</summary>
public sealed record GeneratedDocumentKeys
{
    /// <summary>"GET /orders/{}" — method plus normalized path.</summary>
    public List<string> Routes { get; init; } = [];
    /// <summary>Messaging resource names (topics, queues, subscriptions, event hubs, consumer groups).</summary>
    public List<string> MessagingResources { get; init; } = [];
    public List<string> Entities { get; init; } = [];
    public List<string> Relationships { get; init; } = [];
    public List<string> ConfigurationKeys { get; init; } = [];
    /// <summary>Source project / component identifiers named in code spans, tables or diagram labels.</summary>
    public List<string> Components { get; init; } = [];
    /// <summary>Technology vocabulary ids (e.g. "db.postgresql") the document names. Vocabulary matching, never similarity.</summary>
    public List<string> Technologies { get; init; } = [];
    /// <summary>Versions a changelog declares, newest first.</summary>
    public List<string> Versions { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record GeneratedDocumentationEvidence
{
    public string EvidenceId { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public string ModuleId { get; init; } = "";
    public string SafeRelativePath { get; init; } = "";
    public GeneratedDocumentKind DocumentKind { get; init; }
    public string KindBasis { get; init; } = "";
    public DocumentationOrigin Origin { get; init; } = DocumentationOrigin.Generated;
    public string OriginBasis { get; init; } = "";
    public GeneratorType GeneratorKind { get; init; } = GeneratorType.Unknown;
    /// <summary>Ids of <see cref="GeneratorWorkflowEvidence"/> that link this document.</summary>
    public List<string> GeneratorEvidence { get; init; } = [];
    public string ContentFingerprint { get; init; } = "";
    public long Bytes { get; init; }
    public int Lines { get; init; }
    /// <summary>The document's generation time: a date the document declares ("Last updated: …") or a trustworthy archive time. Null when neither exists.</summary>
    public DatedEvidence? LastModified { get; init; }
    /// <summary>The latest dated RELEVANT source evidence of the document's scope, or null.</summary>
    public DatedEvidence? SourceLastModified { get; init; }
    public GeneratedDocumentationFreshnessStatus FreshnessStatus { get; init; } = GeneratedDocumentationFreshnessStatus.Unknown;
    public string FreshnessReason { get; init; } = "";
    public GeneratedDocumentationProvenance Provenance { get; init; } = new();
    public List<string> RelatedSourceEvidenceIds { get; init; } = [];
    /// <summary>Contract evidence ids (Source Analysis → Contracts) parsed from this generated document — contract evidence, never a Specification.</summary>
    public List<string> RelatedContractEvidenceIds { get; init; } = [];
    public GeneratedDocumentKeys Keys { get; init; } = new();
    /// <summary>A generated contract that does not parse: a deterministic structural failure (the only Fail condition).</summary>
    public bool StructurallyInvalid { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>A generated document a workflow declares for this module's output directory, with whether the module's source shows the
/// capability the document describes. Missing only counts when the capability is present.</summary>
public sealed record ExpectedGeneratedDocument(string FileName, GeneratedDocumentKind Kind, bool Present, bool CapabilityPresent, string Basis);

/// <summary>Bounded source-derived keys of one module scope, kept on the snapshot so authored comparisons never reparse the archive.</summary>
public sealed record ModuleSourceKeys
{
    public List<string> Routes { get; init; } = [];
    /// <summary>Health and GraphQL endpoint mappings: they satisfy a documented route but are not REST operations a contract must list.</summary>
    public List<string> AuxiliaryRoutes { get; init; } = [];
    public bool RoutesComplete { get; init; }
    public List<string> MessagingResources { get; init; } = [];
    public List<string> Entities { get; init; } = [];
    public List<string> ConfigurationKeys { get; init; } = [];
    public List<string> Projects { get; init; } = [];
    public List<string> DeployableComponents { get; init; } = [];
    public List<string> Technologies { get; init; } = [];
    public List<string> Capabilities { get; init; } = [];
    public bool Truncated { get; init; }
}

/// <summary>One comparison family for one module: "HTTP routes", "Messaging resources", "Data model", "Configuration keys",
/// "Architecture components", "API contract", "GraphQL schema", "Routes (UI)".</summary>
public sealed record GeneratedComparison(string Family, GeneratedComparisonState State, string Detail, int Candidates);

/// <summary>A module scope: the directory a generated-documentation folder documents. Source of module A never dates module B's documents.</summary>
public sealed record GeneratedDocumentationModule
{
    public string ModuleId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>Repository-relative root of the documented scope ("" = repository root).</summary>
    public string RootPath { get; init; } = "";
    public string GeneratedDirectory { get; init; } = "";
    /// <summary>"Module" or "Repository" (a repository-root output documents the whole repository).</summary>
    public string Scope { get; init; } = "Module";
    public int SourceFileCount { get; init; }
    public List<string> DocumentIds { get; init; } = [];
    public List<string> GeneratorIds { get; init; } = [];
    public GeneratedDocumentationFreshnessStatus Freshness { get; init; } = GeneratedDocumentationFreshnessStatus.Unknown;
    public string FreshnessReason { get; init; } = "";
    public DatedEvidence? LatestSourceEvidence { get; init; }
    public DatedEvidence? LatestGeneratedEvidence { get; init; }
    /// <summary>Supporting only (weaker than source evidence): changelog and source version metadata.</summary>
    public List<string> SupportingFreshnessSignals { get; init; } = [];
    public List<ExpectedGeneratedDocument> ExpectedDocuments { get; init; } = [];
    public List<string> MissingExpectedDocs { get; init; } = [];
    /// <summary>Generated documents not declared by a detected workflow. Informational — never a failure.</summary>
    public List<string> AdditionalDocs { get; init; } = [];
    public List<GeneratedComparison> Comparisons { get; init; } = [];
    public ModuleSourceKeys SourceKeys { get; init; } = new();
}

/// <summary>One side of a drift candidate: what it is, where, and which version. Values are names/keys only.</summary>
public sealed record DriftEvidenceRef(string Kind, string Label, string Path, int Line = 0, string? ArtifactId = null, string? Fingerprint = null, string? Value = null);

/// <summary>A POTENTIAL drift between two artifacts — "Potential drift – requires review". Never proof of a defect; never decides which side is wrong.
/// One logical mismatch is one candidate, even when several generated documents state it (they are listed as several evidence references).</summary>
public sealed record CrossArtifactDriftCandidate
{
    public string Id { get; init; } = "";
    public CrossArtifactDriftType DriftType { get; init; }
    public string ModuleId { get; init; } = "";
    public string Family { get; init; } = "";
    public List<DriftEvidenceRef> LeftEvidence { get; init; } = [];
    public List<DriftEvidenceRef> RightEvidence { get; init; } = [];
    public string StructuredKey { get; init; } = "";
    public DriftDifferenceKind DifferenceKind { get; init; }
    /// <summary>Reuses the Source Analysis evidence states: Confirmed for exact structured comparisons, Inferred for vocabulary-based ones.</summary>
    public ArchitectureEvidenceState Confidence { get; init; } = ArchitectureEvidenceState.Inferred;
    public bool ReviewRequired { get; init; } = true;
    public DriftReviewStatus ReviewStatus { get; init; } = DriftReviewStatus.NeedsReview;
    public Guid SourceSnapshotId { get; init; }
    public string? ArtifactFingerprint { get; init; }
    public string Explanation { get; init; } = "";
    public string WhyReview { get; init; } = "";
    /// <summary>Freshness of the generated document(s) involved: a mismatch against potentially stale documentation is still only a candidate.</summary>
    public GeneratedDocumentationFreshnessStatus? GeneratedFreshness { get; init; }
    /// <summary>Existing evidence this candidate relates to instead of duplicating it (e.g. "Implementation Review").</summary>
    public List<string> RelatedFindings { get; init; } = [];
}

/// <summary>All generated-documentation evidence of ONE immutable Source Analysis snapshot (null on snapshots analyzed before it existed).</summary>
public sealed record GeneratedDocumentationSnapshot
{
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset AnalyzedAt { get; init; }
    public List<GeneratorWorkflowEvidence> Generators { get; init; } = [];
    public List<GeneratedDocumentationEvidence> Documents { get; init; } = [];
    public List<GeneratedDocumentationModule> Modules { get; init; } = [];
    /// <summary>Source/configuration/contract vs generated documentation, plus authored contracts (Spec-Kit contracts folders) vs generated contracts.</summary>
    public List<CrossArtifactDriftCandidate> Drift { get; init; } = [];
    /// <summary>Whether archive entry times can date anything: a downloaded or exported archive gives every entry the same time.</summary>
    public bool ArchiveTimestampsReliable { get; init; }
    public string ArchiveTimestampBasis { get; init; } = "";
    public int AuthoredDocumentationFiles { get; init; }
    public int UnclassifiedDocumentationFiles { get; init; }
    public long AnalysisMilliseconds { get; init; }
    public List<string> Limitations { get; init; } = [];
}

// ── Diagnostic (System Settings → Developer → Generated Documentation Health) ─────────────────────────────────────────────

/// <summary>An authored artifact the current workspace selected for a role (Constitution, Specification, Plan, Tasks). Content is read
/// for this request only and never stored. <see cref="SourcePath"/> scopes it to a module when it lies inside one.</summary>
public sealed record AuthoredArtifactInput(string Role, string ArtifactId, string DisplayName, string? SourcePath, string Fingerprint, string Content);

public sealed record GeneratedDocumentationDiagnosticRequest
{
    /// <summary>An exact snapshot (e.g. a historical one). Null: the current project's bound snapshot, else the newest Source Analysis snapshot.</summary>
    public Guid? SnapshotId { get; init; }
    /// <summary>The current workspace's Project Import identity, when the workspace is an imported project.</summary>
    public string? ProjectImportId { get; init; }
    public List<AuthoredArtifactInput> AuthoredArtifacts { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedDocumentationRunState { NoSnapshot, NotAvailableForSnapshot, NoGeneratedDocumentation, Ready }

public sealed record GeneratedDocumentationSnapshotBinding
{
    public Guid SnapshotId { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public string ArchiveName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string? ProjectImportId { get; init; }
    /// <summary>"Requested snapshot", "Snapshot of the current imported project", "Newest Source Analysis snapshot".</summary>
    public string Basis { get; init; } = "";
    public bool IsNewest { get; init; }
    public int AnalyzerVersion { get; init; }
}

public sealed record GeneratedDocumentationHealthResult
{
    public string Module { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string RootPath { get; init; } = "";
    public string GeneratedDirectory { get; init; } = "";
    public bool GeneratorDetected { get; init; }
    public string GeneratorSummary { get; init; } = "";
    public bool GeneratedDocsDetected { get; init; }
    public GeneratedDocumentationFreshnessStatus Freshness { get; init; }
    public string FreshnessReason { get; init; } = "";
    public DatedEvidence? LatestSourceEvidence { get; init; }
    public DatedEvidence? LatestGeneratedEvidence { get; init; }
    public List<string> SupportingFreshnessSignals { get; init; } = [];
    public List<string> MissingExpectedDocs { get; init; } = [];
    public List<string> AdditionalDocs { get; init; } = [];
    public List<string> SourceDiscrepancies { get; init; } = [];
    public List<string> CrossArtifactDrift { get; init; } = [];
    public List<GeneratedComparison> Comparisons { get; init; } = [];
    public GeneratedDocumentationHealthStatus Status { get; init; }
    public string StatusReason { get; init; } = "";
    public List<GeneratedDocumentationEvidence> Evidence { get; init; } = [];
}

public sealed record GeneratedDocumentationSummary
{
    public int ModulesWithGeneratedDocs { get; init; }
    public int GeneratedDocuments { get; init; }
    public int ModulesWithGeneratorDetected { get; init; }
    public int Fresh { get; init; }
    public int PotentiallyStale { get; init; }
    public int FreshnessUnknown { get; init; }
    public int MissingExpectedDocs { get; init; }
    public int SourceDocumentationDiscrepancies { get; init; }
    public int CrossArtifactDriftCandidates { get; init; }
    public int StructuralFailures { get; init; }
}

/// <summary>Whether authored artifacts were compared, and why not when they were not (project isolation and snapshot binding come first).</summary>
public sealed record AuthoredComparisonState(bool Compared, string Reason, List<string> Artifacts);

public sealed record GeneratedDocumentationDiagnosticRun
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public long DurationMilliseconds { get; init; }
    public GeneratedDocumentationRunState State { get; init; }
    public string StateMessage { get; init; } = "";
    public GeneratedDocumentationSnapshotBinding? Snapshot { get; init; }
    public GeneratedDocumentationSummary Summary { get; init; } = new();
    public List<GeneratedDocumentationHealthResult> Modules { get; init; } = [];
    public List<GeneratorWorkflowEvidence> Generators { get; init; } = [];
    public List<CrossArtifactDriftCandidate> Drift { get; init; } = [];
    public AuthoredComparisonState Authored { get; init; } = new(false, "No authored artifacts were provided.", []);
    public List<string> Limitations { get; init; } = [];
    public bool FromCache { get; init; }
    public int AnalyzerVersion { get; init; }
}

public static class GeneratedDocumentationText
{
    public const string Description = "Checks whether generated project documentation is detected, traceable to its generation workflow, fresh relative to relevant source evidence, and consistent enough to flag potential drift for review.";
    public const string EmptyState = "No generated documentation detected in the current source snapshot.";
    public const string NoGenerator = "Generated documentation detected. Generator provenance could not be established.";
    public const string ReviewStatus = "Potential drift – requires review";
    public const string Boundary = "Authored documentation ≠ generated documentation ≠ source evidence ≠ runtime evidence. Source is stronger implementation evidence than generated documentation; a discrepancy is a candidate for review, never proof of a defect, and BirkNext does not decide which side is wrong.";
    public const string NotSynchronized = "No discrepancy found does not mean the documentation is fully synchronized: only structured keys BirkNext can extract deterministically are compared.";

    public static string Label(GeneratedDocumentationFreshnessStatus status) => status switch
    {
        GeneratedDocumentationFreshnessStatus.Current => "Current",
        GeneratedDocumentationFreshnessStatus.Stale => "Potentially stale",
        GeneratedDocumentationFreshnessStatus.NotApplicable => "Not applicable",
        GeneratedDocumentationFreshnessStatus.NotEnoughEvidence => "Not enough evidence",
        _ => "Unknown",
    };

    public static string Label(GeneratedDocumentationHealthStatus status) => status switch
    {
        GeneratedDocumentationHealthStatus.Pass => "Pass",
        GeneratedDocumentationHealthStatus.Partial => "Partial",
        GeneratedDocumentationHealthStatus.Warning => "Needs review",
        _ => "Fail",
    };

    public static string Label(GeneratedDocumentKind kind) => kind switch
    {
        GeneratedDocumentKind.DataFlow => "Data flow",
        GeneratedDocumentKind.DataModel => "Data model",
        GeneratedDocumentKind.ApiContract => "API contract",
        GeneratedDocumentKind.GraphQlSchema => "GraphQL schema",
        GeneratedDocumentKind.OtherGeneratedDocumentation => "Other generated documentation",
        _ => kind.ToString(),
    };

    public static string Label(GeneratorType type) => type switch
    {
        GeneratorType.AgentWorkflow => "Agent workflow",
        GeneratorType.BuildGenerator => "Build generator",
        GeneratorType.DocumentationTool => "Documentation tool",
        GeneratorType.Script => "Script",
        _ => "Unknown",
    };

    public static string Label(CrossArtifactDriftType type) => type switch
    {
        CrossArtifactDriftType.DocumentationDrift => "Documentation drift (generated vs source)",
        CrossArtifactDriftType.SpecificationDrift => "Specification drift",
        CrossArtifactDriftType.ConstitutionDrift => "Constitution drift",
        CrossArtifactDriftType.PlanDrift => "Plan drift",
        _ => "Delivery drift",
    };

    public static string Label(DriftDifferenceKind kind) => kind switch
    {
        DriftDifferenceKind.MissingOnLeft => "Missing on left",
        DriftDifferenceKind.MissingOnRight => "Missing on right",
        DriftDifferenceKind.ValueMismatch => "Value mismatch",
        DriftDifferenceKind.NameMismatch => "Name mismatch",
        DriftDifferenceKind.VersionMismatch => "Version mismatch",
        DriftDifferenceKind.StructureMismatch => "Structure mismatch",
        DriftDifferenceKind.StaleEvidence => "Stale evidence",
        _ => "Unable to compare",
    };

    public static string Label(GeneratedComparisonState state) => state switch
    {
        GeneratedComparisonState.Equivalent => "Equivalent",
        GeneratedComparisonState.PotentiallyDrifted => "Potentially drifted",
        GeneratedComparisonState.NotApplicable => "Not applicable",
        _ => "Unable to compare",
    };
}
