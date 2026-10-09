using System.Text;
using System.Text.RegularExpressions;
using BirkNext.ApiReview;
using HotChocolate.Language;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

/// <summary>
/// One parameter-capable read-only request: the destination URL (origin + path + encoded query), approved extra headers and, for GraphQL,
/// the query document. Built only from a contract-derived fuzz case; re-validated by <see cref="ApiSafeRequestGuard"/> on the public path and
/// again inside the authenticated execution service before the credential is applied.
/// </summary>
public sealed record ApiSafeRequest(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string? GraphQlQuery = null, bool AllowGraphQlSyntaxError = false,
    string? Body = null, string? ContentType = null, BodyFuzzApproval? BodyApproval = null);

/// <summary>
/// Server-built approval for one body case: the target was resolved as trusted by the server registry and the operation is explicitly
/// opted in as read-only body safe. Never deserialized from a client; the guard and the authenticated execution service both require it.
/// </summary>
public sealed record BodyFuzzApproval(bool TrustedTarget, BirkNext.RuntimeSecurity.BodyFuzzingPolicy Policy, string Operation);

/// <summary>
/// Backend safety rules for every safe-fuzzing request. Nothing is sent unless all hold: the environment decision allows active testing;
/// the destination origin is exactly the reviewed target's origin (no arbitrary URL, no redirect target, no user info); REST methods are
/// GET/HEAD/OPTIONS and GraphQL is one POSTed query without variables (a deliberately malformed document is allowed only for the
/// malformed-syntax case and never contains an operation keyword other than query); headers are declared non-sensitive names (never
/// Authorization, cookies, proxy or gateway infrastructure headers); URL, parameter and payload sizes stay within the clamped limits.
/// </summary>
public static partial class ApiSafeRequestGuard
{
    private static readonly HashSet<string> SafeRestMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "host", "origin", "referer", "content-length", "transfer-encoding", "connection",
        "te", "upgrade", "expect", "keep-alive", "forwarded", "via", "x-api-key", "api-key", "x-auth-token", "x-csrf-token", "x-xsrf-token",
        "x-real-ip", "x-client-ip", "true-client-ip", "x-http-method-override", "x-method-override", "x-http-method",
    };

    private static readonly string[] ForbiddenHeaderPrefixes = ["x-forwarded-", "x-original-", "x-ms-", "x-azure-", "x-arr-", "ocp-apim-", "x-envoy-", "x-amz-", "proxy-", "sec-", "cf-"];

    /// <summary>Headers a fuzz case may never set: credentials, cookies, routing and gateway infrastructure.</summary>
    public static bool IsForbiddenHeader(string name) =>
        ForbiddenHeaders.Contains(name.Trim()) || ForbiddenHeaderPrefixes.Any(p => name.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the request may be sent; otherwise why it is blocked (SafetyBlocked).</summary>
    public static string? Validate(ApiSafeRequest request, string boundOrigin, ApiFuzzingSettings settings, ApiEnvironmentSafetyDecision safety)
    {
        if (!safety.ActiveTestingAllowed) return $"Environment safety: {safety.Reason}";
        if (request.Url.Length > ApiFuzzingLimits.MaxUrlLength) return $"The URL exceeds {ApiFuzzingLimits.MaxUrlLength} characters.";
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return "Only an absolute http(s) URL without user info or fragment is allowed.";
        if (!Uri.TryCreate(boundOrigin, UriKind.Absolute, out var bound) || !string.Equals(uri.GetLeftPart(UriPartial.Authority), bound.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return "The destination is not the reviewed target's origin; arbitrary destinations are never fuzzed.";
        if (TraversalPattern().IsMatch(uri.OriginalString)) return "Path traversal sequences are not allowed.";
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (Uri.UnescapeDataString(part.Replace('+', ' ')).Length > settings.MaxParameterLength + 64)
                return $"A query parameter exceeds the parameter limit ({settings.MaxParameterLength} characters).";

        if (request.Body is not null || request.BodyApproval is not null)
        {
            if (BodyRejection(request, settings.MaxBodyBytes) is { } bodyRejection) return bodyRejection;
        }
        else if (request.GraphQlQuery is null)
        {
            if (!SafeRestMethods.Contains(request.Method)) return $"{request.Method} is not a read-only method; only GET, HEAD and OPTIONS are fuzzed.";
        }
        else
        {
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return "GraphQL queries are sent as POST only.";
            if (GraphQlQueryRejection(request.GraphQlQuery, request.AllowGraphQlSyntaxError, settings.MaxPayloadBytes) is { } rejection) return rejection;
        }

        foreach (var (name, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(name) || !HeaderNamePattern().IsMatch(name)) return "Invalid header name.";
            if (IsForbiddenHeader(name)) return $"The header {name} is never set by safe fuzzing (credentials, cookies and infrastructure headers are excluded).";
            if (value.Length > settings.MaxParameterLength || value.Contains('\r') || value.Contains('\n')) return $"The value of header {name} exceeds the limit or contains a line break.";
        }
        return null;
    }

    private static readonly HashSet<string> BodyMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };
    private static readonly HashSet<string> BodyContentTypes = new(StringComparer.OrdinalIgnoreCase) { "application/json", "text/plain" };

    /// <summary>
    /// Body-case rule: a server-built approval for a trusted target and a read-only-body-safe opt-in, POST/PUT/PATCH only (never DELETE,
    /// never GraphQL), a body within the byte limit and a JSON or text/plain Content-Type (or none, for the missing-content-type case).
    /// State-changing operations are never executed by safe fuzzing in this milestone.
    /// </summary>
    public static string? BodyRejection(ApiSafeRequest request, int maxBodyBytes)
    {
        if (request.BodyApproval is not { } approval) return "A request body needs a server-built body-fuzzing approval; unknown operation safety is blocked.";
        if (!approval.TrustedTarget) return "Body fuzzing runs only against a server-registered trusted target.";
        if (approval.Policy != BirkNext.RuntimeSecurity.BodyFuzzingPolicy.ReadOnlyBodySafe)
            return approval.Policy == BirkNext.RuntimeSecurity.BodyFuzzingPolicy.StateChangingWithCleanup
                ? "State-changing body fuzzing is blocked: no executable cleanup contract in this milestone."
                : "The operation is not opted in for body fuzzing.";
        if (request.GraphQlQuery is not null) return "GraphQL mutations and GraphQL body fuzzing are never sent.";
        if (!BodyMethods.Contains(request.Method)) return $"{request.Method} cannot carry a fuzzed body; only explicitly opted-in POST, PUT and PATCH operations.";
        if (request.Body is null) return "The body case has no body within the limits.";
        if (Encoding.UTF8.GetByteCount(request.Body) > maxBodyBytes) return $"The request body exceeds {maxBodyBytes} bytes.";
        if (request.ContentType is not null && !BodyContentTypes.Contains(request.ContentType)) return "Only application/json or text/plain bodies are sent.";
        return null;
    }

    /// <summary>
    /// GraphQL query-only rule. A parseable document must be exactly one QUERY without variables; a mutation or subscription is always
    /// rejected. An unparseable document is accepted only when the case is the malformed-syntax case and no operation keyword other than
    /// "query" appears in it (a syntax error is rejected by the server before any execution).
    /// </summary>
    public static string? GraphQlQueryRejection(string query, bool allowSyntaxError, int maxPayloadBytes)
    {
        if (string.IsNullOrWhiteSpace(query)) return "An empty GraphQL document is not sent.";
        if (Encoding.UTF8.GetByteCount(query) > maxPayloadBytes) return $"The GraphQL document exceeds {maxPayloadBytes} bytes.";
        if (MutationKeyword().IsMatch(query)) return "GraphQL mutations and subscriptions are never sent.";
        try
        {
            var document = Utf8GraphQLParser.Parse(query);
            var operations = document.Definitions.OfType<OperationDefinitionNode>().ToArray();
            if (operations.Length != 1 || operations[0].Operation != OperationType.Query) return "Exactly one GraphQL query operation is allowed.";
            if (operations[0].VariableDefinitions.Count != 0) return "GraphQL variables are not used by safe fuzzing (inline literals only).";
            return null;
        }
        catch (SyntaxException)
        {
            return allowSyntaxError ? null : "The GraphQL document does not parse.";
        }
    }

    [GeneratedRegex(@"\.\.[/\\]|%2e%2e|%2f|%5c|\\", RegexOptions.IgnoreCase)]
    private static partial Regex TraversalPattern();

    [GeneratedRegex(@"\b(mutation|subscription)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationKeyword();

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex HeaderNamePattern();
}
