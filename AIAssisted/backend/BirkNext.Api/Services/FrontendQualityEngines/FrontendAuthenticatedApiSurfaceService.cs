using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.FrontendQualityEngines;

/// <summary>
/// The Frontend Quality Review's authenticated API-surface probes: approved read-only requests (REST GET of the configured REST base
/// and health endpoint, a side-effect-free GraphQL query) executed through <see cref="IAuthenticatedReviewGateway"/> with the
/// memory-only Local HTTPS proxy credential. The review receives sanitized results (status, media type, latency, allow-listed
/// security headers) or a typed "not executed" reason — never a token, cookie, raw header dump or body.
/// </summary>
public interface IFrontendAuthenticatedApiSurfaceService
{
    Task<FrontendAuthenticatedApiSurfaceResult> ProbeAsync(FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken = default);
}

public sealed class FrontendAuthenticatedApiSurfaceService(IAuthenticatedReviewGateway gateway) : IFrontendAuthenticatedApiSurfaceService
{
    /// <summary>Introspection-free, side-effect-free query: the schema's query type name.</summary>
    public const string GraphQlProbe = "query { __typename }";

    public async Task<FrontendAuthenticatedApiSurfaceResult> ProbeAsync(FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken = default)
    {
        var capabilities = gateway.Resolve(request.Identity);
        if (!capabilities.AuthenticatedApi)
            return new FrontendAuthenticatedApiSurfaceResult
            {
                Capabilities = capabilities, ContextAvailable = false,
                NotExecutedReason = string.IsNullOrWhiteSpace(capabilities.Reason)
                    ? "Authenticated API context not available."
                    : capabilities.Reason,
            };

        var probes = new List<(string Label, string Url, bool GraphQl)>();
        if (IsHttps(request.RestBaseUrl)) probes.Add(("REST API", request.RestBaseUrl!, false));
        if (IsHttps(request.HealthEndpoint)) probes.Add(("Health", request.HealthEndpoint!, false));
        if (IsHttps(request.GraphQlEndpoint)) probes.Add(("GraphQL", request.GraphQlEndpoint!, true));
        if (probes.Count == 0)
            return new FrontendAuthenticatedApiSurfaceResult
            {
                Capabilities = capabilities, ContextAvailable = true,
                NotExecutedReason = "No HTTPS REST base, health or GraphQL endpoint is configured for this Target Environment.",
            };

        var checks = new List<FrontendAuthenticatedApiCheck>();
        foreach (var (label, url, graphQl) in probes)
        {
            var outcome = graphQl
                ? await gateway.ExecuteGraphQlQueryAsync(request.Identity, url, GraphQlProbe, cancellationToken)
                : await gateway.ExecuteRestAsync(request.Identity, "GET", url, cancellationToken);
            checks.Add(new FrontendAuthenticatedApiCheck
            {
                Label = label, Url = Sanitize(url), Mode = outcome.Mode, Status = outcome.Status,
                StatusCode = outcome.Result?.StatusCode, ContentType = outcome.Result?.ContentType, ElapsedMs = outcome.Result?.ElapsedMs,
                GraphQlHasData = outcome.Result?.GraphQlHasData, GraphQlErrorCount = outcome.Result?.GraphQlErrorCount,
                Outcome = outcome.Message, SecurityHeaders = outcome.Result?.SecurityHeaders ?? new Dictionary<string, string>(),
            });
        }

        return new FrontendAuthenticatedApiSurfaceResult { Capabilities = capabilities, ContextAvailable = true, Checks = checks };
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;

    /// <summary>Scheme + host + path only; a query string can carry secrets and is never echoed.</summary>
    private static string Sanitize(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "";
}
