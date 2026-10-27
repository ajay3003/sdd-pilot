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

/// <summary>Legacy marker kept for stored snapshots: BirkNext does not decide which artifact is wrong and has no drift review workflow. Whether a
/// candidate is a review candidate comes from <see cref="GeneratedDocumentationSemantics.Classify"/>, never from this value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftReviewStatus { NeedsReview }

/// <summary>Result of one structured comparison family for one module, kept separate from freshness. PotentialInconsistency needs two comparable
/// claims that conflict; a generated claim BirkNext could not correlate with source is SourceCorrelationUnresolved (neutral, never drift).
/// <see cref="PotentiallyDrifted"/> is the legacy state of stored snapshots; it is reclassified on read.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedComparisonState { Equivalent, PotentiallyDrifted, UnableToCompare, NotApplicable, Consistent, PotentialInconsistency, SourceCorrelationUnresolved, NotAssessed }

/// <summary>What one difference candidate is. Only PotentialInconsistency is a review candidate (two comparable claims conflict); the rest are
/// diagnostics: a claim not correlated with the other side, a source item generated documentation does not name, or no comparison possible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedConsistencyCategory { PotentialInconsistency, SourceCorrelationUnresolved, SourceOnly, UnableToCompare }

/// <summary>Whether a generated document is expected. Required needs an explicit declaration; ExpectedByProfile needs a generator/profile rule that maps
/// a capability to the document AND deterministic capability evidence. Generator capability alone is ApplicabilityNotEstablished, never missing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExpectedDocumentState { RequiredPresent, RequiredMissing, ExpectedByProfile, ApplicabilityNotEstablished, Optional, NotApplicable, Unknown }

/// <summary>Overall module review state. ReviewRequired only with a provenance-backed reason (an explicitly required document is missing);
/// otherwise at most ReviewSuggested. Never a release gate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedDocumentationReviewState { NoReviewSuggested, ReviewSuggested, ReviewRequired }

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
    /// <summary>File names this file declares as outputs of a declared directory, e.g. "openapi.yaml" — what the generator CAN produce.</summary>
    public List<string> DeclaredOutputFiles { get; init; } = [];
    /// <summary>Declared outputs the file states unconditionally as required ("must/always/required" without a condition) — the only basis for "Required".</summary>
    public List<string> RequiredOutputFiles { get; init; } = [];
    /// <summary>Declared outputs the file maps to a capability ("file|Messaging": produced when the module has messaging) — the profile rule for "Expected by profile".</summary>
    public List<string> ConditionalOutputFiles { get; init; } = [];
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
/// capability the document describes. Missing counts only when <see cref="State"/> establishes a requirement (null on legacy snapshots: classified on read).</summary>
public sealed record ExpectedGeneratedDocument(string FileName, GeneratedDocumentKind Kind, bool Present, bool CapabilityPresent, string Basis)
{
    public ExpectedDocumentState? State { get; init; }
    public string StateBasis { get; init; } = "";
}

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
/// "Architecture components", "API contract", "GraphQL schema", "Routes (UI)". <see cref="Candidates"/> counts every difference; the split
/// says how many are comparable inconsistencies, unresolved correlations and source-only observations.</summary>
public sealed record GeneratedComparison(string Family, GeneratedComparisonState State, string Detail, int Candidates)
{
    public int Inconsistencies { get; init; }
    public int Unresolved { get; init; }
    public int SourceOnly { get; init; }
}

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

