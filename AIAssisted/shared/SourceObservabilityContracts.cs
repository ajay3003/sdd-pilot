using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;

namespace BirkNext.SourceObservability;

// ── Source Analysis → Observability (correlation/tracing, logging quality, telemetry configuration) ─────────────────────────────
// Source-derived ONLY: what the analyzed source and configuration implement — never that context flows, logs are complete or telemetry is
// delivered at runtime. Evidence states are the Source Analysis states (Confirmed … Conflict); a finding's severity is a separate axis.
// Generic by design: rules key on languages, frameworks, libraries, APIs and configuration patterns — never on a project or repository
// name. Evidence keeps file, line, symbol and a pattern label; source text, configuration values and secret literals are never stored.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservabilityFindingKind { Observation, Finding, Limitation, Unresolved }

/// <summary>How much attention a finding asks for. Never Pass/Fail: these are review prompts over source, not verdicts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservabilitySeverity { Info, NeedsReview, Warning, HighPriority }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservabilityCategory
{
    Tracing, Correlation, StructuredLogging, ExceptionPreservation, CatchAndSwallow, LogLevel, RetryFailure, HandlerObservability,
    CorrelationContextLogging, SensitiveData, PayloadLogging, Redaction, Telemetry, LoggingConfiguration, FrontendLogging, DeveloperTests,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationBoundaryType { HttpInbound, HttpOutbound, MessageProduce, MessageConsume }

/// <summary>What source shows for one boundary. Framework instrumentation / configured / explicit are source facts; none is runtime success.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PropagationState { FrameworkInstrumentation, PropagationConfigured, PropagationExplicit, PropagationInferred, Unresolved, Conflicting }

/// <summary>Kinds of context identity. They are different things unless source maps one to another.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationIdentityType { W3CTraceContext, ActivityTrace, CorrelationId, RequestId, MessageId, OperationId, CustomContext }

/// <summary>A summary dimension's source state. Descriptive, never a grade.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservabilityDimensionState { Detected, StronglySupported, Partial, NeedsReview, NotFound, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyzerSupport { Supported, Partial, Unsupported }

/// <summary>Where evidence sits: file, line, enclosing symbol and the detected pattern (a label — never the source text or a value).</summary>
public sealed record ObservabilityEvidence(string File, int Line, string Symbol, string Pattern, string? Component = null);

public sealed record ObservabilityFinding
{
    public string Id { get; init; } = "";
    public ObservabilityFindingKind Kind { get; init; }
    public ObservabilityCategory Category { get; init; }
    public string? Component { get; init; }
    public string Technology { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public ObservabilitySeverity Severity { get; init; }
    public int Occurrences { get; init; } = 1;
    public List<ObservabilityEvidence> Evidence { get; init; } = [];
    public string? Limitation { get; init; }
}

/// <summary>One correlation/trace mechanism found in source (e.g. Activity tracing, a custom correlation header, a request id).</summary>
public sealed record CorrelationMechanism
{
    public string Id { get; init; } = "";
    public CorrelationIdentityType Identity { get; init; }
    public string Name { get; init; } = "";
    public string Technology { get; init; } = "";
    public List<string> Components { get; init; } = [];
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public List<ObservabilityEvidence> Evidence { get; init; } = [];
}

/// <summary>One boundary where context could be received or sent: HTTP in/out, message produce/consume.</summary>
public sealed record CorrelationBoundary
{
    public string Id { get; init; } = "";
    public string Component { get; init; } = "";
    public CorrelationBoundaryType Type { get; init; }
    /// <summary>The other side when Architecture resolves it (component, channel or external target); null when not resolved.</summary>
    public string? Peer { get; init; }
    public string Transport { get; init; } = "";
    public string Mechanism { get; init; } = "";
    public List<CorrelationIdentityType> Context { get; init; } = [];
    public PropagationState Propagation { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public List<ObservabilityEvidence> Evidence { get; init; } = [];
    public string? Limitation { get; init; }
}

/// <summary>A source-derived propagation edge between two components through a boundary pair (e.g. producer → channel → consumer).</summary>
public sealed record CorrelationEdge(string From, string To, string Via, string Mechanism, ArchitectureEvidenceState EvidenceState, string? Limitation);

public sealed record CorrelationSummary
{
    public int ComponentsWithTracing { get; init; }
    public int HttpBoundaries { get; init; }
    public int MessagingBoundaries { get; init; }
    public int PropagationSupported { get; init; }
    public int PropagationUnresolved { get; init; }
    public int ConflictingMechanisms { get; init; }
}

public sealed record ObservabilityDimension(string Id, string Title, ObservabilityDimensionState State, string Detail);

public sealed record LoggingSummary
{
    public List<string> Frameworks { get; init; } = [];
    public int StructuredCalls { get; init; }
    public int InterpolatedCalls { get; init; }
    public int ConcatenatedCalls { get; init; }
    public int ExceptionPreserved { get; init; }
    public int ExceptionMessageOnly { get; init; }
    public List<ObservabilityDimension> Dimensions { get; init; } = [];
}

/// <summary>A configured setting with its file and environment ("Development", "Production", "Default"). Values are safe labels only.</summary>
public sealed record ConfiguredSetting(string Component, string Key, string Value, string File, string Environment);

public sealed record TelemetrySummary
{
    public List<string> Exporters { get; init; } = [];
    public List<ConfiguredSetting> ServiceNames { get; init; } = [];
    public List<ConfiguredSetting> LogLevels { get; init; } = [];
    public List<ConfiguredSetting> Sampling { get; init; } = [];
    public List<string> Sinks { get; init; } = [];
}

public sealed record ObservabilityComponent
{
    public string ComponentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ComponentType { get; init; } = "";
    public List<string> TracingTechnologies { get; init; } = [];
    public List<string> LoggingFrameworks { get; init; } = [];
    public List<string> TelemetryExporters { get; init; } = [];
    public int LogCalls { get; init; }
    public ObservabilityDimensionState Correlation { get; init; }
    public ObservabilityDimensionState Logging { get; init; }
    /// <summary>Always "Not assessed in Source Analysis": runtime telemetry belongs to runtime reviews.</summary>
    public string Runtime { get; init; } = ObservabilitySnapshot.RuntimeNotAssessed;
}

public sealed record AnalyzerCapability(string Technology, AnalyzerSupport Correlation, AnalyzerSupport Logging, string Detail);

public sealed record SourceObservabilitySnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset ExtractedAt { get; init; }
    /// <summary>Analysis completeness (Complete / Partial / Unsupported) — never logging quality.</summary>
    public ArchitectureStatus Status { get; init; }
    public CorrelationSummary Correlation { get; init; } = new();
    public LoggingSummary Logging { get; init; } = new();
    public TelemetrySummary Telemetry { get; init; } = new();
    public List<ObservabilityComponent> Components { get; init; } = [];
    public List<CorrelationMechanism> Mechanisms { get; init; } = [];
    public List<CorrelationBoundary> Boundaries { get; init; } = [];
    public List<CorrelationEdge> Edges { get; init; } = [];
    public List<ObservabilityFinding> Findings { get; init; } = [];
    public List<AnalyzerCapability> Capabilities { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public List<string> UnsupportedEvidence { get; init; } = [];
}

/// <summary>A source-to-source difference in observability evidence; it does not imply runtime telemetry behavior changed.</summary>
public sealed record SourceObservabilityChange(string Kind, string Area, string Subject, string Detail, string? EntityId = null);

/// <summary>Canonical comparison of the stored observability snapshots, shared by Source Analysis and downstream evidence consumers.</summary>
public static class SourceObservabilityComparison
{
    public static List<SourceObservabilityChange> Compare(SourceObservabilitySnapshot previous, SourceObservabilitySnapshot current)
    {
        static string ComponentName(SourceObservabilitySnapshot snapshot, string? id) => id is null ? "Snapshot-wide"
            : snapshot.Components.FirstOrDefault(c => c.ComponentId == id)?.Name ?? (id.IndexOf(':') is var i and >= 0 ? id[(i + 1)..] : id);
        var changes = new List<SourceObservabilityChange>();
        var before = previous.Findings.Where(f => f.Kind != ObservabilityFindingKind.Limitation).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var after = current.Findings.Where(f => f.Kind != ObservabilityFindingKind.Limitation).ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var f in after.Values.Where(f => !before.ContainsKey(f.Id))) changes.Add(new("Added", ObservabilitySnapshot.Label(f.Category), ComponentName(current, f.Component), f.Title, f.Component));
        foreach (var f in before.Values.Where(f => !after.ContainsKey(f.Id))) changes.Add(new("No longer found", ObservabilitySnapshot.Label(f.Category), ComponentName(previous, f.Component), f.Title, f.Component));
        foreach (var f in after.Values.Where(f => before.TryGetValue(f.Id, out var p) && p.Occurrences != f.Occurrences))
            changes.Add(new("Changed", ObservabilitySnapshot.Label(f.Category), ComponentName(current, f.Component), $"{f.Title}: {before[f.Id].Occurrences} → {f.Occurrences} occurrence(s)", f.Component));
        var oldBoundaries = previous.Boundaries.ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var b in current.Boundaries)
        {
            if (!oldBoundaries.TryGetValue(b.Id, out var old)) changes.Add(new("Added", "Correlation boundary", ComponentName(current, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport} · {ObservabilitySnapshot.Label(b.Propagation)}", b.Component));
            else if (old.Propagation != b.Propagation)
                changes.Add(new("Changed", "Correlation boundary", ComponentName(current, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport}: {ObservabilitySnapshot.Label(old.Propagation)} → {ObservabilitySnapshot.Label(b.Propagation)}", b.Component));
        }
        foreach (var b in previous.Boundaries.Where(b => current.Boundaries.All(x => x.Id != b.Id)))
            changes.Add(new("No longer found", "Correlation boundary", ComponentName(previous, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport}", b.Component));
        return changes.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Area, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal).ToList();
    }
}

public static class ObservabilitySnapshot
{
    public const string SourceLimitation = "Source-derived only: this shows what the selected source implements and configures. It does not prove that context propagates, that logs are complete, or that telemetry is delivered at runtime.";
    public const string RuntimeNotAssessed = "Not assessed in Source Analysis";

    public static string Label(PropagationState state) => state switch
    {
        PropagationState.FrameworkInstrumentation => "Framework instrumentation detected",
        PropagationState.PropagationConfigured => "Propagation configured",
        PropagationState.PropagationExplicit => "Propagation explicit",
        PropagationState.PropagationInferred => "Propagation inferred",
        PropagationState.Conflicting => "Conflicting",
        _ => "Unresolved",
    };

    public static bool Supported(PropagationState state) => state is PropagationState.FrameworkInstrumentation or PropagationState.PropagationConfigured or PropagationState.PropagationExplicit;

    public static string Label(ObservabilityDimensionState state) => state switch
    {
        ObservabilityDimensionState.StronglySupported => "Strongly supported",
        ObservabilityDimensionState.NeedsReview => "Needs review",
        ObservabilityDimensionState.NotFound => "Not found",
        ObservabilityDimensionState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string Label(ArchitectureEvidenceState state) => state == ArchitectureEvidenceState.StronglySupported ? "Strongly supported" : state.ToString();

    public static string Label(ObservabilitySeverity severity) => severity switch { ObservabilitySeverity.NeedsReview => "Needs review", ObservabilitySeverity.HighPriority => "High priority", _ => severity.ToString() };

    public static string Label(ObservabilityCategory category) => category switch
    {
        ObservabilityCategory.StructuredLogging => "Structured logging",
        ObservabilityCategory.ExceptionPreservation => "Exception preservation",
        ObservabilityCategory.CatchAndSwallow => "Catch-and-swallow",
        ObservabilityCategory.LogLevel => "Log levels",
        ObservabilityCategory.RetryFailure => "Retry / failure",
        ObservabilityCategory.HandlerObservability => "Message handlers",
        ObservabilityCategory.CorrelationContextLogging => "Correlation context in logs",
        ObservabilityCategory.SensitiveData => "Sensitive-data risk",
        ObservabilityCategory.PayloadLogging => "Payload logging",
        ObservabilityCategory.LoggingConfiguration => "Logging configuration",
        ObservabilityCategory.FrontendLogging => "Frontend logging",
        ObservabilityCategory.DeveloperTests => "Developer tests",
        _ => category.ToString(),
    };

    public static string Label(CorrelationBoundaryType type) => type switch
    {
        CorrelationBoundaryType.HttpInbound => "HTTP in",
        CorrelationBoundaryType.HttpOutbound => "HTTP out",
        CorrelationBoundaryType.MessageProduce => "Message produce",
        _ => "Message consume",
    };

    public static string Label(CorrelationIdentityType type) => type switch
    {
        CorrelationIdentityType.W3CTraceContext => "W3C trace context",
        CorrelationIdentityType.ActivityTrace => "Activity trace",
        CorrelationIdentityType.CorrelationId => "Correlation id",
        CorrelationIdentityType.RequestId => "Request id",
        CorrelationIdentityType.MessageId => "Message id",
        CorrelationIdentityType.OperationId => "Operation id",
        _ => "Custom context",
    };
}
