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
    public TestLevel IntendedLevel { get; init; } = TestLevel.Unknown;
    public string IntendedLevelBasis { get; init; } = "";
    public TestExecutionState Execution { get; init; } = TestExecutionState.NotVerified;
}

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
    public CoverageSnapshotRef Current { get; init; } = new(Guid.Empty, "", "", "", default);
    public CoverageSnapshotRef? Baseline { get; init; }
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
}

// ── Request / decisions / history ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>Workspace evidence the browser owns: requirements, acceptance scenarios (QA candidates) and imported test executions.</summary>
public sealed record CoverageWorkspaceEvidence
{
    public string? ProjectName { get; init; }
    public bool SpecificationAvailable { get; init; }
    public List<CoverageRequirement> Requirements { get; init; } = [];
    public List<QaTestCandidate> AcceptanceScenarios { get; init; } = [];
    public List<CoverageExecution> Executions { get; init; } = [];
}

public sealed record CoverageRequirement(string Id, string Text, string? Priority);

/// <summary>An imported execution result (TRX via the SDD lifecycle). TestId is the correlated test definition id when known.</summary>
public sealed record CoverageExecution(string TestId, string TestName, string Result);

public sealed record TestCoverageReviewRequest
{
    public Guid CurrentSnapshotId { get; init; }
    public Guid? BaselineSnapshotId { get; init; }
    /// <summary>Optional Target Environment whose Critical E2E flows count as QA automated tests (definitions and recorded runs only; nothing runs).</summary>
    public string? EnvironmentId { get; init; }
    public CoverageWorkspaceEvidence? Workspace { get; init; }
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