/// <summary>A difference between two artifacts. Only a <see cref="GeneratedConsistencyCategory.PotentialInconsistency"/> is a review candidate; never proof of a defect; never decides which side is wrong.
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
    /// <summary>True only for a comparable inconsistency (set on read; legacy snapshots stored true for every candidate).</summary>
    public bool ReviewRequired { get; init; } = true;
    public DriftReviewStatus ReviewStatus { get; init; } = DriftReviewStatus.NeedsReview;
    /// <summary>Set on read by <see cref="GeneratedDocumentationSemantics.Classify"/>; null on stored legacy candidates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GeneratedConsistencyCategory? Consistency { get; init; }
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
    /// <summary>Documents with an established requirement or profile applicability that are absent. Nothing else counts as missing.</summary>
    public List<string> MissingExpectedDocs { get; init; } = [];
    /// <summary>Declared by a generator, absent, with no requirement or applicability established. Neutral — never missing.</summary>
    public List<string> ApplicabilityUnresolvedDocs { get; init; } = [];
    public List<ExpectedGeneratedDocument> ExpectedDocuments { get; init; } = [];
    public List<string> AdditionalDocs { get; init; } = [];
    /// <summary>Source consistency review candidates: generated vs source claims that conflict.</summary>
    public List<string> SourceDiscrepancies { get; init; } = [];
    /// <summary>Authored/cross-artifact consistency review candidates: authored artifacts vs generated documentation claims that conflict.</summary>
    public List<string> CrossArtifactDrift { get; init; } = [];
    /// <summary>Candidates whose claim could not be correlated with the other side, or source items generated documentation does not name. Diagnostics only.</summary>
    public List<string> UnresolvedCorrelations { get; init; } = [];
    public List<GeneratedComparison> Comparisons { get; init; } = [];
    /// <summary>Health of the generated documentation only (parseability, freshness, required documents) — consistency never changes it.</summary>
    public GeneratedDocumentationHealthStatus Status { get; init; }
    public string StatusReason { get; init; } = "";
    public GeneratedDocumentationReviewState Review { get; init; }
    /// <summary>Why review is suggested/required — each reason names its evidence.</summary>
    public List<string> ReviewReasons { get; init; } = [];
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
    /// <summary>Required (or profile-expected) documents missing — established requirement/applicability only.</summary>
    public int MissingExpectedDocs { get; init; }
    public int ApplicabilityUnresolved { get; init; }
    /// <summary>Source consistency review candidates (comparable generated vs source claims that conflict).</summary>
    public int SourceDocumentationDiscrepancies { get; init; }
    /// <summary>Authored (Specification, Constitution, Plan) vs generated review candidates.</summary>
    public int AuthoredGeneratedCandidates { get; init; }
    /// <summary>Every non-source review candidate (authored/generated plus task/delivery ↔ generated ↔ source).</summary>
    public int CrossArtifactDriftCandidates { get; init; }
    public int CorrelationUnresolved { get; init; }
    public int SourceOnlyObservations { get; init; }
    /// <summary>Comparison families (per module) that could not be compared.</summary>
    public int UnableToCompare { get; init; }
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
    /// <summary>Autodoc diagnostics: which modules/APIs of the snapshot have module-specific generated output. Null when the snapshot holds no
    /// generated-documentation evidence at all (analyzed before it existed).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AutodocDiagnostics? Autodoc { get; init; }
}

// ── Autodoc diagnostics (a presentation of the same evidence: no parser, no store, no policy) ───────────────────────────────

/// <summary>
/// Module-level autodoc usage — separate from freshness and health. Configured = module-specific generated output with a traced generator and
/// every declared output present; Partial = output exists but the generator is not traced or a declared output is absent; NotConfigured =
/// no module-specific generated output (neutral: nothing requires autodoc); Error = a generated artifact of the module could not be parsed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutodocUsage { Configured, Partial, NotConfigured, Error }

