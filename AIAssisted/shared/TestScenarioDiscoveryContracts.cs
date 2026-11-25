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
public sealed record CandidateTestAssociation(string TestId, string Name, string? File, string Basis, bool Confirmed = true)
{
    /// <summary>How the test is tied to THIS scenario (2026-11-25): it names the scenario itself, or only a requirement the scenario also names
    /// (supporting evidence — never shown as a link to the scenario).</summary>
    public TestScenarioLinkOrigin Origin { get; init; } = TestScenarioLinkOrigin.SharedRequirement;
    /// <summary>Unit / Integration / Contract / FrontendComponent / Unknown (from the test project; never inferred from the name).</summary>
    public string? Layer { get; init; }
    public string? Framework { get; init; }
    public string? ClassName { get; init; }
    public string? MethodName { get; init; }
    /// <summary>SHA-256 of the test method's source: a reviewer decision made on another fingerprint must be reviewed again.</summary>
    public string? SourceFingerprint { get; init; }
}

/// <summary>Whether a test names the scenario itself or only a requirement the scenario also names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestScenarioLinkOrigin { SharedRequirement, Scenario }

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
    /// <summary>The user story the scenario is written under (specification acceptance scenarios only); null = no story identified. Kept out of
    /// <see cref="Fingerprint"/>: a story move is reported on its own, never as a changed scenario.</summary>
    public BirkNext.TestCoverage.CoverageUserStory? Story { get; init; }
    /// <summary>The specification's clarifications about this candidate's requirements or story (open, documented, resolved).</summary>
    public List<BirkNext.TestCoverage.CoverageClarification> Clarifications { get; init; } = [];
    /// <summary>The scenario's number within its story.</summary>
    public int? NumberInStory { get; init; }
    /// <summary>Project-independent identity (capability + canonical key): the same scenario in another import of the same application.</summary>
    public string SourceIdentity { get; init; } = "";
    /// <summary>Identities this scenario had under an earlier identity rule (2026-11-23: UI scenarios were keyed by project), so cases prepared
    /// before still find it. Never shown; empty for most candidates.</summary>
    public List<string> FormerIdentities { get; init; } = [];
    /// <summary>Canonical keys of exact duplicates from other sources merged into this candidate (their evidence is kept above).</summary>
    public List<string> MergedFrom { get; init; } = [];
    /// <summary>Equivalent, related, variant or conflicting candidates of other sources (never merged).</summary>
    public List<ScenarioRelationNote> Relations { get; init; } = [];
    /// <summary>Expected-result conflicts with other sources for the same situation (each claim kept with its provenance).</summary>
    public List<ScenarioConflict> Conflicts { get; init; } = [];
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
    /// <summary>The analyzer failed for at least one source of the basis (another may still have had evidence): its absence there is not evidence.</summary>
    public bool PartialFailure { get; init; }
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
    /// <summary>Every source snapshot bound to the project (any role, any version). The server maps each to its logical source and resolves the
    /// version through the analysis baseline, so the result has one consistent source basis. Empty = only <see cref="SnapshotId"/>.</summary>
    public List<Guid> MemberSnapshotIds { get; init; } = [];
    /// <summary>True = a deliberate look at <see cref="SnapshotId"/> even when it is not the source's current baseline version (historical
    /// inspection). Such a result never becomes the project's current discovery.</summary>
    public bool Inspect { get; init; }
    /// <summary>True = use the browser's working specification text (<see cref="Workspace"/>) instead of the imported specification. The result is
    /// then marked as draft-based with the working text's fingerprint; it is never presented as the imported source.</summary>
    public bool UseWorkingSpecification { get; init; }
}

/// <summary>What a discovery result rests on: Current = the analysis baseline of every source; Historical = a deliberate inspection of another
/// version; DocumentsOnly = no source snapshot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryBasisKind { Current, Historical, DocumentsOnly }

/// <summary>Where the specification scenarios came from: the imported specification of the basis (server-held), the browser's working text (draft),
/// the browser text of an older client (no imported specification known), or none.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecificationOrigin { None, ImportedSnapshot, WorkingDraft, BrowserUnverified }

