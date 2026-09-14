using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Persisted snapshot of the last successful/meaningful target detection so a restart can restore it as "Previously detected" and
/// revalidate it against the current saved environment, instead of forcing a fresh Detect every time.
///
/// It carries only safe, public discovery evidence — the same values the read-only detection view already shows: reachability,
/// framework, suggested environment, endpoint and integration proposals, and detected authentication metadata (type/authority/tenant/
/// client id/redirect URLs). It NEVER carries a bearer token, cookie, Authorization header, proxy session, CDP session or any runtime
/// credential; detection is unauthenticated public discovery of the target.
///
/// Validity is bound to the target identity fingerprint (profile id + normalized Frontend URL, see <see cref="TargetDiscoveryFingerprint"/>)
/// captured at detection time, plus a timestamp for the optional freshness limit.
/// </summary>
public sealed class DetectionSnapshot
{
    /// <summary>The full public detection result. No secret fields.</summary>
    [JsonPropertyName("result")] public TargetEnvironmentDetectionResult Result { get; set; } = new();

    /// <summary>Profile id the detection was gathered for (identity component of the fingerprint).</summary>
    [JsonPropertyName("detectedProfileId")] public string? DetectedProfileId { get; set; }

    /// <summary>Normalized Frontend URL the detection was gathered for (identity component of the fingerprint).</summary>
    [JsonPropertyName("detectedUrl")] public string? DetectedUrl { get; set; }

    /// <summary>When the detection was captured (UTC), for the freshness limit.</summary>
    [JsonPropertyName("detectedAt")] public DateTimeOffset DetectedAt { get; set; }

    /// <summary>Human-readable source of the snapshot, e.g. "Detect Settings".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "Detect Settings";
}
