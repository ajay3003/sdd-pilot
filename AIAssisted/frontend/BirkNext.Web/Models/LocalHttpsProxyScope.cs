using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>
/// Derives the Local HTTPS proxy scope of a Target Environment: the explicitly configured hosts that may be intercepted, the
/// context fingerprint that binds the runtime session (and its memory-only credential) to the environment's target-relevant
/// configuration, and the environment gate. Never contains credentials.
/// </summary>
public static class LocalHttpsProxyScope
{
    /// <summary>
    /// Digest of everything the proxy credential is bound to: profile identity, target URL, environment type, authentication
    /// configuration (including the testing method) and every REST/GraphQL target. Any change stales the runtime session, which wipes
    /// the in-memory credential on the backend.
    /// </summary>
    public static string Fingerprint(FrontendAnalysisProfile profile)
    {
        var context = JsonSerializer.Serialize(new
        {
            profile.Id, profile.TargetUrl, profile.EnvironmentType, profile.Authentication,
            profile.RestBaseUrl, profile.GraphQlEndpoint, profile.HealthEndpoint, profile.SwaggerUrl, profile.ExpectedApiGateway,
            profile.AllowedRestHosts, profile.AllowedGraphQlEndpoints,
            SecurityRestHosts = profile.Security.AllowedRestHosts, SecurityGraphQlHosts = profile.Security.AllowedGraphQlHosts
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
    }

    public static bool EnvironmentAllowed(FrontendAnalysisProfile? profile) =>
        profile is not null && LocalHttpsProxyEnvironmentPolicy.IsAllowed(profile.EnvironmentType.ToString());

    /// <summary>Explicitly configured HTTPS authorities (<c>host:port</c>) of the environment. Plain host entries default to port 443; non-HTTPS URLs are ignored.</summary>
    public static IReadOnlyList<string> ApprovedHosts(FrontendAnalysisProfile profile)
    {
        var authorities = new SortedSet<string>(StringComparer.Ordinal);
        void AddUrl(string? url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns)
                authorities.Add($"{uri.IdnHost.ToLowerInvariant()}:{uri.Port}");
        }
        void AddHostOrUrl(string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            var value = entry.Trim();
            if (value.Contains("://", StringComparison.Ordinal)) { AddUrl(value); return; }
            var host = value;
            var port = 443;
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535) { host = value[..colon]; port = parsed; }
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.Contains('*') || host.Contains('/') || Uri.CheckHostName(host) != UriHostNameType.Dns) return;
            authorities.Add($"{host}:{port}");
        }
        AddUrl(profile.TargetUrl);
        AddUrl(profile.RestBaseUrl);
        AddUrl(profile.GraphQlEndpoint);
        AddUrl(profile.HealthEndpoint);
        AddUrl(profile.SwaggerUrl);
        AddUrl(profile.ExpectedApiGateway);
        foreach (var host in profile.AllowedRestHosts) AddHostOrUrl(host);
        foreach (var endpoint in profile.AllowedGraphQlEndpoints) AddHostOrUrl(endpoint);
        foreach (var host in profile.Security.AllowedRestHosts) AddHostOrUrl(host);
        foreach (var host in profile.Security.AllowedGraphQlHosts) AddHostOrUrl(host);
        return authorities.ToList();
    }

    public static LocalHttpsProxyScopeRequest Request(FrontendAnalysisProfile profile) => new(
        profile.Id, Fingerprint(profile), profile.EnvironmentType.ToString(), profile.TargetUrl ?? "", ApprovedHosts(profile),
        Guid.TryParse(profile.Authentication.ExpectedTenant, out var tenant) ? tenant.ToString("D") : null);

    /// <summary>Harmless authenticated REST GET candidate: the configured health endpoint, else the REST base URL. Null when neither is an HTTPS URL.</summary>
    public static string? RestCheckUrl(FrontendAnalysisProfile profile) =>
        FirstHttps(profile.HealthEndpoint) ?? FirstHttps(profile.RestBaseUrl);

    public static string? GraphQlCheckUrl(FrontendAnalysisProfile profile) => FirstHttps(profile.GraphQlEndpoint);

    /// <summary>Introspection-free, side-effect-free GraphQL query: the schema's query type name.</summary>
    public const string GraphQlCheckQuery = "query { __typename }";

    private static string? FirstHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
}
