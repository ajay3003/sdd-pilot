using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// Project-scoped test scenario discovery for Test Plan Suggestions. A small orchestration over analyzers BirkNext already registers (specification
// parsing, System Understanding guides, frontend UI discovery, contract evidence, declared/pack integration journeys, the xUnit test inventory).
// Each capability reports only what its analyzer supports for the selected project; nothing here invents a scenario, an expected result or a
// duplicate. Candidates are guidance: not Test Cases, not coverage, never executed.

/// <summary>What a candidate verifies. A route-opening check is <see cref="UiSmoke"/>, never <see cref="BusinessE2E"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioCandidateType { BusinessFunctional, UiFunctional, UiSmoke, ApiContract, Integration, BusinessE2E, Performance, SecurityAuthorization }

/// <summary>How a candidate is established. <see cref="Insufficient"/>: too little evidence to say what to verify.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CandidateConfidence { ExplicitSpecification, SourceDerived, ContractDerived, ExistingTest, CandidateAssociation, HumanConfirmed, Insufficient }

/// <summary>Available: the analyzer ran and found evidence. NoEvidence: it ran, the project has none of its input. NotApplicable: its input is
/// not part of this project (e.g. no source snapshot). Failed: the analyzer could not read the evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryCapabilityState { Available, NoEvidence, NotApplicable, Failed }

/// <summary>Readiness of a case for test preparation. "No test data described" is never <see cref="NotRequired"/>: only a person decides that.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaseReadiness { NotAssessed, NotRequired, RequiredAndAvailable, RequiredButMissing, NeedsClarification, LinkedButUnsuitable, BlockedByPrerequisite }

/// <summary>Whether a case needs Test Data. Unknown unless evidence states inputs (Required) or a person decides.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataNeed { Unknown, NotRequired, Required }

public static class ScenarioCandidateTypes
{
    /// <summary>The Test Case type a discovered candidate type corresponds to (null = not classified).</summary>
    public static TestCaseType ToTestCaseType(ScenarioCandidateType? type) => type switch
    {
        ScenarioCandidateType.BusinessFunctional or ScenarioCandidateType.UiFunctional or ScenarioCandidateType.UiSmoke => TestCaseType.Functional,
        ScenarioCandidateType.ApiContract => TestCaseType.ApiContract,
        ScenarioCandidateType.Integration => TestCaseType.Integration,
        ScenarioCandidateType.BusinessE2E => TestCaseType.EndToEnd,
        ScenarioCandidateType.Performance => TestCaseType.Performance,
        ScenarioCandidateType.SecurityAuthorization => TestCaseType.Security,
        _ => TestCaseType.NotClassified,
    };

    public static string Label(ScenarioCandidateType type) => type switch
    {
        ScenarioCandidateType.BusinessFunctional => "Business functional",
        ScenarioCandidateType.UiFunctional => "UI functional",
        ScenarioCandidateType.UiSmoke => "UI smoke",
        ScenarioCandidateType.ApiContract => "API / contract",
        ScenarioCandidateType.Integration => "Integration",
        ScenarioCandidateType.BusinessE2E => "Business end-to-end",
        ScenarioCandidateType.Performance => "Performance",
        ScenarioCandidateType.SecurityAuthorization => "Security / authorization",
        _ => type.ToString(),
    };

    public static string ConfidenceLabel(CandidateConfidence confidence) => confidence switch
    {
        CandidateConfidence.ExplicitSpecification => "Explicit in the specification",
        CandidateConfidence.SourceDerived => "Derived from source code",
        CandidateConfidence.ContractDerived => "Derived from an API contract",
        CandidateConfidence.ExistingTest => "From an existing test",
        CandidateConfidence.CandidateAssociation => "Candidate association — needs review",
        CandidateConfidence.HumanConfirmed => "Confirmed by a person",
        _ => "Insufficient evidence",
    };
}

/// <summary>
/// What kind of thing is still open about a scenario (2026-11-18). One "Needs clarification" used to stand for all of these; they need
/// different people and different actions, so they are kept apart. Grounded in the evidence that produced the scenario — never cosmetic.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceGapKind
{
    /// <summary>The expected business behaviour is not established by an authoritative specification or approved rule.</summary>
    BusinessExpectationUnclear,
    /// <summary>The expectation is documented, but the implementation cannot be established from the available source evidence.</summary>
    ImplementationEvidenceMissing,
    /// <summary>The expectation is clear; the test needs Test Data, credentials, access, configuration or a controlled environment first.</summary>
    TestPrerequisiteMissing,
    /// <summary>The expected result is understood, but no permitted way (API, log, database, read contract) exists to observe the actual result.</summary>
    VerificationMechanismMissing,
    /// <summary>Potentially relevant evidence exists, but its relationship to this project or scenario is not confirmed.</summary>
    AssociationNeedsReview,
    /// <summary>Static evidence (specification, source review, contract) exists; no execution has verified the behaviour.</summary>
    RuntimeEvidenceMissing,
    /// <summary>Two trustworthy sources disagree; both are kept and a person decides.</summary>
    ContradictoryEvidence,
    /// <summary>Relevant evidence exists but has not been examined yet.</summary>
    EvidenceNotEvaluated,
}

