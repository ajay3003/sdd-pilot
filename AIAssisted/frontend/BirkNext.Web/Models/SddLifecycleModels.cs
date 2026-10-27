namespace BirkNext.Web.Models;

/// <summary>Persisted, deterministic SDD lifecycle metadata for one workspace.</summary>
public sealed class SddLifecycleState
{
    public int SchemaVersion { get; set; } = 1;
    public List<SddArtifactRevision> Revisions { get; set; } = [];
    public List<SddClarification> Questions { get; set; } = [];
    public List<SddDecision> Decisions { get; set; } = [];
    public List<SddTraceabilityLink> Links { get; set; } = [];
    public List<SddImplementationEvidence> ImplementationEvidence { get; set; } = [];
    public List<SddTestEvidence> TestEvidence { get; set; } = [];
    public List<SddTestExecutionEvidence> TestExecutions { get; set; } = [];
    public List<SddTestDefinitionEvidence> TestDefinitions { get; set; } = [];
    public List<SddTestResultArtifact> TestResultArtifacts { get; set; } = [];
    public List<SddTestRunEvidence> TestRuns { get; set; } = [];
    public List<SddSourceSnapshotReference> SourceSnapshots { get; set; } = [];
    public List<SddQualityReviewRun> QualityReviewRuns { get; set; } = [];
    public List<SddReviewRun> ReviewRuns { get; set; } = [];
    public List<SddRequirementSnapshot> RequirementSnapshots { get; set; } = [];
    public List<SddRequirementChange> RequirementChanges { get; set; } = [];
    public List<SddBaselineManifest> Baselines { get; set; } = [];
    public List<SddExplorerSelection> ExplorerSelections { get; set; } = [];
    /// <summary>Which module (Spec-Kit sub-project) reviews and explorers focus on, per workspace scope. Only used when the scope has several modules.</summary>
    public List<SddModuleFocus> ModuleFocus { get; set; } = [];
    /// <summary>Project archives imported into this workspace (one record per import identity, i.e. per exact archive). History only.</summary>
    public List<SddProjectImportRecord> ProjectImports { get; set; } = [];
    /// <summary>The imported project the workspace currently shows (artifact scope <c>import:{id}</c>), or null. Mutually exclusive with a selected Sample Project.</summary>
    public string? CurrentProjectImportId { get; set; }
    /// <summary>One Generated Documentation Set per Project Import: references to the generated documents of the import's source snapshot.</summary>
    public List<SddGeneratedDocumentationSet> GeneratedDocumentationSets { get; set; } = [];
    /// <summary>Generated-document text keyed by content fingerprint: stored once however many imports carry the same document.</summary>
    public Dictionary<string, string> GeneratedDocumentContents { get; set; } = [];
    /// <summary>Saved Documentation Drift comparisons: exact references and the result as computed (immutable).</summary>
    public List<SddDocumentationDriftComparison> DocumentationDriftComparisons { get; set; } = [];
    /// <summary>The artifact pack explorers and reviews currently view, per scope and module. Navigation only — never authority.</summary>
    public List<SddPackFocus> PackFocus { get; set; } = [];
    /// <summary>
    /// The requirement scope (workspace|module|pack) of the viewed artifact pack, or null when the module is not organised in several packs.
    /// Requirements, links and evidence recorded now carry it; only items of this scope apply. Follows the pack focus; never authority.
    /// </summary>
    public string? ActiveRequirementScope { get; set; }
}

/// <summary>Which feature pack of a module is being viewed (a pack folder such as <c>002-testcontainers</c>). Viewing is not choosing an authority.</summary>
public sealed class SddPackFocus
{
    public string? WorkspaceScope { get; set; }
    public string Module { get; set; } = "";
    public string Pack { get; set; } = "";
    public DateTimeOffset? ViewedAt { get; set; }
}