/// <summary>One detected module/project of the snapshot and its autodoc state. The inventory comes from Source Analysis (never from autodoc folders).</summary>
public sealed record AutodocModuleInventory
{
    public string Name { get; init; } = "";
    /// <summary>Stable identity: the architecture component id, else the project path.</summary>
    public string Identity { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    /// <summary>"REST API", "GraphQL API", "REST/GraphQL API", "API", "Worker/service", "Web/UI", "Library", "Unknown".</summary>
    public string ModuleType { get; init; } = "Unknown";
    public string TypeBasis { get; init; } = "";
    public bool ApiCapable { get; init; }
    public AutodocUsage Usage { get; init; } = AutodocUsage.NotConfigured;
    public string UsageReason { get; init; } = "";
    public bool GeneratedOutputPresent { get; init; }
    public int ArtifactCount { get; init; }
    /// <summary>The existing generated-document classification of this module's artifacts (types present), never a second classifier.</summary>
    public List<GeneratedDocumentKind> ArtifactKinds { get; init; } = [];
    public string? GeneratedModuleId { get; init; }
    public string? OutputPath { get; init; }
    /// <summary>The generator name only when a workflow names it; "Unknown generator" when output exists without one; null when not configured.</summary>
    public string? Generator { get; init; }
    public List<string> WorkflowSources { get; init; } = [];
    public GeneratedDocumentationFreshnessStatus Freshness { get; init; } = GeneratedDocumentationFreshnessStatus.NotApplicable;
    /// <summary>Health of the module's generated documentation; null when not configured (neutral, never unhealthy).</summary>
    public GeneratedDocumentationHealthStatus? Health { get; init; }
    public int ParseErrors { get; init; }
    public int MissingExpectedDocs { get; init; }
    public int DiscrepancyCandidates { get; init; }
    /// <summary>Only from trustworthy dated evidence; never the archive import time.</summary>
    public DatedEvidence? LastGenerated { get; init; }
    /// <summary>True when the row is a generated-documentation module no Source Analysis project/component matched (kept visible, type Unknown).</summary>
    public bool FromGeneratedDocumentationOnly { get; init; }
}

public sealed record AutodocDiagnostics
{
    /// <summary>A repository-level maintain-docs workflow (skill/hook) exists. Never implies module usage.</summary>
    public bool MaintainDocsWorkflowDetected { get; init; }
    public List<string> WorkflowSources { get; init; } = [];
    /// <summary>"Source Analysis architecture components" or "Source Analysis projects".</summary>
    public string InventoryBasis { get; init; } = "";
    public int DetectedModules { get; init; }
    public int ApiModules { get; init; }
    public int ModulesUsingAutodoc { get; init; }
    public int ModulesWithoutAutodoc { get; init; }
    public int ModulesPartial { get; init; }
    public int ApisUsingAutodoc { get; init; }
    public int ApisWithoutAutodoc { get; init; }
    public int GeneratedArtifacts { get; init; }
    public int Fresh { get; init; }
    public int PotentiallyStale { get; init; }
    public int FreshnessUnknown { get; init; }
    public int MissingExpectedDocs { get; init; }
    public int DiscrepancyCandidates { get; init; }
    public int ParseErrors { get; init; }
    public List<AutodocModuleInventory> Modules { get; init; } = [];
    /// <summary>Repository-root generated documentation: shown separately, never a module or API in coverage.</summary>
    public List<string> RepositoryLevelDocuments { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public static class GeneratedDocumentationText
{
    public const string AutodocDisclaimer = "Autodoc diagnostics shows coverage and health of generated documentation. It does not determine source correctness, runtime behavior, or whether a module must use autodoc.";

    public static string Label(AutodocUsage usage) => usage switch
    {
        AutodocUsage.Configured => "Configured",
        AutodocUsage.Partial => "Partial",
        AutodocUsage.Error => "Error",
        _ => "Not configured",
    };

    public const string Description = "Checks whether generated (implementation-derived) documentation is detected, traceable to its generation workflow, fresh relative to relevant same-scope source evidence and complete against declared requirements — and, separately, whether it is consistent with the available evidence.";
    public const string NoGeneratorEstablished = "Generated documentation detected · generator not established";
    public const string ConsistencyBoundary = "Consistency results are diagnostics: a review candidate needs two comparable claims that conflict. Unresolved correlation, source-only items and unable-to-compare are not drift, not findings and do not change health status.";
    public const string UnresolvedConfigurationKey = "The generated documentation names this configuration key, but BirkNext could not establish a corresponding source declaration/read in the current analysis scope.";
    public const string EmptyState = "No generated documentation detected in the current source snapshot.";
    public const string NoGenerator = "Generated documentation detected. Generator provenance could not be established.";
    /// <summary>Legacy wording; use <see cref="Label(GeneratedConsistencyCategory)"/>.</summary>
    public const string ReviewStatus = "Potential inconsistency – review suggested";
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
        GeneratedDocumentationHealthStatus.Warning => "Review suggested",
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
        CrossArtifactDriftType.DocumentationDrift => "Source consistency (generated vs source)",
        CrossArtifactDriftType.SpecificationDrift => "Authored/generated consistency · Specification",
        CrossArtifactDriftType.ConstitutionDrift => "Authored/generated consistency · Constitution",
        CrossArtifactDriftType.PlanDrift => "Authored/generated consistency · Plan",
        _ => "Cross-artifact consistency · Tasks",
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
        GeneratedComparisonState.Consistent => "Consistent with available evidence",
        GeneratedComparisonState.PotentialInconsistency => "Potential inconsistency",
        GeneratedComparisonState.SourceCorrelationUnresolved => "Source correlation unresolved",
        GeneratedComparisonState.PotentiallyDrifted => "Review candidates",
        GeneratedComparisonState.NotApplicable => "Not applicable",
        GeneratedComparisonState.NotAssessed => "Not assessed",
        _ => "Unable to compare",
    };

    public static string Label(GeneratedConsistencyCategory category, CrossArtifactDriftType type = CrossArtifactDriftType.DocumentationDrift) => category switch
    {
        GeneratedConsistencyCategory.PotentialInconsistency => "Potential inconsistency",
        GeneratedConsistencyCategory.SourceCorrelationUnresolved => type == CrossArtifactDriftType.DocumentationDrift ? "Source correlation unresolved" : "Correlation unresolved",
        GeneratedConsistencyCategory.SourceOnly => "Source only — not named in generated documentation",
        _ => "Unable to compare",
    };

    public static string Label(ExpectedDocumentState state) => state switch
    {
        ExpectedDocumentState.RequiredPresent => "Required and present",
        ExpectedDocumentState.RequiredMissing => "Required and missing",
        ExpectedDocumentState.ExpectedByProfile => "Applicable / expected by profile",
        ExpectedDocumentState.ApplicabilityNotEstablished => "Applicability not established",
        ExpectedDocumentState.Optional => "Optional",
        ExpectedDocumentState.NotApplicable => "Not applicable",
        _ => "Unknown",
    };

    public static string Label(GeneratedDocumentationReviewState state) => state switch
    {
        GeneratedDocumentationReviewState.ReviewRequired => "Review required",
        GeneratedDocumentationReviewState.ReviewSuggested => "Review suggested",
        _ => "No review suggested",
    };
}

// ── Health vs consistency semantics (one interpretation for the analyzer, the diagnostic, Source Analysis and AI review) ─────────
// Applied when evidence is produced AND when a stored snapshot is read, so snapshots analyzed under earlier rules are interpreted the same
// way without re-analysis. Idempotent and pure: no parser, no store, no policy beyond what workflow files declare.

public static class GeneratedDocumentationSemantics
{
    /// <summary>The basis archive entry times carry. A ZIP/copy time is supporting context only — never freshness evidence.</summary>
    public const string ArchiveTimeBasis = "Archive modification time";

