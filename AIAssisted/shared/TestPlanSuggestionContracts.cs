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
public enum ScenarioSourceKind { SystemGuide, IntegrationJourney, CriticalE2EFlow,
    /// <summary>A candidate from project-scoped scenario discovery (<see cref="DiscoveredScenario"/>): Owner = project key, ScenarioId = candidate id.</summary>
    ProjectDiscovery,
    /// <summary>A test case a person created by hand (no discovered source). Owner = project key.</summary>
    Manual }

/// <summary>Generated → UnderReview → NeedsClarification | Accepted (for preparation/export) | Excluded; Superseded when the scenario left the
/// source. Accepted is a BirkNext review decision: never an Azure DevOps approval and never an execution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCaseReviewState { Generated, UnderReview, NeedsClarification, Accepted, Excluded, Superseded }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCaseRisk { NotAssessed, Low, Medium, High }

/// <summary>What a test case verifies. Independent of <see cref="TestExecutionMethod"/>: a performance case is not "automated" by type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCaseType { NotClassified, Functional, Integration, EndToEnd, Performance, Security, ApiContract }

/// <summary>How a test case is carried out. Independent of <see cref="TestCaseType"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionMethod { NotDecided, Manual, ToolAssisted, Automated }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceTestSubtype { NotSet, Load, Stress, Spike, Soak, Capacity, Baseline }

/// <summary>
/// A performance test case in a tester's words: every field is optional free text, so a manual tester can describe the business workload and
/// leave the technical parts for a performance tester. Nothing is generated or assumed (no default users, durations or thresholds), and
/// nothing here is executed. Execution and its evidence belong to Performance Test Review.
/// </summary>
public sealed record PerformanceCaseDetails
{
    public PerformanceTestSubtype Subtype { get; init; }
    public string? OperationUnderTest { get; init; }
    public string? Workload { get; init; }
    public string? Concurrency { get; init; }
    public string? ArrivalRate { get; init; }
    public string? RampUp { get; init; }
    public string? SteadyStateDuration { get; init; }
    public string? RampDown { get; init; }
    public string? TargetEnvironment { get; init; }
    public string? DataVolume { get; init; }
    public string? Measurements { get; init; }
    /// <summary>Explicit thresholds and conditions (e.g. "p95 under 800 ms at 50 users"); empty = not defined, review required.</summary>
    public string? AcceptanceCriteria { get; init; }
    public string? ThresholdSource { get; init; }
    public string? Monitoring { get; init; }
    public string? ResponsibleRole { get; init; }
    public string? Cleanup { get; init; }
    /// <summary>A reference to the Performance Test Review scenario that refines/executes this case (its owner keeps execution evidence).</summary>
    public string? PerformanceReviewReference { get; init; }
    [JsonIgnore] public bool IsEmpty => Subtype == PerformanceTestSubtype.NotSet && new[] { OperationUnderTest, Workload, Concurrency, ArrivalRate, RampUp, SteadyStateDuration,
        RampDown, TargetEnvironment, DataVolume, Measurements, AcceptanceCriteria, ThresholdSource, Monitoring, ResponsibleRole, Cleanup, PerformanceReviewReference }.All(string.IsNullOrWhiteSpace);
}

/// <summary>How an expected result is established: by a reviewed (published) guide revision, by an unreviewed curated draft, by a reviewer's
/// own clarification, or not at all. Implementation behaviour is never a basis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExpectedResultBasis { ReviewedGuide, UnreviewedGuide, ReviewerClarified, Missing, NeedsClarification,
    /// <summary>Stated explicitly by the project's specification (an acceptance scenario's "Then" or a requirement statement); confirmed by accepting.</summary>
    Specification,
    /// <summary>Documented by an API contract (declared responses); confirmed by accepting.</summary>
    Contract,
    /// <summary>Proposed from source code (e.g. a static message). Implementation behaviour is not a basis: a reviewer must confirm or clarify it.</summary>
    SourceDerived }

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

    // Project discovery (null/empty for System Understanding guide cases, which keep loading unchanged).
    /// <summary>The suite title proposed for <see cref="StageId"/> when it is a project area ("area:…") rather than a guide stage.</summary>
    public string? GroupTitle { get; init; }
    public ScenarioCandidateType? CandidateType { get; init; }
    public CandidateConfidence? Confidence { get; init; }
    /// <summary>The expected result of each step, parallel to <see cref="Steps"/>; null = not in the evidence (Needs clarification).</summary>
    public List<string?> StepExpected { get; init; } = [];
    /// <summary>Type-specific template fields (operation, trigger, producer …); a null value is not in the evidence and is never filled in.</summary>
    public List<CandidateTemplateField> Template { get; init; } = [];
    public List<CandidateEvidenceRef> Evidence { get; init; } = [];
    public List<CandidateTestAssociation> ExistingTests { get; init; } = [];
    public List<string> OpenQuestions { get; init; } = [];
    /// <summary>The specification's clarifications about this case's requirements or story, as they were when the case was prepared.</summary>
    public List<BirkNext.TestCoverage.CoverageClarification> Clarifications { get; init; } = [];
    /// <summary>Required when the evidence states inputs; otherwise Unknown — absence of a description never means "not required".</summary>
    public TestDataNeed TestDataNeed { get; init; }
    public string? Capability { get; init; }
    /// <summary>The user story the scenario was written under when the case was prepared (its key, number, priority, Independent Test …);
    /// null when none was identified. Evidence for preparing the case; requirements are never inherited from it.</summary>
    public BirkNext.TestCoverage.CoverageUserStory? Story { get; init; }
    public int? NumberInStory { get; init; }

    // 2026-11-23 source-driven planning. All nullable: omitted for older cases so their plan fingerprint is unchanged.
    /// <summary>The candidate's project-independent identity (capability + canonical key): finds the same scenario in a later import.</summary>
    public string? SourceIdentity { get; init; }
    /// <summary>The source snapshot the scenario was read from when prepared ("" = workspace documents).</summary>
    public string? SnapshotKey { get; init; }
    /// <summary>The fingerprint of the discovery basis the case was prepared from.</summary>
    public string? BasisFingerprint { get; init; }
    /// <summary>Expected-result conflicts between sources when prepared (the case is not Ready until a person resolves them).</summary>
    public List<ScenarioConflict>? Conflicts { get; init; }
}

