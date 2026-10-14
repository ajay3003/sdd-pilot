using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Message Verification: what actually happened to one Skolenærvær message, from read-only evidence that already exists (BirkNext run
// history, MM and Skoletjenesten telemetry). It is recorded on the existing journey run of the read-only message-verification scenario —
// no separate history. Every observation names its provider, how it was correlated and what it cannot show; a verdict is Passed only when
// the evidence proves the expected result. HTTP 200 (Mottatt) is never a business pass, and missing evidence is never a failure by itself.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How far one observation goes. These are evidence stages, not test verdicts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageEvidenceStage
{
    Generated, Submitted, Confirmed, TransportAccepted, MmProcessingObserved, SkoleHttpResponseObserved, SkoleProcessingObserved,
    InternalOutcomeVerified, FinalOutcomeVerified, NotObserved, NotEvaluated, AccessDenied, Unavailable,
}

/// <summary>A verification result. Passed only when the evidence proves the expected result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageVerificationVerdict { Passed, Failed, Blocked, NotRun, NotEvaluated }

/// <summary>
/// What an evidence provider could do. QueryCompletedNoEvidence = the read ran inside its window and found nothing (not a failure);
/// NotConfigured = no approved source for this environment; AccessDenied = the source refused the backend's identity; Unavailable = the
/// source could not be reached; NotEvaluated = nothing to look up (e.g. no correlating identifier); Blocked = no approved access exists.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageEvidenceProviderState { Available, QueryCompletedNoEvidence, NotConfigured, AccessDenied, Unavailable, NotEvaluated, Blocked, NotApplicable }

/// <summary>How two identifiers were linked. Timestamps, a shared child or a shared period never link two messages.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageCorrelationCertainty { ExactIdentifierMatch, ExplicitMappingObserved, PartialCorrelation, TesterAsserted, Uncorrelated }

public sealed record MessageEvidenceProviderStatus(
    string ProviderId,
    string DisplayName,
    string System,
    MessageEvidenceProviderState State,
    string Detail,
    string EvidenceBasis,
    DateTimeOffset? CapturedAt,
    string ResponsibleTeam);

/// <summary>One observation: allow-listed facts only (message ids, outcome and reason codes, attempts, times) — never a payload or person.</summary>
public sealed record MessageEvidenceObservation(
    string System,
    MessageEvidenceStage Stage,
    string Summary,
    string ProviderId,
    DateTimeOffset? At,
    string CorrelationMethod,
    IReadOnlyList<JourneyEvidenceFact> Facts,
    string Limitation);

public sealed record MessageCorrelation(
    string SourceType,
    string SourceValue,
    string TargetType,
    string? TargetValue,
    string ProviderId,
    DateTimeOffset? At,
    string MatchingRule,
    MessageCorrelationCertainty Certainty,
    string Limitation);

public sealed record MessageAssertion(string Id, string Label, string Expected, string Observed, MessageVerificationVerdict Verdict, string EvidenceBasis);

public sealed record MessageMissingEvidence(string What, string Why, string Required, string ResponsibleTeam, string NextAction);

/// <summary>
/// The verification of one message, stored on its journey run. Identifiers here are message identifiers (UUIDs, Service Bus ids, a short
/// Altinn instance reference) — never a person, payload, token or full instance id. A refresh is a new run; earlier records stay unchanged.
/// </summary>
public sealed record MessageVerificationRecord
{
    /// <summary>The assertion rule set that evaluated this record; earlier records keep the rules they were evaluated with.</summary>
    public string RuleVersion { get; init; } = "1";
    public string ExpectedScenario { get; init; } = "";
    public string ExpectedScenarioName { get; init; } = "";
    /// <summary>What this verification is about (message id, source run or instance reference), so refreshes of one message group together.</summary>
    public string TargetKey { get; init; } = "";
    public string TargetLabel { get; init; } = "";
    public Guid? SourceRunId { get; init; }
    public string? AltinnInstanceReference { get; init; }
    public string? MessageId { get; init; }
    public int WindowHours { get; init; }
    public MessageVerificationVerdict Verdict { get; init; } = MessageVerificationVerdict.NotRun;
    public string VerdictReason { get; init; } = "";
    public IReadOnlyList<MessageEvidenceProviderStatus> Providers { get; init; } = [];
    public IReadOnlyList<MessageEvidenceObservation> Observations { get; init; } = [];
    public IReadOnlyList<MessageCorrelation> Correlations { get; init; } = [];
    public IReadOnlyList<MessageAssertion> Assertions { get; init; } = [];
    public IReadOnlyList<MessageMissingEvidence> MissingEvidence { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; }
}