    /// <summary>Families that compare two complete contracts: any difference is a conflict of comparable claims.</summary>
    private static readonly HashSet<string> ContractFamilies = new(StringComparer.Ordinal) { "API contract", "GraphQL schema", "Specification contract" };

    /// <summary>
    /// A review candidate needs two comparable claims that conflict: a value/version/structure mismatch, or a difference between two complete
    /// contracts (including a source route a generated API contract omits). A claim with no counterpart on the other side is an unresolved
    /// correlation (the source may bind it indirectly or BirkNext may not read the pattern); a source item generated prose does not name is
    /// source-only. Neither is drift.
    /// </summary>
    public static GeneratedConsistencyCategory Classify(CrossArtifactDriftCandidate candidate) => candidate.Consistency ?? candidate.DifferenceKind switch
    {
        DriftDifferenceKind.UnableToCompare => GeneratedConsistencyCategory.UnableToCompare,
        DriftDifferenceKind.ValueMismatch or DriftDifferenceKind.VersionMismatch or DriftDifferenceKind.StructureMismatch => GeneratedConsistencyCategory.PotentialInconsistency,
        _ when ContractFamilies.Contains(candidate.Family) => GeneratedConsistencyCategory.PotentialInconsistency,
        DriftDifferenceKind.MissingOnLeft when candidate.DriftType == CrossArtifactDriftType.DocumentationDrift && candidate.Family == "HTTP routes" => GeneratedConsistencyCategory.PotentialInconsistency,
        DriftDifferenceKind.MissingOnLeft when candidate.DriftType == CrossArtifactDriftType.DocumentationDrift => GeneratedConsistencyCategory.SourceOnly,
        _ => GeneratedConsistencyCategory.SourceCorrelationUnresolved,
    };

