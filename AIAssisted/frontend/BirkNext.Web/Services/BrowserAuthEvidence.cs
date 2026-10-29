using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The exact application a browser login is about: origin plus path prefix ("https://org.apps.example" + "/org/app").</summary>
public sealed record BrowserAuthAppScope(string Origin, string PathPrefix)
{
    public bool Contains(string? origin, string? path) =>
        string.Equals(origin?.TrimEnd('/'), Origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && (PathPrefix is "" or "/" || (path ?? "").StartsWith(PathPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && ((path ?? "").Length == PathPrefix.TrimEnd('/').Length || (path ?? "")[PathPrefix.TrimEnd('/').Length] == '/'));

    public string Display => $"{Origin.TrimEnd('/')}{PathPrefix}";
}

/// <summary>An identity provider host family ("test.idporten.no" → "ID-porten TEST"). Matched by host suffix only.</summary>
public sealed record BrowserIdentityProvider(string HostSuffix, string Label);

/// <summary>
/// What Browser Discovery shows about a browser login of one exact application: the level reached, the provider, when, and the steps that
/// prove it. <see cref="ProviderObservedElsewhere"/> means an identity provider was seen in this environment's browser evidence but not as a
/// login of this application (provider-level evidence never proves an app-level login).
/// </summary>
public sealed record BrowserAuthEvidenceResult(
    BrowserAuthEvidenceLevel Level,
    BrowserAuthEvidenceLevel ObservedLevel,
    string? Provider,
    DateTimeOffset? LastObservedAt,
    IReadOnlyList<string> Steps,
    bool ProviderObservedElsewhere)
{
    public const string Source = "Browser Discovery";
    public static readonly BrowserAuthEvidenceResult None = new(BrowserAuthEvidenceLevel.Unknown, BrowserAuthEvidenceLevel.Unknown, null, null, [], false);

    /// <summary>Usable as the hop's runtime evidence: the login of this exact application was at least seen to complete (return observed).</summary>
    public bool Usable => ObservedLevel is BrowserAuthEvidenceLevel.AuthenticatedReturnObserved or BrowserAuthEvidenceLevel.AuthenticatedSessionObserved;

    public bool Reached(BrowserAuthEvidenceLevel level) => ObservedLevel switch
    {
        BrowserAuthEvidenceLevel.AuthenticatedSessionObserved => level is BrowserAuthEvidenceLevel.LoginFlowObserved or BrowserAuthEvidenceLevel.AuthenticatedReturnObserved or BrowserAuthEvidenceLevel.AuthenticatedSessionObserved,
        BrowserAuthEvidenceLevel.LoggedOut => level is not BrowserAuthEvidenceLevel.Unknown and not BrowserAuthEvidenceLevel.Historical,
        BrowserAuthEvidenceLevel.AuthenticatedReturnObserved => level is BrowserAuthEvidenceLevel.LoginFlowObserved or BrowserAuthEvidenceLevel.AuthenticatedReturnObserved,
        BrowserAuthEvidenceLevel.LoginFlowObserved => level == BrowserAuthEvidenceLevel.LoginFlowObserved,
        _ => false,
    };
}

/// <summary>
/// Deterministic classification of Browser Discovery evidence into browser-login evidence for one exact application. Browser Discovery owns
/// the observations; this only reads them (discover once, record once, reuse everywhere). Rules:
/// <list type="bullet">
/// <item>Login flow observed: an identity-provider page or authentication request whose preceding page (or Referer) is the application.</item>
/// <item>Authenticated return observed: after that, the next non-provider page is the same application.</item>
/// <item>Authenticated session observed: after the return, the application made a successful request carrying a bearer token, or to one of
/// the caller's session-only paths.</item>
/// <item>Logged out: after that, the application answered 401, or the provider's logout/end-session page was seen.</item>
/// </list>
/// Generic browser evidence — DOM, accessibility, performance, ordinary navigation, static, configuration or API traffic without these steps —
/// never counts. Another application's login (another origin or path) never counts. No token, cookie or header value is read or kept.
/// </summary>
public static class BrowserAuthEvidence
{
    private sealed record Visit(string Origin, string Path, DateTimeOffset At);
    private sealed record ProviderEvent(DateTimeOffset At, BrowserIdentityProvider Provider, string? RefererOrigin, string? RefererPath, string Path);

    public static BrowserAuthEvidenceResult Classify(EndpointDiscoverySnapshot? snapshot, BrowserAuthAppScope scope, IReadOnlyList<BrowserIdentityProvider> providers,
        IReadOnlyList<string> sessionPathSuffixes, DateTimeOffset now, TimeSpan? historicalAfter = null)
    {
        if (snapshot is null) return BrowserAuthEvidenceResult.None;
        BrowserIdentityProvider? ProviderOf(string? hostOrOrigin)
        {
            var host = hostOrOrigin is null ? null : Uri.TryCreate(hostOrOrigin, UriKind.Absolute, out var uri) ? uri.Host : hostOrOrigin;
            return host is null ? null : providers.FirstOrDefault(p => host.Equals(p.HostSuffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + p.HostSuffix, StringComparison.OrdinalIgnoreCase));
        }

        // Page visits: a page's first observation and (when later) its last observation are both visits.
        var visits = snapshot.Pages.SelectMany(p => p.LastObservedAt > p.FirstObservedAt
                ? new[] { new Visit(p.PageOrigin, p.PagePath, p.FirstObservedAt), new Visit(p.PageOrigin, p.PagePath, p.LastObservedAt) }
                : [new Visit(p.PageOrigin, p.PagePath, p.FirstObservedAt)])
            .OrderBy(v => v.At).ToList();
        var endpoints = snapshot.Pages.SelectMany(p => p.Endpoints).Concat(snapshot.Shared).ToList();

        // Identity-provider events: a provider page, or an authentication request to a provider host (with its Referer page, when known).
        var providerEvents = visits.Where(v => ProviderOf(v.Origin) is not null).Select(v => new ProviderEvent(v.At, ProviderOf(v.Origin)!, null, null, v.Path))
            .Concat(endpoints.Where(e => ProviderOf(e.Host) is not null)
                .Select(e => new ProviderEvent(e.FirstObservedAt, ProviderOf(e.Host)!, e.PageOrigin, e.PagePath, e.Path)))
            .OrderBy(e => e.At).ToList();
        var providerSeen = providerEvents.Count > 0;
        var appVisits = visits.Where(v => scope.Contains(v.Origin, v.Path)).ToList();
        var others = visits.Where(v => ProviderOf(v.Origin) is null).ToList();

        var steps = new List<string>();
        var level = BrowserAuthEvidenceLevel.Unknown;
        string? provider = null;
        DateTimeOffset? last = null;
        foreach (var e in providerEvents)
        {
            var fromApp = e.RefererOrigin is not null ? scope.Contains(e.RefererOrigin, e.RefererPath)
                : others.LastOrDefault(v => v.At <= e.At) is { } before && scope.Contains(before.Origin, before.Path);
            if (!fromApp) continue;
            provider ??= e.Provider.Label;
            if (level < BrowserAuthEvidenceLevel.LoginFlowObserved)
            {
                level = BrowserAuthEvidenceLevel.LoginFlowObserved; last = e.At;
                steps.Add($"{scope.Display} → {e.Provider.Label} ({e.At:yyyy-MM-dd HH:mm} UTC)");
            }
            // The next non-provider page after the provider must be this application (another application's return never counts).
            if (others.FirstOrDefault(v => v.At > e.At) is not { } next || !scope.Contains(next.Origin, next.Path)) continue;
            if (level < BrowserAuthEvidenceLevel.AuthenticatedReturnObserved)
            {
                level = BrowserAuthEvidenceLevel.AuthenticatedReturnObserved; last = next.At;
                steps.Add($"{e.Provider.Label} → {scope.Display}{(next.Path.Length > scope.PathPrefix.Length ? next.Path[scope.PathPrefix.TrimEnd('/').Length..] : "")} ({next.At:yyyy-MM-dd HH:mm} UTC)");
            }
            var session = endpoints.Where(x => x.LastObservedAt >= next.At && scope.Contains($"{x.Scheme}://{x.Host}{(x.Port is 443 or 80 ? "" : $":{x.Port}")}", x.Path)
                    && x.LastStatus is >= 200 and < 300 && x.Category != ObservedTrafficCategory.StaticAsset
                    && (x.AuthObserved || sessionPathSuffixes.Any(s => x.Path.EndsWith(s, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(x => x.LastObservedAt).FirstOrDefault();
            if (session is not null && level < BrowserAuthEvidenceLevel.AuthenticatedSessionObserved)
            {
                level = BrowserAuthEvidenceLevel.AuthenticatedSessionObserved; last = session.LastObservedAt;
                steps.Add($"Authenticated application request {session.Method} {session.Path} → {session.LastStatus}{(session.AuthObserved ? " (bearer present, value never captured)" : "")}");
            }
            if (level == BrowserAuthEvidenceLevel.AuthenticatedSessionObserved && last is { } sessionAt)
            {
                var lost = endpoints.Where(x => x.LastObservedAt > sessionAt && x.LastStatus == 401 && scope.Contains($"{x.Scheme}://{x.Host}{(x.Port is 443 or 80 ? "" : $":{x.Port}")}", x.Path))
                    .Select(x => x.LastObservedAt)
                    .Concat(providerEvents.Where(p => p.At > sessionAt && (p.Path.Contains("logout", StringComparison.OrdinalIgnoreCase) || p.Path.Contains("endsession", StringComparison.OrdinalIgnoreCase))).Select(p => p.At))
                    .OrderBy(t => t).FirstOrDefault();
                if (lost != default)
                {
                    level = BrowserAuthEvidenceLevel.LoggedOut; last = lost;
                    steps.Add($"Authentication lost or logged out ({lost:yyyy-MM-dd HH:mm} UTC)");
                }
            }
        }

        var elsewhere = providerSeen && level == BrowserAuthEvidenceLevel.Unknown;
        if (level == BrowserAuthEvidenceLevel.Unknown) return BrowserAuthEvidenceResult.None with { ProviderObservedElsewhere = elsewhere };
        var historical = last is { } at && now - at > (historicalAfter ?? TimeSpan.FromHours(12));
        return new(historical ? BrowserAuthEvidenceLevel.Historical : level, level, provider, last, steps, false);
    }

    /// <summary>"Authenticated session observed" / "Historical · Authenticated session observed".</summary>
    public static string Label(BrowserAuthEvidenceResult result) =>
        result.Level == BrowserAuthEvidenceLevel.Historical ? $"Historical · {InteractionAuthRules.LevelLabel(result.ObservedLevel)}" : InteractionAuthRules.LevelLabel(result.Level);
}
