using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ITargetPreflightService
{
    Task<TargetPreflightResult> CheckTargetAsync(string targetUrl);
}

public sealed class TargetPreflightResult
{
    public PreflightStatus Status { get; init; }
    public string Message { get; init; } = "";
    public bool IsBlazorWasm { get; init; }
    public bool RedirectOccurred { get; init; }
    public string? FinalUrl { get; init; }
    public int? ResponseStatusCode { get; init; }
    public bool IsLikelyLoginPage { get; init; }
    /// <summary>Precise reachability classification from the server-side probe, when one was executed.</summary>
    public TargetReachability? Reachability { get; init; }
    /// <summary>Probe duration in milliseconds, when a probe was executed.</summary>
    public double? ElapsedMs { get; init; }
}

/// <summary>Wire shape of the backend <c>api/frontend-target/reachability</c> response. Non-secret.</summary>
public sealed class TargetReachabilityProbeDto
{
    [JsonPropertyName("targetUrl")] public string TargetUrl { get; set; } = "";
    [JsonPropertyName("reachability")] public TargetReachability Reachability { get; set; } = TargetReachability.Unknown;
    [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }
    [JsonPropertyName("finalUrl")] public string? FinalUrl { get; set; }
    [JsonPropertyName("authenticationRequired")] public bool AuthenticationRequired { get; set; }
    [JsonPropertyName("redirectCount")] public int RedirectCount { get; set; }
    [JsonPropertyName("elapsedMs")] public double ElapsedMs { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
}

/// <summary>
/// Target reachability preflight for the Frontend Quality Review. The probe is executed by the BirkNext backend
/// (<c>api/frontend-target/reachability</c>) on the same network path the Static Security and Passive Performance engines use.
/// It is deliberately NOT a browser-side fetch: a Blazor WebAssembly request to a cross-origin target is subject to CORS policy and
/// in-browser protection, which turned reachable targets into "Network error" / generic timeout results.
/// The timeout wording is used only when the backend probe actually timed out.
/// </summary>
public sealed class TargetPreflightService : ITargetPreflightService
{
    public const string TimeoutMessage = "Target did not respond within timeout period.";
    public const string BackendUnavailableMessage = "Reachability check unavailable: the BirkNext backend could not be reached.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public TargetPreflightService(HttpClient http)
        => _http = http;

    public async Task<TargetPreflightResult> CheckTargetAsync(string targetUrl)
    {
        // Validate URL syntax first (no network)
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return new TargetPreflightResult
            {
                Status = PreflightStatus.InvalidTarget,
                Message = $"Target URL '{targetUrl}' is not a valid absolute URL.",
            };
        }

        TargetReachabilityProbeDto? probe;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var response = await _http.PostAsJsonAsync("api/frontend-target/reachability", new { targetUrl = uri.AbsoluteUri }, JsonOptions, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new TargetPreflightResult
                {
                    Status = PreflightStatus.ScannerUnavailable,
                    Message = $"Reachability check unavailable: the BirkNext backend returned HTTP {(int)response.StatusCode}.",
                };
            }
            probe = await response.Content.ReadFromJsonAsync<TargetReachabilityProbeDto>(JsonOptions, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TargetPreflightResult { Status = PreflightStatus.ScannerUnavailable, Message = BackendUnavailableMessage };
        }

        if (probe is null)
            return new TargetPreflightResult { Status = PreflightStatus.ScannerUnavailable, Message = BackendUnavailableMessage };

        return Map(probe, uri.AbsoluteUri);
    }

    /// <summary>Deterministic mapping of the probe to the review preflight status. Pure; unit-tested.</summary>
    public static TargetPreflightResult Map(TargetReachabilityProbeDto probe, string requestedUrl)
    {
        var redirected = probe.RedirectCount > 0 || (!string.IsNullOrWhiteSpace(probe.FinalUrl) &&
            !string.Equals(probe.FinalUrl.TrimEnd('/'), requestedUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var status = probe.Reachability switch
        {
            TargetReachability.Timeout => PreflightStatus.TimedOut,
            TargetReachability.AuthenticationRequired => PreflightStatus.AuthenticationRequired,
            TargetReachability.Reachable when probe.StatusCode is >= 500 => PreflightStatus.ReadyWithWarnings,
            TargetReachability.Reachable => PreflightStatus.Ready,
            TargetReachability.Unknown when probe.BlockReason == "INVALID_URL" => PreflightStatus.InvalidTarget,
            TargetReachability.Unknown => PreflightStatus.ScannerUnavailable,
            _ => PreflightStatus.Unreachable,
        };
        var message = status == PreflightStatus.TimedOut ? TimeoutMessage
            : string.IsNullOrWhiteSpace(probe.Message) ? DefaultMessage(status, probe) : probe.Message;
        return new TargetPreflightResult
        {
            Status = status,
            Message = message,
            FinalUrl = probe.FinalUrl,
            RedirectOccurred = redirected,
            ResponseStatusCode = probe.StatusCode,
            IsLikelyLoginPage = probe.AuthenticationRequired && probe.StatusCode is not (401 or 403),
            Reachability = probe.Reachability,
            ElapsedMs = probe.ElapsedMs,
        };
    }

    private static string DefaultMessage(PreflightStatus status, TargetReachabilityProbeDto probe) => status switch
    {
        PreflightStatus.Ready => "Target is reachable and ready for analysis.",
        PreflightStatus.ReadyWithWarnings => $"Target returned HTTP {probe.StatusCode}. Server error detected.",
        PreflightStatus.AuthenticationRequired => "The frontend host requires authentication.",
        PreflightStatus.InvalidTarget => "Target URL is not a valid absolute URL.",
        PreflightStatus.ScannerUnavailable => BackendUnavailableMessage,
        _ => probe.StatusCode.HasValue ? $"Target returned HTTP {probe.StatusCode}." : "Target unreachable.",
    };
}