/// <summary>One logical source of a basis: its identity, the version used and why (baseline state code).</summary>
public sealed record DiscoverySourceMember
{
    /// <summary>The logical source identity (repository key), shared by every import of the same source.</summary>
    public string RepositoryKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Guid? SnapshotId { get; init; }
    public string? ArchiveName { get; init; }
    public DateTimeOffset? AnalyzedAt { get; init; }
    public string? ContentFingerprint { get; init; }
    /// <summary>BASELINE_SELECTED, SINGLE_SNAPSHOT, IDENTICAL_CONTENT, BASELINE_NOT_SELECTED, BASELINE_UNAVAILABLE, or INSPECTED (a deliberate other version).</summary>
    public string StateCode { get; init; } = "";
    /// <summary>A newer import of this source that is not the version used (shown as "Ny kildeversjon"); null when none.</summary>
    public Guid? NewerSnapshotId { get; init; }
    public string? NewerArchiveName { get; init; }
    [JsonIgnore] public bool Resolved => SnapshotId is not null;
}

/// <summary>The consistent source basis of a discovery result (and of a plan prepared from it).</summary>
public sealed record DiscoverySourceBasis
{
    /// <summary>The application the sources belong to: the logical identity of its application source (the same for every import of it); the
    /// project key when no source snapshot is known.</summary>
    public string ApplicationKey { get; init; } = "";
    public DiscoveryBasisKind Kind { get; init; }
    public List<DiscoverySourceMember> Members { get; init; } = [];
    public SpecificationOrigin SpecificationOrigin { get; init; }
    /// <summary>The imported specification catalog used (its key and revision), or the working text's fingerprint for a draft.</summary>
    public string? SpecificationReference { get; init; }
    public string? SpecificationFingerprint { get; init; }
    /// <summary>Identity of the basis: changes when any member version or the specification changes.</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary>Sources that could not be resolved (no baseline among different versions, unavailable baseline …), in plain words.</summary>
    public List<string> Problems { get; init; } = [];
    [JsonIgnore] public bool Complete => Problems.Count == 0 && Members.All(m => m.Resolved);
}

/// <summary>How two candidates of different sources relate (2026-11-23 consolidation). Exact duplicates are merged (evidence kept); everything else
/// stays a separate candidate with this note for a reviewer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioRelation { ExactDuplicate, Equivalent, Related, Conflicting, Variant }

public sealed record ScenarioRelationNote(ScenarioRelation Relation, string OtherCandidateId, string Reason);

/// <summary>One source's claim about the expected result of the same situation; kept with its exact provenance, never chosen automatically.</summary>
public sealed record ScenarioClaim(string CandidateId, string SourceCategory, string Expected, string EvidenceReference);

/// <summary>Why two expected results disagree (or apparently disagree).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictKind { ContradictoryExpectedResult, DifferentPreconditions, DifferentSourceVersion, DifferentFailureCondition, TranslationInconsistency, UnsupportedClaim }

/// <summary>An expected-result conflict between sources for the same situation. Generated cases with an unresolved conflict are never Ready.</summary>
public sealed record ScenarioConflict
{
    public ConflictKind Kind { get; init; }
    public List<ScenarioClaim> Claims { get; init; } = [];
    /// <summary>Plain Norwegian explanation shown to testers.</summary>
    public string Explanation { get; init; } = "";
}

/// <summary>
/// The selection a discovery is computed for (2026-11-25): the module scope and the selected specification of the workspace evidence the discovery
/// reads, or «all specifications» when no feature is chosen. A result is shown only for the selection it was computed for, so another module's
/// scenarios are never shown as the current module's. Derived from the same evidence the request sends (no second identity).
/// </summary>
public static class DiscoveryScope
{
    public const string AllSpecifications = "*";

    public static string Of(BirkNext.TestCoverage.CoverageWorkspaceEvidence? workspace)
    {
        var module = (workspace?.ArtifactScope ?? "").Trim().Replace('\\', '/').ToLowerInvariant();
        var spec = workspace?.SpecificationArtifacts.FirstOrDefault()?.Path is { Length: > 0 } path ? path.Trim().Replace('\\', '/').ToLowerInvariant() : AllSpecifications;
        return module + "|" + spec;
    }
}

