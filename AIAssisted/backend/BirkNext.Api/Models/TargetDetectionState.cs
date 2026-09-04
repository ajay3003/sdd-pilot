using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

/// <summary>
/// Enum representing the current state of target environment detection.
/// Used to track detection progress and readiness for profile activation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetDetectionState
{
    /// <summary>
    /// Default state: no detection has been attempted for this target URL.
    /// No detection metadata available.
    /// </summary>
    NotChecked = 0,

    /// <summary>
    /// Detection is currently in progress (reserved for future use when browser automation runs).
    /// </summary>
    Checking = 1,

    /// <summary>
    /// Detection completed successfully.
    /// Target is reachable and accessible (no auth required, or auth was successfully handled).
    /// Profile is ready for activation.
    /// </summary>
    Complete = 2,

    /// <summary>
    /// Detection found an authentication boundary (401/403 or known IdP redirect).
    /// Target requires authentication before it can be fully accessed.
    /// Activation remains blocked; browser automation is needed for full detection.
    /// </summary>
    AuthenticationRequired = 3,

    /// <summary>
    /// Detection partially succeeded - got some metadata but detection is incomplete.
    /// Used when browser returns partial results (e.g., some cookies but not full auth flow).
    /// </summary>
    Partial = 4,

    /// <summary>
    /// Detection result is stale - the target URL has changed since detection.
    /// LastDetectedUrl differs from current profile TargetUrl.
    /// </summary>
    Stale = 5,

    /// <summary>
    /// Detection failed - network error, SSRF rejection, timeout, or other fatal error.
    /// No reachability metadata available.
    /// </summary>
    Failed = 6
}
