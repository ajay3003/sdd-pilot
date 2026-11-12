using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// Test Plan Suggestions: turns existing scenario candidates (System Understanding "What to test") into reviewable Test Plan → Test Suite →
// Test Case DRAFTS, links Test Data by its immutable asset id, records human review, freezes the proposal in immutable snapshots and compares
// two deliveries. It is not a scenario engine (scenarios stay owned by the guide), not a snapshot platform for anything else, and never an Azure
// DevOps Test Plan: nothing here is written to Azure DevOps and no Test Case record is claimed to exist there.
// Architecture: docs/audits/birknext-test-plan-suggestions-architecture.md.

/// <summary>Where a scenario candidate comes from. Only <see cref="SystemGuide"/> produces Test Case drafts; executable definitions are
/// correlation targets (an existing automated or runnable test), never drafts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioSourceKind { SystemGuide, IntegrationJourney, CriticalE2EFlow }

/// <summary>Generated → UnderReview → NeedsClarification | Accepted (for preparation/export) | Excluded; Superseded when the scenario left the
/// source. Accepted is a BirkNext review decision: never an Azure DevOps approval and never an execution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCaseReviewState { Generated, UnderReview, NeedsClarification, Accepted, Excluded, Superseded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCaseRisk { NotAssessed, Low, Medium, High }

/// <summary>How an expected result is established: by a reviewed (published) guide revision, by an unreviewed curated draft, by a reviewer's
/// own clarification, or not at all. Implementation behaviour is never a basis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExpectedResultBasis { ReviewedGuide, UnreviewedGuide, ReviewerClarified, Missing, NeedsClarification }

/// <summary>Known (established), Missing, NotVerified (cannot be established here, e.g. environment availability), NeedsAssistance.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestabilityFact { Known, Missing, NotVerified, NeedsAssistance }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationState { Candidate, Confirmed, Rejected }

