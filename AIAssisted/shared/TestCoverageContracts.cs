using System.Text.Json.Serialization;

namespace BirkNext.TestCoverage;

// Test Coverage & Overlap Review. What is already tested, what does that test prove, at which boundary, what part of the real
// journey is still unverified, and where QA may repeat developer automation. Deterministic: no language model, no coverage percentage.
// Semantics kept apart everywhere: discovered ≠ analyzed ≠ executed ≠ passed ≠ requirement covered ≠ boundary verified ≠ runtime verified;
// a mocked broker is not a verified broker; same requirement is not the same test; no evidence found is not "no test exists".

// ── Source evidence captured once at Source Analysis upload ─────────────────────────────────────────────────────────────

/// <summary>How a test reaches a dependency: a real engine, an in-process host, an in-memory substitute, a mock/fake, or unknown.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoundaryMode { Real, RealContainer, InProcess, InMemory, Mocked, Fake, Unknown }

/// <summary>The kind of system boundary a test touches (or a journey step crosses).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoundaryKind { Http, GraphQl, Database, EventHub, ServiceBus, Messaging, Browser, FileSystem, ExternalService, Cdc, Storage, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestAssertionKind
{
    HttpStatus, ExceptionThrown, RecordPersisted, RecordCount, MessagePublished, MockInvocationVerified, ValueEquality, CollectionShape,
    AuthorizationResult, NotNullOnly, Snapshot, BooleanCondition, Rendered, Other,
}

/// <summary>A setup fact read from test syntax: what the test hosts, substitutes or connects to. Detail is a type name, never a value.</summary>
public sealed record TestBoundarySignal(BoundaryKind Kind, BoundaryMode Mode, string Detail, string Scope, int Line);

/// <summary>What a test asserts. Detail is a status code, exception type or member name — never a literal value from the test.</summary>
public sealed record TestAssertionFact(TestAssertionKind Kind, string Detail, int Line);

