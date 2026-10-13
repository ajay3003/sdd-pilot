using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Payload & Mapping Flow: what a journey run (or dry run) can say about the payload at each stage of a message flow. Every stage, fact,
// mapping row and payload carries its evidence level and source, so a generated payload is never shown as submitted, an expected
// downstream contract never as observed, and a stage without runtime evidence never as passed or failed. Payload values are masked before
// they are put here; a raw personal identifier, secret or token is never representable.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How far the evidence for one stage goes. Expected = what the contract/configuration/design says should happen (no runtime evidence);
/// Generated = built by BirkNext, not sent; Submitted = sent and accepted by the receiving API; Observed = seen at runtime by a reader;
/// Confirmed = read back from the system itself; NotObserved = the run happened but no evidence source exists or nothing was seen;
/// NotEvaluated = this run did not get far enough to say; Unavailable = the evidence source could not be reached; Failed = the stage
/// answered with a failure. NotObserved is not Failed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowStageEvidenceLevel { Expected, Generated, Submitted, Observed, Confirmed, NotObserved, NotEvaluated, Unavailable, Failed }

/// <summary>How a source field relates to a target field. Matched is never inferred from similar names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadMappingStatus { Matched, Transformed, Dropped, Added, Unknown, NotObserved }

/// <summary>Where a mapping row or value comes from, strongest first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadProvenance { Observed, Configured, SourceDerived, Documented }

/// <summary>One payload field as shown to a tester. <see cref="Value"/> is already masked when the field is sensitive.</summary>
public sealed record PayloadFieldValue(string Path, string Label, string Value, string Source, string? Note = null);

/// <summary>One labelled fact of a stage (contract, correlation, evidence) with the source it comes from.</summary>
public sealed record FlowStageFact(string Label, string Value, string Source);

/// <summary>A validation or contract check: what was expected, what is actually known (or "Not evaluated"), and the evidence source.</summary>
public sealed record FlowStageCheck(string Label, string Expected, string Actual, string Source);

/// <summary>One source → target field relation between stages, with values masked and its provenance.</summary>
public sealed record PayloadMappingEntry(
    string SourceStage,
    string SourcePath,
    string SourceValue,
    string TargetStage,
    string TargetPath,
    string TargetValue,
    PayloadMappingStatus Status,
    PayloadProvenance Provenance,
    string? Transformation = null,
    string Notes = "");

/// <summary>
/// A payload exactly as a step generated (and possibly sent) it — with every sensitive value masked before it is stored. The unmasked bytes
/// are never kept; <see cref="ByteCount"/> describes the body that was actually built.
/// </summary>
public sealed record JourneyPayloadCapture
{
    /// <summary>xml, json or text.</summary>
    public string Format { get; init; } = "text";
    public string ContentType { get; init; } = "";
    public string? DataType { get; init; }
    public int ByteCount { get; init; }
    /// <summary>The payload with sensitive values masked. Display only: it is not the sent body.</summary>
    public string MaskedContent { get; init; } = "";
    public IReadOnlyList<PayloadFieldValue> Fields { get; init; } = [];
    public IReadOnlyList<PayloadMappingEntry> Mappings { get; init; } = [];
    /// <summary>True when the implementation sent this very body (no change between generation and the request).</summary>
    public bool SentAsGenerated { get; init; }
    public string Note { get; init; } = "";
}

public sealed record FlowStageEvidence
{
    public string StageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int Sequence { get; init; }
    public FlowStageEvidenceLevel Level { get; init; } = FlowStageEvidenceLevel.NotEvaluated;
    /// <summary>One line a tester reads first (e.g. "Generated and submitted").</summary>
    public string Summary { get; init; } = "";
    /// <summary>The runtime observation, separately from the expectation (e.g. "Not observed").</summary>
    public string Runtime { get; init; } = "";
    public DateTimeOffset? Timestamp { get; init; }
    public string EvidenceSource { get; init; } = "";
    /// <summary>For expectations shown next to a recorded run: the expectation is the current one, not something recorded during the run.</summary>
    public string? ExpectationBasis { get; init; }
    /// <summary>How to read the payload: "Generated payload", "Generated and submitted", "Expected representation", "No payload".</summary>
    public string PayloadState { get; init; } = "No payload";
    public IReadOnlyList<PayloadFieldValue> Payload { get; init; } = [];
    /// <summary>A payload observed at runtime by a reader; empty when nothing was observed. A difference is shown only when both exist.</summary>
    public IReadOnlyList<PayloadFieldValue> ObservedPayload { get; init; } = [];
    public JourneyPayloadCapture? TechnicalPayload { get; init; }
    public IReadOnlyList<FlowStageFact> Contract { get; init; } = [];
    public IReadOnlyList<FlowStageCheck> Validation { get; init; } = [];
    public IReadOnlyList<PayloadMappingEntry> Mappings { get; init; } = [];
    /// <summary>Why no (or only some) mapping rows are shown.</summary>
    public string MappingNote { get; init; } = "";
    public IReadOnlyList<FlowStageFact> Correlation { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? Diagnostic { get; init; }
}

/// <summary>A timeline point. <see cref="At"/> is null when it was not observed; times are never synthesized.</summary>
public sealed record FlowTimelineEntry(string Label, DateTimeOffset? At, string State);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayloadFlowMode { DryRun, Run }

public sealed record PayloadFlowView
{
    public PayloadFlowMode Mode { get; init; }
    public Guid? RunId { get; init; }
    public string Title { get; init; } = "Payload & Mapping Flow";
    public IReadOnlyList<FlowStageEvidence> Stages { get; init; } = [];
    public IReadOnlyList<FlowTimelineEntry> Timeline { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}