/// <summary>One open point of a scenario: what kind it is, the question in plain language, what it rests on and the supported next action.</summary>
public sealed record EvidenceGap(EvidenceGapKind Kind, string Question, string? Basis = null, string? NextAction = null);

/// <summary>A source BirkNext examined for a scenario and what it found there (Found false = examined, nothing relevant).</summary>
public sealed record EvidenceSourceCheck(string Source, bool Found, string Detail);

public static class EvidenceGaps
{
    public static string Label(EvidenceGapKind kind) => kind switch
    {
        EvidenceGapKind.BusinessExpectationUnclear => "Business expectation unclear",
        EvidenceGapKind.ImplementationEvidenceMissing => "Implementation evidence missing",
        EvidenceGapKind.TestPrerequisiteMissing => "Test prerequisite missing",
        EvidenceGapKind.VerificationMechanismMissing => "Verification mechanism missing",
        EvidenceGapKind.AssociationNeedsReview => "Source association needs review",
        EvidenceGapKind.RuntimeEvidenceMissing => "Runtime evidence missing",
        EvidenceGapKind.ContradictoryEvidence => "Contradictory evidence",
        EvidenceGapKind.EvidenceNotEvaluated => "Evidence not evaluated",
        _ => kind.ToString(),
    };

    /// <summary>Who usually resolves it — shown so a tester knows whether to ask the business, prepare data, or run something.</summary>
    public static string Owner(EvidenceGapKind kind) => kind switch
    {
        EvidenceGapKind.BusinessExpectationUnclear or EvidenceGapKind.ContradictoryEvidence => "Product owner or business analyst",
        EvidenceGapKind.ImplementationEvidenceMissing => "Developer or source analysis",
        EvidenceGapKind.TestPrerequisiteMissing => "Tester (Test Data, access, environment)",
        EvidenceGapKind.VerificationMechanismMissing => "Developer or operations (observability)",
        EvidenceGapKind.AssociationNeedsReview or EvidenceGapKind.EvidenceNotEvaluated => "Tester (review)",
        EvidenceGapKind.RuntimeEvidenceMissing => "Tester (run)",
        _ => "",
    };
}

/// <summary>One piece of evidence behind a candidate. Kind: specification, requirement, guide-scenario, source-file, contract-operation, journey,
/// test. Reference is a stable pointer (file:line, operation, id); Detail is short plain text (never raw markup).</summary>
public sealed record CandidateEvidenceRef(string Kind, string Reference, string? Detail = null);

/// <summary>An existing test associated with a candidate through an explicit reference (requirement id in the test's traits/references), never by name.
/// <see cref="Confirmed"/> false: the reference is inferred and needs review.</summary>
public sealed record CandidateTestAssociation(string TestId, string Name, string? File, string Basis, bool Confirmed = true);

/// <summary>A type-specific template field (operation, trigger, producer, workload …). Value null = not in the evidence (never filled in).</summary>
public sealed record CandidateTemplateField(string Key, string Label, string? Value);

/// <summary>One step with its expected result (null = not in the evidence: Needs clarification).</summary>
public sealed record DiscoveredStep(string Action, string? Expected);