/// <summary>A stable pointer at one scenario in its owning source. For a guide: guide key, the revision it was read from and that scenario's own
/// content fingerprint (so a later change of the scenario is detectable without copying the guide).</summary>
public sealed record ScenarioReference
{
    public ScenarioSourceKind Kind { get; init; }
    /// <summary>Guide key, pack/journey id or flow id.</summary>
    public string Owner { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public int? Revision { get; init; }
    public string? Fingerprint { get; init; }
    [JsonIgnore] public string Key => $"{Kind}:{Owner}:{ScenarioId}";
}

/// <summary>A business rule (guide rule) the scenario is linked to by the guide itself.</summary>
public sealed record TestCaseRequirement(string RuleId, string Title, string? ExpectedBehavior);

/// <summary>A Test Data asset linked by its immutable asset id; the fingerprint/approval/validation are the values seen when linked (and are
/// re-read for a snapshot). Values are never copied.</summary>
public sealed record TestCaseDataLink
{
    public string AssetId { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Fingerprint { get; init; }
    public string? Approval { get; init; }
    public string? Validation { get; init; }
    public string? SchemaCheck { get; init; }
    /// <summary>False when the asset no longer exists in the catalog (kept, shown as unresolved).</summary>
    public bool Resolved { get; init; } = true;
}

/// <summary>The content generated from the source scenario. Read-only to people: a reviewer's changes live in <see cref="TestCaseEdits"/>.</summary>
public sealed record TestCaseContent
{
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string? Preconditions { get; init; }
    public List<string> Steps { get; init; } = [];
    public string? ExpectedResult { get; init; }
    public ExpectedResultBasis ExpectedBasis { get; init; } = ExpectedResultBasis.Missing;
    public string? RequiredTestData { get; init; }
    public string? ObservationMethod { get; init; }
    public string? EnvironmentPrerequisites { get; init; }
    public List<string> FailureCases { get; init; } = [];
    public List<TestCaseRequirement> Requirements { get; init; } = [];
    /// <summary>Test names the guide mentions (text, not verified identities).</summary>
    public List<string> NamedExistingTests { get; init; } = [];
    public string? FollowUp { get; init; }
    /// <summary>The business-process stage the scenario's first step belongs to (the evidence used to propose its suite).</summary>
    public string? StageId { get; init; }
}

/// <summary>Human-owned overrides. Null = take the generated value. Kept apart from the generated content so neither overwrites the other.</summary>
public sealed record TestCaseEdits
{
    public string? Title { get; init; }
    public string? Preconditions { get; init; }
    public List<string>? Steps { get; init; }
    public string? ExpectedResult { get; init; }
    public string? ObservationMethod { get; init; }
    public string? EnvironmentPrerequisites { get; init; }
    [JsonIgnore] public bool IsEmpty => Title is null && Preconditions is null && Steps is null && ExpectedResult is null && ObservationMethod is null && EnvironmentPrerequisites is null;
}

public sealed record TestCaseReviewEvent(TestCaseReviewState State, string Reviewer, string? Rationale, DateTimeOffset At);

/// <summary>A link to an executable definition (journey scenario or Critical E2E flow). Candidate links come only from explicit evidence
/// (the guide names the executable id); never from title similarity. A reviewer confirms or rejects.</summary>
public sealed record ScenarioCorrelation
{
    public ScenarioReference Target { get; init; } = new();
    public string Label { get; init; } = "";
    public CorrelationState State { get; init; }
    public string Basis { get; init; } = "";
    /// <summary>For a journey scenario: the pack's own runnable/verification capability text (what it says, not a result).</summary>
    public string? Capability { get; init; }
    /// <summary>Several executable definitions match the same explicit reference: none is chosen automatically.</summary>
    public bool Ambiguous { get; init; }
}

/// <summary>Manual testability: each question answered from evidence, never "ready" because a scenario is documented.</summary>
public sealed record TestCaseTestability
{
    public TestabilityFact InputMethod { get; init; }
    public TestabilityFact TestData { get; init; }
    public TestabilityFact ExpectedBehavior { get; init; }
    public TestabilityFact Observation { get; init; }
    public TestabilityFact Environment { get; init; } = TestabilityFact.NotVerified;
    public TestabilityFact Permissions { get; init; } = TestabilityFact.NotVerified;
    public bool NeedsTechnicalAssistance { get; init; }
    /// <summary>"Ready for manual execution" only when every fact is Known; otherwise the open items in plain words.</summary>
    public string Summary { get; init; } = "";
    public List<string> Blockers { get; init; } = [];
    [JsonIgnore] public bool Ready => Blockers.Count == 0;
}

public sealed record TestCaseDraft
{
    public ScenarioReference Source { get; init; } = new();
    public string SuiteId { get; init; } = "";
    public TestCaseContent Generated { get; init; } = new();
    public TestCaseEdits Edits { get; init; } = new();
    public TestCaseReviewState Review { get; init; } = TestCaseReviewState.Generated;
    public List<TestCaseReviewEvent> ReviewHistory { get; init; } = [];
    public TestCaseRisk Risk { get; init; }
    public List<TestCaseDataLink> TestData { get; init; } = [];
    public List<ScenarioCorrelation> Correlations { get; init; } = [];
    /// <summary>Computed on the server from the generated content, edits, links and correlations (never trusted from a client).</summary>
    public TestCaseTestability Testability { get; init; } = new();
    /// <summary>Plain-language missing information (expected result, test data, observation method …).</summary>
    public List<string> Missing { get; init; } = [];
    /// <summary>The scenario changed in the source since this draft was generated (fingerprint differs): needs review.</summary>
    public bool SourceChanged { get; init; }
    /// <summary>A verified Azure DevOps Test Case id, only when verified by a reader; BirkNext writes none and never assumes one.</summary>
    public string? VerifiedAzureDevOpsTestCaseId { get; init; }

