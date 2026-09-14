using System.Text.Json;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Non-secret metadata of one intercepted request/response pair, used to classify authenticated API endpoints from observed traffic.
/// Deliberately never carries the bearer token, Authorization value, cookies, request body or response body — only a bool that a Bearer
/// was present and, for a GraphQL candidate, the operation kind/name already parsed transiently by the server.
/// </summary>
internal sealed record ObservedRequestMetadata
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Method { get; init; }
    /// <summary>Request target as received (origin-form path, possibly with a query string, which is stripped during classification).</summary>
    public required string Target { get; init; }
    public string? RequestContentType { get; init; }
    public int ResponseStatus { get; init; }
    public string? ResponseContentType { get; init; }
    public bool BearerObserved { get; init; }
    public GraphQlOperationType GraphQlOperationType { get; init; }
    public string? GraphQlOperationName { get; init; }
}

/// <summary>
/// Parses the transient, bounded prefix of a request body to decide whether it is a GraphQL operation and, if so, its kind and name.
/// The body text is never stored or logged; only the derived <see cref="GraphQlOperationType"/> and optional name are kept. Fully guarded:
/// any malformed or non-GraphQL body yields <see cref="GraphQlOperationType.None"/> without throwing.
/// </summary>
internal static class GraphQlBodyInspector
{
    public static (GraphQlOperationType Type, string? Name) Classify(ReadOnlySpan<char> body)
    {
        var text = body.Trim();
        if (text.IsEmpty || text[0] != '{') return (GraphQlOperationType.None, null);
        string? query;
        string? operationName = null;
        try
        {
            using var document = JsonDocument.Parse(text.ToString());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (GraphQlOperationType.None, null);
            if (!document.RootElement.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
                return (GraphQlOperationType.None, null);
            query = queryElement.GetString();
            if (document.RootElement.TryGetProperty("operationName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                operationName = nameElement.GetString();
        }
        catch (JsonException) { return (GraphQlOperationType.None, null); }
        return ClassifyQuery(query, operationName);
    }

    /// <summary>Derives the operation kind from the GraphQL document text: the leading keyword (shorthand <c>{ ... }</c> is a query), and the operation name when present.</summary>
    private static (GraphQlOperationType, string?) ClassifyQuery(string? query, string? operationName)
    {
        if (string.IsNullOrWhiteSpace(query)) return (GraphQlOperationType.None, null);
        var i = 0;
        SkipInsignificant(query, ref i);
        if (i >= query.Length) return (GraphQlOperationType.None, null);
        // Shorthand anonymous query: the document begins with a selection set.
        if (query[i] == '{') return (GraphQlOperationType.Query, operationName);
        var keyword = ReadWord(query, ref i);
        var type = keyword switch
        {
            "query" => GraphQlOperationType.Query,
            "mutation" => GraphQlOperationType.Mutation,
            "subscription" => GraphQlOperationType.Subscription,
            _ => GraphQlOperationType.None
        };
        if (type == GraphQlOperationType.None) return (GraphQlOperationType.None, null);
        // A named operation follows the keyword: "query Foo { ... }". Prefer the explicit operationName field when it was supplied.
        SkipInsignificant(query, ref i);
        var name = operationName;
        if (name is null && i < query.Length && (char.IsLetter(query[i]) || query[i] == '_'))
        {
            var word = ReadWord(query, ref i);
            if (word.Length > 0) name = word;
        }
        return (type, name);
    }

    // Skips whitespace, commas and GraphQL "# ..." line comments.
    private static void SkipInsignificant(string text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (c == '#') { while (i < text.Length && text[i] != '\n') i++; continue; }
            break;
        }
    }

    private static string ReadWord(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        return text[start..i];
    }
}

/// <summary>
/// Classifies one observed authenticated request into an <see cref="ObservedAuthenticatedEndpoint"/> from safe metadata only. Enforces:
/// authenticated-only (a Bearer must have been observed), HTML SPA documents and static assets are never verified REST, GraphQL is
/// recognised from POST+JSON with a parsed operation (query distinguished from mutation/subscription), and verified REST requires a
/// read-only method with an API-compatible (JSON) response. Never assumes <c>/health</c> or <c>/graphql</c>.
/// </summary>
internal static class ObservedTrafficClassifier
{
    private static readonly string[] StaticAssetExtensions =
        [".js", ".mjs", ".css", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif",
         ".woff", ".woff2", ".ttf", ".eot", ".otf", ".mp4", ".webm", ".wasm", ".txt", ".xml"];

    private static readonly string[] SafeReadMethods = ["GET", "HEAD", "OPTIONS"];

    /// <summary>Returns the classified endpoint, or null when the request is not authenticated (no Bearer observed) and so is not recorded.</summary>
    public static ObservedAuthenticatedEndpoint? Classify(ObservedRequestMetadata metadata, DateTimeOffset observedAt)
    {
        if (!metadata.BearerObserved) return null;

        var origin = metadata.Port == 443 ? $"https://{metadata.Host}" : $"https://{metadata.Host}:{metadata.Port}";
        var path = NormalizePath(metadata.Target);
        var method = (metadata.Method ?? "").ToUpperInvariant();

        var baseEndpoint = new ObservedAuthenticatedEndpoint
        {
            Origin = origin,
            Path = path,
            Method = method,
            ResponseStatus = metadata.ResponseStatus,
            RequestContentType = Simplify(metadata.RequestContentType),
            ResponseContentType = Simplify(metadata.ResponseContentType),
            BearerObserved = true,
            Count = 1,
            LastObservedAt = observedAt
        };

        // GraphQL: recognised only from an actually parsed operation in the request body (POST + JSON), never from the path.
        if (metadata.GraphQlOperationType != GraphQlOperationType.None)
            return baseEndpoint with
            {
                EndpointType = ObservedEndpointType.GraphQl,
                OperationType = metadata.GraphQlOperationType,
                OperationName = metadata.GraphQlOperationName,
                // A parsed authenticated GraphQL operation with a received response is verified; an HTML response is not a clean GraphQL response.
                Confidence = IsHtml(metadata.ResponseContentType) ? ObservedEndpointConfidence.Candidate : ObservedEndpointConfidence.Verified
            };

        // REST classification.
        var confidence =
            IsStaticAsset(path) || IsHtml(metadata.ResponseContentType) ? ObservedEndpointConfidence.Rejected
            : SafeReadMethods.Contains(method) && IsJson(metadata.ResponseContentType) ? ObservedEndpointConfidence.Verified
            : ObservedEndpointConfidence.Candidate;

        return baseEndpoint with { EndpointType = ObservedEndpointType.Rest, Confidence = confidence, OperationType = GraphQlOperationType.None };
    }

    /// <summary>A request body is worth transiently inspecting for a GraphQL operation only when it is a bounded JSON POST.</summary>
    public static bool IsGraphQlBodyCandidate(string method, string? requestContentType) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && IsJson(requestContentType);

    private static string NormalizePath(string target)
    {
        if (string.IsNullOrEmpty(target)) return "/";
        var path = target;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];   // never keep a query string; it can carry secrets
        var fragment = path.IndexOf('#');
        if (fragment >= 0) path = path[..fragment];
        if (path.Length == 0) return "/";
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private static bool IsStaticAsset(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        var extension = path[dot..].ToLowerInvariant();
        return StaticAssetExtensions.Contains(extension);
    }

    private static bool IsHtml(string? contentType) => Simplify(contentType) is "text/html" or "application/xhtml+xml";

    private static bool IsJson(string? contentType)
    {
        var value = Simplify(contentType);
        if (value is null) return false;
        return value is "application/json" or "text/json"
            || value.EndsWith("+json", StringComparison.Ordinal)
            || value == "application/graphql-response+json";
    }

    /// <summary>Lower-cased media type without parameters (drops "; charset=..."). Never a credential.</summary>
    private static string? Simplify(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var value = contentType;
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }
}

/// <summary>Non-secret metadata of one intercepted exchange for page-oriented network discovery, including the safe page-correlation Referer.</summary>
internal sealed record NetworkRequestMetadata
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Method { get; init; }
    public required string Target { get; init; }
    public string? RequestContentType { get; init; }
    public int ResponseStatus { get; init; }
    public string? ResponseContentType { get; init; }
    public bool BearerObserved { get; init; }
    public bool IsWebSocket { get; init; }
    public GraphQlOperationType GraphQlOperationType { get; init; }
    public string? GraphQlOperationName { get; init; }
    /// <summary>The request Referer header, if any. Used only to derive the correlating page origin/path; the value is never persisted.</summary>
    public string? Referer { get; init; }
}

/// <summary>
/// Classifies one browser-observed exchange into an <see cref="ObservedNetworkEndpoint"/> for the page-oriented discovery view. Unlike
/// the authenticated-only <see cref="ObservedTrafficClassifier"/>, this records every approved-host exchange (authenticated or not) and
/// assigns a conservative category (REST, GraphQL, WebSocket, Authentication, Static, Telemetry, Other) plus a safe page correlation from
/// the Referer. Never assumes <c>/graphql</c>; never classifies an SPA HTML document as REST. Carries no credential, body or query string.
/// </summary>
internal static class NetworkTrafficClassifier
{
    private static readonly string[] StaticExtensions =
        [".js", ".mjs", ".css", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif",
         ".woff", ".woff2", ".ttf", ".eot", ".otf", ".mp4", ".webm", ".wasm"];
    private static readonly string[] TelemetryMarkers =
        ["/telemetry", "/analytics", "insights", "/collect", "/beacon", "/rum", "/track", "/appcenter", "app_insights"];
    private static readonly string[] AuthMarkers =
        ["/oauth", "/oauth2", "/authorize", "/connect/token", "/signin", "/sign-in", "/login", "/.well-known/openid", "/adfs/", "/msal"];

