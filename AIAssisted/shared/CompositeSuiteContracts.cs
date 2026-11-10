using System.Text.Json.Serialization;

namespace BirkNext.IntegrationSuites;

// ── Multi-source end-to-end suites ───────────────────────────────────────────────────────────────────────────────────────────────
// A Composite Source Context is a versioned, logical association of EXISTING Source Analysis snapshots (possibly of different projects) with
// the stages of one integration scope. It references snapshots by id, project key and archive fingerprint — it never copies source content,
// contracts or schemas and is not a second snapshot store. Every association needs a recorded human approval before a suite may rely on it;
// associating projects grants nothing: the same write/execute policies apply and each source keeps its own project identity.

/// <summary>How well a stage is supported by the sources: approved by a person / one candidate project / several competing projects / none.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeMatchClass { Confirmed, Candidate, Ambiguous, Missing }

/// <summary>What a candidate snapshot is for the integration: an application source, supporting infrastructure, a shared library, or a
/// built-in Test Pack reference (no import).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeSourceKind { Application, Infrastructure, Library, TestPackReference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeApproval { Proposed, Approved, Rejected }

/// <summary>Static (source-declared) compatibility of one dimension of a cross-stage transition — never a runtime claim.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransitionCompatibility { Compatible, PotentiallyCompatible, Incompatible, NotAssessed, MissingEvidence }

/// <summary>Readiness of one of the three levels (source coverage, stage execution, complete E2E) or of one stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeReadiness { Ready, Partial, Blocked, NeedsReview, NotAssessed, NotSupported }

/// <summary>The kind of boundary between two stages.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransitionChannel { HttpApi, Webhook, Message, Internal, Dataset }

/// <summary>A stage's state in one composite run (asynchronous stages wait and observe; a timeout is never proof of failure).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeStageState { NotRun, Blocked, Waiting, Observing, Succeeded, Failed, TimedOut, UnknownOutcome, NotObservable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeRunKind { FullE2E, SubflowTest }

/// <summary>The verdict of one composite run. FULL_E2E_VERIFIED needs every mandatory stage executed or observed in ONE run with every boundary
/// correlated; a subflow is at most SUBFLOW_VERIFIED; separate runs are never combined.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeRunVerdict { NotRun, Running, FullE2EVerified, SubflowVerified, Failed, Incomplete, UnknownOutcome, WrongCorrelation }

/// <summary>How an identifier crossed a boundary in a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationState { Confirmed, Carried, Missing, WrongCorrelation, NotObserved }

/// <summary>Coverage ladder of a stage or scenario; each level implies the previous one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompositeCoverageLevel { None, Generated, Configured, Executable, Executed, Verified }

/// <summary>A stage's source requirement inside a scope: the logical names that recognise its source (word match on components, projects and
/// top folders — never archive names), names that rule a candidate out, and what crosses the boundary into the next stage.</summary>
public sealed record StageSourceRequirement
{
    public List<string> Tokens { get; init; } = [];
    public List<string> ExcludedTokens { get; init; } = [];
    /// <summary>A built-in Test Pack reference that covers the stage without an import (e.g. the Altinn app reference).</summary>
    public string? ReferenceSource { get; init; }
    /// <summary>A dataset contract (retained Parquet contract document) also counts as evidence for the stage.</summary>
    public bool DatasetContractEvidence { get; init; }
    public TransitionChannel ChannelToNext { get; init; } = TransitionChannel.HttpApi;
    /// <summary>Identifier names that may correlate this stage with the next (documented response/parameter names).</summary>
    public List<string> CorrelationFields { get; init; } = [];
    /// <summary>Words that mark an operation as this stage's state-changing action (e.g. a period lock).</summary>
    public List<string> ActionWords { get; init; } = [];
    public bool Mandatory { get; init; } = true;
}

public sealed record CompositeSourceCandidate
{
    public Guid SnapshotId { get; init; }
    public string ProjectKey { get; init; } = "";
    public string ArchiveFileName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string AnalysisStatus { get; init; } = "";
    public CompositeSourceKind Kind { get; init; }
    /// <summary>Why the snapshot matches the stage (component/project/folder/contract names, never source text).</summary>
    public List<string> MatchedOn { get; init; } = [];
    /// <summary>Top folders of the archive that hold the stage's source (used as the snapshot's modules for this stage).</summary>
    public List<string> Modules { get; init; } = [];
    /// <summary>Stage-relevant artifacts found (contracts, XSD namespaces, dataset contracts).</summary>
    public List<string> Artifacts { get; init; } = [];
    public string Confidence { get; init; } = "";
    /// <summary>Older snapshots of the same project (history, never alternatives).</summary>
    public int OlderSnapshots { get; init; }
}

