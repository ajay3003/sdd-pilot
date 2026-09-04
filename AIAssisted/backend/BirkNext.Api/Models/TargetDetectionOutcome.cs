using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

/// <summary>
/// Wraps detection results with state information and activation readiness.
/// Combines the preflight response with computed state and decision metadata.
/// </summary>
public sealed class TargetDetectionOutcome
{
    /// <summary>
    /// The underlying detection response containing reachability, auth metadata, etc.
    /// </summary>
    [JsonPropertyName("detectionResponse")]
    public TargetEnvironmentDetectionResponse DetectionResponse { get; set; } = new();

    /// <summary>
    /// Current detection state (NotChecked, Checking, Complete, AuthenticationRequired, etc.)
    /// </summary>
    [JsonPropertyName("state")]
    public TargetDetectionState State { get; set; } = TargetDetectionState.NotChecked;

    /// <summary>
    /// Whether the profile is ready for activation.
    /// True only when state is Complete and the URL has not changed.
    /// </summary>
    [JsonPropertyName("isActivationReady")]
    public bool IsActivationReady { get; set; }

    /// <summary>
    /// Suggested detection strategy based on current state.
    /// Examples: "direct-access", "browser-auth-required", "no-action-needed"
    /// </summary>
    [JsonPropertyName("strategySuggestion")]
    public string? StrategySuggestion { get; set; }

    /// <summary>
    /// Timestamp when this detection was performed (UTC).
    /// Null if no detection has been attempted.
    /// </summary>
    [JsonPropertyName("detectedAt")]
    public DateTime? DetectedAt { get; set; }

    /// <summary>
    /// The URL that was detected (for staleness checking).
    /// Should match the profile's TargetUrl for state to be valid.
    /// </summary>
    [JsonPropertyName("detectedUrl")]
    public string? DetectedUrl { get; set; }

    /// <summary>
    /// Whether the current state is up-to-date with the profile's TargetUrl.
    /// False if URL has changed since detection (state becomes Stale).
    /// </summary>
    [JsonPropertyName("isUrlCurrent")]
    public bool IsUrlCurrent { get; set; }

    /// <summary>
    /// Human-readable message explaining the current state and any actions needed.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Whether this Partial state specifically requires browser runtime inspection.
    /// When true: Continue detection in browser is available.
    /// When false or null: Partial for other reasons (e.g., temporary error, incomplete detection).
    /// </summary>
    [JsonPropertyName("browserRuntimeInspectionRequired")]
    public bool? BrowserRuntimeInspectionRequired { get; set; }
}
