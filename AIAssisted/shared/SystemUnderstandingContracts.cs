using System.Text.Json.Serialization;

namespace BirkNext.SystemUnderstanding;

// System Understanding (Systemforståelse): an explanation of the SYSTEM UNDER TEST — what it does, how information moves, which rules apply and
// what to test. Not BirkNext's own user guide, not a topology engine and not a second evidence store: a guide is curated, versioned content
// whose technical claims POINT AT authoritative evidence (source snapshots, contracts, tests, reviews) with an explicit classification.
// Model: docs/architecture/birknext-system-understanding.md.

/// <summary>
/// How strongly a claim is established. Source code proves that code exists, not that an integration works; a test source file proves a test
/// is defined, not that it passed; a written PASS report is historical evidence, not a current execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideEvidenceClass { SourceImplemented, Specified, RuntimeVerified, TestVerified, TestDefined, Inferred, NeedsClarification, Conflicting, NotAssessed }

/// <summary>Draft (editable), InReview, Approved, Published (the revision readers see), Superseded (an older published revision, kept).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideRevisionState { Draft, InReview, Approved, Published, Superseded }

/// <summary>What kind of transfer a flow edge is: the main report path, supporting data, an identity or placement lookup, a reverse
/// acknowledgment, a periodic export or an event. Each kind is drawn differently: not every arrow carries the same data.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideFlowKind { MainReport, SupportingData, IdentityLookup, PlacementLookup, Acknowledgment, PeriodicExport, Event }

/// <summary>Suggested (guidance), ExistingDefinition (a test definition exists), Executed (a run exists), Verified (a current passing run).
/// Guidance is never test coverage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideScenarioState { Suggested, NeedsClarification, ExistingDefinition, Executed, Verified }

/// <summary>
/// A pointer at authoritative evidence. Nothing here copies source text: a file/symbol/line in a named source snapshot (or a contract,
/// requirement, test, document), with its classification and an optional BirkNext deep link to the owning explorer.
/// </summary>
public sealed record GuideEvidence
{
    public GuideEvidenceClass Class { get; init; } = GuideEvidenceClass.NotAssessed;
    /// <summary>The source role (e.g. "skole", "mm", "mu"): which imported repository the reference belongs to.</summary>
    public string? SourceRole { get; init; }
    /// <summary>The reviewed source the reference was taken from (archive label as reviewed), never a guessed branch name.</summary>
    public string? SourceLabel { get; init; }
    public string? File { get; init; }
    public string? Symbol { get; init; }
    public string? Lines { get; init; }
    public string? ContractId { get; init; }
    public string? RequirementId { get; init; }
    public string? TestId { get; init; }
    /// <summary>A document in this repository (e.g. a reviewed audit) that records the evidence.</summary>
    public string? Document { get; init; }
    public string? Note { get; init; }
    /// <summary>An existing BirkNext route (relative, no scheme) that opens the owning explorer.</summary>
    public string? Link { get; init; }
}

public sealed record GuideComponent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>One short line shown on the diagram.</summary>
    public string Responsibility { get; init; } = "";
    public string? Description { get; init; }
    public List<string> Inputs { get; init; } = [];
    public List<string> Outputs { get; init; } = [];
    public string? Technology { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public string? ExpectedSuccess { get; init; }
    public string? CommonFailure { get; init; }
    /// <summary>"main" for the main journey lane, otherwise the side-flow lane it belongs to.</summary>
    public string Lane { get; init; } = "main";
    /// <summary>External (another owner's system) vs part of the system under test.</summary>
    public bool External { get; init; }
    public List<GuideEvidence> Evidence { get; init; } = [];
    public List<string> TestScenarioIds { get; init; } = [];
}

