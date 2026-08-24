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
}

public sealed class TargetPreflightService : ITargetPreflightService
{
    private readonly HttpClient _http;

    public TargetPreflightService(HttpClient http)
        => _http = http;

    public async Task<TargetPreflightResult> CheckTargetAsync(string targetUrl)
    {
        // Validate URL syntax first
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
        {
            return new TargetPreflightResult
            {
                Status = PreflightStatus.InvalidTarget,
                Message = $"Target URL '{targetUrl}' is not a valid absolute URL.",
            };
        }

        try
        {
            // Make a HEAD request first to avoid downloading full content
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            request.Headers.Add("User-Agent", "BirkNext/1.0");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
            var redirected = finalUrl != targetUrl;

            // Check for redirect loops or auth redirects
            if (redirected && IsLikelyAuthRedirect(finalUrl))
            {
                return new TargetPreflightResult
                {
                    Status = PreflightStatus.AuthenticationRequired,
                    Message = "Target appears to require authentication (redirect detected).",
                    FinalUrl = finalUrl,
                    RedirectOccurred = true,
                    ResponseStatusCode = (int)response.StatusCode,
                };
            }

            // Check status code
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    return new TargetPreflightResult
                    {
                        Status = PreflightStatus.Unreachable,
                        Message = $"Target returned HTTP {response.StatusCode}. Frontend may be inaccessible.",
                        FinalUrl = finalUrl,
                        RedirectOccurred = redirected,
                        ResponseStatusCode = (int)response.StatusCode,
                    };
                }

                if ((int)response.StatusCode >= 500)
                {
                    return new TargetPreflightResult
                    {
                        Status = PreflightStatus.ReadyWithWarnings,
                        Message = $"Target returned HTTP {response.StatusCode}. Server error detected.",
                        FinalUrl = finalUrl,
                        RedirectOccurred = redirected,
                        ResponseStatusCode = (int)response.StatusCode,
                    };
                }
            }

            // Check for Blazor WASM
            var isBlazorWasm = response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true ||
                              (response.Headers.TryGetValues("content-type", out var ct) &&
                               ct.Any(c => c.Contains("text/html")));

            return new TargetPreflightResult
            {
                Status = redirected && IsLikelyLoginPage(finalUrl)
                    ? PreflightStatus.AuthenticationRequired
                    : PreflightStatus.Ready,
                Message = "Target is reachable and ready for analysis.",
                FinalUrl = finalUrl,
                RedirectOccurred = redirected,
                ResponseStatusCode = (int)response.StatusCode,
                IsBlazorWasm = isBlazorWasm,
                IsLikelyLoginPage = redirected && IsLikelyLoginPage(finalUrl),
            };
        }
        catch (HttpRequestException ex)
        {
            return new TargetPreflightResult
            {
                Status = PreflightStatus.Unreachable,
                Message = $"Network error: {ex.Message}",
            };
        }
        catch (TaskCanceledException)
        {
            return new TargetPreflightResult
            {
                Status = PreflightStatus.Unreachable,
                Message = "Target did not respond within timeout period.",
            };
        }
        catch (Exception ex)
        {
            return new TargetPreflightResult
            {
                Status = PreflightStatus.ScannerUnavailable,
                Message = $"Preflight check error: {ex.Message}",
            };
        }
    }

    private static bool IsLikelyAuthRedirect(string? finalUrl) =>
        !string.IsNullOrWhiteSpace(finalUrl) && (
            finalUrl.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            finalUrl.Contains("signin", StringComparison.OrdinalIgnoreCase) ||
            finalUrl.Contains("authorize", StringComparison.OrdinalIgnoreCase) ||
            finalUrl.Contains("oauth", StringComparison.OrdinalIgnoreCase) ||
            finalUrl.Contains("auth", StringComparison.OrdinalIgnoreCase));

    private static bool IsLikelyLoginPage(string? finalUrl) =>
        !string.IsNullOrWhiteSpace(finalUrl) && (
            finalUrl.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            finalUrl.Contains("signin", StringComparison.OrdinalIgnoreCase));
}