    public static bool IsReviewCandidate(CrossArtifactDriftCandidate candidate) => Classify(candidate) == GeneratedConsistencyCategory.PotentialInconsistency;

    /// <summary>Specification, Constitution and Plan vs generated documentation (Tasks are the cross-artifact delivery family).</summary>
    public static bool IsAuthoredGenerated(CrossArtifactDriftType type) => type is CrossArtifactDriftType.SpecificationDrift or CrossArtifactDriftType.ConstitutionDrift or CrossArtifactDriftType.PlanDrift;

    public static CrossArtifactDriftCandidate Classified(CrossArtifactDriftCandidate candidate)
    {
        var category = Classify(candidate);
        return candidate with { Consistency = category, ReviewRequired = category == GeneratedConsistencyCategory.PotentialInconsistency };
    }

    /// <summary>Expected only with an explicit requirement (A/B/D: a workflow declares it required) or a profile rule mapping a capability to it plus
    /// that capability's evidence (C). What a generator can produce is never, alone, a missing document.</summary>
    public static ExpectedGeneratedDocument Expected(ExpectedGeneratedDocument document, IReadOnlyList<GeneratorWorkflowEvidence> generators, IReadOnlyCollection<string> capabilities)
    {
        bool Names(string file) => string.Equals(file, document.FileName, StringComparison.OrdinalIgnoreCase);
        var required = generators.Where(g => g.RequiredOutputFiles.Any(Names)).Select(g => g.Path).Distinct(StringComparer.Ordinal).ToList();
        if (required.Count > 0)
            return document with
            {
                State = document.Present ? ExpectedDocumentState.RequiredPresent : ExpectedDocumentState.RequiredMissing,
                StateBasis = $"Declared required by {string.Join(", ", required)}.",
            };
        var rules = generators.SelectMany(g => g.ConditionalOutputFiles.Select(r => r.Split('|')).Where(p => p.Length == 2 && Names(p[0])).Select(p => (g.Path, Capability: p[1]))).ToList();
        if (rules.Count > 0)
        {
            var applicable = rules.FirstOrDefault(r => capabilities.Contains(r.Capability));
            return applicable.Path is not null
                ? document with { State = ExpectedDocumentState.ExpectedByProfile, StateBasis = $"{applicable.Path} maps {applicable.Capability} to {document.FileName}; the module shows {applicable.Capability} evidence." }
                : document with
                {
                    State = ExpectedDocumentState.NotApplicable,
                    StateBasis = $"{string.Join(", ", rules.Select(r => r.Path).Distinct(StringComparer.Ordinal))} produces {document.FileName} only for {string.Join(", ", rules.Select(r => r.Capability).Distinct(StringComparer.Ordinal))}; the module shows no such evidence.",
                };
        }
        return document with
        {
            State = document.Present ? ExpectedDocumentState.Optional : ExpectedDocumentState.ApplicabilityNotEstablished,
            StateBasis = document.Present ? "Present · the generator supports this document type; no requirement is declared."
                : "Generator supports this document type · applicability not established" + (document.CapabilityPresent
                    ? " (the module shows related capability evidence, but no generator/profile rule or policy requires this document)." : "."),
        };
    }

    /// <summary>Counts toward "Required docs missing": an established requirement or profile applicability, and absent.</summary>
    public static bool CountsAsMissing(ExpectedGeneratedDocument document) =>
        document.State == ExpectedDocumentState.RequiredMissing || document.State == ExpectedDocumentState.ExpectedByProfile && !document.Present;