/// <summary>
/// The generated documentation that arrived with one Project Import (a logical set, not an independent snapshot): the import revision, the
/// source snapshot whose generated-documentation evidence identified the documents, and one reference per document. Present = documents were
/// identified; NotPresent = the snapshot was evaluated and holds none; NotEvaluated = no snapshot (no source, Source Analysis off, failed).
/// An earlier import's set is never reused for a later import.
/// </summary>
public sealed class SddGeneratedDocumentationSet
{
    public string SetId { get; set; } = "";
    public string ProjectImportId { get; set; } = "";
    public Guid? SourceSnapshotId { get; set; }
    public string State { get; set; } = "NotEvaluated";
    public List<SddGeneratedDocumentArtifact> Artifacts { get; set; } = [];
}

/// <summary>A generated document of a set: module, path, kind, fingerprint, generator (only when proven). Text lives in the content store by fingerprint.</summary>
public sealed class SddGeneratedDocumentArtifact
{
    public string ModuleId { get; set; } = "";
    public string ModuleRoot { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? Generator { get; set; }
    /// <summary>False when the import did not carry the document text (e.g. a retried source part): the reference stays, comparison is unavailable.</summary>
    public bool ContentCaptured { get; set; }
}

/// <summary>One side of a saved Documentation Drift comparison: exact identity as it was compared.</summary>
public sealed class SddDriftSide
{
    public string DocumentId { get; set; } = "";
    public string Origin { get; set; } = "";
    public string LogicalKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid? RevisionId { get; set; }
    /// <summary>The artifact revision number as compared ("rev 2"); null for generated documents and comparisons saved before it was kept.</summary>
    public int? RevisionNumber { get; set; }
    public string? ProjectImportId { get; set; }
    /// <summary>The import label as compared ("M2LB · Import 2"); comparisons saved before lineages show their global "R4".</summary>
    public string? ProjectRevision { get; set; }
    /// <summary>The project lineage of the import as compared; null for manual documents and comparisons saved before lineages.</summary>
    public string? LineageKey { get; set; }
    public string? GeneratedSetId { get; set; }
    public Guid? SourceSnapshotId { get; set; }
    public string? Generator { get; set; }
}

public sealed class SddDriftItem
{
    public string Kind { get; set; } = "";
    public string Section { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Left { get; set; } = [];
    public List<string> Right { get; set; } = [];
}

public sealed class SddDocumentationDriftComparison
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public string Intent { get; set; } = "";
    public string Scope { get; set; } = "Whole document";
    public string RevisionContext { get; set; } = "";
    public SddDriftSide Left { get; set; } = new();
    public SddDriftSide Right { get; set; } = new();
    public string Outcome { get; set; } = "";
    public Dictionary<string, int> Counts { get; set; } = [];
    public List<SddDriftItem> Items { get; set; } = [];
    public int AnalysisVersion { get; set; }
    public string? WorkspaceScope { get; set; }
    /// <summary>Composite comparisons: every authored contributor exactly as compared (ids + fingerprints). Empty for two-document comparisons.</summary>
    public List<SddDriftSide> Contributors { get; set; } = [];
    public string? Module { get; set; }
    public string? Concern { get; set; }
    /// <summary>"Text" (section diff) or "StructuredDataModel". Null for comparisons saved before structured comparison existed (= Text).</summary>
    public string? ComparisonKind { get; set; }
    public string? CompositeFingerprint { get; set; }
    public int? AlgorithmVersion { get; set; }
}

/// <summary>
/// One Project Import as the artifact repository records it: which archive, when, which documents became artifacts, and which Source Analysis
/// snapshot the same import created. Provenance only — the snapshot itself is owned by Source Analysis, the artifacts by the revisions.
/// </summary>
public sealed class SddProjectImportRecord
{
    public string ImportId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    /// <summary>ArchiveRoot or ArchiveFileName: how <see cref="ProjectName"/> was derived.</summary>
    public string ProjectNameBasis { get; set; } = "";
    public string ArchiveFileName { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
    public long ArchiveSizeBytes { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    /// <summary>Roles with exactly one detected document (selected on import).</summary>
    public List<string> DetectedRoles { get; set; } = [];
    /// <summary>Roles with several detected documents: nothing is selected until the user chooses.</summary>
    public List<string> AmbiguousRoles { get; set; } = [];
    public int ArtifactDocumentCount { get; set; }
    /// <summary>NotDetected, Created, Reused, NotCreated or Failed (ProjectImportSourceState).</summary>
    public string SourceState { get; set; } = "";
    public Guid? SourceSnapshotId { get; set; }
    public DateTimeOffset? SourceAnalyzedAt { get; set; }
    /// <summary>Why no snapshot was created (NotCreated/Failed), as the backend stated it. Never a stack trace.</summary>
    public string? SourceMessage { get; set; }
    /// <summary>The staged archive that a failed source part can be retried from, while it is kept (<see cref="SourceStagedUntil"/>).</summary>
    public Guid? SourceStagingId { get; set; }
    public DateTimeOffset? SourceStagedUntil { get; set; }
}

/// <summary>Immutable manifest of explicit artifact revision references; content remains owned by Revisions.</summary>
public sealed class SddBaselineManifest
{
    public Guid BaselineId { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "Baseline";
    public List<Guid> ArtifactRevisionIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "Current";
    public DateTimeOffset? SupersededAt { get; set; }
}

public enum LifecycleProjectionMode { ActiveWorkspace, AuthoritativeBaseline, HistoricalBaseline }
public sealed record LifecycleProjectionMetadata(LifecycleProjectionMode Mode, Guid? BaselineId,
    IReadOnlyList<Guid> ArtifactRevisionIds, IReadOnlyList<string> ArtifactFingerprints, DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Limitations);

public sealed class SddSourceTargetResolution
{
    public string OldSnapshotId { get; set; } = "";
    public string NewSnapshotId { get; set; } = "";
    public string OldFingerprint { get; set; } = "";
    public string NewFingerprint { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string State { get; set; } = "NotAssessed";
    public string TargetPath { get; set; } = "";
    public string? MatchedPath { get; set; }
    public bool? ContentChanged { get; set; }
    public DateTimeOffset ResolvedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
}

public sealed class SddSourceSnapshotReference
{
    public string SnapshotId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string EnvironmentReference { get; set; } = "";
    public string AnalysisStatus { get; set; } = "Unknown";
    public string AnalyzerVersion { get; set; } = "";
    public DateTimeOffset AnalyzedAt { get; set; }
    public List<string> Limitations { get; set; } = [];
    public string Currentness { get; set; } = "Historical";
    public BirkNext.Integrations.SourceTargetIndex? TargetIndex { get; set; }
}

public sealed class SddQualityReviewRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public string LifecycleGraphFingerprint { get; set; } = "";
    public string Currentness { get; set; } = "Current";
    public List<SddQualityReviewFinding> Findings { get; set; } = [];
    public string ProjectionMode { get; set; } = "ActiveWorkspace";
    public Guid? BaselineId { get; set; }
    public List<Guid> ArtifactRevisionIds { get; set; } = [];
    public List<string> ArtifactFingerprints { get; set; } = [];
    /// <summary>The exact inputs the review read (role, path, pack, scope, fingerprint); empty for runs saved before packs.</summary>
    public List<SddReviewInput> Inputs { get; set; } = [];
    /// <summary>The requirement scope (workspace|module|pack) the run's findings belong to; null without packs.</summary>
    public string? RequirementScope { get; set; }
}

/// <summary>
/// One artifact a review read, as it was read: never re-resolved from a later selection. <see cref="Scope"/> is PackLocal, ModuleShared or
/// ProjectShared.
/// </summary>
public sealed class SddReviewInput
{
    public string Role { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Module { get; set; }
    public string? Pack { get; set; }
    public string Scope { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

public sealed record SddQualityReviewFinding(string Code, string Severity, string RequirementId, string Message, string EvidenceReference);

public sealed class SddRequirementSnapshot
{
    public string RequirementId { get; set; } = "";
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
    public string Fingerprint { get; set; } = "";
    public string AcceptanceCriteriaFingerprint { get; set; } = "";
    public List<string> AcceptanceCriterionIds { get; set; } = [];
    public Guid? ArtifactRevisionId { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsCurrent { get; set; }
}

public sealed class SddRequirementChange
{
    public string RequirementId { get; set; } = "";
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
    public string Change { get; set; } = "NeedsReview";
    public string PreviousFingerprint { get; set; } = "";
    public string CurrentFingerprint { get; set; } = "";
    public string ChangeDetail { get; set; } = "";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SddArtifactRevision
{
    public Guid RevisionId { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "Other";
    public string FileName { get; set; } = "";
    public string? SourceReference { get; set; }
    public string Content { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Revision { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Authority { get; set; } = "Unknown";
    public bool IsCurrentSelection { get; set; }
    public Guid? SupersedesRevisionId { get; set; }
    public Guid? SupersededByRevisionId { get; set; }
    /// <summary>Project the revision belongs to: a Sample Project slug, or null for the manual workspace (no project selected).</summary>
    public string? WorkspaceScope { get; set; }
    /// <summary>How the content arrived: File, Drop or Paste for explorer imports, ProjectImport for a project archive; null for older or programmatic captures.</summary>
    public string? Origin { get; set; }
    /// <summary>The Project Import that captured this revision (shared provenance with its source snapshot); null otherwise.</summary>
    public string? ProjectImportId { get; set; }
}

/// <summary>
/// Which artifact an explorer shows for a role in a workspace scope when several are available. A viewing choice only:
/// it never changes authority or baseline.
/// </summary>
public sealed class SddExplorerSelection
{
    public string? WorkspaceScope { get; set; }
    public string Role { get; set; } = "";
    public string ArtifactId { get; set; } = "";
    /// <summary>
    /// The module the selection applies to (a module root path, "" for project-level). Null in selections saved before module scoping,
    /// which are read as project-level. Selecting for one module never changes another module's selection.
    /// </summary>
    public string? Module { get; set; }
    /// <summary>The feature pack the selected artifact belongs to; null for shared artifacts and for selections saved before packs.</summary>
    public string? Pack { get; set; }
    /// <summary>When the selection was made (history only; null for older selections).</summary>
    public DateTimeOffset? SelectedAt { get; set; }
}

/// <summary>The module a workspace scope's reviews focus on. A viewing scope only: it never changes authority, selection or source snapshots.</summary>
public sealed class SddModuleFocus
{
    public string? WorkspaceScope { get; set; }
    /// <summary>Module root path, or "" for the project-level group.</summary>
    public string Module { get; set; } = "";
}

public sealed class SddClarification
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string? Owner { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionReference { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public List<string> RequirementIds { get; set; } = [];
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
}

public sealed class SddDecision
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Proposed";
    public string? SourceReference { get; set; }
    public List<string> ResolvesQuestionIds { get; set; } = [];
    public List<string> AffectedRequirementIds { get; set; } = [];
    public List<string> SupersedesDecisionIds { get; set; } = [];
}

public sealed class SddTraceabilityLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Relationship { get; set; } = "References";
    public string Confidence { get; set; } = "Unresolved";
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedAt { get; set; }
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
}

public sealed class SddImplementationEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequirementId { get; set; } = "";
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
    public string EvidenceType { get; set; } = "ManualEvidence";
    public string Reference { get; set; } = "";
    public string? SourceSnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public string? SourceEvidenceId { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderVersion { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
    public string? FilePath { get; set; }
    public string SourceValidation { get; set; } = "NotAssessed";
    public string? StableKey { get; set; }
    public string? CurrentnessReason { get; set; }
    public string Confidence { get; set; } = "Unresolved";
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
    public List<SddSourceTargetResolution> TargetResolutions { get; set; } = [];
    public string TargetValidation { get; set; } = "NotAssessed";
}

/// <summary>Immutable execution observation imported from a test provider or report.</summary>
public sealed class SddTestExecutionEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public List<string> RequirementReferences { get; set; } = [];
    public List<string> AcceptanceCriterionReferences { get; set; } = [];
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
    public string EvidenceKind { get; set; } = "Executed";
    public string ExecutionState { get; set; } = "Unknown";
    public string Result { get; set; } = "Unknown";
    public string ProviderId { get; set; } = "ManualImport";
    public string ResultSource { get; set; } = "";
    public string? ProviderResultId { get; set; }
    public string? EnvironmentReference { get; set; }
    public string? BuildReference { get; set; }
    public string? SourceSnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string Currentness { get; set; } = "Current";
    public string? CurrentnessReason { get; set; }
    public string Fingerprint { get; set; } = "";
    // Result-artifact provenance (set by provider imports such as TRX; null for the generic JSON import).
    public Guid? RunId { get; set; }
    public Guid? ArtifactId { get; set; }
    public string? FullyQualifiedTestName { get; set; }
    public string? DataRowLabel { get; set; }
    public string? ProviderOutcome { get; set; }
    public double? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    /// <summary>Correlated source test definition (only when correlation is Confirmed).</summary>
    public string? TestDefinitionId { get; set; }
    public string CorrelationState { get; set; } = "NotAssessed";
    public string? CorrelationBasis { get; set; }
    /// <summary>Inferred references of the correlated source test that match a current requirement/AC: candidates for review, never links.</summary>
    public List<string> CandidateRequirementReferences { get; set; } = [];
    /// <summary>Provided when the run is bound to a source snapshot by the user; NotAssessed when the source version is unknown.</summary>
    public string SourceCurrentness { get; set; } = "NotAssessed";
}

public sealed class SddTestExecutionImportRecord
{
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public List<string> RequirementReferences { get; set; } = [];
    public List<string> AcceptanceCriterionReferences { get; set; } = [];
    public string ExecutionState { get; set; } = "Completed";
    public string Result { get; set; } = "Unknown";
    public string? ProviderId { get; set; }
    public string? ResultSource { get; set; }
    public string? ProviderResultId { get; set; }
    public string? EnvironmentReference { get; set; }
    public string? BuildReference { get; set; }
    public string? SourceSnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
}

public sealed class SddTestEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequirementId { get; set; } = "";
    /// <summary>Requirement scope (workspace|module|pack) this item belongs to; null without packs (and for items saved before packs).</summary>
    public string? RequirementScope { get; set; }
    public string? AcceptanceCriterionId { get; set; }
    public string TestReference { get; set; } = "";
    public string State { get; set; } = "Unknown";
    public string? Result { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Provenance { get; set; } = "Manual";
    public string Currentness { get; set; } = "Current";
    public string? CurrentnessReason { get; set; }
}

public sealed class SddReviewRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The Artifact Pack scope the run reviewed; a later run supersedes only runs of the same scope. Null for runs before packs.</summary>
    public string? RequirementScope { get; set; }
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public string SpecificationFingerprint { get; set; } = "";
    public string PlanFingerprint { get; set; } = "";
    public string TasksFingerprint { get; set; } = "";
    public string SourceSnapshotReference { get; set; } = "Not available";
    public string SourceFingerprint { get; set; } = "";
    public string LifecycleEvidenceFingerprint { get; set; } = "";
    public string TestEvidenceFingerprint { get; set; } = "";
    public int ReviewVersion { get; set; } = 1;
    public string Currentness { get; set; } = "Current";
    public int RequirementCount { get; set; }
    public int RequirementsWithPlanEvidence { get; set; }
    public int RequirementsWithTaskEvidence { get; set; }
    public string ProjectionMode { get; set; } = "ActiveWorkspace";
    public Guid? BaselineId { get; set; }
    public List<Guid> ArtifactRevisionIds { get; set; } = [];
    public List<string> ArtifactFingerprints { get; set; } = [];
}
