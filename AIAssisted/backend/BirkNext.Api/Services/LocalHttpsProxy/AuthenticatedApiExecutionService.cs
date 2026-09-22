using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Executes approved authenticated API checks with the memory-only credential: REST GET/HEAD/OPTIONS and GraphQL QUERY only, HTTPS
/// only, approved hosts of the same environment only, bounded timeout, sanitized result (status, media type, length, elapsed,
/// GraphQL error/data presence). The credential never leaves this service.
/// </summary>
public interface IAuthenticatedApiExecutionService
{
    Task<AuthenticatedApiExecutionResult> ExecuteRestAsync(AuthenticatedRestRequest request, CancellationToken cancellationToken = default);
    Task<AuthenticatedApiExecutionResult> ExecuteGraphQlQueryAsync(AuthenticatedGraphQlRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Profile-keyed authenticated REST execution for reviews: resolves the approved host scope from the memory-only context store
    /// (no runtime session id needed), re-validates the context at execution time, applies the credential internally and returns a
    /// sanitized result. Throws <see cref="AuthenticatedContextUnavailableException"/> when there is no usable context or the host is out of scope.
    /// </summary>
    Task<AuthenticatedApiExecutionResult> ExecuteRestForProfileAsync(string profileId, string contextFingerprint, string method, string url, CancellationToken cancellationToken = default);

    /// <summary>Profile-keyed authenticated GraphQL QUERY execution for reviews. Same contract as <see cref="ExecuteRestForProfileAsync"/>; mutations/subscriptions/variables rejected.</summary>
    Task<AuthenticatedApiExecutionResult> ExecuteGraphQlQueryForProfileAsync(string profileId, string contextFingerprint, string endpointUrl, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Profile-keyed authenticated GraphQL introspection (the standard IntrospectionQuery, a QUERY). Returns the introspection document
    /// (schema metadata only — type and field names, never user data) or a typed "introspection disabled" outcome. Bounded size.
    /// </summary>
    Task<AuthenticatedGraphQlSchemaOutcome> ExecuteGraphQlIntrospectionForProfileAsync(string profileId, string contextFingerprint, string endpointUrl, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a profile-keyed authenticated execution cannot run because the memory-only context is missing/expired or the target host is out of scope. Carries no credential.</summary>
public sealed class AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus status, string message) : Exception(message)
{
    public AuthenticatedExecutionStatus Status { get; } = status;
}

public sealed class AuthenticatedApiExecutionService : IAuthenticatedApiExecutionService, IDisposable
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };
    private const long MaxObservedBytes = 1024 * 1024;

    private readonly ILocalHttpsProxySessionAccess _sessions;
    private readonly TransientAuthenticatedApiContextStore _store;
    private readonly HttpClient _http;

    public AuthenticatedApiExecutionService(ILocalHttpsProxySessionAccess sessions, TransientAuthenticatedApiContextStore store, IOptions<LocalHttpsProxyOptions> options, HttpMessageHandler? handler = null)
    {
        _sessions = sessions;
        _store = store;
        _http = new HttpClient(handler ?? CreateHandler(options.Value.UpstreamProxy), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-AuthenticatedApiCheck/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(BirkNext.LocalHttpsProxy.NetworkEvidencePolicy.ProvenanceHeader, "BirkNextDiagnostic");
    }

    private static HttpMessageHandler CreateHandler(string? upstreamProxy)
    {
        // Never route through the loopback inspection proxy itself (the user may have set it as the system proxy).
        var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(upstreamProxy))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"http://{upstreamProxy}") { BypassProxyOnLocal = true };
        }
        return handler;
    }

    public async Task<AuthenticatedApiExecutionResult> ExecuteRestAsync(AuthenticatedRestRequest request, CancellationToken cancellationToken = default)
    {
        var session = new LocalHttpsProxySessionRequest(request.SessionId, request.ProfileId, request.ContextFingerprint);
        var scope = _sessions.GetScope(session);
        if (string.IsNullOrWhiteSpace(request.Method) || !SafeMethods.Contains(request.Method.Trim()))
            throw new ArgumentException("Only GET, HEAD and OPTIONS are allowed for authenticated REST checks in this phase; no request may mutate DEV data.");
        var uri = ApprovedUri(scope, request.Url);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method.Trim().ToUpperInvariant()), uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Apply(session, message);
        return await SendAsync(message, graphQl: false, cancellationToken);
    }

    public async Task<AuthenticatedApiExecutionResult> ExecuteGraphQlQueryAsync(AuthenticatedGraphQlRequest request, CancellationToken cancellationToken = default)
    {
        var session = new LocalHttpsProxySessionRequest(request.SessionId, request.ProfileId, request.ContextFingerprint);
        var scope = _sessions.GetScope(session);
        ManagedEdgePolicy.QueryOnly(request.Query); // exactly one QUERY operation, no variables; mutations and subscriptions are rejected
        var uri = ApprovedUri(scope, request.EndpointUrl);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = request.Query }), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
        Apply(session, message);
        return await SendAsync(message, graphQl: true, cancellationToken);
    }

    public async Task<AuthenticatedApiExecutionResult> ExecuteRestForProfileAsync(string profileId, string contextFingerprint, string method, string url, CancellationToken cancellationToken = default)
    {
        var scope = ScopeForProfile(profileId, contextFingerprint);
        if (string.IsNullOrWhiteSpace(method) || !SafeMethods.Contains(method.Trim()))
            throw new ArgumentException("Only GET, HEAD and OPTIONS are allowed for authenticated REST checks in this phase; no request may mutate DEV data.");
        var uri = ApprovedUriForProfile(scope, url);
        using var message = new HttpRequestMessage(new HttpMethod(method.Trim().ToUpperInvariant()), uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyForProfile(profileId, contextFingerprint, message);
        return await SendAsync(message, graphQl: false, cancellationToken);
    }

    public async Task<AuthenticatedApiExecutionResult> ExecuteGraphQlQueryForProfileAsync(string profileId, string contextFingerprint, string endpointUrl, string query, CancellationToken cancellationToken = default)
    {
        var scope = ScopeForProfile(profileId, contextFingerprint);
        ManagedEdgePolicy.QueryOnly(query);
        var uri = ApprovedUriForProfile(scope, endpointUrl);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
        ApplyForProfile(profileId, contextFingerprint, message);
        return await SendAsync(message, graphQl: true, cancellationToken);
    }

    /// <summary>Standard introspection query (schema metadata only). Kept in sync with the contract-analysis fetcher.</summary>
    internal const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            types { ...FullType }
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            directives { name }
          }
        }
        fragment FullType on __Type {
          kind name description
          fields(includeDeprecated: true) { name description args { ...InputValue } type { ...TypeRef } isDeprecated deprecationReason }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue { name description type { ...TypeRef } defaultValue }
        fragment TypeRef on __Type { kind name ofType { ...TypeRef } }
        """;
    private const long MaxSchemaBytes = 4 * 1024 * 1024;

    public async Task<AuthenticatedGraphQlSchemaOutcome> ExecuteGraphQlIntrospectionForProfileAsync(string profileId, string contextFingerprint, string endpointUrl, CancellationToken cancellationToken = default)
    {
        var scope = ScopeForProfile(profileId, contextFingerprint);
        ManagedEdgePolicy.QueryOnly(IntrospectionQuery);
        var uri = ApprovedUriForProfile(scope, endpointUrl);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = IntrospectionQuery }), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
        ApplyForProfile(profileId, contextFingerprint, message);
        var stopwatch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (buffer.Length < MaxSchemaBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
        }
        stopwatch.Stop();
        var status = (int)response.StatusCode;
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var disabled = false;
        string? schemaJson = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("__schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                schemaJson = text;
            else disabled = document.RootElement.TryGetProperty("errors", out _) || status is >= 200 and < 300;
        }
        catch (JsonException) { }
        return new AuthenticatedGraphQlSchemaOutcome
        {
            Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, StatusCode = status,
            IntrospectionDisabled = schemaJson is null && disabled, SchemaJson = schemaJson, ElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
            Message = schemaJson is not null ? $"HTTP {status}; introspection schema returned." : disabled ? $"HTTP {status}; introspection is disabled or rejected." : $"HTTP {status}; response was not a GraphQL result document.",
        };
    }

    private ApprovedHostSet ScopeForProfile(string profileId, string contextFingerprint) =>
        ((ITransientCredentialSink)_store).ScopeOf(profileId, contextFingerprint)
        ?? throw new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.NoContext,
            "Authenticated API context unavailable. Perform an authenticated action in the browser through the proxy first.");

    private static Uri ApprovedUriForProfile(ApprovedHostSet scope, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new ArgumentException("Only HTTPS URLs are allowed for authenticated checks.");
        if (!scope.ContainsUri(uri))
            throw new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.OutOfScope, "The requested host is not approved for the selected Target Environment.");
        return uri;
    }

    private void ApplyForProfile(string profileId, string contextFingerprint, HttpRequestMessage message)
    {
        // Re-validate at execution time: the context may have expired or been invalidated since the review was planned.
        if (!_store.IsAuthenticatedApiContextAvailable(profileId, contextFingerprint))
            throw new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.Expired,
                "Authenticated API session expired or was invalidated. Continue using the target application in the proxy-configured browser to refresh the session.");
        if (!((ITransientCredentialSink)_store).TryApply(profileId, contextFingerprint, message))
            throw new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.OutOfScope, "The authenticated API context does not cover this request.");
    }

    private static Uri ApprovedUri(ApprovedHostSet scope, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !scope.ContainsUri(uri))
            throw new ArgumentException("Only HTTPS URLs on the approved hosts of the selected Target Environment are allowed.");
        return uri;
    }

    private void Apply(LocalHttpsProxySessionRequest session, HttpRequestMessage message)
    {
        if (!_store.IsAuthenticatedApiContextAvailable(session.ProfileId, session.ContextFingerprint))
            throw new InvalidOperationException("Authenticated API context unavailable. Perform an authenticated action in the browser through the proxy first.");
        if (!((ITransientCredentialSink)_store).TryApply(session.ProfileId, session.ContextFingerprint, message))
            throw new InvalidOperationException("The authenticated API context does not cover this request.");
    }

    private async Task<AuthenticatedApiExecutionResult> SendAsync(HttpRequestMessage message, bool graphQl, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        // The body is read into memory only to derive structural evidence (JSON shape, error-leak indicators); it is never returned.
        var keepSample = graphQl || ApiQuality.JsonBodyInspector.IsJsonMediaType(mediaType) || (int)response.StatusCode >= 400;
        var (length, sample) = await ReadBoundedAsync(response, keepSample, cancellationToken);
        stopwatch.Stop();
        var status = (int)response.StatusCode;
        int? errors = null;
        bool? hasData = null;
        bool? jsonValid = null;
        IReadOnlyList<BirkNext.ApiReview.JsonShapeEntry> shape = [];
        var leaks = new List<string>();
        if (sample is not null && sample.Length > 0)
        {
            var text = Encoding.UTF8.GetString(sample);
            leaks = ApiQuality.JsonBodyInspector.LeakIndicators(text);
            try
            {
                using var document = JsonDocument.Parse(sample);
                jsonValid = true;
                shape = ApiQuality.JsonBodyInspector.Shape(document.RootElement);
                if (graphQl && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    errors = document.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
                    hasData = document.RootElement.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                }
            }
            catch (JsonException) { jsonValid = false; }
            Array.Clear(sample);
        }
        return new AuthenticatedApiExecutionResult
        {
            StatusCode = status, ContentType = mediaType, ContentLength = length, ElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
            GraphQlErrorCount = errors, GraphQlHasData = hasData, Outcome = DescribeOutcome(status, mediaType, length, graphQl, errors, hasData),
            SecurityHeaders = CollectSecurityHeaders(response), BodyShape = shape, LeakIndicators = leaks, JsonValid = jsonValid,
            ProblemDetails = ApiQuality.JsonBodyInspector.IsProblemDetails(mediaType, shape),
        };
    }

    /// <summary>Allow-listed security posture headers only (transport, content-type sniffing, framing, CORS). Nothing else leaves the service.</summary>
    internal static IReadOnlyDictionary<string, string> CollectSecurityHeaders(HttpResponseMessage response)
    {
        const int maxValueLength = 512;
        var collected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AuthenticatedApiExecutionResult.SecurityHeaderAllowList)
        {
            if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            {
                var value = string.Join(", ", values);
                collected[name] = value.Length > maxValueLength ? value[..maxValueLength] : value;
            }
        }
        return collected;
    }

    private static string DescribeOutcome(int status, string? mediaType, long length, bool graphQl, int? errors, bool? hasData)
    {
        if (status is 401 or 403) return $"Access denied with the in-memory credential (HTTP {status}). The credential may not be valid for this API audience.";
        if (!graphQl) return $"HTTP {status}{(mediaType is null ? "" : " " + mediaType)}; {length} bytes; response body not captured.";
        if (status is < 200 or >= 300) return $"HTTP {status} from the GraphQL endpoint.";
        if (hasData == true && errors == 0) return $"HTTP {status}; GraphQL data returned without errors.";
        if (hasData == true) return $"HTTP {status}; GraphQL data returned with {errors} error(s).";
        if (errors is > 0) return $"HTTP {status}; GraphQL returned {errors} error(s) and no data.";
        return $"HTTP {status}; response was not a GraphQL result document.";
    }

    private static async Task<(long Length, byte[]? Sample)> ReadBoundedAsync(HttpResponseMessage response, bool keepSample, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16384];
        long total = 0;
        using var sample = keepSample ? new MemoryStream() : null;
        while (total < MaxObservedBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            total += read;
            sample?.Write(buffer, 0, read);
        }
        return (total, sample?.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