    public static ObservedNetworkEndpoint Classify(NetworkRequestMetadata metadata, DateTimeOffset observedAt)
    {
        var path = NormalizePath(metadata.Target);
        var method = (metadata.Method ?? "").ToUpperInvariant();
        var reqCt = Simplify(metadata.RequestContentType);
        var respCt = Simplify(metadata.ResponseContentType);
        var (category, confidence) = Categorize(metadata, path, method, reqCt, respCt);
        var (pageOrigin, pagePath) = CorrelatePage(metadata, category, method, respCt);

        return new ObservedNetworkEndpoint
        {
            Category = category,
            Scheme = metadata.IsWebSocket ? "wss" : "https",
            Host = metadata.Host,
            Port = metadata.Port,
            Path = path,
            Method = metadata.IsWebSocket ? "WS" : method,
            AuthObserved = metadata.BearerObserved,
            LastStatus = metadata.ResponseStatus,
            Source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
            Confidence = confidence,
            Count = 1,
            FirstObservedAt = observedAt,
            LastObservedAt = observedAt,
            OperationType = metadata.GraphQlOperationType,
            OperationName = metadata.GraphQlOperationName,
            PageOrigin = pageOrigin,
            PagePath = pagePath
        };
    }

    private static (ObservedTrafficCategory, ObservedEndpointConfidence) Categorize(NetworkRequestMetadata m, string path, string method, string? reqCt, string? respCt)
    {
        if (m.IsWebSocket) return (ObservedTrafficCategory.WebSocket, ObservedEndpointConfidence.Verified);
        if (m.GraphQlOperationType != GraphQlOperationType.None)
            return (ObservedTrafficCategory.GraphQl, IsHtml(respCt) ? ObservedEndpointConfidence.Candidate : ObservedEndpointConfidence.Verified);
        if (IsStaticAsset(path) || IsStaticContentType(respCt)) return (ObservedTrafficCategory.StaticAsset, ObservedEndpointConfidence.Verified);
        if (ContainsAny(path, TelemetryMarkers)) return (ObservedTrafficCategory.Telemetry, ObservedEndpointConfidence.Candidate);
        if (ContainsAny(path, AuthMarkers)) return (ObservedTrafficCategory.Authentication, ObservedEndpointConfidence.Candidate);
        if (IsHtml(respCt)) return (ObservedTrafficCategory.OtherHttp, ObservedEndpointConfidence.Candidate);   // SPA document, never REST
        if (IsJson(respCt))
            return (ObservedTrafficCategory.Rest, method is "GET" or "HEAD" or "OPTIONS" ? ObservedEndpointConfidence.Verified : ObservedEndpointConfidence.Candidate);
        if (IsJson(reqCt) || IsApiLikePath(path)) return (ObservedTrafficCategory.Rest, ObservedEndpointConfidence.Candidate);
        return (ObservedTrafficCategory.OtherHttp, ObservedEndpointConfidence.Candidate);
    }