/// <summary>What the current source basis says about a saved case, separate from the review state (never rewrites the case).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaseSourceState
{
    /// <summary>The scenario is in the current basis unchanged.</summary>
    Current,
    /// <summary>The scenario changed in the current basis (expected result, steps, prerequisites …).</summary>
    Changed,
    /// <summary>A complete, successful comparison shows the scenario is no longer in the current source ("Utgått scenario").</summary>
    Removed,
    /// <summary>The scenario could not be compared: its source role is missing, unresolved or its analyzer failed ("Kilde mangler"). Never "removed".</summary>
    SourceUnavailable,
}

/// <summary>A reviewer's resolution of an expected-result conflict: which claim (or own wording) is the expected result, and why.</summary>
public sealed record ConflictResolution(string ExpectedResult, string? ChosenCandidateId, string Rationale, string ResolvedBy, DateTimeOffset ResolvedAt);

/// <summary>One change a person made to a case's human-owned fields (the fields named, never the old values).</summary>
public sealed record TestCaseEditEvent(string Editor, DateTimeOffset At, List<string> Fields);

/// <summary>Human-owned overrides. Null = take the generated value. Kept apart from the generated content so neither overwrites the other.</summary>
public sealed record TestCaseEdits
{
    public string? Title { get; init; }
    public string? Preconditions { get; init; }
    public List<string>? Steps { get; init; }
    public string? ExpectedResult { get; init; }
    public string? ObservationMethod { get; init; }
    public string? EnvironmentPrerequisites { get; init; }
    /// <summary>The reviewer's classification (null = not classified yet; never inferred from the scenario text).</summary>
    public TestCaseType? TestType { get; init; }
    public TestExecutionMethod? ExecutionMethod { get; init; }
    /// <summary>Manuell or Teknisk for THIS case, chosen by a person (2026-11-25). Null = «Ikke angitt» (older cases are never given a type they
    /// were not given). Separate from the scenario's recommended approach (<see cref="ScenarioTestKindDecision"/>), from how the case is executed
    /// (<see cref="ExecutionMethod"/>: Teknisk is not "automated"), and from review, readiness or results.</summary>
    public ScenarioTestKind? TestKind { get; init; }
    /// <summary>Only meaningful for <see cref="TestCaseType.Performance"/>; kept if the type changes so nothing a tester wrote is lost.</summary>
    public PerformanceCaseDetails? Performance { get; init; }
    /// <summary>The expected result per edited step, parallel to <see cref="Steps"/> (only meaningful when Steps is set).</summary>
    public List<string?>? StepExpected { get; init; }
    public string? Purpose { get; init; }
    public string? Roles { get; init; }
    public string? Dependencies { get; init; }
    public string? Priority { get; init; }
    /// <summary>The tester's decision whether the case needs Test Data (null = take the evidence; Unknown is never "not required").</summary>
    public TestDataNeed? TestDataNeed { get; init; }
    /// <summary>A prerequisite that blocks the case (e.g. "Payment sandbox not provisioned"); null when nothing is known to block it.</summary>
    public string? BlockedBy { get; init; }
    /// <summary>Template values a person supplied (key → value), shown apart from the evidence.</summary>
    public Dictionary<string, string>? TemplateValues { get; init; }
    [JsonIgnore] public bool IsEmpty => Title is null && Preconditions is null && Steps is null && ExpectedResult is null && ObservationMethod is null && EnvironmentPrerequisites is null
        && TestType is null && ExecutionMethod is null && TestKind is null && (Performance is null || Performance.IsEmpty) && StepExpected is null && Purpose is null && Roles is null
        && Dependencies is null && Priority is null && TestDataNeed is null && BlockedBy is null && (TemplateValues is null || TemplateValues.Count == 0);
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
    /// <summary>The case's readiness for test preparation (Test Data, clarification, prerequisites) in one state with its reason.</summary>
    public CaseReadiness Readiness { get; init; }
    public string? ReadinessReason { get; init; }
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
    /// <summary>Which human-owned fields changed, by whom and when (newest last).</summary>
    public List<TestCaseEditEvent> EditHistory { get; init; } = [];
    /// <summary>Requirements a tester linked to this case by hand (manually confirmed associations). Kept apart from the automatic links the
    /// case's own evidence carries (<see cref="TestCaseContent.Requirements"/>): removing a manual link never touches automatic evidence, and an
    /// automatic link is never turned into a manual one. Older plans have none.</summary>
    [JsonIgnore] public List<ManualRequirementLink> RequirementLinks { get => StoredRequirementLinks ?? []; init => StoredRequirementLinks = value is { Count: > 0 } ? value : null; }
    /// <summary>Stored form of <see cref="RequirementLinks"/>: omitted when empty, so a plan without links keeps its content fingerprint.</summary>
    [JsonPropertyName("requirementLinks")] public List<ManualRequirementLink>? StoredRequirementLinks { get; init; }

    // ── Scenario-centric planning (2026-11-20). Every field below is omitted when empty, so older plans keep their content fingerprint. ──