public sealed record GuideEdge
{
    public string Id { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public GuideFlowKind Kind { get; init; }
    /// <summary>How it travels, e.g. "Webhook + XML retrieval", "Service Bus reference + encrypted blob", "HTTP POST".</summary>
    public string Transport { get; init; } = "";
    /// <summary>What travels, e.g. "XML (v2.0.0 flat)", "JSON", "Parquet + manifest".</summary>
    public string DataFormat { get; init; } = "";
    public string? Contract { get; init; }
    public string? ContractVersion { get; init; }
    public string? Description { get; init; }
    /// <summary>Uncertainty kept visible (e.g. "transport not source-verified").</summary>
    public string? Uncertainty { get; init; }
    public List<GuideEvidence> Evidence { get; init; } = [];
}

public sealed record GuideFlow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public GuideFlowKind Kind { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>The ordered component ids of this flow (for highlighting).</summary>
    public List<string> Path { get; init; } = [];
}

public sealed record GuideStep
{
    public string Id { get; init; } = "";
    public int Order { get; init; }
    public string Stage { get; init; } = "";
    public string Title { get; init; } = "";
    public string ComponentId { get; init; } = "";
    /// <summary>One or two sentences: what happens and why.</summary>
    public string Summary { get; init; } = "";
    public string? Trigger { get; init; }
    public string? Input { get; init; }
    public string? Processing { get; init; }
    public string? Output { get; init; }
    public string? DataFormat { get; init; }
    public string? ExpectedResult { get; init; }
    public List<string> WhatCanGoWrong { get; init; } = [];
    public string? HowToVerify { get; init; }
    public List<GuideEvidence> Evidence { get; init; } = [];
    public List<string> TestScenarioIds { get; init; } = [];
}

public sealed record GuideStage
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>The flow this stage belongs to (main or a side flow).</summary>
    public string FlowId { get; init; } = "";
}

public sealed record GuideRule
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? WhyItMatters { get; init; }
    public string? Conditions { get; init; }
    public string? ExpectedBehavior { get; init; }
    public string? ComponentId { get; init; }
    /// <summary>How the rule is established in source/specification (never "verified" merely because code exists).</summary>
    public GuideEvidenceClass Implementation { get; init; } = GuideEvidenceClass.NotAssessed;
    /// <summary>Whether an executed test established it (NotAssessed until execution evidence exists).</summary>
    public GuideEvidenceClass Verification { get; init; } = GuideEvidenceClass.NotAssessed;
    public List<GuideEvidence> Evidence { get; init; } = [];
    public List<string> TestScenarioIds { get; init; } = [];
}

public sealed record GuideTestScenario
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Objective { get; init; } = "";
    public string? Preconditions { get; init; }
    public string? TestData { get; init; }
    public string? ExpectedOutcome { get; init; }
    public string? ObservableEvidence { get; init; }
    public List<string> FailureCases { get; init; } = [];
    public GuideScenarioState State { get; init; } = GuideScenarioState.Suggested;
    /// <summary>Existing test definitions (source tests or BirkNext scenarios) this guidance relates to — never counted as coverage here.</summary>
    public List<string> ExistingTests { get; init; } = [];
    public string? FollowUp { get; init; }
    public List<string> StepIds { get; init; } = [];
    public List<GuideEvidence> Evidence { get; init; } = [];
}

