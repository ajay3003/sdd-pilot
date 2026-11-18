using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Network isolation for the repository browser tests (the C# counterpart of <c>acceptance-network-guard.cjs</c>): every request a fixture
/// browser context makes must go to the BirkNext under test (its frontend and backend origins). Anything else — a real target such as
/// m2lbdev, Altinn, TT02, a CDN or the developer's own BirkNext on 5000/5173 — is aborted before it is sent and recorded as a violation, and
/// the fixture fails the test. Tests that need a target answer it with page routes (which take precedence over this context route) and synthetic
/// responses; nothing is ever fetched from the network for them.
/// </summary>
public sealed class BrowserNetworkGuard
{
    private readonly HashSet<string> _allowed;
    private readonly List<string> _violations = [];

    private BrowserNetworkGuard(IEnumerable<string> allowedOrigins) =>
        _allowed = allowedOrigins.Select(Origin).Where(o => o is not null).Select(o => o!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests that were blocked (method and URL without query), in order.</summary>
    public IReadOnlyList<string> Violations { get { lock (_violations) return _violations.ToList(); } }

    public static async Task<BrowserNetworkGuard> InstallAsync(IBrowserContext context, params string[] allowedOrigins)
    {
        var guard = new BrowserNetworkGuard(allowedOrigins.Length > 0 ? allowedOrigins : [BrowserTestServers.FrontendUrl, BrowserTestServers.BackendUrl]);
        await context.RouteAsync("**/*", async route =>
        {
            if (guard.IsAllowed(route.Request.Url)) { await route.FallbackAsync(); return; }
            var url = route.Request.Url;
            var withoutQuery = url.Split('?', 2)[0];
            lock (guard._violations) guard._violations.Add($"{route.Request.Method} {withoutQuery}");
            await route.AbortAsync("blockedbyclient");
        });
        return guard;
    }

    public bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is "data" or "blob" or "about") return true;
        return Origin(url) is { } origin && _allowed.Contains(origin);
    }

    /// <summary>Throws when anything was blocked — a test must never reach beyond the isolated BirkNext.</summary>
    public void AssertNoViolations()
    {
        var violations = Violations;
        if (violations.Count > 0)
            throw new InvalidOperationException($"The browser tried to reach {violations.Count} request(s) outside the isolated BirkNext (blocked): {string.Join("; ", violations.Take(10))}");
    }

    /// <summary>scheme://host:port with loopback names unified (localhost, 127.0.0.1 and [::1] are the same machine).</summary>
    private static string? Origin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IsLoopback ? "loopback" : uri.Host.ToLowerInvariant();
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
