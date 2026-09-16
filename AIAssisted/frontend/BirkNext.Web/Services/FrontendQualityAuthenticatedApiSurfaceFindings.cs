using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Turns the sanitized authenticated API-surface probes (executed by the backend gateway with the Local HTTPS proxy credential)
/// into review findings and limitations. Static Security owns the API security-header / CORS findings; Passive Performance owns
/// the authenticated latency findings. Rejected credentials (401/403) are reported as findings, never hidden. Nothing here touches a token.
/// </summary>
public static class FrontendQualityAuthenticatedApiSurfaceFindings
{
    public const string SourceSystem = "Authenticated API (Local HTTPS Proxy)";

    public static IReadOnlyList<FrontendQualityFinding> Build(FrontendAuthenticatedApiSurfaceResult surface, FrontendPerformanceThresholds thresholds, bool securityActive, bool performanceActive)
    {
        var findings = new List<FrontendQualityFinding>();
        foreach (var check in surface.Checks.Where(c => c.Executed))
        {
            if (securityActive)
            {
                if (check.AuthenticationRejected)
                    findings.Add(Finding($"auth-api-rejected-{Slug(check.Label)}", $"Authenticated API request rejected: {check.Label}", FrontendQualitySeverity.High, FrontendQualityCategory.Security,
                        $"The in-memory credential captured by the Local HTTPS proxy was rejected by {check.Url} with HTTP {check.StatusCode}. The credential may not be valid for this API audience or the endpoint requires a different scope.",
                        "Verify the API audience/scope the application requests and that the configured endpoint belongs to the same API as the intercepted traffic.",
                        [$"URL: {check.Url}", $"HTTP {check.StatusCode}", $"Latency: {check.ElapsedMs} ms"], FrontendQualityEngineId.StaticSecurity, "auth-api-rejected"));
                if (!check.AuthenticationRejected)
                    findings.AddRange(SecurityHeaderFindings(check));
            }
            if (performanceActive && !check.AuthenticationRejected && check.ElapsedMs is { } latency && latency > thresholds.MaxSingleRequestLatencyMs)
                findings.Add(Finding($"auth-api-latency-{Slug(check.Label)}", $"Authenticated API latency above threshold: {check.Label}", FrontendQualitySeverity.Medium, FrontendQualityCategory.Performance,
                    $"{check.Url} answered in {latency:0} ms with the authenticated context; the configured single-request threshold is {thresholds.MaxSingleRequestLatencyMs} ms.",
                    "Profile the authenticated endpoint (authorization middleware, data access, payload size) and compare with the anonymous response time.",
                    [$"URL: {check.Url}", $"Latency: {latency:0} ms", $"Threshold: {thresholds.MaxSingleRequestLatencyMs} ms", $"HTTP {check.StatusCode}"], FrontendQualityEngineId.PassivePerformance, "auth-api-latency"));
        }
        return findings;
    }

    /// <summary>Non-secret provenance lines for the report's limitations list.</summary>
    public static IReadOnlyList<string> Limitations(FrontendAuthenticatedApiSurfaceResult surface) =>
        surface.ContextAvailable && surface.ExecutedCount > 0
            ? [$"Authenticated API surface checked through the Local HTTPS proxy gateway: {surface.ExecutedCount} approved read-only request(s) ({string.Join(", ", surface.Checks.Where(c => c.Executed).Select(c => $"{c.Label} HTTP {c.StatusCode}"))}). Response bodies are never captured."]
            : [$"Authenticated API surface not checked: {surface.NotExecutedReason}"];

    private static IEnumerable<FrontendQualityFinding> SecurityHeaderFindings(FrontendAuthenticatedApiCheck check)
    {
        var headers = check.SecurityHeaders;
        var url = check.Url;
        if (!headers.ContainsKey("strict-transport-security"))
            yield return Finding($"auth-api-hsts-{Slug(check.Label)}", $"API response without Strict-Transport-Security: {check.Label}", FrontendQualitySeverity.Medium, FrontendQualityCategory.Security,
                $"The authenticated response from {url} carries no Strict-Transport-Security header.", "Send Strict-Transport-Security on the API host (for example max-age=31536000; includeSubDomains).",
                [$"URL: {url}", "Header absent: strict-transport-security"], FrontendQualityEngineId.StaticSecurity, "auth-api-hsts");
        if (!headers.ContainsKey("x-content-type-options"))
            yield return Finding($"auth-api-xcto-{Slug(check.Label)}", $"API response without X-Content-Type-Options: {check.Label}", FrontendQualitySeverity.Low, FrontendQualityCategory.Security,
                $"The authenticated response from {url} carries no X-Content-Type-Options header.", "Send X-Content-Type-Options: nosniff on API responses.",
                [$"URL: {url}", "Header absent: x-content-type-options"], FrontendQualityEngineId.StaticSecurity, "auth-api-xcto");
        if (headers.TryGetValue("access-control-allow-origin", out var origin) && origin.Trim() == "*" &&
            headers.TryGetValue("access-control-allow-credentials", out var credentials) && credentials.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            yield return Finding($"auth-api-cors-{Slug(check.Label)}", $"API allows any origin with credentials: {check.Label}", FrontendQualitySeverity.High, FrontendQualityCategory.Security,
                $"{url} returns Access-Control-Allow-Origin: * together with Access-Control-Allow-Credentials: true on an authenticated response.",
                "Restrict Access-Control-Allow-Origin to the frontend origins and never combine a wildcard with credentials.",
                [$"URL: {url}", "access-control-allow-origin: *", "access-control-allow-credentials: true"], FrontendQualityEngineId.StaticSecurity, "auth-api-cors");
        if (headers.TryGetValue("cache-control", out var cache) && !cache.Contains("no-store", StringComparison.OrdinalIgnoreCase) && !cache.Contains("private", StringComparison.OrdinalIgnoreCase)
            && check.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            yield return Finding($"auth-api-cache-{Slug(check.Label)}", $"Authenticated API response is cacheable: {check.Label}", FrontendQualitySeverity.Low, FrontendQualityCategory.Security,
                $"{url} returns Cache-Control \"{cache}\" for an authenticated JSON response, so shared caches may store user-specific data.",
                "Send Cache-Control: no-store (or private) on authenticated API responses.",
                [$"URL: {url}", $"cache-control: {cache}"], FrontendQualityEngineId.StaticSecurity, "auth-api-cache");
    }

    private static FrontendQualityFinding Finding(string id, string title, FrontendQualitySeverity severity, FrontendQualityCategory category, string description, string recommendation, List<string> evidence, FrontendQualityEngineId engine, string rule) => new()
    {
        Id = id, Title = title, Severity = severity, Category = category, Description = description, Recommendation = recommendation,
        Evidence = evidence, SourceSystem = SourceSystem, EngineId = engine, SourceRuleId = rule, Status = CheckExecutionStatus.Failed,
    };

    private static string Slug(string label) => new(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
