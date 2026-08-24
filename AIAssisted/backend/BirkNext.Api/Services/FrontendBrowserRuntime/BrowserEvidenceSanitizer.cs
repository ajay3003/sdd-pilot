using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>
/// Sanitizes browser evidence before storage/export.
/// Removes credentials, tokens, API keys, and other secrets from URLs, error messages, and console output.
/// </summary>
public sealed class BrowserEvidenceSanitizer
{
    private static readonly string[] SensitiveQueryParams = new[]
    {
        "token", "access_token", "id_token", "refresh_token",
        "api_key", "key", "apikey", "secret",
        "password", "passwd", "pwd",
        "bearer", "authorization",
        "auth", "session", "sessionid", "sid"
    };

    private static readonly string[] SensitivePatterns = new[]
    {
        @"Bearer\s+[A-Za-z0-9\-._~+/]+=*", // Bearer tokens
        @"[A-Za-z0-9]{32,}", // Generic 32+ char tokens
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", // JWT
    };

    public string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            var sanitizedQuery = SanitizeQueryString(uri.Query);
            var sanitizedUrl = url.Replace(uri.Query, sanitizedQuery);

            // Remove userinfo (credentials in URL)
            if (!string.IsNullOrEmpty(uri.UserInfo))
                sanitizedUrl = sanitizedUrl.Replace($"{uri.UserInfo}@", "[REDACTED]@");

            return sanitizedUrl;
        }
        catch
        {
            return url;
        }
    }

    public string SanitizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var sanitized = message;

        // Sanitize patterns
        foreach (var pattern in SensitivePatterns)
        {
            try
            {
                sanitized = Regex.Replace(sanitized, pattern, "[REDACTED]", RegexOptions.IgnoreCase);
            }
            catch
            {
                // Invalid regex, skip
            }
        }

        // Sanitize query-param-like values
        foreach (var param in SensitiveQueryParams)
        {
            var pattern = $@"(?i){param}=([^\s&""']+)";
            try
            {
                sanitized = Regex.Replace(sanitized, pattern, $"{param}=[REDACTED]", RegexOptions.IgnoreCase);
            }
            catch
            {
                // Invalid regex, skip
            }
        }

        return sanitized;
    }

    public List<BrowserConsoleEvent> SanitizeConsoleEvents(List<BrowserConsoleEvent> events)
    {
        if (events == null || events.Count == 0)
            return events;

        return events.Select(e => new BrowserConsoleEvent(
            e.Type,
            SanitizeMessage(e.Message),
            e.Location != null ? SanitizeUrl(e.Location) : null,
            e.LineNumber,
            e.ColumnNumber
        )).ToList();
    }

    public List<BrowserResourceFailure> SanitizeResourceFailures(List<BrowserResourceFailure> failures)
    {
        if (failures == null || failures.Count == 0)
            return failures;

        return failures.Select(f => new BrowserResourceFailure(
            SanitizeUrl(f.Url),
            f.ResourceType,
            SanitizeMessage(f.FailureReason),
            f.StatusCode
        )).ToList();
    }

    public List<BrowserPageError> SanitizePageErrors(List<BrowserPageError> errors)
    {
        if (errors == null || errors.Count == 0)
            return errors;

        return errors.Select(e => new BrowserPageError(
            SanitizeMessage(e.Message),
            e.Location != null ? SanitizeUrl(e.Location) : null,
            e.Stack != null ? SanitizeMessage(e.Stack) : null
        )).ToList();
    }

    public List<BrowserRuntimeFinding> SanitizeFindings(List<BrowserRuntimeFinding> findings)
    {
        if (findings == null || findings.Count == 0)
            return findings;

        return findings.Select(f => new BrowserRuntimeFinding(
            f.Id,
            f.Title,
            f.Severity,
            f.Category,
            SanitizeMessage(f.Description),
            SanitizeMessage(f.Recommendation),
            f.Evidence?.Select(SanitizeMessage).ToList() ?? new List<string>()
        )).ToList();
    }

    private static string SanitizeQueryString(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var parts = query.TrimStart('?').Split('&');
        var sanitized = new List<string>();

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            var key = keyValue[0].ToLowerInvariant();

            // Check if key matches sensitive pattern
            var isSensitive = SensitiveQueryParams.Any(p => key.Contains(p));

            if (isSensitive)
                sanitized.Add($"{key}=[REDACTED]");
            else
                sanitized.Add(part);
        }

        return "?" + string.Join("&", sanitized);
    }
}
