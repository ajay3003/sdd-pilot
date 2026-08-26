using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineId
{
    StaticSecurity,
    PassivePerformance,
    BrowserRuntime,
    Accessibility,
    Lighthouse,
    PassiveSecurity,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineRequirement { Required, Optional }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineExecutionState
{
    Assessed,
    Disabled,
    Unavailable,
    SafetyBlocked,
    AuthenticationRequired,
    TimedOut,
    Cancelled,
    EngineError,
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineReadinessState
{
    NotEvaluated,
    Ready,
    Unavailable,
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityRequiredCoverageState
{
    AllRequiredAssessed,
    SomeRequiredNotAssessed,
    NoTrustworthyRequiredAssessment,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityReleaseDisposition
{
    Blocked,
    ReviewRequired,
    NoAutomatedBlockDetected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEvidenceStrength
{
    DirectObservation,
    ToolDiagnostic,
    StaticIndicator,
    DerivedSummary,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityReviewDisposition
{
    AutomatedFinding,
    ManualVerificationRequired,
    Informational,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEvidenceConfidence { High, Moderate, Uncertain }

/// <summary>
/// Explicit product policy. Enabling an engine or installing its tool never changes its requirement.
/// No default policy is supplied in Phase 2E-1.
/// </summary>
public sealed class FrontendQualityEngineRequirementPolicy
{
    private readonly IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineRequirement> _requirements;

    public FrontendQualityEngineRequirementPolicy(
        IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineRequirement> requirements) =>
        _requirements = requirements;

    public FrontendQualityEngineRequirement GetRequirement(FrontendQualityEngineId engineId) =>
        _requirements.TryGetValue(engineId, out var requirement)
            ? requirement
            : throw new InvalidOperationException($"No explicit requirement is configured for engine '{engineId}'.");
}

public sealed record FrontendQualityEvidenceDescriptor
{
    [JsonPropertyName("strength")] public FrontendQualityEvidenceStrength Strength { get; init; }
    [JsonPropertyName("disposition")] public FrontendQualityReviewDisposition Disposition { get; init; }
    [JsonPropertyName("confidence")] public FrontendQualityEvidenceConfidence Confidence { get; init; }
}

/// <summary>
/// Data-minimized aggregate outcome. Values must already be sanitized by the source engine.
/// Raw bodies, credentials, cookies, DOM/storage data and unsanitized URLs do not belong here.
/// </summary>
public sealed record FrontendQualityEngineOutcome
{
    [JsonPropertyName("engineId")] public FrontendQualityEngineId EngineId { get; init; }
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("requirement")] public FrontendQualityEngineRequirement Requirement { get; init; }
    [JsonPropertyName("readinessState")] public FrontendQualityEngineReadinessState ReadinessState { get; init; }
    [JsonPropertyName("readinessReason")] public string? ReadinessReason { get; init; }
    [JsonPropertyName("executionState")] public FrontendQualityEngineExecutionState ExecutionState { get; init; }
    [JsonPropertyName("requestedTarget")] public string? RequestedTarget { get; init; }
    [JsonPropertyName("finalTarget")] public string? FinalTarget { get; init; }
    [JsonPropertyName("startedAt")] public DateTime? StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
    [JsonPropertyName("toolVersion")] public string? ToolVersion { get; init; }
    [JsonPropertyName("browserName")] public string? BrowserName { get; init; }
    [JsonPropertyName("browserVersion")] public string? BrowserVersion { get; init; }
    [JsonPropertyName("findingCount")] public int? FindingCount { get; init; }
    [JsonPropertyName("evidenceCount")] public int? EvidenceCount { get; init; }
    [JsonPropertyName("sanitizedFailureReason")] public string? SanitizedFailureReason { get; init; }
    [JsonPropertyName("limitations")] public List<string> Limitations { get; init; } = [];
    [JsonPropertyName("manualTestingObligations")] public List<string> ManualTestingObligations { get; init; } = [];
    [JsonPropertyName("evidence")] public List<FrontendQualityEvidenceDescriptor> Evidence { get; init; } = [];

    public static FrontendQualityEngineOutcome CreateWithSanitizedFailure(
        FrontendQualityEngineId engineId,
        string displayName,
        bool enabled,
        FrontendQualityEngineRequirement requirement,
        FrontendQualityEngineExecutionState executionState,
        string? sourceFailureReason,
        Func<string?, string?> sourceEngineSanitizer) => new()
        {
            EngineId = engineId,
            DisplayName = displayName,
            Enabled = enabled,
            Requirement = requirement,
            ExecutionState = executionState,
            SanitizedFailureReason = sourceEngineSanitizer(sourceFailureReason),
        };
}

public sealed class FrontendQualityCoverage
{
    [JsonPropertyName("requiredCoverageState")]
    public FrontendQualityRequiredCoverageState RequiredCoverageState { get; init; }

    public static FrontendQualityCoverage Evaluate(IReadOnlyCollection<FrontendQualityEngineOutcome> outcomes)
    {
        var required = outcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Required).ToList();
        var assessed = required.Count(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        return new FrontendQualityCoverage
        {
            RequiredCoverageState = required.Count > 0 && assessed == required.Count
                ? FrontendQualityRequiredCoverageState.AllRequiredAssessed
                : assessed > 0
                    ? FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed
                    : FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment,
        };
    }

    public AssessmentCompleteness ToLegacyCompleteness() => RequiredCoverageState switch
    {
        FrontendQualityRequiredCoverageState.AllRequiredAssessed => AssessmentCompleteness.Full,
        FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed => AssessmentCompleteness.Partial,
        _ => AssessmentCompleteness.Failed,
    };
}

public static class FrontendQualityEngineCompatibility
{
    public static List<string> Assessed(IReadOnlyCollection<FrontendQualityEngineOutcome> outcomes) =>
        Names(outcomes, o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);

    public static List<string> Failed(IReadOnlyCollection<FrontendQualityEngineOutcome> outcomes) =>
        Names(outcomes, o => o.ExecutionState is FrontendQualityEngineExecutionState.TimedOut
            or FrontendQualityEngineExecutionState.Cancelled
            or FrontendQualityEngineExecutionState.EngineError);

    public static List<string> Skipped(IReadOnlyCollection<FrontendQualityEngineOutcome> outcomes) =>
        Names(outcomes, o => o.ExecutionState is FrontendQualityEngineExecutionState.Disabled
            or FrontendQualityEngineExecutionState.Unavailable
            or FrontendQualityEngineExecutionState.SafetyBlocked
            or FrontendQualityEngineExecutionState.AuthenticationRequired
            or FrontendQualityEngineExecutionState.NotApplicable);

    private static List<string> Names(
        IEnumerable<FrontendQualityEngineOutcome> outcomes,
        Func<FrontendQualityEngineOutcome, bool> predicate) => outcomes
        .Where(predicate)
        .OrderBy(o => o.EngineId)
        .Select(o => o.DisplayName)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