    /// <summary>
    /// Potentially stale needs a trustworthy generation time AND later scope-relevant source evidence. Archive (ZIP/copy) times establish neither,
    /// and a different declared commit does not show that source relevant to the document changed: both become "Not enough evidence".
    /// </summary>
    public static GeneratedDocumentationEvidence Freshness(GeneratedDocumentationEvidence document)
    {
        if (document.FreshnessStatus is not (GeneratedDocumentationFreshnessStatus.Current or GeneratedDocumentationFreshnessStatus.Stale)) return document;
        var commitOnly = document.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale && document.SourceLastModified is null
            && document.FreshnessReason.StartsWith("Declares source commit", StringComparison.Ordinal);
        var archiveGenerated = document.SourceLastModified is not null && document.LastModified?.Basis == ArchiveTimeBasis;
        var archiveSource = document.SourceLastModified?.Basis == ArchiveTimeBasis;
        if (!commitOnly && !archiveGenerated && !archiveSource) return document;
        var reason = commitOnly ? $"{document.FreshnessReason} A different commit does not show that source relevant to this document changed. Not stale — not established."
            : archiveGenerated ? "The generation time comes only from an archive modification time (ZIP/copy time), which is not trustworthy freshness evidence. Not stale — not established."
            : "Only archive modification times date this scope's source (ZIP/copy time, not scope-relevant change evidence). Not stale — not established.";
        return document with { FreshnessStatus = GeneratedDocumentationFreshnessStatus.NotEnoughEvidence, FreshnessReason = reason, SourceLastModified = archiveSource ? null : document.SourceLastModified };
    }

    /// <summary>Module freshness from its own generated documents (history records and documents of unknown origin are not assessed).</summary>
    public static (GeneratedDocumentationFreshnessStatus Status, string Reason) ModuleFreshness(IReadOnlyList<GeneratedDocumentationEvidence> documents)
    {
        var assessed = documents.Where(e => e.Origin == DocumentationOrigin.Generated && e.FreshnessStatus != GeneratedDocumentationFreshnessStatus.NotApplicable).ToList();
        var status = assessed.Count == 0 ? GeneratedDocumentationFreshnessStatus.NotApplicable
            : assessed.Any(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale) ? GeneratedDocumentationFreshnessStatus.Stale
            : assessed.All(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Current) ? GeneratedDocumentationFreshnessStatus.Current
            : assessed.Any(e => e.FreshnessStatus is GeneratedDocumentationFreshnessStatus.Current or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence) && assessed.All(e => e.LastModified is not null)
                ? GeneratedDocumentationFreshnessStatus.NotEnoughEvidence : GeneratedDocumentationFreshnessStatus.Unknown;
        var stale = assessed.Where(e => e.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale).ToList();
        var reason = status switch
        {
            GeneratedDocumentationFreshnessStatus.Stale => $"{stale.Count} generated document(s) predate relevant source evidence of this scope: {string.Join("; ", stale.Take(3).Select(e => $"{System.IO.Path.GetFileName(e.SafeRelativePath)} ({e.FreshnessReason})"))}",
            GeneratedDocumentationFreshnessStatus.Current => "Every assessed generated document is at least as new as the relevant dated source evidence of this scope.",
            GeneratedDocumentationFreshnessStatus.NotEnoughEvidence => $"Freshness could not be established from trustworthy evidence: generation dates are known, but this scope has no trustworthy, relevant dated source evidence for {assessed.Count(e => e.FreshnessStatus != GeneratedDocumentationFreshnessStatus.Current)} document(s). Not stale — not established.",
            GeneratedDocumentationFreshnessStatus.NotApplicable => "No generated document with a source scope to compare (history records and documents of unknown origin are not assessed).",
            _ => "The generation time of at least one document could not be established (no declared date, and archive times are not trustworthy).",
        };
        return (status, reason);
    }