    /// <summary>The scenario a hand-written case was written under (the discovered scenario's id in this plan). Discovered cases do not store it:
    /// their own scenario id is their scenario. Null on an older hand-written case = no scenario can be established (shown as unresolved, never
    /// assigned by guesswork).</summary>
    public string? ScenarioKey { get; init; }
    /// <summary>The scenario's title when the case was written under it (shown if the scenario's own case later leaves the plan).</summary>
    public string? ScenarioTitle { get; init; }
    /// <summary>The case this one was duplicated from (a new identity; nothing else is shared, no Azure DevOps id is copied).</summary>
    public string? DuplicatedFrom { get; init; }
    /// <summary>The test-design proposal (variant key) this case was added from (2026-11-24): the proposal is not offered again even after the
    /// case's title is edited. Null for other cases.</summary>
    public string? FromVariant { get; init; }
    /// <summary>Translations of the case by language code ("nb" = Norwegian bokmål, …), machine-translated and/or written by a person. The
    /// original stays authoritative and unchanged.</summary>
    public Dictionary<string, TestCaseTranslation>? Translations { get; init; }
    /// <summary>Read-only compatibility: plans saved on 2026-11-20 before translations were kept per language stored the Norwegian text here.</summary>
    [JsonPropertyName("norwegian")] public TestCaseTranslation? LegacyNorwegian { get; init; }

    /// <summary>The case's text in <paramref name="language"/>, or null.</summary>
    public TestCaseTranslation? Translation(string? language) => language is null ? null
        : Translations is { } all && all.TryGetValue(language, out var t) ? t
        : language == ScenarioPlanning.Norwegian ? LegacyNorwegian : null;

    /// <summary>The languages the case has text in.</summary>
    [JsonIgnore] public IEnumerable<string> TranslatedLanguages =>
        (Translations?.Keys ?? Enumerable.Empty<string>()).Concat(LegacyNorwegian is not null && Translations?.ContainsKey(ScenarioPlanning.Norwegian) != true ? [ScenarioPlanning.Norwegian] : []);

    /// <summary>The case with its text in <paramref name="language"/> set (null removes it); the legacy Norwegian field is folded in.</summary>
    public TestCaseDraft WithTranslation(string language, TestCaseTranslation? translation)
    {
        var all = new Dictionary<string, TestCaseTranslation>(Translations ?? [], StringComparer.OrdinalIgnoreCase);
        if (LegacyNorwegian is { } legacy && !all.ContainsKey(ScenarioPlanning.Norwegian)) all[ScenarioPlanning.Norwegian] = legacy;
        if (translation is null) all.Remove(language); else all[language] = translation with { Language = language };
        return this with { Translations = all.Count == 0 ? null : all, LegacyNorwegian = null };
    }
    /// <summary>Requirement suggestions a tester rejected for this case (never proposed again for it; a rejection is not a decision on the requirement).</summary>
    public List<RejectedRequirementSuggestion>? RejectedSuggestions { get; init; }

    /// <summary>Read-time comparison with the current source basis (null = not compared; older plans). Never stored as a decision.</summary>
    public CaseSourceState? SourceState { get; init; }
    /// <summary>Why <see cref="SourceState"/> is what it is, in plain Norwegian (e.g. which source version no longer has the scenario).</summary>
    public string? SourceStateReason { get; init; }
    /// <summary>A reviewer's resolution of the case's expected-result conflict (null = unresolved or no conflict).</summary>
    public ConflictResolution? ConflictResolution { get; init; }
    /// <summary>True when the case carries a source conflict nobody resolved: never Ready, never approvable.</summary>
    [JsonIgnore] public bool HasUnresolvedConflict => Generated.Conflicts is { Count: > 0 } && ConflictResolution is null;

    /// <summary>The scenario this case belongs to: its own id for a discovered or guide case; the stored scenario for a case written under one;
    /// null for a hand-written case no scenario is established for.</summary>
    [JsonIgnore] public string? ParentScenario => Source.Kind == ScenarioSourceKind.Manual ? ScenarioKey : Source.ScenarioId;

    [JsonIgnore] public string EffectiveTitle => Edits.Title ?? Generated.Title;
    [JsonIgnore] public string EffectivePurpose => Edits.Purpose ?? Generated.Purpose;
    /// <summary>The expected result per effective step (null = Needs clarification).</summary>
    [JsonIgnore] public List<string?> EffectiveStepExpected
    {
        get
        {
            var steps = EffectiveSteps;
            var source = Edits.Steps is not null ? Edits.StepExpected ?? [] : Generated.StepExpected;
            return [.. steps.Select((_, i) => i < source.Count && !string.IsNullOrWhiteSpace(source[i]) ? source[i] : null)];
        }
    }
    [JsonIgnore] public TestDataNeed EffectiveTestDataNeed => Edits.TestDataNeed is { } n && n != TestDataNeed.Unknown ? n : Generated.TestDataNeed;
    [JsonIgnore] public string? EffectiveExpected => Edits.ExpectedResult ?? Generated.ExpectedResult;
    [JsonIgnore] public string? EffectivePreconditions => Edits.Preconditions ?? Generated.Preconditions;
    [JsonIgnore] public List<string> EffectiveSteps => Edits.Steps ?? Generated.Steps;
    [JsonIgnore] public string? EffectiveObservation => Edits.ObservationMethod ?? Generated.ObservationMethod;
    [JsonIgnore] public string? EffectiveEnvironment => Edits.EnvironmentPrerequisites ?? Generated.EnvironmentPrerequisites;
    /// <summary>The reviewer's classification, else the type the discovery evidence states (never inferred from text); NotClassified otherwise.</summary>
    [JsonIgnore] public TestCaseType EffectiveType => Edits.TestType ?? ScenarioCandidateTypes.ToTestCaseType(Generated.CandidateType);
    [JsonIgnore] public TestExecutionMethod EffectiveExecutionMethod => Edits.ExecutionMethod ?? TestExecutionMethod.NotDecided;
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
    /// <summary>The owner key stored with the draft: a System Understanding guide key for historical guide plans, or "project:{projectKey}" for a
    /// project plan (see <see cref="ProjectKey"/>).</summary>
    public string GuideKey { get; init; } = "";
    /// <summary>The project this plan belongs to (null for historical guide plans, which keep loading unchanged).</summary>
    public string? ProjectKey { get; init; }
    public string? ProjectName { get; init; }
    /// <summary>The source snapshot and discovery result the plan was prepared from (project plans).</summary>
    public string? SnapshotKey { get; init; }
    public string? DiscoveryId { get; init; }
    /// <summary>The Project Test Setup test areas the selection was made in (ids only; empty = entire project).</summary>
    public List<string> ScopeIds { get; init; } = [];
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
    /// <summary>Requirements a tester confirmed for a user story as a whole (an association with the story; never inherited by its scenarios or
    /// cases and never counted as coverage). Omitted when empty.</summary>
    public List<StoryRequirementLink>? StoryRequirementLinks { get; init; }
    /// <summary>Requirement suggestions a tester rejected for a user story (not proposed again for it). Omitted when empty.</summary>
    public List<StoryRequirementRejection>? StoryRejectedSuggestions { get; init; }
    /// <summary>User story text translated per language (omitted when empty).</summary>
    public List<StoryTranslation>? StoryTranslations { get; init; }
    /// <summary>Rejected and deferred test-design proposals (omitted when empty).</summary>
    public List<TestDesignDecision>? DesignDecisions { get; init; }
    /// <summary>Requirement wording translated for display (omitted when empty).</summary>
    public List<RequirementTextTranslation>? RequirementTranslations { get; init; }
    /// <summary>The application the plan belongs to (logical identity of its application source), shared by later imports of the same application:
    /// a plan follows the application, not one import. Null for plans before 2026-11-23 (their project key is then the application).</summary>
    public string? ApplicationKey { get; init; }
    /// <summary>The source basis of the discovery the plan was last prepared or updated from (null for older plans).</summary>
    public DiscoverySourceBasis? DiscoveryBasis { get; init; }
}