/// <summary>One test method as Source Analysis read it (C# syntax only). Facts, not judgements.</summary>
public sealed record TestBehaviorFact
{
    /// <summary>The source test inventory's id when the test is in it (xUnit); otherwise a stable id from project + fully-qualified name.</summary>
    public string TestId { get; init; } = "";
    public bool InInventory { get; init; }
    public string Project { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public string ClassName { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string FullyQualifiedName { get; init; } = "";
    public string Framework { get; init; } = "";
    public bool Skipped { get; init; }
    public List<string> Categories { get; init; } = [];
    /// <summary>Production projects the test project references (project references, names only).</summary>
    public List<string> ProductionProjects { get; init; } = [];
    /// <summary>Production projects that declare the types the test constructs or calls (from a type-name index; ambiguous names are skipped).</summary>
    public List<string> TargetProjects { get; init; } = [];
    /// <summary>Non-test types the test constructs or calls ("PersonService", "PersonService.CreateAsync"). Capped.</summary>
    public List<string> Targets { get; init; } = [];
    public List<TestBoundarySignal> Signals { get; init; } = [];
    public List<TestAssertionFact> Assertions { get; init; } = [];
    /// <summary>Explicit requirement/acceptance identifiers found in the test (names, traits, comments).</summary>
    public List<string> RequirementReferences { get; init; } = [];
    /// <summary>Normalized behaviour tokens from the test name (lower case, test noise words removed).</summary>
    public List<string> BehaviorTokens { get; init; } = [];
    /// <summary>Expected outcomes the assertions name (HTTP status codes, exception types) — used for outcome matching.</summary>
    public List<string> ExpectedOutcomes { get; init; } = [];
}

/// <summary>Test files in languages whose source Source Analysis does not retain: counted by path, never analyzed or called clean.</summary>
public sealed record UnsupportedTestFiles(string Language, string Framework, int Files, string Reason);

public sealed record TestBehaviorSourceEvidence
{
    public Guid SnapshotId { get; init; }
    public int AnalyzerVersion { get; init; } = 1;
    public string Language { get; init; } = "C#";
    public string AnalyzerId { get; init; } = "dotnet-roslyn-syntax";
    public int TestFilesAnalyzed { get; init; }
    public List<TestBehaviorFact> Tests { get; init; } = [];
    public List<UnsupportedTestFiles> Unsupported { get; init; } = [];
    /// <summary>Test project → referenced production project names.</summary>
    public Dictionary<string, List<string>> ProjectReferences { get; init; } = [];
    public bool Truncated { get; init; }
    public List<string> Limitations { get; init; } = [];
}

// ── Review model ────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestLevel { Unit, Component, Repository, Contract, Integration, Api, Ui, E2E, Security, Performance, Runtime, Unknown, NeedsReview }

/// <summary>User-facing evidence strength. There is no global confidence percentage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceStrength { Strong, Some, Weak, NotEnough }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestOwnership { DeveloperAutomated, QaAutomated, QaManual, Unknown }

/// <summary>Execution state from imported results. Discovered/analyzed tests without results are NotVerified, never Passed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionState { Passed, Failed, Skipped, NotExecuted, NotVerified }

public sealed record CoverageTest
{
    public string TestId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Project { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public string Framework { get; init; } = "";
    public TestLevel Level { get; init; }
    public EvidenceStrength LevelEvidence { get; init; }
    public string LevelBasis { get; init; } = "";
    public TestOwnership Ownership { get; init; }
    public string OwnershipBasis { get; init; } = "";
    public bool Analyzed { get; init; }
    public bool Skipped { get; init; }
    public TestExecutionState Execution { get; init; }
    public List<string> Components { get; init; } = [];
    public List<TestBoundarySignal> Boundaries { get; init; } = [];
    public List<TestAssertionFact> Assertions { get; init; } = [];
    public List<string> RequirementIds { get; init; } = [];
    /// <summary>Plain-language description of what the test appears to check, built from facts; hedged when uncertain.</summary>
    public string Purpose { get; init; } = "";
    public EvidenceStrength Strength { get; init; }
}

/// <summary>A QA test candidate: an acceptance scenario from the specification, a QA-owned automated test, or a Critical E2E flow.</summary>
public sealed record QaTestCandidate
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public TestOwnership Ownership { get; init; } = TestOwnership.QaManual;
    public List<string> RequirementIds { get; init; } = [];
    public string? Given { get; init; }
    public string? When { get; init; }
    public string? Then { get; init; }
    /// <summary>The user story the scenario is written under (<see cref="CoverageUserStory.Key"/>), from the document structure; null = none.</summary>
    public string? StoryKey { get; init; }
    /// <summary>The scenario's number within that story (1-based); null without a story.</summary>
    public int? NumberInStory { get; init; }
    public TestLevel IntendedLevel { get; init; } = TestLevel.Unknown;
    public string IntendedLevelBasis { get; init; } = "";
    public TestExecutionState Execution { get; init; } = TestExecutionState.NotVerified;
    /// <summary>Test Data assets this QA test is linked to by an authoritative source (immutable asset ids; values never copied). Empty for older results.</summary>
    public List<CoverageTestDataLink> TestData { get; init; } = [];
    /// <summary>Whether the source states a data need: Linked, RequiredButMissing (stated without a link) or NotAssessed (never "no data needed").</summary>
    public CoverageDataNeed DataNeed { get; init; } = CoverageDataNeed.NotAssessed;
    /// <summary>Why the data-need state was given (the source statement), or null when not assessed.</summary>
    public string? DataNeedBasis { get; init; }
}

/// <summary>Data need of a QA test as its source states it. Absence of a link is <see cref="NotAssessed"/>, never "no data needed".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageDataNeed { NotAssessed, Linked, RequiredButMissing, LinkNeedsReview }

/// <summary>One Test Data asset a QA test is linked to. <see cref="Source"/> names the owner of the link ("Critical E2E flow definition",
/// "Test Plan draft …", "Test Data consumer reference …"); <see cref="Fingerprint"/> is the asset revision bound when linked, when the source recorded
/// one. <see cref="State"/> is the Test Data usage state of a consumer reference ("Resolved" or "NeedsReview": deprecated, no longer approved,
/// invalid or changed since it was bound); null for sources without a usage state (counted as resolved).</summary>
public sealed record CoverageTestDataLink(string AssetId, string? Alias, string? Fingerprint, string Source, string? State = null, string? StateReason = null)
{
    /// <summary>The link points at the revision that is approved and current now. An unresolved link is shown, never counted as linked.</summary>
    public bool Resolved => State is null || State == "Resolved";
}

/// <summary>How a Test Coverage QA test is named as a Test Data consumer (Test Data → requirements and usage references). The consumer id is
/// project-scoped ("{projectKey}/{qaTestId}") so the same acceptance-scenario id in two projects never shares a link.</summary>
public static class CoverageTestDataConsumers
{
    public const string ConsumerType = "Test Coverage QA test";

    public static string ConsumerId(string? projectKey, string qaTestId) =>
        string.IsNullOrWhiteSpace(projectKey) ? qaTestId : $"{projectKey.Trim()}/{qaTestId}";
}

/// <summary>An Integration Journey whose declared component chain equals or contains this source journey's chain (structure only). A candidate is
/// never an association: it is shown "Candidate — needs review" and counts for nothing until a reviewer confirms it.</summary>
/// <param name="IntegrationJourneyId">"packId:journeyId" (the Scope Registry Journey member id).</param>
/// <param name="Match">"Equal" (same chain) or "Contains" (the integration journey's chain contains this one contiguously).</param>
/// <param name="SourceChain">The source journey's node names in order (evidence shown to the reviewer).</param>
/// <param name="DeclaredChain">The Integration Journey's declared component chain in order, as declared (consecutive duplicates collapsed).</param>
public sealed record JourneyAssociationCandidate(string IntegrationJourneyId, string DisplayName, string Match, string Basis,
    IReadOnlyList<string>? SourceChain = null, IReadOnlyList<string>? DeclaredChain = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlapKind { HighConfidenceOverlap, PartialOverlap, ComplementaryCoverage, DifferentBoundary, InsufficientEvidence, NeedsTesterReview }

/// <summary>Which evidence dimensions matched. High-confidence overlap needs several, never a name alone.</summary>
public sealed record OverlapDimensions(bool SameRequirement, bool SameBehavior, bool SameOutcome, bool SameComponent, bool SameLevel, bool SameBoundary, bool DeveloperTestAnalyzed, bool DeveloperTestExecuted);

public sealed record TestOverlap
{
    public string Id { get; init; } = "";
    public string QaTestId { get; init; } = "";
    public string QaTitle { get; init; } = "";
    public List<string> DeveloperTestIds { get; init; } = [];
    public OverlapKind Kind { get; init; }
    public OverlapDimensions Dimensions { get; init; } = new(false, false, false, false, false, false, false, false);
    public EvidenceStrength Strength { get; init; }
    public List<string> Same { get; init; } = [];
    public List<string> Different { get; init; } = [];
    public string RemainingRisk { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public List<string> Reasons { get; init; } = [];
    public string? ReviewerDecision { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DimensionStatus { Strong, Some, Partial, MissingEvidence, NotAssessed }

public sealed record ComponentDimension(string Dimension, DimensionStatus Status, string Explanation, List<string> TestIds);

public sealed record ComponentCoverage
{
    public string ComponentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string SourceProject { get; init; } = "";
    public int Tests { get; init; }
    public int Skipped { get; init; }
    public List<ComponentDimension> Dimensions { get; init; } = [];
}

/// <summary>How a journey connection is supported. Reviewer confirmation is kept apart from source evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionEvidence { Confirmed, StronglySupported, Suggested, NeedsConfirmation, Unsupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepCoverage { Covered, PartlyCovered, MockOnly, LowerLevelOnly, NotVerified }

public sealed record JourneyNode(string Id, string Name, string Kind);

public sealed record JourneyStep
{
    public string Id { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string FromName { get; init; } = "";
    public string ToName { get; init; } = "";
    public BoundaryKind Boundary { get; init; }
    public ConnectionEvidence Evidence { get; init; }
    public List<string> Provenance { get; init; } = [];
    /// <summary>Confirmed / Rejected by a reviewer, or null. Never changes <see cref="Evidence"/>.</summary>
    public string? ReviewerDecision { get; init; }
    public StepCoverage Developer { get; init; }
    public string DeveloperExplanation { get; init; } = "";
    public List<string> DeveloperTestIds { get; init; } = [];
    public StepCoverage Qa { get; init; } = StepCoverage.NotVerified;
    public string QaExplanation { get; init; } = "";
    /// <summary>Runtime evidence is a separate source; without it the step is runtime NotVerified.</summary>
    public string Runtime { get; init; } = "Not verified";
}

public sealed record Journey
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<JourneyNode> Nodes { get; init; } = [];
    public List<JourneyStep> Steps { get; init; } = [];
    public ConnectionEvidence Weakest { get; init; }
    public List<string> FocusForTesters { get; init; } = [];
    /// <summary>Integration Journeys proposed for review from structure alone (see <see cref="JourneyAssociationCandidate"/>). Empty for older results.</summary>
    public List<JourneyAssociationCandidate> AssociationCandidates { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapKind { NoTestEvidence, UnitOnly, MockOnly, JourneyStepNotVerified, SkippedTests, RequirementWithoutTests, AuthorizationHappyPathOnly, RetryNotTested, ChangedWithoutTestChange, NewComponentWithoutTests, UnsupportedTests }

public sealed record CoverageGap
{
    public string Id { get; init; } = "";
    public GapKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    /// <summary>"Needs attention" / "Review recommended" — no invented business risk.</summary>
    public string Attention { get; init; } = "Review recommended";
    public List<string> Subjects { get; init; } = [];
    public List<string> Evidence { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageRecommendationKind
{
    StronglyCoveredSameLevel, CoveredAtLowerLevelOnly, CoveredByMocksOnly, CoveredByAutomatedE2E, PartlyCovered, NoTestEvidence,
    ChangedSinceTest, RemainingIntegrationRisk, NeedsHumanReview,
}

/// <summary>One behaviour or requirement with its coverage in plain language.</summary>
public sealed record BehaviorCoverage
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> RequirementIds { get; init; } = [];
    public List<string> Components { get; init; } = [];
    public CoverageRecommendationKind Recommendation { get; init; }
    public string Status { get; init; } = "";
    public List<string> AlreadyTested { get; init; } = [];
    public List<string> WhereTested { get; init; } = [];
    public List<string> NotVerifiedYet { get; init; } = [];
    public string ForTesters { get; init; } = "";
    public List<string> DeveloperTestIds { get; init; } = [];
    public int QaTests { get; init; }
    public EvidenceStrength Strength { get; init; }
    public List<string> Why { get; init; } = [];
}

public sealed record RemainingQaScope(List<string> Keep, List<string> ProbablyUnnecessary, List<string> NeedsReview);

public sealed record CoverageInventory
{
    public int TestProjects { get; init; }
    public int TestsDiscovered { get; init; }
    public int TestsAnalyzed { get; init; }
    public int TestsWithExecutionEvidence { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int SkippedDeclared { get; init; }
    public List<string> Frameworks { get; init; } = [];
    public Dictionary<string, int> ByLevel { get; init; } = [];
    public List<UnsupportedTestFiles> Unsupported { get; init; } = [];
    public string ExecutionEvidence { get; init; } = "";
    public string CodeCoverage { get; init; } = "No code coverage report was found; no coverage percentage is shown.";
}

public sealed record ReviewPhase(string Name, int Items, long Milliseconds);

public sealed record CoverageSnapshotRef(Guid SnapshotId, string Repository, string ArchiveName, string Fingerprint, DateTimeOffset AnalyzedAt);

public sealed record TestCoverageReviewResult
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public DateTimeOffset CompletedAt { get; init; }
    public string? ProjectName { get; init; }
    /// <summary>The document scope the requirements came from (e.g. "Module Person"); null for older results and projects without modules.</summary>
    public string? ArtifactScope { get; init; }
    /// <summary>The specification artifact(s) the requirement views were built from; empty for older results or when none was selected.</summary>
    public List<CoverageArtifactRef> SpecificationArtifacts { get; init; } = [];
    public CoverageSnapshotRef Current { get; init; } = new(Guid.Empty, "", "", "", default);
    public CoverageSnapshotRef? Baseline { get; init; }
    /// <summary>How the baseline relates to the current snapshot (same identity, or declared by the reviewer). Null without a baseline or for older results.</summary>
    public BirkNext.SourceEvidence.BaselineRelation? BaselineRelation { get; init; }
    public CoverageInventory Inventory { get; init; } = new();
    public List<CoverageTest> Tests { get; init; } = [];
    public List<QaTestCandidate> QaTests { get; init; } = [];
    public List<ComponentCoverage> Components { get; init; } = [];
    public List<Journey> Journeys { get; init; } = [];
    public List<BehaviorCoverage> Behaviors { get; init; } = [];
    public List<TestOverlap> Overlaps { get; init; } = [];
    public List<CoverageGap> Gaps { get; init; } = [];
    public RemainingQaScope QaScope { get; init; } = new([], [], []);
    public List<ReviewPhase> Phases { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public bool Partial { get; init; }
    /// <summary>Which contract operations / message roots discovered tests reference (shared contract evidence). Discovered tests, not executions.</summary>
    public List<ContractCoverageItem> Contracts { get; init; } = [];
    /// <summary>Imported executions that match no discovered test (execution-only), preserved.</summary>
    public List<CoverageExecutionOnly> ExecutionOnly { get; init; } = [];
    /// <summary>Coverage dimensions from shared Test Evidence (discovered / explicitly linked / candidate-only / in CI / executed / passed) — never one percentage.</summary>
    public BirkNext.TestEvidence.TestCoverageDimensions? Dimensions { get; init; }
    /// <summary>Source test-discovery limitations (unsupported frameworks are limitations, not zero tests).</summary>
    public List<string> DiscoveryLimitations { get; init; } = [];
    /// <summary>How each test project's pipeline test steps relate to deployments (shared delivery evidence): configured ≠ gating ≠ executed.</summary>
    public List<CoverageDeliveryTest> PipelineGating { get; init; } = [];
    /// <summary>The loaded project's saved test cases (Test Plan Suggestions) as test-design evidence against the reviewed specification. Separate from
    /// every count above (never a discovered, executed or passed test); null for older results or without a project.</summary>
    public BirkNext.TestPlanning.PreparedCaseTraceView? PreparedCases { get; init; }
}

/// <summary>One pipeline test step that runs a test project: whether it gates a deployment (from source dependencies/conditions). Never a run result.</summary>
public sealed record CoverageDeliveryTest(string Project, string Pipeline, string Step, string Category, string Gating, string Execution);

/// <summary>Test links of one source contract: <see cref="Covered"/> of <see cref="Elements"/> operations/message roots are named by a test or the
/// contract file is named by test code. A link is evidence a test targets the contract — never that it ran or passed.</summary>
public sealed record ContractCoverageItem(string ContractId, string Name, string Format, int Elements, int Covered,
    List<BirkNext.ContractSchemaEvidence.ContractTestLink> Links, string Text);

// ── Request / decisions / history ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>Workspace evidence the browser owns: requirements, acceptance scenarios (QA candidates) and imported test executions.</summary>
public sealed record CoverageWorkspaceEvidence
{
    public string? ProjectName { get; init; }
    /// <summary>Which project documents were read, e.g. "Module Person" in a monorepo; null for a project without modules.</summary>
    public string? ArtifactScope { get; init; }
    /// <summary>The selected specification artifact(s) the requirements came from (id, path, module, content fingerprint).</summary>
    public List<CoverageArtifactRef> SpecificationArtifacts { get; init; } = [];
    public bool SpecificationAvailable { get; init; }
    public List<CoverageRequirement> Requirements { get; init; } = [];
    public List<QaTestCandidate> AcceptanceScenarios { get; init; } = [];
    public List<CoverageExecution> Executions { get; init; } = [];
    /// <summary>The user stories of the selected specification (story headings only; in-text references are not stories). Empty for older
    /// clients and for specifications without user stories.</summary>
    public List<CoverageUserStory> Stories { get; init; } = [];
    /// <summary>The specification's clarifications (## Clarifications Q → A, with the state recorded in BirkNext); superseded ones are not sent.
    /// Empty for older clients.</summary>
    public List<CoverageClarification> Clarifications { get; init; } = [];
}

/// <summary>A clarification of the specification as discovery reads it.</summary>
/// <param name="State">"Open" (no answer), "Documented" (an answer is written in the specification but nobody confirmed it), or "Resolved"
/// (resolved in BirkNext with an authoritative reference).</param>
/// <param name="AffectedIds">The requirement, user story and success-criterion ids the question or answer names (FR-012, US1, SC-003);
/// empty = the clarification names none and applies to the specification as a whole.</param>
public sealed record CoverageClarification(string Id, string Question, string? Answer, string State, string? Resolution, List<string> AffectedIds)
{
    public const string Open = "Open", Documented = "Documented", Resolved = "Resolved";
}

/// <summary>A requirement a task of the story names in tasks.md: the task id, its line and text (evidence for a structural story → requirement link).</summary>
public sealed record StoryTaskRequirement(string RequirementId, string? TaskId, string TaskText, string TasksPath, int? Line);

/// <summary>
/// A user story of a specification feature as the document states it. <see cref="Key"/> = specification file + the story's own number
/// ("SkoleService/specs/001-skoleservice/spec.md#US6"), or its position when unnumbered ("…#story-2") — never the title, and the same number in
/// another feature's file is another story. Text is the source's, unchanged.
/// </summary>
public sealed record CoverageUserStory
{
    public string Key { get; init; } = "";
    /// <summary>The number the document gives ("US6"); null when the story is unnumbered.</summary>
    public string? StoryId { get; init; }
    public int Ordinal { get; init; }
    public string Title { get; init; } = "";
    public string? Priority { get; init; }
    public string? Description { get; init; }
    public string? WhyThisPriority { get; init; }
    /// <summary>The story's "Independent Test" text: test-design evidence (setup, what to observe), never turned into steps by itself.</summary>
    public string? IndependentTest { get; init; }
    public string? SpecificationPath { get; init; }
    public string? Module { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }
    public int Scenarios { get; init; }
    /// <summary>Requirement ids the story's own text names (level A evidence).</summary>
    public List<string> NamedRequirements { get; init; } = [];
    /// <summary>Requirement ids named by tasks tagged with this story in the same feature's tasks.md (level B, structural), with the task as evidence.</summary>
    public List<StoryTaskRequirement> TaskRequirements { get; init; } = [];

    public static string KeyOf(string? specificationPath, string storyKey) => $"{(specificationPath ?? "specification").Replace('\\', '/')}#{storyKey}";
}

public sealed record CoverageRequirement(string Id, string Text, string? Priority);

/// <summary>One project artifact a review read, bound by its content fingerprint so a stored result names exactly what it reviewed.</summary>
public sealed record CoverageArtifactRef(string ArtifactId, string Path, string? Module, string Fingerprint);

/// <summary>An imported execution result (TRX via the SDD lifecycle). TestId is the correlated test definition id when known.</summary>
/// <summary>One imported execution. <see cref="TestId"/> is the canonical test identity (stable identity or definition id) when correlated.
/// <see cref="Environment"/>/<see cref="ExecutedAt"/> keep the execution context explicit: a local run never answers for QA.</summary>
public sealed record CoverageExecution(string TestId, string TestName, string Result, string? Environment = null, DateTimeOffset? ExecutedAt = null);

/// <summary>An imported execution that matches no discovered test — kept (never discarded) with why it did not match.</summary>
public sealed record CoverageExecutionOnly(string TestName, string Result, string? Environment, string Basis);

public sealed record TestCoverageReviewRequest
{
    public Guid CurrentSnapshotId { get; init; }
    public Guid? BaselineSnapshotId { get; init; }
    /// <summary>The reviewer confirms that a baseline of another repository identity is a version of the same repository (recorded as declared).</summary>
    public bool BaselineSameRepositoryConfirmed { get; init; }
    /// <summary>Remember a confirmed declaration for future comparisons (stored with who and when; revocable).</summary>
    public bool RememberRepositoryAssociation { get; init; }
    /// <summary>Optional Target Environment whose Critical E2E flows count as QA automated tests (definitions and recorded runs only; nothing runs).</summary>
    public string? EnvironmentId { get; init; }
    public CoverageWorkspaceEvidence? Workspace { get; init; }
    /// <summary>The loaded project's key: project-declared Integration Journeys and project-scoped Test Data consumer references of this project
    /// are read; null reads neither.</summary>
    public string? ProjectKey { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageDecisionKind { JourneyConnection, ProjectOwnership, Overlap }

/// <summary>A reviewer decision. Stored as review metadata; it never overwrites source-derived evidence.</summary>
public sealed record CoverageDecision
{
    public string RepositoryKey { get; init; } = "";
    public CoverageDecisionKind Kind { get; init; }
    public string SubjectKey { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Note { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

public sealed record TestCoverageRunSummary(Guid RunId, DateTimeOffset CompletedAt, Guid CurrentSnapshotId, Guid? BaselineSnapshotId, string Label);

public static class TestCoverageText
{
    public const string Title = "Test Coverage & Overlap Review";
    public const string Hero = "See what is already tested, what still needs testing, and where QA may be repeating developer tests.";
    public const string Subtext = "BirkNext compares developer tests, QA tests, requirements and system journeys. Recommendations are based on available evidence and should be reviewed by the test lead.";
    public const string NoCertification = "This review never certifies that a system is fully tested or that a QA test is unnecessary. It shows what the available evidence supports.";
    public const string NoExecution = "Tests were found, but BirkNext does not have proof that they were executed.";
    public const string SourceRequired = "Source analysis is required before test coverage can be reviewed.";
    public const string NoPercentage = "No code coverage report was found; no coverage percentage is shown.";
}

// ── User story interpretation (2026-11-24) ───────────────────────────────────────────────────────────────────────────────────────────
// A user story explains WHY its acceptance scenarios matter; its "Independent Test" names what a tester must be able to show (coverage
// objectives). Both are read from the story's own stored text, deterministically — nothing is invented, and an interpretation that is not a
// plain list split is marked for review with its original sentence kept.

/// <summary>What an objective is: a condition to exercise (an input or situation) or something to verify.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryObjectiveKind { Condition, Verification }

/// <summary>One coverage objective from a user story's "Independent Test", with the sentence it came from. <see cref="NeedsReview"/> when the
/// split is an interpretation rather than an explicit list in the text.</summary>
public sealed record StoryTestObjective(string Text, StoryObjectiveKind Kind, string Source, bool NeedsReview);

public static partial class StoryInterpretation
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\[(US-[A-Za-z0-9]+(?:\s*,\s*US-[A-Za-z0-9]+)*)\]")]
    private static partial System.Text.RegularExpressions.Regex ReferenceBlock();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:verify|verifies|check|checks|confirm|confirms|ensure|ensures|assert|asserts|observe|observes)\s+(?:that\s+)?(?<list>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VerifyClause();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:\w+\s+)?(?<verb>submit|send|create|post|publish|run|use|enter|search|open|request|deliver|upload)\s+(?<mods>(?:[\w-]+,\s+)*[\w-]+\s+(?:and|or)\s+[\w-]+)\s+(?<noun>[\w-]+)(?<rest>.*?)(?:\s+and\s*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ConditionList();

    /// <summary>Story ids the description links to ("[US-B1, US-B2]"). Never the story's own id: US1 is not an alias of US-B1.</summary>
    public static IReadOnlyList<string> ReferencedStories(string? description) =>
        description is null ? [] : [.. ReferenceBlock().Matches(description).SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal)];

    /// <summary>The coverage objectives of an "Independent Test" text: conditions to exercise and things to verify, in source order.</summary>
    public static IReadOnlyList<StoryTestObjective> Objectives(string? independentTest)
    {
        if (string.IsNullOrWhiteSpace(independentTest)) return [];
        var result = new List<StoryTestObjective>();
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(independentTest.Trim(), @"(?<=[.!?])\s+"))
        {
            var sentence = raw.Trim().TrimEnd('.');
            if (sentence.Length == 0) continue;
            var verify = VerifyClause().Match(sentence);
            var before = verify.Success ? sentence[..verify.Index].Trim().TrimEnd(',') : sentence;
            var conditions = ConditionList().Match(before);
            if (conditions.Success)
            {
                var mods = System.Text.RegularExpressions.Regex.Split(conditions.Groups["mods"].Value, @",\s+|\s+(?:and|or)\s+").Where(m => m.Length > 0).ToList();
                var noun = conditions.Groups["noun"].Value;
                var rest = conditions.Groups["rest"].Value.Trim();
                foreach (var m in mods)
                    result.Add(new StoryTestObjective($"{m} {noun}{(rest.Length > 0 ? " " + rest : "")}", StoryObjectiveKind.Condition, raw.Trim(), NeedsReview: true));
            }
            else if (before.Length > 0 && verify.Success)
                result.Add(new StoryTestObjective(before, StoryObjectiveKind.Condition, raw.Trim(), NeedsReview: true));
            if (verify.Success)
            {
                var items = SplitList(verify.Groups["list"].Value);
                foreach (var item in items)
                    result.Add(new StoryTestObjective(item, StoryObjectiveKind.Verification, raw.Trim(), NeedsReview: items.Count == 1 && item.Contains(" and ", StringComparison.Ordinal)));
            }
            else if (!conditions.Success)
                result.Add(new StoryTestObjective(sentence, StoryObjectiveKind.Verification, raw.Trim(), NeedsReview: true));
        }
        return result;
    }

    /// <summary>A comma list ("the stored outcome, the rejection reasons, the flags, and that no … exists") — split at commas only, so an
    /// "and" inside one item is never split; a leading "that" is dropped.</summary>
    private static List<string> SplitList(string list) =>
        [.. System.Text.RegularExpressions.Regex.Split(list.Trim().TrimEnd('.'), @",\s+(?:and\s+)?")
            .Select(x => System.Text.RegularExpressions.Regex.Replace(x.Trim(), @"^(?:and\s+)?(?:that\s+)?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(x => x.Length > 0)];
}
