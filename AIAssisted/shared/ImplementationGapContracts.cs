using System.Text.Json.Serialization;

namespace BirkNext.ImplementationGaps;

// ── Implementation gap detection ─────────────────────────────────────────────────────────────────────────────────────────────
// Compares what SDD documents say (tasks, gates, decisions, contracts) with what a source snapshot actually contains (file inventory,
// declared types, declared packages, test definitions) and with recorded test executions. Every dimension is kept apart:
//   task state (the checkbox)  ≠  implementation evidence (source)  ≠  verification evidence (tests, runtime).
// A checked task is never proof of code; an unchecked task never proves code is absent; a package is never proof of behaviour; a test
// definition is never an execution. Nothing here is a percentage score: dimensions carry explicit numerators, denominators and exclusions.
// Source line numbers appear only when the evidence carries them (document lines, Roslyn symbol lines) — never inferred.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapTaskState { NotStarted, InProgress, Completed, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapImplementationEvidence
{
    /// <summary>No source snapshot, the task names no source artifact, or the inventory cannot decide (truncated symbols).</summary>
    NotAssessed,
    /// <summary>The expected artifacts were looked for (exact path, module-relative path, same file name, declared type, package) and none was found.</summary>
    NoMatchingSource,
    /// <summary>Something matching was found, but not where the task says, or only by name: a person must confirm it.</summary>
    CandidateImplementation,
    /// <summary>Some, not all, expected artifacts were found where the task says.</summary>
    PartialImplementation,
    /// <summary>Every expected artifact was found where the task says.</summary>
    ConfirmedSourceImplementation,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapVerificationEvidence { NotAssessed, NotApplicable, TestNotFound, TestDefined, TestExecuted, TestPassed, RuntimeVerified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapGateStatus { Approved, Pending, Rejected, Unknown, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapRelationship { Confirmed, Candidate, Ambiguous, Missing, Unresolved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapReadiness { Blocked, NotImplemented, PartiallyImplemented, ImplementedButNotVerified, Verified, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapArtifactKind { File, Type, Package, Test }

/// <summary>Detection rules A–P of the gap register, plus the archive-scope limitation that keeps "not in this archive" apart from "missing".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapFindingType
{
    TaskFileMissing,                 // A
    TaskTypeMissing,                 // B
    CompletedTaskWithoutImplementation, // C
    OpenTaskWithImplementation,      // D
    PackageMissing,                  // E
    PackageWithoutBehaviour,         // F
    TestMissing,                     // G
    TestNotExecuted,                 // H
    ImplementationWithoutTask,       // I
    BlockedByGate,                   // J
    DecisionNotApproved,             // K
    IntegrationNotImplemented,       // L
    NoRuntimeVerification,           // M
    ArtifactMovedOrRenamed,          // N
    MultipleCandidates,              // O
    InventoryIncomplete,             // P
    ImplementationScopeNotInArchive, // scope limitation: the declared implementation scope is absent from the supplied archive
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapFindingSeverity { High, Medium, Low, Info }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapConfidence { High, Medium, Low }

/// <summary>A place in an input: a document line (1-based) or a source file (line only when the analyzer recorded one).</summary>
public sealed record GapSourceRef(string Path, int? Line = null, string? Note = null);

// ── Extracted SDD inputs (persisted instead of document text) ───────────────────────────────────────────────────────────────

public sealed record GapDocumentRef(string Path, string Sha256, string Role);

public sealed record GapTaskInput
{
    public string TaskId { get; init; } = "";
    public string DocumentPath { get; init; } = "";
    public int Line { get; init; }
    public GapTaskState State { get; init; }
    /// <summary>The task line as written (Markdown removed only where noted); never shortened.</summary>
    public string Text { get; init; } = "";
    public List<string> Continuation { get; init; } = [];
    public string? UserStory { get; init; }
    public string? Phase { get; init; }
    /// <summary>Paths the task's own line names in its module (as written): the expected artifacts.</summary>
    public List<string> Paths { get; init; } = [];
    /// <summary>Paths named as references/templates (another module, continuation bullets): context only, never expected.</summary>
    public List<string> ReferencedPaths { get; init; } = [];
    /// <summary>Types the task says to implement/create/add ("Implement `X`") — the only identifiers that must exist.</summary>
    public List<string> DeclaredTypes { get; init; } = [];
    /// <summary>Other identifiers the task mentions (context for alternative search; never required).</summary>
    public List<string> MentionedIdentifiers { get; init; } = [];
    /// <summary>Package ids the task pins/adds to a project file.</summary>
    public List<string> Packages { get; init; } = [];
    public List<string> RequirementIds { get; init; } = [];
    public List<string> DecisionIds { get; init; } = [];
    /// <summary>Gate ids named by an inline <c>**Gate: …**</c> marker.</summary>
    public List<string> GateMarkers { get; init; } = [];
    /// <summary>Other tasks this task names (e.g. "(after T119)").</summary>
    public List<string> ReferencedTaskIds { get; init; } = [];
    public List<string> ContractPaths { get; init; } = [];
    public bool IsTestTask { get; init; }
    /// <summary>Deferred/optional/future/out-of-scope (by its phase heading or an explicit marker): excluded from readiness and findings.</summary>
    public string? NotApplicableReason { get; init; }
}

public sealed record GapGateInput
{
    /// <summary>The decision id when the gate names one (ADR-028), otherwise the gate's label.</summary>
    public string GateId { get; init; } = "";
    public string Label { get; init; } = "";
    public List<string> DecisionIds { get; init; } = [];
    public List<string> BlockedTaskIds { get; init; } = [];
    public string? Blocks { get; init; }
    public string? Owner { get; init; }
    public GapSourceRef Source { get; init; } = new("");
}

public sealed record GapDecisionEntry(string Id, string? Title, string StatusText, GapGateStatus Status, GapSourceRef Source);

public sealed record GapDocumentSet
{
    public List<GapDocumentRef> Documents { get; init; } = [];
    public List<GapTaskInput> Tasks { get; init; } = [];
    public List<GapGateInput> Gates { get; init; } = [];
    public List<GapDecisionEntry> Decisions { get; init; } = [];
    /// <summary>Decision registers that were read (so "not in the register" can be told from "no register supplied").</summary>
    public List<string> DecisionRegisters { get; init; } = [];
    /// <summary>Titles given to decisions by tasks that draft them (e.g. Draft ADR-028 "Parquet.Net for the Bufdata delivery").</summary>
    public Dictionary<string, string> DecisionTitles { get; init; } = [];
    /// <summary>Requirement ids mentioned per contract document (path → ids).</summary>
    public Dictionary<string, List<string>> ContractRequirements { get; init; } = [];
    /// <summary>Requirement ids defined or mentioned in specification documents.</summary>
    public List<string> SpecifiedRequirementIds { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

// ── Evidence inputs ──────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record GapSymbol(string Name, string? Namespace, string Kind, string File, int Line);
public sealed record GapPackage(string Name, string? Version, string OwnerFile, List<string> ReferencedBy);
public sealed record GapTestDefinition(string ClassName, string MethodName, string FilePath, string FullyQualifiedName);
public sealed record GapProject(string Name, string Path, bool IsTest);

public sealed record GapSourceEvidence
{
    public Guid SnapshotId { get; init; }
    public string ArchiveName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public string? SnapshotFingerprint { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public List<string> Files { get; init; } = [];
    public bool FileInventoryComplete { get; init; }
    public List<GapSymbol> Symbols { get; init; } = [];
    /// <summary>False when the snapshot has no symbol evidence at all (older analyzer): type checks are then Not assessed.</summary>
    public bool SymbolsCaptured { get; init; }
    public bool SymbolsTruncated { get; init; }
    public List<GapPackage> Packages { get; init; } = [];
    public bool PackagesCaptured { get; init; }
    public List<GapTestDefinition> Tests { get; init; } = [];
    public bool TestsCaptured { get; init; }
    public List<GapProject> Projects { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapExecutionOutcome { Passed, Failed, NotExecuted, Unknown }

/// <summary>A recorded execution of one test (imported TRX). Synthetic or real is stated by <see cref="Source"/>; nothing here is inferred.</summary>
public sealed record GapExecutedTest(string FullyQualifiedName, GapExecutionOutcome Outcome, string Source, DateTimeOffset? ExecutedAt = null);

/// <summary>Runtime verification of a task or type (an observed behaviour), recorded elsewhere and referenced here; never derived from static source.</summary>
public sealed record GapRuntimeObservation(string Subject, string Evidence, DateTimeOffset ObservedAt);

// ── Results ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record GapArtifactCheck
{
    public GapArtifactKind Kind { get; init; }
    public string Expected { get; init; } = "";
    public GapRelationship Relationship { get; init; }
    public List<GapSourceRef> Actual { get; init; } = [];
    public string Rule { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed record GapTaskAssessment
{
    public string TaskId { get; init; } = "";
    public GapSourceRef Source { get; init; } = new("");
    public string Text { get; init; } = "";
    public GapTaskState TaskState { get; init; }
    public GapImplementationEvidence Implementation { get; init; }
    public GapVerificationEvidence Verification { get; init; }
    /// <summary>True when the declared implementation scope (e.g. <c>Skole/src</c>) has no file at all in the supplied archive.</summary>
    public bool ScopeAbsentFromArchive { get; init; }
    /// <summary>Where the task's implementation belongs (e.g. "Skole/src/", "Skole/.pipeline/", or the module when it names only types).</summary>
    public List<string> DeclaredScopes { get; init; } = [];
    public List<GapArtifactCheck> Artifacts { get; init; } = [];
    public List<string> GateIds { get; init; } = [];
    public List<string> TestTaskIds { get; init; } = [];
    public List<string> RequirementIds { get; init; } = [];
    public bool IsTestTask { get; init; }
    public string? NotApplicableReason { get; init; }
    public string Explanation { get; init; } = "";
}

public sealed record GapGateAssessment
{
    public string GateId { get; init; } = "";
    public string Label { get; init; } = "";
    public GapGateStatus Status { get; init; }
    public List<string> BlockedTaskIds { get; init; } = [];
    public List<GapSourceRef> Evidence { get; init; } = [];
    public string Reason { get; init; } = "";
}

/// <summary>An evidence-backed dimension: <c>Numerator</c> of <c>Denominator</c>, with what was excluded and why. Null denominator = not assessed.</summary>
public sealed record GapDimension(string Name, int Numerator, int? Denominator, int Excluded, string Basis);

public sealed record GapFeatureAssessment
{
    public string FeatureId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>How the feature was delimited (a gate's blocked tasks, a user story).</summary>
    public string Basis { get; init; } = "";
    public string DocumentPath { get; init; } = "";
    public List<string> TaskIds { get; init; } = [];
    public List<string> ImplementationTaskIds { get; init; } = [];
    public List<string> TestTaskIds { get; init; } = [];
    public List<string> GateIds { get; init; } = [];
    public List<GapSourceRef> Contracts { get; init; } = [];
    public List<string> RequirementIds { get; init; } = [];
    public List<string> ExpectedArtifacts { get; init; } = [];
    public GapReadiness Readiness { get; init; }
    public string Explanation { get; init; } = "";
    public List<GapDimension> Dimensions { get; init; } = [];
    public string SourceImplementation { get; init; } = "";
    public string Tests { get; init; } = "";
    public string RuntimeExecution { get; init; } = "";
    public string DependencyEvidence { get; init; } = "";
}

public sealed record GapRuleRef(string Id, string Description);

public sealed record GapFinding
{
    /// <summary>Stable across re-evaluations of the same project: type + document + task/gate/feature + expected artifact.</summary>
    public string Id { get; init; } = "";
    public GapFindingType Type { get; init; }
    public string Title { get; init; } = "";
    public GapFindingSeverity Severity { get; init; }
    public string? FeatureId { get; init; }
    public string? FeatureName { get; init; }
    public string? TaskId { get; init; }
    public string? GateId { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? ArchiveSha256 { get; init; }
    public string Expected { get; init; } = "";
    public List<GapSourceRef> ActualEvidence { get; init; } = [];
    public List<string> MissingEvidence { get; init; } = [];
    public GapRuleRef Rule { get; init; } = new("", "");
    public GapConfidence Confidence { get; init; }
    /// <summary>True when certainty is insufficient for an automated conclusion.</summary>
    public bool NeedsHumanReview { get; init; }
    /// <summary>Not reviewed, or the current manual decision (Acceptable, IssueFound, NeedsEvidence, NotApplicable, RiskAccepted).</summary>
    public string ReviewStatus { get; init; } = "Not reviewed";
    public string? ReviewedBy { get; init; }
    public string Guidance { get; init; } = "";
    public DateTimeOffset EvaluatedAt { get; init; }
    public List<GapSourceRef> Sources { get; init; } = [];
}

public sealed record GapEvaluation
{
    public Guid Id { get; init; }
    public string ScopeKey { get; init; } = "";
    public string? ProjectImportId { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? ArchiveName { get; init; }
    public string? ArchiveSha256 { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public string RuleSetVersion { get; init; } = "";
    public List<GapDocumentRef> Documents { get; init; } = [];
    public int TaskCount { get; init; }
    public int CompletedTaskCount { get; init; }
    public List<GapTaskAssessment> Tasks { get; init; } = [];
    public List<GapGateAssessment> Gates { get; init; } = [];
    public List<GapFeatureAssessment> Features { get; init; } = [];
    public List<GapFinding> Findings { get; init; } = [];
    public List<string> ExecutionSources { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record GapEvaluationSummary(Guid Id, string ScopeKey, string? ProjectImportId, Guid? SnapshotId, string? ArchiveName, DateTimeOffset EvaluatedAt,
    int Tasks, int Findings, int Features);

public sealed record GapReevaluateRequest
{
    public List<GapExecutedTest> Executions { get; init; } = [];
    public List<GapRuntimeObservation> RuntimeObservations { get; init; } = [];
}

public sealed record GapReviewRequest
{
    public string FindingId { get; init; } = "";
    /// <summary>A <c>ManualReviewDecisionValue</c> name.</summary>
    public string Decision { get; init; } = "";
    public string? Note { get; init; }
}

public static class GapRules
{
    public const string RuleSetVersion = "gap-rules/1";
    public const string ReviewModule = "implementation-gaps";

    public static readonly IReadOnlyDictionary<GapFindingType, GapRuleRef> Rules = new Dictionary<GapFindingType, GapRuleRef>
    {
        [GapFindingType.TaskFileMissing] = new("GAP-A", "A file named by a task is not in the inventory at the stated path, the module-relative path or under the same file name elsewhere."),
        [GapFindingType.TaskTypeMissing] = new("GAP-B", "A type the task says to implement/create is not declared anywhere in the snapshot's symbol inventory."),
        [GapFindingType.CompletedTaskWithoutImplementation] = new("GAP-C", "The task is checked complete but none of its expected artifacts exists in the snapshot."),
        [GapFindingType.OpenTaskWithImplementation] = new("GAP-D", "The task is unchecked but its expected artifacts exist (checkbox may be stale)."),
        [GapFindingType.PackageMissing] = new("GAP-E", "A package the task adds/pins is not declared by any project in the snapshot."),
        [GapFindingType.PackageWithoutBehaviour] = new("GAP-F", "The package is declared, but the types that would use it (other tasks of the same feature) are absent."),
        [GapFindingType.TestMissing] = new("GAP-G", "A test task's test file or test class is not in the snapshot."),
        [GapFindingType.TestNotExecuted] = new("GAP-H", "Test definitions exist, but no recorded execution covers them."),
        [GapFindingType.ImplementationWithoutTask] = new("GAP-I", "A production project in a module with tasks is named by no task (no explicit association)."),
        [GapFindingType.BlockedByGate] = new("GAP-J", "The task is blocked by a gate whose decision is not approved."),
        [GapFindingType.DecisionNotApproved] = new("GAP-K", "A mandatory architecture decision has no recorded approval (draft, pending sign-off or absent from the register)."),
        [GapFindingType.IntegrationNotImplemented] = new("GAP-L", "A contract-backed feature has tasks but no implementation evidence for them."),
        [GapFindingType.NoRuntimeVerification] = new("GAP-M", "Static source evidence exists, but no runtime verification is recorded."),
        [GapFindingType.ArtifactMovedOrRenamed] = new("GAP-N", "The expected file is not at the stated path, but a file with the same name is elsewhere (moved/renamed — not missing)."),
        [GapFindingType.MultipleCandidates] = new("GAP-O", "Several candidates match one expected artifact; a person must choose."),
        [GapFindingType.InventoryIncomplete] = new("GAP-P", "The source inventory is incomplete or truncated, or inputs were unreadable: absence cannot be concluded there."),
        [GapFindingType.ImplementationScopeNotInArchive] = new("GAP-S", "No file of the declared implementation scope is in the supplied archive: implementation is not evidenced in this archive (which does not prove it is absent elsewhere)."),
    };
}
