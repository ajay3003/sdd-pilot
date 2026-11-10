using System.Text.Json.Serialization;

namespace BirkNext.CriticalE2E;

// ── Suggested E2E tests ──────────────────────────────────────────────────────────────────────────────────────────────────────
// Source Analysis reads UI source once (Razor/CSHTML/HTML) and keeps FACTS only (routes, forms, controls, labels, test ids, validation
// constraints, handlers) — never markup. The suggestion engine turns those facts, existing flows and recorded requirement references into DRAFT
// Critical E2E flows in the existing flow format. A source fact is never a runtime locator: a locator is VERIFIED only by the Edge Companion
// on the attended page, and only for the page it was observed on. Expected results come only from explicit evidence; otherwise the
// suggestion says NEEDS_EXPECTED_RESULT and has no final assertion.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceUiControlKind { TextInput, NumberInput, DateInput, Select, Checkbox, TextArea, FileInput, Button, SubmitButton, Link, ValidationMessage, ValidationSummary, Heading, Component }

/// <summary>One control as the UI source declares it. Candidate facts only: the rendered DOM is verified separately.</summary>
public sealed record SourceUiControl
{
    public string Id { get; init; } = "";
    public SourceUiControlKind Kind { get; init; }
    /// <summary>The ARIA role the control renders with when the source makes it unambiguous (textbox, combobox, checkbox, button, link, heading).</summary>
    public string? Role { get; init; }
    public string? TestId { get; init; }
    public string? ElementId { get; init; }
    /// <summary>The label text associated in source (label for=id, wrapping label, aria-label, placeholder as last resort).</summary>
    public string? Label { get; init; }
    /// <summary>Visible text of a button, link or heading (static text only; Razor expressions are not evaluated).</summary>
    public string? Text { get; init; }
    /// <summary>The bound model member ("Model.Name" → "Name") for inputs.</summary>
    public string? Binding { get; init; }
    public string? InputType { get; init; }
    public string? Href { get; init; }
    public string? Handler { get; init; }
    public string? FormId { get; init; }
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? RangeMin { get; init; }
    public string? RangeMax { get; init; }
    public string? Pattern { get; init; }
    /// <summary>The [Required] attribute's explicit message (ErrorMessage = "…"), when the source declares one.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>The length/range attribute's explicit message, when declared. Never confused with the required message.</summary>
    public string? ConstraintMessage { get; init; }
    public List<string> Options { get; init; } = [];
    /// <summary>Conditional (@if) or inside a loop (@foreach): may be absent or repeated at runtime.</summary>
    public bool Conditional { get; init; }
    public bool Repeated { get; init; }
    public int Line { get; init; }
    public string Confidence { get; init; } = "Source";
}

public sealed record SourceUiForm(string Id, string? ModelType, string? SubmitHandler, bool HasValidation, int Line);