public sealed record CompositeStageDiscovery
{
    public string StageId { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Mandatory { get; init; }
    public CompositeMatchClass Class { get; init; }
    public List<CompositeSourceCandidate> Candidates { get; init; } = [];
    public string? ReferenceSource { get; init; }
    public List<string> Missing { get; init; } = [];
    public string Explanation { get; init; } = "";
}

public sealed record CompositeDiscovery
{
    public string ScopeId { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public List<CompositeStageDiscovery> Stages { get; init; } = [];
    /// <summary>Infrastructure-as-Code and library snapshots: supporting evidence, never stage alternatives.</summary>
    public List<CompositeSourceCandidate> Supporting { get; init; } = [];
    public int SnapshotsConsidered { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>One snapshot in a composite context: exact identity, role, the stages it covers, evidence, confidence and the human decision.</summary>
public sealed record CompositeSnapshotAssociation
{
    public Guid SnapshotId { get; init; }
    public string ProjectKey { get; init; } = "";
    public string ArchiveFileName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string AnalysisStatus { get; init; } = "";
    public CompositeSourceKind Kind { get; init; }
    public List<string> Stages { get; init; } = [];
    public Dictionary<string, List<string>> ModulesByStage { get; init; } = [];
    public List<string> Evidence { get; init; } = [];
    public string Confidence { get; init; } = "";
    public CompositeApproval Approval { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed record CompositeStageMapping
{
    public string StageId { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Mandatory { get; init; }
    public CompositeMatchClass Class { get; init; }
    public List<Guid> SnapshotIds { get; init; } = [];
    public string? ReferenceSource { get; init; }
    public List<string> Evidence { get; init; } = [];
    public List<string> Missing { get; init; } = [];
}

public sealed record CompatibilityDimension(string Name, TransitionCompatibility State, string Detail, List<string> Evidence);

/// <summary>Static compatibility of one transition between consecutive stages, per dimension; a reviewer may accept a non-compatible transition
/// with a reason — the state stays as assessed, the acceptance is recorded beside it.</summary>
public sealed record CompositeTransition
{
    public string Key { get; init; } = "";
    public string FromStage { get; init; } = "";
    public string ToStage { get; init; } = "";
    public TransitionChannel Channel { get; init; }
    public List<CompatibilityDimension> Dimensions { get; init; } = [];
    public TransitionCompatibility Overall { get; init; }
    public bool Accepted { get; init; }
    public string? AcceptedBy { get; init; }
    public string? AcceptedReason { get; init; }
}

/// <summary>A newer snapshot of an associated project: proposed with the stages it affects, never applied automatically.</summary>
public sealed record CompositeSourceChange(Guid CurrentSnapshotId, Guid NewerSnapshotId, string ProjectKey, string NewerArchiveSha256, DateTimeOffset NewerAnalyzedAt, List<string> AffectedStages);

public sealed record CompositeSourceContext
{
    public Guid Id { get; init; }
    /// <summary>Stable identity across revisions: scope + owning project.</summary>
    public string ContextKey { get; init; } = "";
    public int Revision { get; init; }
    public string OwningProject { get; init; } = "";
    public string ScopeId { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public List<CompositeSnapshotAssociation> Snapshots { get; init; } = [];
    public List<CompositeStageMapping> StageMappings { get; init; } = [];
    public List<CompositeTransition> Compatibility { get; init; } = [];
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string CreatedBy { get; init; } = "";
    /// <summary>What changed against the previous revision (associations, approvals, snapshots).</summary>
    public List<string> Changes { get; init; } = [];
    /// <summary>Computed on read: newer snapshots of associated projects (never stored as a change).</summary>
    public List<CompositeSourceChange> NewerSources { get; init; } = [];
    public CompositeReadinessLevel? SourceCoverage { get; init; }
}

public sealed record CompositeContextSummary(Guid Id, string ContextKey, int Revision, string ScopeId, string OwningProject, int Snapshots, int Approved,
    int StagesCovered, int Stages, DateTimeOffset UpdatedAt);

public sealed record CompositeAssociationInput(Guid SnapshotId, List<string> Stages, CompositeApproval Approval = CompositeApproval.Proposed, string? Reason = null);

/// <summary>Creates the next revision of a context (or the first). <see cref="ExpectedRevision"/> guards against overwriting a newer revision.</summary>
public sealed record CompositeContextSaveRequest
{
    public string ScopeId { get; init; } = "";
    public string? OwningProject { get; init; }
    public List<CompositeAssociationInput> Associations { get; init; } = [];
    public List<CompositeTransitionAcceptance> Acceptances { get; init; } = [];
    public int? ExpectedRevision { get; init; }
}

public sealed record CompositeTransitionAcceptance(string TransitionKey, string Reason);

public sealed record CompositeContextSaveResult(CompositeSourceContext? Context, List<string> Problems, bool Conflict = false, bool Unchanged = false);

// ── Readiness, blockers, coverage ────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record CompositeReadinessLevel(string Level, CompositeReadiness State, string Detail, List<string> Items);

public sealed record CompositeBlocker
{
    public string Id { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Missing { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Action { get; init; } = "";
    /// <summary>Who resolves it: Tester, Test lead, Domain owner, Operator, Platform.</summary>
    public string Owner { get; init; } = "";
    /// <summary>Blocking (stops the E2E verdict) or Limiting (reduces what can be verified).</summary>
    public string Severity { get; init; } = "";
    public string ReviewStatus { get; init; } = "";
}

public sealed record CompositeStageView
{
    public int Order { get; init; }
    public string StageId { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Mandatory { get; init; }
    public CompositeMatchClass SourceClass { get; init; }
    /// <summary>"m2lb · M2LB.zip" style labels of the stage's approved/proposed sources.</summary>
    public List<string> Sources { get; init; } = [];
    public List<string> Contracts { get; init; } = [];
    public string Executor { get; init; } = "";
    public string Observer { get; init; } = "";
    public CompositeReadiness Readiness { get; init; }
    public CompositeCoverageLevel Coverage { get; init; }
    public string? WhyNotVerified { get; init; }
}

/// <summary>One step of the composite transaction plan: an existing contract test of the stage's contract, what it extracts for later stages and
/// which earlier output it takes (the explicit identifier mapping), and — for observers — the bounded observation policy.</summary>
public sealed record CompositePlanStep
{
    public string StepId { get; init; } = "";
    public string StageId { get; init; } = "";
    public string Role { get; init; } = "";  // "Executor" | "Observer" | "Action"
    public Guid? TestId { get; init; }
    public string ContractRef { get; init; } = "";
    public string Operation { get; init; } = "";
    public bool Mutating { get; init; }
    public string? ExtractField { get; init; }
    public string? FromStep { get; init; }
    public string? FromField { get; init; }
    public string? Parameter { get; init; }
    /// <summary>Field of this step's response that echoes the upstream identifier (correlation check), when documented.</summary>
    public string? EchoField { get; init; }
    public int ObserveAttempts { get; init; }
    public int ObserveDelayMs { get; init; }
    public List<string> Evidence { get; init; } = [];
}

public sealed record CompositePlan
{
    public Guid? ChainId { get; init; }
    public int? ChainRevision { get; init; }
    /// <summary>Draft until a person approves it (the chain's own state); a draft never runs.</summary>
    public string State { get; init; } = "Draft";
    public List<CompositePlanStep> Steps { get; init; } = [];
    public List<string> Missing { get; init; } = [];
    public string Confidence { get; init; } = "";
}

public sealed record CompositeCoverageRow(string Subject, string Kind, CompositeCoverageLevel Level, string? WhyNotVerified);

/// <summary>The composite part of a suite generated from a context.</summary>
public sealed record SuiteComposite
{
    public Guid ContextId { get; init; }
    public string ContextKey { get; init; } = "";
    public int ContextRevision { get; init; }
    public bool PartialDraft { get; init; }
    public List<SuiteSourceBinding> Sources { get; init; } = [];
    public List<CompositeStageView> Stages { get; init; } = [];
    public List<CompositeTransition> Transitions { get; init; } = [];
    public List<CompositeReadinessLevel> Readiness { get; init; } = [];
    public List<CompositeBlocker> Blockers { get; init; } = [];
    public CompositePlan Plan { get; init; } = new();
    public List<CompositeCoverageRow> Coverage { get; init; } = [];
    public List<string> PeriodLock { get; init; } = [];
    public string BufdataDelivery { get; init; } = "";
}

public sealed record SuiteSourceBinding
{
    public Guid SnapshotId { get; init; }
    public string ProjectKey { get; init; } = "";
    public string ArchiveFileName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string Approval { get; init; } = "";
    public List<string> Stages { get; init; } = [];
    public List<string> Modules { get; init; } = [];
}

// ── Composite execution ──────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record CompositeRunRequest
{
    /// <summary>Stages to run, a prefix of the plan's stages (empty = all = full E2E). Anything less is a SUBFLOW_TEST.</summary>
    public List<string> Stages { get; init; } = [];
    public string EnvironmentId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public bool IsProduction { get; init; }
    public string? ProfileId { get; init; }
    public string? ContextFingerprint { get; init; }
    public bool ConfirmMutatingCalls { get; init; }
    /// <summary>The tester's explicit authorisation. Without it nothing is sent.</summary>
    public bool Authorized { get; init; }
}

public sealed record CompositePreflight(bool CanRun, CompositeRunKind Kind, List<string> Stages, List<string> MutatingOperations, List<string> Problems, List<string> Warnings);

public sealed record CompositeRunResult(Guid? RunId, string State, string Message, CompositePreflight Preflight);

public sealed record CompositeCorrelationLink(string Boundary, string FromStep, string ToStep, string Field, CorrelationState State, string? Value, string Evidence);

public sealed record CompositeStageRun(string StageId, string Label, CompositeStageState State, List<Guid> CallIds, int Attempts, string Detail);

public sealed record CompositeRunView
{
    public Guid RunId { get; init; }
    public Guid SuiteId { get; init; }
    public int SuiteRevision { get; init; }
    public CompositeRunKind Kind { get; init; }
    public CompositeRunVerdict Verdict { get; init; }
    public string VerdictReason { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public string Initiator { get; init; } = "";
    public string ExecutionState { get; init; } = "";
    public List<CompositeStageRun> Stages { get; init; } = [];
    public List<CompositeCorrelationLink> Correlation { get; init; } = [];
    public List<SuiteSourceBinding> Sources { get; init; } = [];
    public string? ChainRef { get; init; }
}
