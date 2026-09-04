using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Infers target-environment semantics from a target hostname. This is the
/// shared frontend authority for both detection suggestions and profile
/// validation; inferred values are advisory and are never persisted directly.
/// </summary>
public static class TargetEnvironmentTypeClassifier
{
    public static FrontendEnvironmentType? InferFromUrl(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) ||
            !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return null;

        return InferFromHostname(uri.Host);
    }

    public static FrontendEnvironmentType? InferFromHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;

        var lower = hostname.ToLowerInvariant();
        if (lower.Contains("prod") || lower.Contains("production"))
            return FrontendEnvironmentType.Production;
        if (lower.Contains("dev") || lower.Contains("development"))
            return FrontendEnvironmentType.Development;
        if (lower.Contains("qa") || lower.Contains("test"))
            return FrontendEnvironmentType.QA;
        if (lower.Contains("rc") || lower.Contains("staging"))
            return FrontendEnvironmentType.RC;
        if (IsRecognizedLocalHost(lower))
            return FrontendEnvironmentType.Local;

        return null;
    }

    public static bool IsRecognizedLocalUrl(string? targetUrl) =>
        !string.IsNullOrWhiteSpace(targetUrl) &&
        Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
        IsRecognizedLocalHost(uri.Host);

    private static bool IsRecognizedLocalHost(string hostname)
    {
        var normalized = hostname.Trim('[', ']').ToLowerInvariant();
        return normalized == "localhost" || normalized == "::1" ||
               normalized == "127.0.0.1" || normalized.StartsWith("127.") ||
               normalized.EndsWith(".localhost");
    }
}