public sealed record DiscoveredScenario
{
    /// <summary>Stable, project-scoped id derived from the project key, the capability and the source identity (never the snapshot).</summary>
    public string CandidateId { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    /// <summary>The source snapshot the candidate was read from ("" for workspace documents only).</summary>
    public string SnapshotKey { get; init; } = "";
    public string CapabilityId { get; init; } = "";
    /// <summary>Plain-language source category ("Specification", "UI source", "API contract" …) used for grouping and filtering.</summary>
    public string SourceCategory { get; init; } = "";
    /// <summary>The module, feature, page or contract the evidence names; null when none is stated.</summary>
    public string? Area { get; init; }
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public ScenarioCandidateType Type { get; init; }
    public CandidateConfidence Confidence { get; init; }
    /// <summary>The expected behaviour as the evidence states it; null = Needs clarification (never invented).</summary>
    public string? ExpectedBehaviour { get; init; }
    public string? ExpectedBasis { get; init; }
    public List<string> Preconditions { get; init; } = [];
    public List<DiscoveredStep> Steps { get; init; } = [];
    public List<CandidateTemplateField> Template { get; init; } = [];
    public List<string> TestDataNeeds { get; init; } = [];
    public List<CandidateEvidenceRef> Evidence { get; init; } = [];
    /// <summary>Requirement ids the evidence names explicitly (FR-001 …).</summary>
    public List<string> RequirementIds { get; init; } = [];
    /// <summary>The canonical identity the candidate is deduplicated on ("scenario:US1-1", "requirement:FR-001", "ui:sug-…" …).</summary>
    public string CanonicalKey { get; init; } = "";
    /// <summary>Project Test Setup scope member keys ("Scenario:…", "Journey:…") this candidate is known by, for the test-area filter.</summary>
    public List<string> ScopeKeys { get; init; } = [];
    public List<CandidateTestAssociation> ExistingTests { get; init; } = [];
    public List<string> Prerequisites { get; init; } = [];
    public string RuleVersion { get; init; } = "";
    public string Engine { get; init; } = "";
    public List<string> OpenQuestions { get; init; } = [];
    /// <summary>The open points, each classified (expectation, implementation, prerequisite, verification, association, runtime …).</summary>
    public List<EvidenceGap> Gaps { get; init; } = [];
    /// <summary>The sources BirkNext examined for this candidate and what each contributed (so "not found" is distinguishable from "not looked at").</summary>
    public List<EvidenceSourceCheck> SourcesChecked { get; init; } = [];
    /// <summary>Candidates that may describe the same behaviour (shared requirement id, different source); kept separate on purpose.</summary>
    public List<string> PossibleDuplicates { get; init; } = [];
    /// <summary>Content fingerprint of the evidence: a change means the candidate changed in the source.</summary>
    public string Fingerprint { get; init; } = "";
    [JsonIgnore] public bool NeedsClarification => ExpectedBehaviour is null;
}

public sealed record DiscoveryCapability
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>What the analyzer reads and supports, in one sentence.</summary>
    public string Description { get; init; } = "";
    public DiscoveryCapabilityState State { get; init; }
    public int Candidates { get; init; }
    /// <summary>Why the state is what it is ("3 OpenAPI operations in 1 contract", "No source snapshot is linked to this project").</summary>
    public string Detail { get; init; } = "";
    public string Engine { get; init; } = "";
    public List<string> Limitations { get; init; } = [];
}

/// <summary>An explicit discovery run for one project. The browser sends the workspace documents it owns (specification requirements and acceptance
/// scenarios, parsed by the existing specification parser); the server reads the source snapshot and registered analyzers.</summary>
public sealed record ScenarioDiscoveryRequest
{
    public string ProjectKey { get; init; } = "";
    public string? ProjectName { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? EnvironmentId { get; init; }
    public BirkNext.TestCoverage.CoverageWorkspaceEvidence? Workspace { get; init; }
}

public sealed record ScenarioDiscoveryResult
{
    /// <summary>Identity of this result: project + snapshot + evidence fingerprint + rule version.</summary>
    public string DiscoveryId { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public string? ProjectName { get; init; }
    public string SnapshotKey { get; init; } = "";
    public string? SnapshotLabel { get; init; }
    public string RuleVersion { get; init; } = "";
    public DateTimeOffset DiscoveredAt { get; init; }
    public List<DiscoveryCapability> Capabilities { get; init; } = [];
    public List<DiscoveredScenario> Candidates { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    [JsonIgnore] public bool HasCandidates => Candidates.Count > 0;
}

/// <summary>Prepare test cases from selected candidates of one project. A plan name is optional here (asked for only at save/export).</summary>
public sealed record CreateProjectTestPlanRequest
{
    public string ProjectKey { get; init; } = "";
    public string? ProjectName { get; init; }
    public string DiscoveryId { get; init; } = "";
    public List<string> CandidateIds { get; init; } = [];
    public string? PlanName { get; init; }
    public string? DeliveryLabel { get; init; }
    /// <summary>The Project Test Setup test areas the selection was made in (ids only; optional).</summary>
    public List<string> ScopeIds { get; init; } = [];
    /// <summary>Add these to an existing draft of the same project instead of creating one.</summary>
    public Guid? DraftId { get; init; }
    public int? BaseRevision { get; init; }
    /// <summary>Start with one hand-written case (e.g. when discovery found nothing). Never generated content.</summary>
    public string? ManualCaseTitle { get; init; }
}

/// <summary>A test case a person writes by hand: title and purpose; steps are added while editing. <see cref="ScenarioKey"/> = write it under that
/// scenario of the plan (it starts from the scenario's starting situation; expected results are never filled in). <see cref="CopyFrom"/> = a new
/// case with its own identity, starting from that case's current text (review, history and Azure DevOps id are not copied).
/// <see cref="Variant"/> = start from a variant the scenario's evidence supports (its key from the variant proposals).</summary>
public sealed record AddManualTestCaseRequest(int BaseRevision, string Title, string? Purpose, ScenarioCandidateType? Type,
    string? ScenarioKey = null, string? CopyFrom = null, string? Variant = null);