    /// <summary>Reclassifies a legacy "potentially drifted" family from its candidates; other states are already final.</summary>
    public static GeneratedComparison Reclassify(GeneratedComparison comparison, IReadOnlyList<CrossArtifactDriftCandidate> candidates)
    {
        if (comparison.State != GeneratedComparisonState.PotentiallyDrifted) return comparison;
        var family = candidates.Where(d => d.Family == comparison.Family || d.Family + "s" == comparison.Family).Select(d => (Candidate: d, Category: Classify(d))).ToList();
        var inconsistent = family.Count(x => x.Category == GeneratedConsistencyCategory.PotentialInconsistency);
        var unresolved = family.Count(x => x.Category is GeneratedConsistencyCategory.SourceCorrelationUnresolved or GeneratedConsistencyCategory.UnableToCompare);
        var sourceOnly = family.Count(x => x.Category == GeneratedConsistencyCategory.SourceOnly);
        var authored = family.Any(x => x.Candidate.DriftType != CrossArtifactDriftType.DocumentationDrift);
        var state = inconsistent > 0 ? GeneratedComparisonState.PotentialInconsistency
            : unresolved > 0 ? GeneratedComparisonState.SourceCorrelationUnresolved : GeneratedComparisonState.Consistent;
        var detail = comparison.Detail + string.Concat(
            inconsistent > 0 ? $" {inconsistent} comparable difference(s) — review candidate(s)." : "",
            unresolved > 0 ? comparison.Family == "Configuration keys"
                ? $" {unresolved} generated configuration reference(s) could not be deterministically correlated with source evidence (options/section binding, environment variables, Key Vault, App Configuration or shared configuration may supply them)."
                : $" {unresolved} generated reference(s) could not be deterministically correlated with {(authored ? "the compared artifact" : "source evidence")} in the current analysis scope." : "",
            sourceOnly > 0 ? $" {sourceOnly} source item(s) not named in generated documentation — not a conflict." : "");
        return comparison with { State = state, Detail = detail, Inconsistencies = inconsistent, Unresolved = unresolved, SourceOnly = sourceOnly };
    }

    /// <summary>Applies these semantics to a snapshot (stored under any earlier rules). Inventory, provenance, identities and fingerprints are unchanged.</summary>
    public static GeneratedDocumentationSnapshot Reinterpret(GeneratedDocumentationSnapshot generated)
    {
        var documents = generated.Documents.Select(Freshness).ToList();
        var drift = generated.Drift.Select(Classified).ToList();
        var modules = generated.Modules.Select(m => Reinterpret(m, documents.Where(d => d.ModuleId == m.ModuleId).ToList(), generated.Generators,
            drift.Where(d => d.ModuleId == m.ModuleId).ToList())).ToList();
        return generated with { Documents = documents, Drift = drift, Modules = modules };
    }

    public static GeneratedDocumentationModule Reinterpret(GeneratedDocumentationModule module, IReadOnlyList<GeneratedDocumentationEvidence> documents,
        IReadOnlyList<GeneratorWorkflowEvidence> generators, IReadOnlyList<CrossArtifactDriftCandidate> candidates)
    {
        var (freshness, reason) = documents.Count == 0 ? (module.Freshness, module.FreshnessReason) : ModuleFreshness(documents);
        var archiveSource = module.LatestSourceEvidence is { Basis: ArchiveTimeBasis } archive ? archive : null;
        var declaring = module.GeneratorIds.Select(id => generators.FirstOrDefault(g => g.Id == id)).OfType<GeneratorWorkflowEvidence>().ToList();
        var expected = module.ExpectedDocuments.Select(e => Expected(e, declaring, module.SourceKeys.Capabilities)).ToList();
        return module with
        {
            Freshness = freshness, FreshnessReason = reason,
            LatestSourceEvidence = archiveSource is null ? module.LatestSourceEvidence : null,
            SupportingFreshnessSignals = archiveSource is null ? module.SupportingFreshnessSignals
                : [.. module.SupportingFreshnessSignals, ArchiveSignal(archiveSource)],
            ExpectedDocuments = expected,
            MissingExpectedDocs = [.. expected.Where(CountsAsMissing).Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            Comparisons = [.. module.Comparisons.Select(c => Reclassify(c, candidates))],
        };
    }

    public static string ArchiveSignal(DatedEvidence archive) =>
        $"Latest archive modification time in this scope {archive.At.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} ({archive.Path}) — supporting only, not freshness evidence.";

    /// <summary>Applicability not established: declared by a generator, absent, without requirement or profile rule. Neutral.</summary>
    public static IEnumerable<string> ApplicabilityUnresolved(GeneratedDocumentationModule module) =>
        module.ExpectedDocuments.Where(e => e.State == ExpectedDocumentState.ApplicabilityNotEstablished).Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase);
}