    /// <summary>A GET returning an HTML document is itself a page; every other request is correlated to the page named by its Referer.</summary>
    private static (string?, string?) CorrelatePage(NetworkRequestMetadata m, ObservedTrafficCategory category, string method, string? respCt)
    {
        if (category is ObservedTrafficCategory.OtherHttp && method == "GET" && IsHtml(respCt))
        {
            var origin = m.Port is 443 or 80 ? $"https://{m.Host}" : $"https://{m.Host}:{m.Port}";
            return (origin, NormalizePath(m.Target));
        }
        if (Uri.TryCreate(m.Referer, UriKind.Absolute, out var referer) && referer.Scheme is "https" or "http")
        {
            var origin = referer.IsDefaultPort ? $"{referer.Scheme}://{referer.Host}" : $"{referer.Scheme}://{referer.Host}:{referer.Port}";
            return (origin, NormalizePath(referer.AbsolutePath));
        }
        return (null, null);   // cannot correlate → Shared / background traffic
    }

    private static bool ContainsAny(string path, string[] markers)
    {
        var lower = path.ToLowerInvariant();
        return markers.Any(lower.Contains);
    }

    private static bool IsApiLikePath(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("/api/") || lower.StartsWith("/api/") || lower.Contains("/v1/") || lower.Contains("/v2/") || lower.Contains("/odata");
    }

