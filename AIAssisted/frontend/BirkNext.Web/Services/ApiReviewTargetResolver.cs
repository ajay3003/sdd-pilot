using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Builds the API Quality Review targets of the ACTIVE Target Environment from what BirkNext actually knows: Endpoint Discovery
/// (REST/GraphQL traffic observed by the Local HTTPS proxy, grouped into services), the saved API configuration (REST base, GraphQL
/// endpoint, health endpoint, OpenAPI URL) and nothing else — never a guessed /health or /graphql. Static/CDN, telemetry, auth hops and
/// WebSockets are not API review targets. Pure: no I/O, no credentials.
/// </summary>
public static class ApiReviewTargetResolver
{
    private static readonly Regex VersionSegment = new("^v\\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] PrefixSegments = ["api", "rest", "internal", "public", "odata"];

    public static List<ApiReviewTarget> Resolve(FrontendAnalysisContext context, EndpointDiscoverySnapshot discovery)
    {
        var environmentId = context.ActiveProfile.Id;
        var configuredAuth = context.RequiresAuthentication || context.ApiAuth.AuthType != TargetApiAuthType.None;
        var observed = discovery.Pages.SelectMany(p => p.Endpoints).Concat(discovery.Shared)
            .Where(NetworkEvidencePolicy.IsApiCandidate).ToList();
        var targets = new List<ApiReviewTarget>();

        // ── GraphQL endpoints (learned path) ──
        foreach (var group in observed.Where(e => e.Category == ObservedTrafficCategory.GraphQl).GroupBy(e => $"{e.Scheme}|{e.Host}|{e.Port}|{e.Path}"))
        {
            var first = group.First();
            var operations = group.GroupBy(e => $"{e.OperationType}|{e.OperationName}").Select(g => new ApiReviewOperation
            {
                Method = "POST", Path = first.Path, OperationType = g.First().OperationType == GraphQlOperationType.None ? GraphQlOperationType.Query : g.First().OperationType, OperationName = g.First().OperationName,
                ObservedCount = g.Sum(e => e.Count), AuthObserved = g.Any(e => e.AuthObserved), LastStatus = g.OrderByDescending(e => e.LastObservedAt).First().LastStatus,
                Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = (ObservedEndpointConfidence)g.Max(e => (int)e.Confidence),
            }).OrderByDescending(o => o.ObservedCount).ToList();
            var confidence = (ObservedEndpointConfidence)group.Max(e => (int)e.Confidence);
            targets.Add(new ApiReviewTarget
            {
                TargetId = Id(ApiReviewTargetType.GraphQl, first.Origin, first.Path), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.GraphQl, Scheme = first.Scheme, Host = first.Host, Port = first.Port,
                BasePath = first.Path, ServiceName = $"GraphQL · {first.Host}{first.Path}", Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = group.Any(e => e.AuthObserved),
                DiscoveredAt = group.Min(e => e.FirstObservedAt), Confidence = confidence, Operations = operations, Selected = confidence == ObservedEndpointConfidence.Verified,
            });
        }

        // ── REST services: origin + service base path ──
        foreach (var group in observed.Where(e => e.Category == ObservedTrafficCategory.Rest).GroupBy(e => $"{e.Scheme}|{e.Host}|{e.Port}|{BasePathOf(e.Path)}"))
        {
            var first = group.First();
            var basePath = BasePathOf(first.Path);
            var operations = group.GroupBy(e => $"{e.Method}|{e.Path}").Select(g => new ApiReviewOperation
            {
                Method = g.First().Method.ToUpperInvariant(), Path = g.First().Path, ObservedCount = g.Sum(e => e.Count), AuthObserved = g.Any(e => e.AuthObserved),
                LastStatus = g.OrderByDescending(e => e.LastObservedAt).First().LastStatus, Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = (ObservedEndpointConfidence)g.Max(e => (int)e.Confidence),
            }).OrderBy(o => o.Path).ThenBy(o => o.Method).ToList();
            var confidence = (ObservedEndpointConfidence)group.Max(e => (int)e.Confidence);
            targets.Add(new ApiReviewTarget
            {
                TargetId = Id(ApiReviewTargetType.Rest, first.Origin, basePath), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.Rest, Scheme = first.Scheme, Host = first.Host, Port = first.Port,
                BasePath = basePath, ServiceName = ServiceName(basePath, first.Host), Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = group.Any(e => e.AuthObserved),
                DiscoveredAt = group.Min(e => e.FirstObservedAt), Confidence = confidence, Operations = operations, Selected = confidence == ObservedEndpointConfidence.Verified,
            });
        }

        // ── Configured REST base / health / OpenAPI ──
        if (Uri.TryCreate(context.RestBaseUrl?.Trim(), UriKind.Absolute, out var restUri) && restUri.Scheme is "https" or "http" && NetworkEvidencePolicy.Classify(restUri.AbsolutePath) == NetworkResourceKind.Unknown)
        {
            var basePath = Normalize(restUri.AbsolutePath);
            var existing = targets.FirstOrDefault(t => t.ApiType == ApiReviewTargetType.Rest && SameOrigin(t, restUri) && (t.BasePath.Equals(basePath, StringComparison.OrdinalIgnoreCase) || t.BasePath.StartsWith(basePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
                targets.Add(new ApiReviewTarget
                {
                    TargetId = Id(ApiReviewTargetType.Rest, Origin(restUri), basePath), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.Rest, Scheme = restUri.Scheme, Host = restUri.Host, Port = restUri.Port,
                    BasePath = basePath, ServiceName = ServiceName(basePath, restUri.Host) + " (configured)", Source = ApiReviewTargetSource.Configured, AuthRequired = configuredAuth, Confidence = ObservedEndpointConfidence.Verified,
                    Operations = [new ApiReviewOperation { Method = "GET", Path = basePath, Source = ApiReviewTargetSource.Configured, Confidence = ObservedEndpointConfidence.Verified, AuthObserved = configuredAuth }], Selected = true,
                });
            else
            {
                targets.Remove(existing);
                targets.Add(existing with { Selected = true, AuthRequired = existing.AuthRequired || configuredAuth });
            }
        }
        if (Uri.TryCreate(context.HealthEndpoint?.Trim(), UriKind.Absolute, out var healthUri) && healthUri.Scheme is "https" or "http")
        {
            var path = Normalize(healthUri.AbsolutePath);
            var owner = targets.FirstOrDefault(t => t.ApiType == ApiReviewTargetType.Rest && SameOrigin(t, healthUri));
            var operation = new ApiReviewOperation { Method = "GET", Path = path, Source = ApiReviewTargetSource.Configured, Confidence = ObservedEndpointConfidence.Verified };
            if (owner is not null && !owner.Operations.Any(o => o.Method == "GET" && o.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Remove(owner);
                targets.Add(owner with { Operations = [.. owner.Operations, operation] });
            }
            else if (owner is null)
                targets.Add(new ApiReviewTarget
                {
                    TargetId = Id(ApiReviewTargetType.Rest, Origin(healthUri), path), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.Rest, Scheme = healthUri.Scheme, Host = healthUri.Host, Port = healthUri.Port,
                    BasePath = path, ServiceName = "Health endpoint (configured)", Source = ApiReviewTargetSource.Configured, AuthRequired = false, Confidence = ObservedEndpointConfidence.Verified, Operations = [operation], Selected = true,
                });
        }
        if (Uri.TryCreate(context.SwaggerUrl?.Trim(), UriKind.Absolute, out var swaggerUri) && swaggerUri.Scheme is "https" or "http")
        {
            var restTargets = targets.Where(t => t.ApiType == ApiReviewTargetType.Rest && string.Equals(t.Host, swaggerUri.Host, StringComparison.OrdinalIgnoreCase)).ToList();
            if (restTargets.Count == 0)
                targets.Add(new ApiReviewTarget
                {
                    TargetId = Id(ApiReviewTargetType.Rest, Origin(swaggerUri), "/"), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.Rest, Scheme = swaggerUri.Scheme, Host = swaggerUri.Host, Port = swaggerUri.Port,
                    BasePath = "/", ServiceName = $"Contract · {swaggerUri.Host}", Source = ApiReviewTargetSource.Contract, AuthRequired = configuredAuth, ContractSource = swaggerUri.GetLeftPart(UriPartial.Path), Confidence = ObservedEndpointConfidence.Verified, Selected = true,
                });
            else
                foreach (var t in restTargets) { targets.Remove(t); targets.Add(t with { ContractSource = t.ContractSource ?? swaggerUri.GetLeftPart(UriPartial.Path) }); }
        }
        if (Uri.TryCreate(context.GraphQlEndpoint?.Trim(), UriKind.Absolute, out var gqlUri) && gqlUri.Scheme is "https" or "http")
        {
            var path = Normalize(gqlUri.AbsolutePath);
            var existing = targets.FirstOrDefault(t => t.ApiType == ApiReviewTargetType.GraphQl && SameOrigin(t, gqlUri) && t.BasePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                targets.Add(new ApiReviewTarget
                {
                    TargetId = Id(ApiReviewTargetType.GraphQl, Origin(gqlUri), path), EnvironmentId = environmentId, ApiType = ApiReviewTargetType.GraphQl, Scheme = gqlUri.Scheme, Host = gqlUri.Host, Port = gqlUri.Port,
                    BasePath = path, ServiceName = $"GraphQL · {gqlUri.Host}{path} (configured)", Source = ApiReviewTargetSource.Configured, AuthRequired = configuredAuth, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
                });
            else { targets.Remove(existing); targets.Add(existing with { Selected = true, AuthRequired = existing.AuthRequired || configuredAuth }); }
        }

        return targets.OrderBy(t => t.ApiType).ThenBy(t => t.ServiceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"/api/v1/children/42/placements" → "/api/v1/children"; "/children" → "/children"; "/" → "/".</summary>
    public static string BasePathOf(string path)
    {
        var segments = Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "/";
        var take = 0;
        while (take < segments.Length && (PrefixSegments.Contains(segments[take].ToLowerInvariant()) || VersionSegment.IsMatch(segments[take]))) take++;
        take = Math.Min(segments.Length, take + 1);
        return "/" + string.Join("/", segments.Take(take));
    }

    public static string ServiceName(string basePath, string host)
    {
        var last = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null || PrefixSegments.Contains(last.ToLowerInvariant()) || VersionSegment.IsMatch(last)) return $"{host} API";
        var words = Regex.Replace(last, "([a-z])([A-Z])", "$1 $2").Replace('-', ' ').Replace('_', ' ');
        return $"{char.ToUpperInvariant(words[0])}{words[1..]} API";
    }

    public static string Normalize(string? path)
    {
        var p = (path ?? "").Split('?')[0].Split('#')[0];
        while (p.Length > 1 && p.EndsWith('/')) p = p[..^1];
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>Stable id from type + origin + base path (no query, no credential): 16 hex chars.</summary>
    public static string Id(ApiReviewTargetType type, string origin, string basePath) =>
        type.ToString().ToLowerInvariant() + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{type}|{origin.ToLowerInvariant()}|{Normalize(basePath).ToLowerInvariant()}")))[..16].ToLowerInvariant();

    private static string Origin(Uri uri) => uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    private static bool SameOrigin(ApiReviewTarget target, Uri uri) => string.Equals(target.Host, uri.Host, StringComparison.OrdinalIgnoreCase) && target.Port == uri.Port && string.Equals(target.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Why Run is enabled or disabled, with the one action that changes it. Pure.</summary>
public sealed record ApiReviewRunEligibility(bool Enabled, string Reason, string? ActionText, string? ActionHref)
{
    public const string NoTargetsReason = "No REST or GraphQL API target is available for review.";
    public const string NoAuthContextReason = "Authenticated API context not available.";
    public const string NoAuthContextAction = "Open Authentication setup";
    public const string TargetEnvironmentsHref = "/admin/system-settings?section=target-environments";

    /// <summary>
    /// The Authentication tab of one Target Environment. Target Environment → Authentication owns the proxy, the dedicated
    /// Edge and the authenticated context; API Quality Review only links there. Opening it selects the profile for viewing
    /// (it does not make it active, start anything or change configuration).
    /// </summary>
    public static string AuthenticationHref(string? profileId) => string.IsNullOrWhiteSpace(profileId)
        ? TargetEnvironmentsHref + "&tab=auth"
        : TargetEnvironmentsHref + "&tab=auth&profile=" + Uri.EscapeDataString(profileId);

    public static ApiReviewRunEligibility Evaluate(FrontendAnalysisContext? context, IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selectedIds, AuthenticatedReviewCapabilities? capabilities)
    {
        if (context is null) return new(false, "Loading the active Target Environment…", null, null);
        if (context.ActiveTargetError is { Length: > 0 } error) return new(false, error, "Open Target Environments", TargetEnvironmentsHref);
        if (targets.Count == 0) return new(false, NoTargetsReason, "View Endpoint Discovery or configure an API target", TargetEnvironmentsHref);
        var selected = targets.Where(t => selectedIds.Contains(t.TargetId)).ToList();
        if (selected.Count == 0) return new(false, "Select at least one API target.", null, null);
        var authenticatedAvailable = capabilities?.AuthenticatedApi == true;
        var runnable = selected.Where(t => !t.AuthRequired || authenticatedAvailable).ToList();
        if (runnable.Count > 0) return new(true, runnable.Count == selected.Count ? $"{Targets(selected.Count)} ready." : $"{runnable.Count} of {Targets(selected.Count)} runnable; authenticated targets will be reported as blocked (no timeout).", null, null);
        var method = context.ActiveProfile.Authentication.AuthenticatedTestingMethod;
        var authHref = AuthenticationHref(context.ActiveProfile.Id);
        return method switch
        {
            AuthenticatedTestingMethod.ManualOnly => new(false, "Authenticated automated API review is unavailable: the environment is manual-verification only.", "Change the authenticated testing method", authHref),
            AuthenticatedTestingMethod.LocalHttpsProxy => new(false, NoAuthContextReason, NoAuthContextAction, authHref),
            _ => new(false, "The Managed Edge (CDP) method provides no authenticated API execution.", "Switch the environment to the Local HTTPS Proxy method", authHref),
        };
    }

    private static string Targets(int count) => count == 1 ? "1 target" : $"{count} targets";
}