public sealed record ScenarioDiscoveryResult
{
    /// <summary>The selection (<see cref="DiscoveryScope"/>) this result was computed for; null for results stored before 2026-11-25 (never shown
    /// for a scoped request, so they are recomputed once).</summary>
    public string? SelectionScope { get; init; }
    /// <summary>Identity of this result: project + snapshot + evidence fingerprint + rule version.</summary>
    public string DiscoveryId { get; init; } = "";
    /// <summary>Whether the source's tests were read for scenario references (2026-11-25). False = no tests, or an older discovery: a scenario
    /// without a linked test is then «ikke vurdert», never «ingen kjent automatisk test».</summary>
    public bool TestsReadForScenarios { get; init; }
    public string ProjectKey { get; init; } = "";
    public string? ProjectName { get; init; }
    public string SnapshotKey { get; init; } = "";
    public string? SnapshotLabel { get; init; }
    public string RuleVersion { get; init; } = "";
    public DateTimeOffset DiscoveredAt { get; init; }
    public List<DiscoveryCapability> Capabilities { get; init; } = [];
    public List<DiscoveredScenario> Candidates { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>The consistent source basis this result was discovered against (null for results stored before 2026-11-23).</summary>
    public DiscoverySourceBasis? Basis { get; init; }
    [JsonIgnore] public bool HasCandidates => Candidates.Count > 0;
    [JsonIgnore] public string ApplicationKey => Basis?.ApplicationKey is { Length: > 0 } a ? a : ProjectKey;
    [JsonIgnore] public bool IsCurrent => Basis is null || Basis.Kind != DiscoveryBasisKind.Historical;
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
/// <see cref="Variant"/> = start from a variant the scenario's evidence supports (its key from the variant proposals).
/// <see cref="TestKind"/> = Manuell or Teknisk chosen when creating the case (2026-11-25); a duplicate keeps its source case's type unless one is given.</summary>
public sealed record AddManualTestCaseRequest(int BaseRevision, string Title, string? Purpose, ScenarioCandidateType? Type,
    string? ScenarioKey = null, string? CopyFrom = null, string? Variant = null, ScenarioTestKind? TestKind = null);

/// <summary>
/// What changed in an application's scenarios with a new import (2026-11-23): the new import's discovery compared with the application's previous
/// current discovery, by the stable scenario identity. Only computed counts: <see cref="Compared"/> false (with <see cref="Reason"/>) when there is
/// nothing to compare with. Removed counts only scenarios whose source was read completely in the new version; the rest are <see cref="NotCompared"/>.
/// </summary>
public sealed record ScenarioChangeSummary
{
    public string ApplicationKey { get; init; } = "";
    public bool Compared { get; init; }
    public string? Reason { get; init; }
    public string CurrentDiscoveryId { get; init; } = "";
    public string? PreviousDiscoveryId { get; init; }
    /// <summary>The archives the previous discovery was made from (for "compared with …").</summary>
    public List<string> PreviousArchives { get; init; } = [];
    public int New { get; init; }
    /// <summary>Title, preconditions, steps or expected result differ (evidence paths alone do not count).</summary>
    public int Changed { get; init; }
    public int Removed { get; init; }
    public int Unchanged { get; init; }
    /// <summary>The same text in a new place (renumbered, or its file moved): the unique scenario with exactly the same content. Not a change.</summary>
    public int Moved { get; init; }
    public int NotCompared { get; init; }
    /// <summary>Scenarios of the new version whose sources disagree about the expected result.</summary>
    public int Conflicts { get; init; }
}

// ── Work queues of Finn og forstå (2026-11-24) ──────────────────────────────────────────────────────────────────────────────────────────────
// What a manual tester should prepare next, computed once on the server from the discovery and every saved plan of the application (the same
// scenario resolution plan status and impact use). Queues are filtered views of the one candidate list, never copies; a scenario may be in several.
// "Recommended" means ready to PREPARE a test case — never that the test is ready to execute (test data, access and observation are separate).

/// <summary>A work queue of Finn og forstå. Membership is independent per queue (a scenario can be in several).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioWorkQueue
{
    /// <summary>Relevant, clear, supported expected result, current source, no current test case: ready for test-case preparation.</summary>
    Recommended,
    /// <summary>Something must be clarified before a test case can be prepared (expected result, conflict, open question, source association).</summary>
    NeedsClarification,
    /// <summary>Has at least one current test case (draft, in progress or approved) in an active plan.</summary>
    Prepared,
    /// <summary>A saved test case of the scenario was prepared from source that has changed since, or the discovery is not on the current source.</summary>
    SourceChanged,
}

/// <summary>Saved test cases of one scenario, by state. Current coverage = active plans, not superseded, not excluded.</summary>
public sealed record ScenarioCaseCoverage
{
    /// <summary>Generated and not yet reviewed (utkast).</summary>
    public int Draft { get; init; }
    /// <summary>Under review or waiting for clarification (under arbeid).</summary>
    public int InProgress { get; init; }
    /// <summary>Accepted by a reviewer in BirkNext (never an Azure DevOps approval or an execution).</summary>
    public int Approved { get; init; }
    /// <summary>Deliberately excluded by a reviewer: a decision, not coverage.</summary>
    public int Excluded { get; init; }
    /// <summary>Superseded (the scenario left the source at the time): history, not current coverage.</summary>
    public int Historical { get; init; }
    /// <summary>Cases in archived plans: kept and readable, not current coverage.</summary>
    public int Archived { get; init; }
    /// <summary>Current cases whose scenario changed in the current source since they were prepared.</summary>
    public int SourceChanged { get; init; }
    /// <summary>Current cases with an unresolved expected-result conflict.</summary>
    public int Conflicts { get; init; }
    /// <summary>The active plans holding current cases of the scenario.</summary>
    public List<Guid> Plans { get; init; } = [];
    [JsonIgnore] public int Current => Draft + InProgress + Approved;
}

/// <summary>One scenario's place in the work queues, with plain-Norwegian reasons (no score) and what stands between it and execution.</summary>
public sealed record ScenarioWorkItem
{
    public string CandidateId { get; init; } = "";
    public List<ScenarioWorkQueue> Queues { get; init; } = [];
    /// <summary>Position in <see cref="ScenarioWorkQueue.Recommended"/> (1 = prepare first); null when not recommended. Deterministic ordering, not a score.</summary>
    public int? Rank { get; init; }
    /// <summary>The user story's declared priority (e.g. "P1"); null when the source states none — never inferred.</summary>
    public string? Priority { get; init; }
    /// <summary>Saved test cases; null when the scenario has none (coverage known, nothing saved).</summary>
    public ScenarioCaseCoverage? Coverage { get; init; }
    /// <summary>Why the scenario is in its queues, short and in Norwegian (for example «Mangler testtilfelle», «Forventet resultat dokumentert»).</summary>
    public List<string> Reasons { get; init; } = [];
    /// <summary>What must be clarified before a test case can be prepared (empty when nothing).</summary>
    public List<string> Clarifications { get; init; } = [];
    /// <summary>Ready to prepare is not ready to execute: test data, access or a way to observe the result that the execution still needs.</summary>
    public List<string> ExecutionNeeds { get; init; } = [];
}

/// <summary>The work queues of one discovery result.</summary>
public sealed record ScenarioWorkQueueView
{
    public string DiscoveryId { get; init; } = "";
    /// <summary>False when the discovery is not on the application's current source (a historical import): nothing is recommended then.</summary>
    public bool DiscoveryCurrent { get; init; }
    /// <summary>False when a saved plan could not be read: a scenario without known cases is then "unknown", never "no test case".</summary>
    public bool CoverageComplete { get; init; }
    public int PlansRead { get; init; }
    public int PlansUnreadable { get; init; }
    /// <summary>Current test cases whose scenario is not in this discovery (removed from the source, or source unavailable): shown, never dropped.</summary>
    public int UnmatchedCases { get; init; }
    public List<ScenarioWorkItem> Items { get; init; } = [];
}
