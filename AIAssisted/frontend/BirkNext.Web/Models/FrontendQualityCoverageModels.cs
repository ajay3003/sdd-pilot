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
    TimedOut,
    Cancelled,
    EngineError,
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineOutcomeReason
{
    None,
    NotSelected,
    BlockedByDeploymentPolicy,
    DisabledInSystemSettings,
    ReadinessUnavailable,
    AuthenticationRequired,
    AuthenticationExpired,
    AuthenticationCancelled,
    UnexpectedOrigin,
    AuthenticationModeUnsupported,
    TargetPolicyRejected,
    SessionUnavailable,
    ResourceUnavailable,
    EngineUnavailable,
    EngineError,
    Cancelled,
    /// <summary>The target could not be reached over the network (DNS, TLS, connection failure or policy block). Not a timeout.</summary>
    TargetUnreachable,
    /// <summary>The target answered with an HTTP error status (e.g. 404, 500); the status is carried in the sanitized failure reason.</summary>
    TargetHttpError,
    /// <summary>A request was actually issued over the correct access path and no response arrived within the timeout.</summary>
    TimedOut,
    /// <summary>Authenticated testing method selected, but its runtime context (Local HTTPS proxy API context) is not available.</summary>
    AuthenticatedContextUnavailable,
    /// <summary>The authenticated API context existed but expired.</summary>
    AuthenticatedContextExpired,
    /// <summary>Managed Edge (CDP) attach to the target tab is refused by enterprise browser protection.</summary>
    EnterpriseBrowserProtectionBlocked,
    /// <summary>The engine needs an authenticated browser DOM, which the selected method (Local HTTPS proxy) can never provide.</summary>
    BrowserDomUnavailableForMethod,
    /// <summary>The environment's authentication method is manual verification only; no automated authenticated access exists.</summary>
    ManualOnlyMethod,
    /// <summary>The engine is disabled in the saved Target Environment configuration; it was not part of this review.</summary>
    DisabledInTargetEnvironment,
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
    [JsonPropertyName("outcomeReason")] public FrontendQualityEngineOutcomeReason OutcomeReason { get; init; }
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

    /// <summary>Access path the engine used or would need (Public HTTP, Authenticated HTTP, Authenticated browser session, Browser runtime).</summary>
    [JsonPropertyName("accessKind")] public FrontendQualityEngineAccessKind? AccessKind { get; init; }
    /// <summary>User-facing access label including the method, e.g. "Public HTTP (frontend shell)".</summary>
    [JsonPropertyName("accessLabel")] public string? AccessLabel { get; init; }
    /// <summary>Contextual next step for a blocked/unsupported engine (never an automatic action).</summary>
    [JsonPropertyName("requiredAction")] public string? RequiredAction { get; init; }
    [JsonPropertyName("actionHref")] public string? ActionHref { get; init; }

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

    /// <summary>Required engines by policy. A required engine that is disabled stays in the denominator: it is a configuration inconsistency that keeps required coverage incomplete, never a silently excluded engine.</summary>
    [JsonPropertyName("requiredTotal")] public int RequiredTotal { get; init; }
    [JsonPropertyName("requiredAssessed")] public int RequiredAssessed { get; init; }
    /// <summary>Optional engines that were ACTIVE for this review (enabled and selected). Disabled or not-selected optional engines are not missed assessments and are excluded.</summary>
    [JsonPropertyName("optionalTotal")] public int OptionalTotal { get; init; }
    [JsonPropertyName("optionalAssessed")] public int OptionalAssessed { get; init; }
    /// <summary>Engines excluded from the denominators because they were disabled or not selected for this review.</summary>
    [JsonPropertyName("inactiveCount")] public int InactiveCount { get; init; }

    public static FrontendQualityCoverage Evaluate(IReadOnlyCollection<FrontendQualityEngineOutcome> outcomes)
    {
        var required = outcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Required).ToList();
        var assessed = required.Count(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        var optionalActive = outcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Optional && !IsInactive(o)).ToList();
        return new FrontendQualityCoverage
        {
            RequiredCoverageState = required.Count > 0 && assessed == required.Count
                ? FrontendQualityRequiredCoverageState.AllRequiredAssessed
                : assessed > 0
                    ? FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed
                    : FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment,
            RequiredTotal = required.Count,
            RequiredAssessed = assessed,
            OptionalTotal = optionalActive.Count,
            OptionalAssessed = optionalActive.Count(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed),
            InactiveCount = outcomes.Count(IsInactive),
        };
    }

    /// <summary>An engine that was not part of this review: disabled in the saved configuration or deselected for this run.</summary>
    public static bool IsInactive(FrontendQualityEngineOutcome outcome) =>
        !outcome.Enabled ||
        outcome.ExecutionState == FrontendQualityEngineExecutionState.Disabled ||
        outcome.OutcomeReason is FrontendQualityEngineOutcomeReason.NotSelected or FrontendQualityEngineOutcomeReason.DisabledInSystemSettings
            or FrontendQualityEngineOutcomeReason.DisabledInTargetEnvironment;

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