/// <summary>A data transfer between components (the Data &amp; Contracts matrix). Version facts stay per source snapshot.</summary>
public sealed record GuideTransfer
{
    public string EdgeId { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Transport { get; init; } = "";
    public string DataFormat { get; init; } = "";
    public string? Contract { get; init; }
    /// <summary>Version facts per source snapshot ("snapshot label → version/route"), never merged into one "current" version.</summary>
    public List<GuideVersionFact> Versions { get; init; } = [];
    public GuideEvidenceClass Evidence { get; init; } = GuideEvidenceClass.NotAssessed;
    public string Verification { get; init; } = "Not verified";
    public string? Link { get; init; }
}

public sealed record GuideVersionFact(string SourceLabel, string Fact, GuideEvidenceClass Class);

public sealed record GuideObservation(string ComponentId, string WhatToObserve, string Availability);

public sealed record GuideGap(string Id, string Title, string Detail, GuideEvidenceClass Class, string? Owner);

/// <summary>Plain-language one-minute overview.</summary>
public sealed record GuideOverview
{
    public string BusinessPurpose { get; init; } = "";
    public string MainInput { get; init; } = "";
    public string Processing { get; init; } = "";
    public string MainOutput { get; init; } = "";
    public List<string> Dependencies { get; init; } = [];
}

/// <summary>A source role the guide's evidence refers to, with how imported snapshots are recognised (for change awareness).</summary>
public sealed record GuideSourceRole(string Role, string Label, List<string> MatchTokens);

/// <summary>The content of one guide revision. Generic: nothing here is specific to one system.</summary>
public sealed record SystemGuideContent
{
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Language { get; init; } = "nb";
    public GuideOverview Overview { get; init; } = new();
    public List<GuideComponent> Components { get; init; } = [];
    public List<GuideEdge> Edges { get; init; } = [];
    public List<GuideFlow> Flows { get; init; } = [];
    public List<GuideStage> Stages { get; init; } = [];
    public List<GuideStep> Steps { get; init; } = [];
    public List<GuideRule> Rules { get; init; } = [];
    public List<GuideTestScenario> Scenarios { get; init; } = [];
    public List<GuideTransfer> Transfers { get; init; } = [];
    public List<GuideObservation> Observability { get; init; } = [];
    public List<GuideGap> Gaps { get; init; } = [];
    public List<GuideSourceRole> SourceRoles { get; init; } = [];
}

/// <summary>A source snapshot a revision was written or reviewed against (identity only; no source content).</summary>
public sealed record GuideSourceBasis(string Role, Guid? SnapshotId, string? Fingerprint, string Label);

/// <summary>One immutable (once beyond Draft) revision of a guide.</summary>
public sealed record SystemGuideRevision
{
    public string GuideKey { get; init; } = "";
    public int Revision { get; init; }
    public GuideRevisionState State { get; init; }
    public string ContentFingerprint { get; init; } = "";
    public SystemGuideContent Content { get; init; } = new();
    public List<GuideSourceBasis> SourceBasis { get; init; } = [];
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string? ChangeSummary { get; init; }
    public string? SubmittedBy { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public int? SupersededByRevision { get; init; }
}

public sealed record SystemGuideRevisionSummary(int Revision, GuideRevisionState State, string ContentFingerprint, string CreatedBy, DateTimeOffset CreatedAt,
    string? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset? PublishedAt, string? ChangeSummary);

/// <summary>Source changes since the shown revision's basis: a hint that the guide may need review — never an automatic rewrite.</summary>
public sealed record GuideSourceChange(string Role, string Label, string Change, int AffectedClaims);

/// <summary>A guide as the page shows it: the revision to read (published, else the latest draft), the draft, history and change awareness.</summary>
public sealed record SystemGuideView
{
    public string GuideKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public SystemGuideRevision? Shown { get; init; }
    public SystemGuideRevision? Draft { get; init; }
    public List<SystemGuideRevisionSummary> Revisions { get; init; } = [];
    /// <summary>"Published", "Draft only — not reviewed", "None".</summary>
    public string ReviewState { get; init; } = "None";
    public List<GuideSourceChange> SourceChanges { get; init; } = [];
    /// <summary>Snapshots currently imported for the guide's roles (identity only).</summary>
    public List<GuideSourceBasis> CurrentSources { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanApprove { get; init; }
}

/// <summary>A guide in the catalog, with whether the current workspace has imported sources for it.</summary>
public sealed record SystemGuideSummary(string GuideKey, string Title, string Purpose, string ReviewState, int? PublishedRevision, int? DraftRevision, int ImportedSourceRoles, int SourceRoles);

public sealed record SaveGuideDraftRequest(SystemGuideContent Content, string? ChangeSummary, string? BaseFingerprint);

/// <summary>The one rule for guide links (backend validation and frontend rendering): a relative BirkNext route — letters, digits and URL
/// punctuation, no scheme, no leading "//" or "\", no traversal. Anything else is shown as text, never as a link.</summary>
public static class SystemGuideLinks
{
    public static bool IsSafeRoute(string link) =>
        link.Length is > 0 and <= 500 && !link.Contains(':') && !link.StartsWith("//", StringComparison.Ordinal) && !link.Contains('\\') && !link.Contains("..", StringComparison.Ordinal)
        && link.All(ch => char.IsLetterOrDigit(ch) || "/-_.?=&%#+~".Contains(ch));
}