    private static string NormalizePath(string target)
    {
        if (string.IsNullOrEmpty(target)) return "/";
        var path = target;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        var fragment = path.IndexOf('#');
        if (fragment >= 0) path = path[..fragment];
        if (path.Length == 0) return "/";
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }

    private static bool IsStaticAsset(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        return StaticExtensions.Contains(path[dot..].ToLowerInvariant());
    }

    private static bool IsStaticContentType(string? ct) =>
        ct is not null && (ct.StartsWith("image/") || ct.StartsWith("font/") || ct.StartsWith("text/css")
            || ct is "application/javascript" or "text/javascript" or "application/wasm");

    private static bool IsHtml(string? ct) => ct is "text/html" or "application/xhtml+xml";

    private static bool IsJson(string? ct) => ct is not null &&
        (ct is "application/json" or "text/json" or "application/graphql-response+json" || ct.EndsWith("+json", StringComparison.Ordinal));

    private static string? Simplify(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var value = contentType;
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Collapses observed network endpoints per page by (page, category, scheme, host, port, path, method), counting repeats. Bounded,
/// thread-safe, memory-only. Holds no credential. Cleared with the session it belongs to.
/// </summary>
internal sealed class ObservedNetworkRegistry(int capacity = 400)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ObservedNetworkEndpoint> _endpoints = new(StringComparer.Ordinal);

    public void Record(ObservedNetworkEndpoint endpoint)
    {
        var key = $"{endpoint.PageOrigin}{endpoint.PagePath}|{endpoint.Category}|{endpoint.Scheme}|{endpoint.Host}|{endpoint.Port}|{endpoint.Path}|{endpoint.Method}";
        lock (_lock)
        {
            if (_endpoints.TryGetValue(key, out var existing))
            {
                _endpoints[key] = existing with
                {
                    Count = existing.Count + 1,
                    LastStatus = endpoint.LastStatus,
                    LastObservedAt = endpoint.LastObservedAt,
                    AuthObserved = existing.AuthObserved || endpoint.AuthObserved,
                    Confidence = (ObservedEndpointConfidence)Math.Max((int)existing.Confidence, (int)endpoint.Confidence),
                    OperationType = endpoint.OperationType != GraphQlOperationType.None ? endpoint.OperationType : existing.OperationType,
                    OperationName = endpoint.OperationName ?? existing.OperationName
                };
                return;
            }
            if (_endpoints.Count >= capacity) return;
            _endpoints[key] = endpoint;
        }
    }

    public IReadOnlyList<ObservedNetworkEndpoint> Snapshot()
    {
        lock (_lock)
            return _endpoints.Values
                .OrderByDescending(e => e.LastObservedAt)
                .ThenByDescending(e => e.Count)
                .ToList();
    }

    public void Clear() { lock (_lock) _endpoints.Clear(); }
}

/// <summary>
/// Collapses observed authenticated endpoints by (origin, path, method, type) so repeated identical requests are counted, not flooded.
/// Bounded in size, thread-safe, memory-only. Holds no credential. Cleared with the session it belongs to.
/// </summary>
internal sealed class ObservedEndpointRegistry(int capacity = 50)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ObservedAuthenticatedEndpoint> _endpoints = new(StringComparer.Ordinal);

    public void Record(ObservedAuthenticatedEndpoint endpoint)
    {
        var key = $"{endpoint.EndpointType}|{endpoint.Origin}|{endpoint.Path}|{endpoint.Method}";
        lock (_lock)
        {
            if (_endpoints.TryGetValue(key, out var existing))
            {
                _endpoints[key] = endpoint with
                {
                    Count = existing.Count + 1,
                    // Keep the strongest confidence and the most informative GraphQL operation ever seen for this endpoint.
                    Confidence = (ObservedEndpointConfidence)Math.Max((int)existing.Confidence, (int)endpoint.Confidence),
                    OperationType = endpoint.OperationType != GraphQlOperationType.None ? endpoint.OperationType : existing.OperationType,
                    OperationName = endpoint.OperationName ?? existing.OperationName
                };
                return;
            }
            if (_endpoints.Count >= capacity) return;   // bounded: never grow without limit
            _endpoints[key] = endpoint;
        }
    }

    /// <summary>Snapshot ordered most-trustworthy first, then most-frequent, then most-recent.</summary>
    public IReadOnlyList<ObservedAuthenticatedEndpoint> Snapshot()
    {
        lock (_lock)
            return _endpoints.Values
                .OrderByDescending(e => (int)e.Confidence)
                .ThenByDescending(e => e.Count)
                .ThenByDescending(e => e.LastObservedAt)
                .ToList();
    }

    public void Clear() { lock (_lock) _endpoints.Clear(); }
}