public sealed record SourceUiPage
{
    public string File { get; init; } = "";
    public string Module { get; init; } = "";
    public List<string> Routes { get; init; } = [];
    public string? Title { get; init; }
    public bool RequiresAuthorization { get; init; }
    public List<string> Roles { get; init; } = [];
    public List<SourceUiForm> Forms { get; init; } = [];
    public List<SourceUiControl> Controls { get; init; } = [];
    /// <summary>Requirement references written in the page source (comments, attributes) — recorded, never inferred.</summary>
    public List<string> RequirementRefs { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>The UI surface of a source snapshot: facts extracted once at analysis, never the markup.</summary>
public sealed record SourceUiSurface
{
    public int Version { get; init; } = 1;
    public List<SourceUiPage> Pages { get; init; } = [];
    public int FilesRead { get; init; }
    public int FilesSkipped { get; init; }
    public List<string> Limitations { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionKind { PageOpens, Navigation, RequiredFields, InvalidInput, ValidSubmission }

/// <summary>Where a suggestion's behaviour comes from (several can apply).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionOrigin { RequirementDerived, SourceImplementationDerived, ExistingTestDerived, ContractDerived, InferredCandidate }

/// <summary>Readiness of a suggestion. Separate from any execution result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionState { Suggested, NeedsDomVerification, NeedsTestData, NeedsExpectedResult, ReadyForReview, Approved, Executable, Executed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionCoverage { NewSuggestion, PartiallyCovered, AlreadyCovered, OverlapRequiresReview }

/// <summary>What the attended page showed for a locator. VERIFIED_UNIQUE only from the live page, never from source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocatorVerification { NotAssessed, VerifiedUnique, Ambiguous, NotFound, NotVisible, UnsupportedFrame, Stale }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocatorDecision { Pending, Accepted, AlternativeChosen, Rejected, Unresolved }

/// <summary>A locator candidate for a step, in priority order (test id → role+name → label → id → attribute → scoped CSS).</summary>
public sealed record SuggestedLocator(CompanionSelector Selector, int Priority, string Basis);

public sealed record SuggestedStep
{
    public string StepId { get; init; } = "";
    public string Description { get; init; } = "";
    public CompanionActionKind Action { get; init; }
    /// <summary>The control this step addresses (source fact id); null for Navigate.</summary>
    public string? ControlId { get; init; }
    public List<SuggestedLocator> Locators { get; init; } = [];
    public string? Value { get; init; }
    public string? Expected { get; init; }
    public CompanionTextMatch Match { get; init; } = CompanionTextMatch.Equals;
    /// <summary>"${data:alias}" when the value comes from Test Data; a generated invalid value states which constraint it violates.</summary>
    public string? ValueBasis { get; init; }
    public bool IsFinalAssertion { get; init; }
    public bool Mutating { get; init; }
}

/// <summary>Locator verification evidence for one step: what the attended page showed, where and when. No DOM is stored.</summary>
public sealed record StepVerification
{
    public string StepId { get; init; } = "";
    public LocatorVerification State { get; init; } = LocatorVerification.NotAssessed;
    public CompanionSelector? Selector { get; init; }
    public List<CompanionSelectorCheck> Checks { get; init; } = [];
    public string? ObservedRoute { get; init; }
    public string? ObservedOrigin { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    /// <summary>The source snapshot the suggestion was generated from. The page was observed in a browser; the running build is not proven to be it.</summary>
    public Guid? SourceSnapshotId { get; init; }
    public string? Note { get; init; }
}

public sealed record StepDecision(string StepId, LocatorDecision Decision, CompanionSelector? Selector, string DecidedBy, DateTimeOffset DecidedAt, string? Reason);

public sealed record TestSuggestion
{
    /// <summary>Stable identity: rule version + project + route + kind + controls; the same inputs give the same id.</summary>
    public string SuggestionId { get; init; } = "";
    public string RuleVersion { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public string Module { get; init; } = "";
    public string Feature { get; init; } = "";
    public SuggestionKind Kind { get; init; }
    public List<SuggestionOrigin> Origins { get; init; } = [];
    public string Route { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> RequirementRefs { get; init; } = [];
    /// <summary>"file:line" of every source fact the suggestion uses.</summary>
    public List<string> SourceRefs { get; init; } = [];
    public List<string> Preconditions { get; init; } = [];
    public List<SuggestedStep> Steps { get; init; } = [];
    /// <summary>The expected result and where it comes from; null when unknown (the suggestion then has no final assertion).</summary>
    public string? ExpectedResult { get; init; }
    public string? ExpectedBasis { get; init; }
    public List<string> TestDataNeeds { get; init; } = [];
    public List<string> Prerequisites { get; init; } = [];
    public string Confidence { get; init; } = "";
    public SuggestionCoverage Coverage { get; init; }
    public string? CoveredByFlowId { get; init; }
    public string? CoverageNote { get; init; }
    public List<SuggestionState> States { get; init; } = [];
    public List<StepVerification> Verifications { get; init; } = [];
    public List<StepDecision> Decisions { get; init; } = [];
    /// <summary>The saved Critical E2E flow after approval (the only executable form).</summary>
    public string? ApprovedFlowId { get; init; }
    public int? ApprovedFlowVersion { get; init; }
    public List<string> Limitations { get; init; } = [];
}

public sealed record SuggestionSummary(int Suggestions, int ExistingReused, int NewSuggested, int MissingDomMappings, int MissingExpectedResults, int MissingTestData, int ReadyForApproval, int Approved);

public sealed record SuggestionGenerationResult
{
    public string ProjectKey { get; init; } = "";
    public Guid SnapshotId { get; init; }
    public string ArchiveFileName { get; init; } = "";
    public SuggestionSummary Summary { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    public List<TestSuggestion> Suggestions { get; init; } = [];
    public int PagesAnalyzed { get; init; }
    public List<string> Limitations { get; init; } = [];
    /// <summary>Changes between the previous suggestion snapshot and this one that affect approved flows (regression impact).</summary>
    public List<SuggestionImpact> Impact { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuggestionImpactKind { LocatorRevalidationNeeded, TestDataReviewNeeded, AssertionReviewNeeded, NewTestSuggested, TestMayBeObsolete }

public sealed record SuggestionImpact(SuggestionImpactKind Kind, string FlowId, string FlowName, string StepId, string Detail);

public sealed record SuggestionGenerateRequest(Guid SnapshotId, string EnvironmentId);
public sealed record SuggestionVerifyRequest(Guid SnapshotId, string SuggestionId, string EnvironmentId, string ProfileId);
public sealed record SuggestionVerifyResult(TestSuggestion? Suggestion, string Message, bool Dispatched);
public sealed record SuggestionDecisionRequest(Guid SnapshotId, string SuggestionId, string EnvironmentId, string StepId, LocatorDecision Decision, CompanionSelector? Selector, string? Reason);
/// <summary>Approval saves the suggestion as a Diagnostic Critical E2E flow (never release evidence until a person makes it Critical). A reviewer
/// may supply the expected route of a submission (UrlEquals/UrlContains) when the source does not state one.</summary>
public sealed record SuggestionApproveRequest(Guid SnapshotId, string SuggestionId, string EnvironmentId, string ProfileId, string? Expected, CriticalE2EOutcomeKind? OutcomeKind);
public sealed record SuggestionApproveResult(CriticalE2EFlowDefinition? Flow, List<string> Problems);
public sealed record SuggestionSnapshotOption(Guid SnapshotId, string ProjectKey, string ArchiveFileName, DateTimeOffset AnalyzedAt, bool HasUiSurface, int Pages, int Routes, int Controls);

/// <summary>One selector checked against the attended page: how many elements it matches, and the first match's state. No value, no HTML.</summary>
public sealed record CompanionSelectorCheck
{
    public CompanionSelector Selector { get; init; } = new();
    public int MatchCount { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public string? TagName { get; init; }
    public string? Role { get; init; }
    public string? AccessibleName { get; init; }
    /// <summary>Matches inside frames or shadow roots are not reachable; reported, never counted.</summary>
    public string? Limitation { get; init; }
}
