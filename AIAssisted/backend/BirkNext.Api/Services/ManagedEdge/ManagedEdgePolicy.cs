using HotChocolate.Language;

namespace BirkNext.Api.Services.ManagedEdge;

public sealed class ManagedEdgeOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:9222";
    /// <summary>Dedicated BirkNext profile directory for launched test Edge instances. Defaults to %LOCALAPPDATA%\BirkNext\ManagedEdgeProfile.</summary>
    public string? ProfileDirectory { get; set; }
    public int LaunchTimeoutSeconds { get; set; } = 20;
    public List<ManagedEdgeTargetRule> Targets { get; set; } = [];
}

/// <summary>Administrator-reviewed contracts, never inferred from a title or arbitrary HTTP 200.</summary>
public sealed class ManagedEdgeTargetRule
{
    public string Origin { get; set; } = "";
    public string? AuthenticatedOnlySelector { get; set; }
    public string? ProtectedGetPath { get; set; }
    public string ProtectedContentType { get; set; } = "application/json";
    public List<string> SafeGetPaths { get; set; } = [];
    public string? GraphQlPath { get; set; }
    public List<string> AllowedGraphQlQueries { get; set; } = [];
}

public static class ManagedEdgePolicy
{
    public static bool IsLoopbackHost(string host) => host.Trim('[', ']').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";

    public static Uri Endpoint(string value, bool websocket = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host) ||
            uri.Scheme != (websocket ? "ws" : "http") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (websocket ? !uri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal) : uri.AbsolutePath != "/"))
            throw new ArgumentException("Only a local loopback CDP endpoint is allowed.");
        // Avoid DNS resolution even for the allowed localhost alias.
        return new UriBuilder(uri) { Host = uri.Host == "localhost" ? "127.0.0.1" : uri.Host }.Uri;
    }

    public static string Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new ArgumentException("A valid HTTP(S) target is required.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    public static bool MatchesOrigin(string pageUrl, string origin)
    {
        try { return Origin(pageUrl) == Origin(origin); }
        catch (ArgumentException) { return false; }
    }

    private const string McasSuffix = ".access.mcas.ms";

    /// <summary>
    /// Application-identity correlation for Defender for Cloud Apps reverse-proxy delivery: HTTPS, host under access.mcas.ms,
    /// and the host prefix is the configured target host with dots replaced by hyphens (optionally followed by "-suffix").
    /// Mirrors <see cref="AuthenticatedReview.AuthenticationOriginPolicy.IsTargetCorrelatedMcas"/>. An arbitrary *.access.mcas.ms host never matches.
    /// </summary>
    public static bool IsCorrelatedMcasProxyOrigin(string candidateUrl, string targetOrigin)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidate) || !Uri.TryCreate(targetOrigin, UriKind.Absolute, out var target)) return false;
        if (candidate.Scheme != Uri.UriSchemeHttps || candidate.UserInfo.Length != 0 || !candidate.IsDefaultPort) return false;
        if (!candidate.IdnHost.EndsWith(McasSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = candidate.IdnHost[..^McasSuffix.Length].TrimEnd('.');
        var encodedTarget = target.IdnHost.Replace('.', '-');
        if (prefix.Length == 0 || encodedTarget.Length == 0 || encodedTarget == candidate.IdnHost) return false;
        return prefix.Equals(encodedTarget, StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith(encodedTarget + "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Any HTTPS access.mcas.ms origin (for example the tenant "aad_login" intermediary) that is not the delivery origin itself.</summary>
    public static bool IsMcasIntermediaryOrigin(string url, string deliveryOrigin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.IdnHost.EndsWith(McasSuffix, StringComparison.OrdinalIgnoreCase) && !MatchesOrigin(url, deliveryOrigin);
    }

    public static bool IsEntraAuthorityOrigin(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.IdnHost, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);

    public static Uri SafePath(string origin, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('\\') || path.Contains('%') || path.Contains('?') || path.Contains('#') || path.Split('/').Contains("..") ||
            !Uri.TryCreate(new Uri(origin), path, out var uri) || Origin(uri.AbsoluteUri) != Origin(origin))
            throw new ArgumentException("Only an approved same-origin path without query data is allowed.");
        return uri;
    }

    public static void QueryOnly(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 8192) throw new ArgumentException("A bounded query is required.");
        try
        {
            var document = Utf8GraphQLParser.Parse(query);
            var operations = document.Definitions.OfType<OperationDefinitionNode>().ToArray();
            if (operations.Length != 1 || operations[0].Operation != OperationType.Query || operations[0].VariableDefinitions.Count != 0)
                throw new ArgumentException("Only one approved GraphQL query without variables is supported.");
        }
        catch (SyntaxException) { throw new ArgumentException("Invalid GraphQL query."); }
    }
}