    [JsonIgnore] public string EffectiveTitle => Edits.Title ?? Generated.Title;
    [JsonIgnore] public string? EffectiveExpected => Edits.ExpectedResult ?? Generated.ExpectedResult;
    [JsonIgnore] public string? EffectivePreconditions => Edits.Preconditions ?? Generated.Preconditions;
    [JsonIgnore] public List<string> EffectiveSteps => Edits.Steps ?? Generated.Steps;
    [JsonIgnore] public string? EffectiveObservation => Edits.ObservationMethod ?? Generated.ObservationMethod;
    [JsonIgnore] public string? EffectiveEnvironment => Edits.EnvironmentPrerequisites ?? Generated.EnvironmentPrerequisites;
}

/// <summary>A suite of the proposal. Origin "stage" = proposed from the guide's business-process stage; "reviewer" = created by a person.</summary>
public sealed record TestSuiteDraft(string Id, string Name, string Origin, string? StageId);

/// <summary>The guide revision and the source snapshots a proposal is based on (identity only).</summary>
public sealed record TestPlanSourceBasis
{
    public string GuideKey { get; init; } = "";
    public int GuideRevision { get; init; }
    public string GuideFingerprint { get; init; } = "";
    /// <summary>"Published", "Draft only — not reviewed" … as the guide reports it.</summary>
    public string GuideReviewState { get; init; } = "";
    public List<TestPlanSourceRole> Roles { get; init; } = [];
    /// <summary>The Source Analysis baseline context fingerprint at that moment (changes when a baseline or the used snapshot changes).</summary>
    public string? SourceContextFingerprint { get; init; }
}

/// <summary>One guide source role and the snapshot analysis uses for it. <see cref="State"/> is the baseline state code
/// (e.g. BASELINE_NOT_SELECTED): alternative versions are never combined.</summary>
public sealed record TestPlanSourceRole(string Role, string Label, Guid? SnapshotId, string? Fingerprint, string State);

public sealed record TestPlanDraft
{
    public Guid Id { get; init; }
    public string GuideKey { get; init; } = "";
    public string PlanName { get; init; } = "";
    public string? DeliveryLabel { get; init; }
    public int Revision { get; init; }
    public TestPlanSourceBasis Basis { get; init; } = new();
    public List<TestSuiteDraft> Suites { get; init; } = [];
    public List<TestCaseDraft> Cases { get; init; } = [];
    public string RuleVersion { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record TestPlanDraftSummary(Guid Id, string GuideKey, string PlanName, string? DeliveryLabel, int Revision, int Cases, int Accepted, int NeedsClarification,
    int MissingExpected, int MissingTestData, int Blocked, string UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>A scenario candidate as discovered (one per guide scenario), with its draft preview and filters' facts.</summary>
public sealed record ScenarioCandidate
{
    public ScenarioReference Source { get; init; } = new();
    public string Title { get; init; } = "";
    public string Objective { get; init; } = "";
    /// <summary>The guide's own scenario state label (Suggested, Needs clarification …): guidance, not coverage.</summary>
    public string GuideState { get; init; } = "";
    public string? StageId { get; init; }
    public string? StageTitle { get; init; }
    public List<TestCaseRequirement> Requirements { get; init; } = [];
    public bool HasExpected { get; init; }
    public bool HasTestData { get; init; }
    public bool HasObservation { get; init; }
    public List<ScenarioCorrelation> Correlations { get; init; } = [];
}

/// <summary>An executable definition offered for an explicit link (journey scenario or Critical E2E flow).</summary>
public sealed record ExecutableScenarioOption(ScenarioReference Reference, string Label, string Detail);

/// <summary>A Test Data asset that can be linked (labels and identity only, never values).</summary>
public sealed record TestDataOption(string AssetId, string Alias, string? Label, string Kind, string Approval, string Validation, string? Fingerprint, string? Schema, string? SchemaCheck, string? Environment);

/// <summary>Everything the page needs in one call (no call per card).</summary>
public sealed record TestPlanWorkspace
{
    public List<TestPlanGuideOption> Guides { get; init; } = [];
    public string? GuideKey { get; init; }
    public TestPlanSourceBasis? Basis { get; init; }
    public List<ScenarioCandidate> Candidates { get; init; } = [];
    public List<TestPlanStage> Stages { get; init; } = [];
    public List<TestPlanDraftSummary> Drafts { get; init; } = [];
    public List<TestPlanSnapshotSummary> Snapshots { get; init; } = [];
    public List<ExecutableScenarioOption> Executables { get; init; } = [];
    public List<TestDataOption> TestData { get; init; } = [];
    public bool CanWrite { get; init; }
    public string RuleVersion { get; init; } = "";
    /// <summary>Guide source changes since the guide revision (the existing change awareness): candidates for regression review.</summary>
    public List<string> SourceChanges { get; init; } = [];
}

public sealed record TestPlanGuideOption(string GuideKey, string Title, string ReviewState);
public sealed record TestPlanStage(string Id, string Title);

public sealed record CreateTestPlanDraftRequest(string GuideKey, string PlanName, string? DeliveryLabel, List<string> ScenarioIds);

/// <summary>A draft save: only human-owned parts are taken from the client; generated content is rebuilt on the server from the recorded
/// guide revision.</summary>
public sealed record SaveTestPlanDraftRequest
{
    public int BaseRevision { get; init; }
    public string PlanName { get; init; } = "";
    public string? DeliveryLabel { get; init; }
    public List<string> ScenarioIds { get; init; } = [];
    public List<TestSuiteDraft> Suites { get; init; } = [];
    public List<TestCaseHumanPart> Cases { get; init; } = [];
}

public sealed record TestCaseHumanPart
{
    public string ScenarioId { get; init; } = "";
    public string SuiteId { get; init; } = "";
    public TestCaseEdits Edits { get; init; } = new();
    public TestCaseRisk Risk { get; init; }
    public List<string> TestDataAssetIds { get; init; } = [];
    /// <summary>Correlation decisions by target key (Confirmed/Rejected); candidates come from the server.</summary>
    public Dictionary<string, CorrelationState> Correlations { get; init; } = [];
    /// <summary>Explicit manual links to executable definitions (target keys).</summary>
    public List<string> ManualLinks { get; init; } = [];
}

public sealed record ReviewTestCaseRequest(int BaseRevision, string ScenarioId, TestCaseReviewState State, string? Rationale);

public sealed record SaveTestPlanSnapshotRequest(int ExpectedRevision, string? DeliveryLabel, string? Note);

public sealed record TestPlanSnapshotSummary(Guid Id, Guid DraftId, string GuideKey, int Sequence, string PlanName, string? DeliveryLabel, int DraftRevision, string ContentFingerprint,
    string RuleVersion, int GuideRevision, int Cases, string CreatedBy, DateTimeOffset CreatedAt, string? Note);

/// <summary>A frozen proposal. Stored once; the database refuses any change to it.</summary>
public sealed record TestPlanSnapshot
{
    public TestPlanSnapshotSummary Summary { get; init; } = null!;
    public TestPlanDraft Plan { get; init; } = new();
}

public sealed record SaveTestPlanSnapshotResult(TestPlanSnapshotSummary Snapshot, bool Unchanged, string Message);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaseChangeKind { Added, Removed, Changed, Unchanged }

/// <summary>What changed in a case. <see cref="Category"/>: ExpectedResult, Requirements, TestData, Preconditions, Steps, Risk, Testability,
/// Review, Suite, Title, Observation, SourceScenario.</summary>
public sealed record CaseFieldChange(string Category, string? Before, string? After);

public sealed record CaseComparison
{
    public string ScenarioId { get; init; } = "";
    public string Title { get; init; } = "";
    public CaseChangeKind Kind { get; init; }
    public List<CaseFieldChange> Changes { get; init; } = [];
    /// <summary>"Potential business-rule change": the expected result changed and both sides are established by a reviewed basis. Otherwise a
    /// change is only "needs review" — a text diff is never declared a behaviour change by itself.</summary>
    public bool PotentialBehaviourChange { get; init; }
    public string Assessment { get; init; } = "";
}

/// <summary>The kind of difference between two snapshots' contexts, kept apart: source, guide (scenario/requirement), review, test data and the
/// suggestion rules. A rule-version change alone is never an application behaviour change.</summary>
public sealed record ContextDifference(string Category, string Before, string After, string Meaning);

public sealed record RegressionRecommendation(string Kind, string ScenarioId, string Title, string Reason);

public sealed record DeliveryComparison
{
    public TestPlanSnapshotSummary From { get; init; } = null!;
    public TestPlanSnapshotSummary To { get; init; } = null!;
    /// <summary>False when the two snapshots belong to different guides (systems): nothing is compared case by case.</summary>
    public bool Comparable { get; init; }
    public string? IncomparableReason { get; init; }
    public List<ContextDifference> Context { get; init; } = [];
    public List<CaseComparison> Cases { get; init; } = [];
    public List<RegressionRecommendation> Recommendations { get; init; } = [];
    [JsonIgnore] public int Added => Cases.Count(c => c.Kind == CaseChangeKind.Added);
    [JsonIgnore] public int Removed => Cases.Count(c => c.Kind == CaseChangeKind.Removed);
    [JsonIgnore] public int Changed => Cases.Count(c => c.Kind == CaseChangeKind.Changed);
    [JsonIgnore] public int Unchanged => Cases.Count(c => c.Kind == CaseChangeKind.Unchanged);
}

/// <summary>The structured export (BirkNext's own JSON document plus the same content as copyable text). Not an Azure DevOps import format.</summary>
public sealed record TestPlanExport(string Format, string FileName, string Json, string Text, List<string> Unresolved, string Boundary);