/// <summary>Source status of a project plan against the application's current basis, in the manual tester's words.</summary>
public sealed record PlanSourceStatus
{
    /// <summary>"Klar" (current), "Ny kildeversjon" (a newer import exists or the basis changed), "Må gjennomgås" (cases changed/removed/conflicting),
    /// "Kilde mangler" (a source could not be resolved), "Ikke sammenlignet" (no current discovery yet).</summary>
    public string State { get; init; } = "";
    public string Message { get; init; } = "";
    public string? PlanBasisFingerprint { get; init; }
    public string? CurrentBasisFingerprint { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    public int Unavailable { get; init; }
    public int Conflicts { get; init; }
    public int NewScenarios { get; init; }
    public List<DiscoverySourceMember> NewerSources { get; init; } = [];
}

/// <summary>What kind of difference a newer source basis makes to a saved case (impact separate from wording).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaseImpactKind { ExpectedResultChanged, ScenarioRemoved, PrerequisiteChanged, RequirementChanged, EvidenceChanged, ConflictIntroduced, WordingOnly, SourceUnavailable, NewScenario }

public sealed record CaseImpact
{
    public string CaseId { get; init; } = "";
    public string Title { get; init; } = "";
    public List<CaseImpactKind> Kinds { get; init; } = [];
    /// <summary>Before → after for the fields that changed (plain text, never raw evidence).</summary>
    public List<CaseImpactChange> Changes { get; init; } = [];
    /// <summary>True when the case was approved or has execution history: the impact needs a person's review.</summary>
    public bool ReviewedBefore { get; init; }
    public string Summary { get; init; } = "";
}

public sealed record CaseImpactChange(string Field, string? Before, string? After);

/// <summary>A reviewable comparison of a plan against the current discovery of its application. Read-only: nothing is rewritten.</summary>
public sealed record PlanImpactAnalysis
{
    public Guid DraftId { get; init; }
    public int PlanRevision { get; init; }
    public DiscoverySourceBasis? PlanBasis { get; init; }
    public DiscoverySourceBasis? CurrentBasis { get; init; }
    public string? CurrentDiscoveryId { get; init; }
    public List<CaseImpact> Cases { get; init; } = [];
    /// <summary>Scenarios of the current basis not in the plan (candidate id, title).</summary>
    public List<CaseImpact> NewScenarios { get; init; } = [];
    public string Summary { get; init; } = "";
}

/// <summary>A reviewer resolves a case's expected-result conflict (chosen claim or own wording, with a rationale).</summary>
public sealed record ResolveConflictRequest(int BaseRevision, string CaseId, string ExpectedResult, string? ChosenCandidateId, string Rationale);

/// <summary>A requirement confirmed for a user story (<see cref="StoryKey"/> = specification file + story number). Who, when and the catalog check
/// are in <see cref="Link"/> as for a case link.</summary>
public sealed record StoryRequirementLink(string StoryKey, ManualRequirementLink Link);

public sealed record StoryRequirementRejection(string StoryKey, RejectedRequirementSuggestion Rejection);

/// <summary>A reviewer's decision on a test-design proposal of a scenario (2026-11-24): rejected or deferred. Accepting adds a case (the existing
/// add-from-variant path), so it is not stored here. Bound to the proposal key, which is derived from the source text: when the source changes,
/// the key no longer matches and the proposal comes back for reconsideration.</summary>
public sealed record TestDesignDecision
{
    public string ScenarioKey { get; init; } = "";
    public string ProposalKey { get; init; } = "";
    public string ProposalTitle { get; init; } = "";
    /// <summary>"Rejected" or "Deferred".</summary>
    public string Decision { get; init; } = "";
    public string? Reason { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

public static class TestDesignDecisions
{
    public const string Rejected = "Rejected", Deferred = "Deferred";
}

public sealed record TestPlanDraftSummary(Guid Id, string GuideKey, string PlanName, string? DeliveryLabel, int Revision, int Cases, int Accepted, int NeedsClarification,
    int MissingExpected, int MissingTestData, int Blocked, string UpdatedBy, DateTimeOffset UpdatedAt)
{
    /// <summary>Active (normal work) or Archived (read-only, listed apart; restorable). Deleted plans are never listed.</summary>
    public TestPlanLifecycle Lifecycle { get; init; } = TestPlanLifecycle.Active;
    /// <summary>When and by whom the plan was last archived, restored or deleted (null = never).</summary>
    public DateTimeOffset? LifecycleChangedAt { get; init; }
    public string? LifecycleChangedBy { get; init; }
    /// <summary>Distinct scenarios the plan's cases belong to (hand-written cases without a scenario are not counted).</summary>
    public int Scenarios { get; init; }
    /// <summary>The application the plan follows (logical identity), or null for older plans.</summary>
    public string? ApplicationKey { get; init; }
    public string? ProjectName { get; init; }
}

/// <summary>
/// A plan's lifecycle (2026-11-23). Archive is the normal way to put a plan away: it is read-only and listed apart until restored. Delete is
/// logical: the plan disappears from every list and cannot be opened, while its content, revisions and frozen snapshots are kept for audit
/// (snapshots are immutable and reference the plan). Only an archived plan can be deleted, after the tester confirms its name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPlanLifecycle { Active, Archived, Deleted }

/// <summary>Archive or restore a plan at the revision the tester saw (a changed plan is refused with 409).</summary>
public sealed record TestPlanLifecycleRequest(int BaseRevision, string? Reason = null);

/// <summary>Delete an archived plan: the revision the tester saw and the plan's name typed as confirmation.</summary>
public sealed record DeleteTestPlanRequest(int BaseRevision, string ConfirmName);

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
    /// <summary>The project the workspace was read for (project plans); null for the guide-only view.</summary>
    public string? ProjectKey { get; init; }
    public List<TestPlanGuideOption> Guides { get; init; } = [];
    public string? GuideKey { get; init; }
    public TestPlanSourceBasis? Basis { get; init; }
    public List<ScenarioCandidate> Candidates { get; init; } = [];
    public List<TestPlanStage> Stages { get; init; } = [];
    public List<TestPlanDraftSummary> Drafts { get; init; } = [];
    /// <summary>Archived plans of the project / application (read-only until restored). Never part of <see cref="Drafts"/>.</summary>
    public List<TestPlanDraftSummary> ArchivedDrafts { get; init; } = [];
    public List<TestPlanSnapshotSummary> Snapshots { get; init; } = [];
    public List<ExecutableScenarioOption> Executables { get; init; } = [];
    public List<TestDataOption> TestData { get; init; } = [];
    public bool CanWrite { get; init; }
    public string RuleVersion { get; init; } = "";
    /// <summary>Guide source changes since the guide revision (the existing change awareness): candidates for regression review.</summary>
    public List<string> SourceChanges { get; init; } = [];
    /// <summary>The application's current source basis (project workspaces; null when unknown).</summary>
    public DiscoverySourceBasis? CurrentBasis { get; init; }
    /// <summary>Per plan: its source status against the current basis (draft id → status).</summary>
    public Dictionary<Guid, PlanSourceStatus>? PlanStatus { get; init; }
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
    /// <summary>The project's current specification requirements, needed only when a save adds a manual requirement link.</summary>
    public ProjectRequirementCatalog? RequirementCatalog { get; init; }
    /// <summary>Story-level requirement links after this save (null = unchanged). New links are checked like case links; the server records who and when.</summary>
    public List<StoryRequirementLink>? StoryRequirementLinks { get; init; }
    /// <summary>Story-level rejected suggestions after this save (null = unchanged).</summary>
    public List<StoryRequirementRejection>? StoryRejectedSuggestions { get; init; }
    /// <summary>User story translations after this save (null = unchanged); a changed text is marked written by a person.</summary>
    public List<StoryTranslation>? StoryTranslations { get; init; }
    /// <summary>Test-design decisions after this save (null = unchanged); the server records who and when for new ones.</summary>
    public List<TestDesignDecision>? DesignDecisions { get; init; }
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
    /// <summary>The case's manually linked requirements after this save (id + specification file). Null = unchanged (an older client). A link the
    /// case does not hold yet must name a requirement of <see cref="SaveTestPlanDraftRequest.RequirementCatalog"/>.</summary>
    public List<ManualRequirementLink>? RequirementLinks { get; init; }
    /// <summary>Translated text after this save, by language (null = unchanged; a language left out is unchanged; an entry with no text removes
    /// that language). The server records who changed it and marks it written by a person.</summary>
    public Dictionary<string, TestCaseTranslation>? Translations { get; init; }
    /// <summary>Suggestions rejected for this case after this save (null = unchanged). The server records who and when for a new rejection.</summary>
    public List<RejectedRequirementSuggestion>? RejectedSuggestions { get; init; }
}

/// <summary>A requirement a tester linked to a saved case (manually confirmed). Identity: requirement id + specification file. Title, who and when
/// are recorded by the server at link time; a manual link is an explicit association, never an execution or a pass.</summary>
public sealed record ManualRequirementLink
{
    public string RequirementId { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    /// <summary>The requirement text when it was linked (shown if the requirement later leaves the specification).</summary>
    public string Title { get; init; } = "";
    public string LinkedBy { get; init; } = "";
    public DateTimeOffset LinkedAt { get; init; }
    /// <summary>What the server checked the link against when it was made: <see cref="ManualRequirementLinkBasis.BrowserSpecification"/> = the
    /// requirement list the browser parsed from the project's specification and sent with the save. The server does not hold the specification, so
    /// this is a consistency check, not an independent verification. Null for links recorded before the basis was kept.</summary>
    public string? CheckedAgainst { get; init; }
    /// <summary>SHA-256 of that requirement list (requirement id and specification file, sorted), so the list a link was checked against can be
    /// compared with a later one.</summary>
    public string? CatalogFingerprint { get; init; }
    /// <summary>Server-checked links: the server catalog revision and the requirement's wording hash when it was linked (null otherwise).</summary>
    public int? CatalogRevision { get; init; }
    public string? RequirementTextSha256 { get; init; }
    /// <summary>"Scenario" = the requirement applies to the business scenario as a whole (an association, never counted as this case's coverage);
    /// null = the case itself is designed to test it (counted as test design).</summary>
    public string? Scope { get; init; }
    /// <summary>When the tester confirmed a BirkNext suggestion: its evidence strength and why it was suggested (what the tester saw). Null = linked
    /// from the search.</summary>
    public RequirementSuggestionLevel? SuggestionLevel { get; init; }
    public string? SuggestionReason { get; init; }
}

public static class RequirementLinkScope
{
    public const string Scenario = "Scenario";
}

public static class ManualRequirementLinkBasis
{
    public const string BrowserSpecification = BirkNext.Specifications.RequirementLinkProvenance.BrowserSpecification;
    /// <summary>Checked against the server-held catalog captured from the imported specification at Project Import.</summary>
    public const string ServerSpecification = BirkNext.Specifications.RequirementLinkProvenance.ServerSpecification;
}

/// <summary>One requirement of the project's selected specification (parsed by the existing specification parser in the browser).</summary>
public sealed record ProjectRequirementOption(string RequirementId, string Title, string SpecificationPath);

/// <summary>The current project's specification requirements a save may link to. A new manual link must name one of them; the catalog must be
/// the plan's own project (another project's catalog is refused).</summary>
public sealed record ProjectRequirementCatalog
{
    public string ProjectKey { get; init; } = "";
    public List<ProjectRequirementOption> Requirements { get; init; } = [];
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

// ── MT-7: reviewed requirement coverage ─────────────────────────────────────────────────────────────────────
// Which specification requirements a tester has reviewed scenario coverage for. Read from the shared canonical relationship registry (scenario →
// requirement VERIFIES relations proposed from what a System Understanding guide states, decided in Relationship Review) and from Test Plan
// Suggestions reviews. Never test execution: a reviewed scenario says what a tester prepared, not that anything ran or passed.

/// <summary>How far a scenario → requirement association is established.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssociationTrust
{
    /// <summary>A published (reviewed) guide names the requirement for the scenario.</summary>
    ExplicitSourceReference,
    /// <summary>A person confirmed the association in Relationship Review, on the current evidence.</summary>
    ReviewedCanonicalAssociation,
    /// <summary>Proposed (a guide draft names it, a named test verifies it, several feature packs define the id) or the evidence changed since a decision.</summary>
    SuggestedAssociation,
    /// <summary>A person rejected it; never counted.</summary>
    Rejected,
}

/// <summary>A requirement's reviewed-scenario coverage state. Distinct facts, never one readiness colour.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementCoverageState
{
    /// <summary>A verified association to a scenario a tester accepted for preparation, in its current revision.</summary>
    ReviewedScenarioCoverage,
    /// <summary>A verified association, but no tester accepted the current scenario revision yet.</summary>
    AssociationVerifiedScenarioNotReviewed,
    /// <summary>Only suggested associations (or decisions made on older evidence): Relationship Review decides.</summary>
    NeedsRelationshipReview,
    /// <summary>No scenario in the interpreted guides states or suggests coverage (not "never tested": only no documented scenario).</summary>
    NoDocumentedScenarioCoverage,
}

/// <summary>One scenario → requirement association with its trust, the decision behind it and the tester review of the scenario.</summary>
public sealed record RequirementScenarioAssociation
{
    public string RelationKey { get; init; } = "";
    public string GuideKey { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public string ScenarioTitle { get; init; } = "";
    public string RequirementKey { get; init; } = "";
    public string RequirementId { get; init; } = "";
    public AssociationTrust Trust { get; init; }
    public string RuleId { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string ReviewState { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string? Reviewer { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    /// <summary>"Approved", "Approved for an earlier scenario revision", "Not reviewed", "No draft".</summary>
    public string ScenarioReview { get; init; } = "";
    public bool ScenarioReviewCurrent { get; init; }
    public Guid? DraftId { get; init; }
    public string? ScenarioReviewedBy { get; init; }
    public DateTimeOffset? ScenarioReviewedAt { get; init; }
    /// <summary>The guide's scenario changed after the interpretation that proposed this association: re-run Analyze relationships.</summary>
    public bool InterpretationOutdated { get; init; }
    [JsonIgnore] public bool Verified => Trust is AssociationTrust.ExplicitSourceReference or AssociationTrust.ReviewedCanonicalAssociation;
}

public sealed record RequirementCoverageRow
{
    public string RequirementKey { get; init; } = "";
    public string RequirementId { get; init; } = "";
    public string? Module { get; init; }
    public string? Feature { get; init; }
    public string Title { get; init; } = "";
    public RequirementCoverageState State { get; init; }
    public string Reason { get; init; } = "";
    public List<RequirementScenarioAssociation> Associations { get; init; } = [];
}

/// <summary>Reviewed requirement coverage for one project scope. <see cref="Known"/> is false (and every count is meaningless) when no
/// interpretation of the scope includes both specification requirements and System Understanding scenarios.</summary>
public sealed record RequirementCoverageView
{
    public string Scope { get; init; } = "";
    public bool Known { get; init; }
    public string? UnknownReason { get; init; }
    public Guid? InterpretationId { get; init; }
    public DateTimeOffset? InterpretedAt { get; init; }
    public int Requirements { get; init; }
    public int Reviewed { get; init; }
    public int VerifiedNotReviewed { get; init; }
    public int NeedsReview { get; init; }
    public int NoScenario { get; init; }
    public List<RequirementCoverageRow> Rows { get; init; } = [];
    /// <summary>Every association by scenario (for Test Plan Suggestions), including rejected ones as history.</summary>
    public List<RequirementScenarioAssociation> Associations { get; init; } = [];
    /// <summary>Guide scenarios in the interpretation with no association at all (scenario without a requirement).</summary>
    public List<string> ScenariosWithoutRequirement { get; init; } = [];
    public string Boundary { get; init; } = RequirementCoverageBoundary;

    public const string RequirementCoverageBoundary = "Reviewed scenario coverage means a tester reviewed a scenario that a verified association links to the requirement. It is not test execution, not a passed test and not an Azure DevOps Test Case.";
}

// ── Prepared test cases in Test Coverage and Requirements Traceability ─────────────────────────────────────────
// Saved project test cases (prepared from discovery or written by hand) as test-design evidence. One identity per saved case
// (draft id + case id); a requirement link only from the case's own evidence (the specification file and id it was prepared from), never
// from similarity; a prepared or approved case is never an executed or passed test. Read-only: plans stay owned by Test Plan Suggestions.

/// <summary>Where a saved case stands in Test Plan Suggestions. Approved = accepted for preparation (a BirkNext review decision).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparedCaseStage { Prepared, NeedsClarification, Approved, Excluded, Superseded }

/// <summary>What the requirement link of a case rests on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparedCaseLinkBasis
{
    /// <summary>The case was prepared from this requirement or from an acceptance scenario of the same specification that names it.</summary>
    SpecificationEvidence,
    /// <summary>An Integration Journey declares the requirement id, without naming a specification file.</summary>
    JourneyReference,
    /// <summary>A System Understanding guide business rule: not a specification requirement (MT-7) and never requirement coverage.</summary>
    GuideBusinessRule,
    /// <summary>A tester linked the case to the requirement by hand in Test Plan Suggestions (manually confirmed association).</summary>
    ManualConfirmation,
}

/// <summary>Whether a requirement link holds against the reviewed specification now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparedLinkValidity
{
    /// <summary>The requirement is in the reviewed specification.</summary>
    Current,
    /// <summary>The link names the reviewed specification file, but the requirement is no longer in it (removed or renamed): kept, never counted.</summary>
    RequirementNotFound,
    /// <summary>The link names another specification file than the one reviewed.</summary>
    OtherSpecification,
    /// <summary>No specification is being reviewed, so the link cannot be checked.</summary>
    NotChecked,
    /// <summary>Not a specification requirement (a guide business rule or a journey's declared id).</summary>
    NotARequirement,
}

/// <summary>One requirement a case names, and whether it counts as test-design coverage of the reviewed specification (with the reason).</summary>
/// <remarks>Title, Validity, LinkedBy and LinkedAt are empty on results stored before manual linking.</remarks>
public sealed record PreparedCaseRequirementLink(string RequirementId, string? SpecificationPath, PreparedCaseLinkBasis Basis, bool Counted, string Explanation,
    string? Title = null, PreparedLinkValidity? Validity = null, string? LinkedBy = null, DateTimeOffset? LinkedAt = null)
{
    [JsonIgnore] public bool Manual => Basis == PreparedCaseLinkBasis.ManualConfirmation;
}

public sealed record PreparedCaseTrace
{
    /// <summary>The single identity of the saved case: "{draft id:N}/{case id}".</summary>
    public string CaseKey { get; init; } = "";
    public Guid DraftId { get; init; }
    public string CaseId { get; init; } = "";
    public string PlanName { get; init; } = "";
    public int PlanRevision { get; init; }
    public string Title { get; init; } = "";
    public ScenarioSourceKind SourceKind { get; init; }
    /// <summary>The discovery capability the case was prepared from (specification, ui-source …); null for a manual case.</summary>
    public string? Capability { get; init; }
    public PreparedCaseStage Stage { get; init; }
    public string StageLabel { get; init; } = "";
    /// <summary>"Not executed", or the latest completed manual run's outcome in words (Manual Testing → Execute &amp; document).</summary>
    public string Execution { get; init; } = PreparedCaseTraceView.NotExecuted;
    /// <summary>The case's latest completed manual run (null = never completed); an abandoned run never counts.</summary>
    public ManualRunSummary? LatestRun { get; init; }
    /// <summary>False when the latest run executed an earlier revision of the case (its content changed since).</summary>
    public bool LatestRunCurrent { get; init; }
    /// <summary>A manual run of this case is in progress.</summary>
    public bool RunInProgress { get; init; }
    /// <summary>The latest completed run actually exercised the case (Passed or Failed); Blocked and Not run do not.</summary>
    [JsonIgnore] public bool Executed => LatestRun is { } r && ManualRunRules.Executed(r.State, r.Outcome);
    [JsonIgnore] public bool Passed => LatestRun is { Outcome: ManualRunOutcome.Passed } && Executed;
    public List<PreparedCaseRequirementLink> Requirements { get; init; } = [];
    /// <summary>The specification acceptance scenario the case was prepared from (the same id Test Coverage lists as a QA test), if any.</summary>
    public string? SpecificationScenarioId { get; init; }
    /// <summary>Critical E2E flows the case comes from or a reviewer confirmed (their runs are the flow's, never this case's).</summary>
    public List<string> CriticalE2EFlowIds { get; init; } = [];
    /// <summary>Drill-down to the case in Test Plan Suggestions.</summary>
    public string Href { get; init; } = "";
    [JsonIgnore] public bool Counted => Requirements.Any(r => r.Counted);
}

/// <summary>One requirement of the reviewed specification with the saved cases that cover it by evidence (counted once, however many cases).</summary>
public sealed record PreparedRequirementTrace
{
    public string RequirementId { get; init; } = "";
    public string? SpecificationPath { get; init; }
    public int ApprovedCases { get; init; }
    public int PreparedCases { get; init; }
    /// <summary>"Approved test case" or "Prepared, not approved yet".</summary>
    public string State { get; init; } = "";
    public List<string> CaseKeys { get; init; } = [];
    /// <summary>The requirement text from the reviewed specification (null when the view was not given it).</summary>
    public string? Title { get; init; }
    /// <summary>Cases linked by their own evidence (automatically identified) and by a tester (manually confirmed); a case with both counts in each.</summary>
    public int AutomaticCases { get; init; }
    public int ManualCases { get; init; }
    /// <summary>"Not executed", or how many of the covering cases were executed in a completed manual run and with which outcome.</summary>
    public string Execution { get; init; } = PreparedCaseTraceView.NotExecuted;
    /// <summary>Covering cases whose latest completed manual run executed them (Passed or Failed), and how many of those passed / failed.</summary>
    public int ExecutedCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
}

/// <summary>A requirement of the reviewed specification no active saved case covers (no test design yet).</summary>
public sealed record PreparedRequirementGap(string RequirementId, string? Title);

/// <summary>
/// A requirement a tester confirmed for a whole user story (Plan &amp; prepare → story card → Requirements). An association with the story, shown
/// apart: never inherited by the story's scenarios or cases and never counted as test design or coverage.
/// </summary>
public sealed record PreparedStoryAssociation(string StoryKey, string? StoryId, string? StoryTitle, string RequirementId, string? SpecificationPath, string? Title,
    string LinkedBy, DateTimeOffset LinkedAt, PreparedLinkValidity? Validity, string PlanName, string Href);

public sealed record PreparedCaseTraceView
{
    public string ProjectKey { get; init; } = "";
    /// <summary>The specification file(s) links are counted against; empty = no specification was given, so no link is counted.</summary>
    public List<string> SpecificationPaths { get; init; } = [];
    public int Plans { get; init; }
    public int Cases { get; init; }
    /// <summary>Cases with at least one counted requirement link.</summary>
    public int LinkedCases { get; init; }
    /// <summary>Cases with no counted link (visible, never counted as requirement coverage).</summary>
    public int UnlinkedCases { get; init; }
    public int RequirementsWithApprovedCase { get; init; }
    public int RequirementsWithPreparedCaseOnly { get; init; }
    public List<PreparedCaseTrace> CaseList { get; init; } = [];
    public List<PreparedRequirementTrace> Requirements { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>How many requirements the reviewed specification has; null when the view was not given the specification's requirements.</summary>
    public int? SpecificationRequirements { get; init; }
    /// <summary>Requirements of the reviewed specification with no counted case (empty when <see cref="SpecificationRequirements"/> is null).</summary>
    public List<PreparedRequirementGap> WithoutTestDesign { get; init; } = [];
    /// <summary>Requirements confirmed for a user story as a whole: shown, never counted (see <see cref="PreparedStoryAssociation"/>).</summary>
    public List<PreparedStoryAssociation> StoryAssociations { get; init; } = [];
    /// <summary>Manual links whose requirement is no longer in the reviewed specification (kept with the case, not counted).</summary>
    public int StaleManualLinks { get; init; }
    /// <summary>Cases executed in a completed manual run (latest run Passed or Failed), and how many passed. Test design never adds to these.</summary>
    public int ExecutedCases { get; init; }
    public int PassedCases { get; init; }
    /// <summary>Requirements with at least one executed covering case, and with at least one passed covering case.</summary>
    public int RequirementsExecuted { get; init; }
    public int RequirementsPassed { get; init; }
    public string Boundary { get; init; } = TraceBoundary;
    /// <summary>Integration testing of the requirements: confirmed requirement ↔ journey links, the suites prepared for those journeys and each
    /// suite's recorded outcome. Kept apart from the manual counts above (never added to them, so nothing is counted twice).</summary>
    public List<RequirementIntegrationTrace> Integration { get; init; } = [];

    public const string NotExecuted = "Not executed";
    public const string TraceBoundary = "Prepared and approved test cases are test design, not test execution. Execution is only what a tester recorded in a completed manual run (Manual Testing → Execute & document). A requirement counts only when the case's own evidence names it in the reviewed specification or a tester linked the case to it.";
}

/// <summary>A requirement a person linked to a declared integration journey, with the suites prepared for that journey.</summary>
public sealed record RequirementIntegrationTrace(string RequirementId, string SpecificationPath, string RequirementTitle, string JourneyId, string JourneyName,
    int LinkedAtJourneyRevision, int CurrentJourneyRevision, List<IntegrationSuiteTrace> Suites);

/// <summary>One suite prepared for the journey: its revision and the outcome its recorded runs give (never inferred from preparation).</summary>
public sealed record IntegrationSuiteTrace(Guid SuiteId, string Name, int Revision, string Outcome, bool Executed, string Href);

/// <summary>The specification a requirements view reads, for counting case links against it (Requirements Traceability).</summary>
public sealed record PreparedCaseTraceRequest(List<string> SpecificationPaths, List<string>? RequirementIds, Dictionary<string, string>? RequirementTitles = null);

// ── Plan version history (append-only revisions of a draft) ───────────────────────────────────────────────────────────

/// <summary>One saved revision of a test plan: who saved it, when, why, and how many cases it held. <see cref="Current"/> marks the draft's revision.</summary>
public sealed record TestPlanRevisionSummary(int Revision, string Reason, string SavedBy, DateTimeOffset SavedAt, string PlanName, int Cases, bool Current);

/// <summary>Restores an earlier revision as a new revision (never rewriting history). <see cref="BaseRevision"/> is the draft revision the tester saw.</summary>
public sealed record RestoreTestPlanRevisionRequest(int BaseRevision, int Revision);
