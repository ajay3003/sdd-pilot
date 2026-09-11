using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ManagedEdge;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.ManagedEdge;

public interface IManagedEdgeBrowser : IDisposable
{
    bool IsConnected { get; }
    int ContextCount { get; }
    /// <summary>Pages the browser allowed the debugger to attach to.</summary>
    IReadOnlyList<IManagedEdgePage> Pages { get; }
    /// <summary>URLs of every page target advertised by /json/list at connect time, including tabs the browser refuses to expose for inspection.</summary>
    IReadOnlyList<string> DiscoveredPageUrls { get; }
}

public interface IManagedEdgePage
{
    string Url { get; }
    bool IsClosed { get; }
    bool NavigationChanged { get; }
    Task<bool> HasAuthenticatedElementAsync(string origin, string selector);
    Task<ManagedEdgeProbeResult> FetchAsync(string origin, string path, string? query);
    /// <summary>Live document origin of the page (location.origin), used to bind a proxied delivery to the page context.</summary>
    Task<string?> GetLocationOriginAsync();
    /// <summary>Origins (scheme://host[:port] only) of the tab's navigation history. Paths, queries and fragments are discarded before leaving the browser.</summary>
    Task<IReadOnlyList<string>> GetNavigationOriginsAsync();
}

public interface IManagedEdgeConnector
{
    Task<IManagedEdgeBrowser> ConnectAsync(string endpoint, CancellationToken cancellationToken);
}

public sealed class ManagedEdgeConnector : IManagedEdgeConnector
{
    public async Task<IManagedEdgeBrowser> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        var local = ManagedEdgePolicy.Endpoint(endpoint);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 16384 };
        using var response = await http.GetAsync(new Uri(local, "/json/version"), cancellationToken);
        // Reject all redirects, including local redirects. Never hand an unchecked HTTP endpoint to Playwright.
        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var websocket = ManagedEdgePolicy.Endpoint(version.GetProperty("webSocketDebuggerUrl").GetString()!, true);
        if (websocket.Port != local.Port || websocket.Host != local.Host)
            throw new ArgumentException("CDP advertised a different endpoint.");
        var discovered = await DiscoverPageUrlsAsync(http, local, cancellationToken);
        var playwright = await Playwright.CreateAsync();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var browser = await playwright.Chromium.ConnectOverCDPAsync(websocket.AbsoluteUri, new() { Timeout = 5000 });
            cancellationToken.ThrowIfCancellationRequested();
            return new AttachedBrowser(playwright, browser, discovered);
        }
        catch { playwright.Dispose(); throw; }
    }

    // Target URLs only, for origin comparison. Titles, target ids and page sockets are never retained.
    private static async Task<IReadOnlyList<string>> DiscoverPageUrlsAsync(HttpClient http, Uri local, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(local, "/json/list"), cancellationToken);
            if (!response.IsSuccessStatusCode) return [];
            var targets = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (targets.ValueKind != JsonValueKind.Array) return [];
            return targets.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object &&
                    t.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "page" &&
                    t.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                .Select(t => t.GetProperty("url").GetString()!).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { return []; }
    }

    private sealed class AttachedBrowser(IPlaywright playwright, IBrowser browser, IReadOnlyList<string> discovered) : IManagedEdgeBrowser
    {
        private readonly Dictionary<IPage, IManagedEdgePage> _pages = [];
        public bool IsConnected => browser.IsConnected;
        public int ContextCount => browser.Contexts.Count;
        public IReadOnlyList<string> DiscoveredPageUrls => discovered;
        public IReadOnlyList<IManagedEdgePage> Pages => browser.Contexts.SelectMany(c => c.Pages).Select(p =>
        {
            if (!_pages.TryGetValue(p, out var page)) _pages[p] = page = new AttachedPage(p);
            return page;
        }).ToArray();
        // Dispose only the protocol transport. Never close the user's browser, contexts or pages.
        public void Dispose() => playwright.Dispose();
    }

    private sealed class AttachedPage : IManagedEdgePage
    {
        private readonly IPage _page;
        private bool _navigated;
        public AttachedPage(IPage page)
        {
            _page = page;
            page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) _navigated = true; };
        }
        public string Url => _page.Url;
        public bool IsClosed => _page.IsClosed;
        public bool NavigationChanged => _navigated;
        public Task<bool> HasAuthenticatedElementAsync(string origin, string selector) => _page.EvaluateAsync<bool>(
            """
            ({origin, selector}) => {
                if (location.origin !== origin) return false;
                const elements = document.querySelectorAll(selector);
                return [...elements].some(e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden');
            }
            """, new { origin, selector });

        public async Task<string?> GetLocationOriginAsync()
        {
            var value = await _page.EvaluateAsync<string>("() => location.origin");
            return string.IsNullOrWhiteSpace(value) || value == "null" ? null : value;
        }

        // Page.getNavigationHistory returns full URLs; only their origins are kept. Nothing else from the CDP session is read.
        public async Task<IReadOnlyList<string>> GetNavigationOriginsAsync()
        {
            var session = await _page.Context.NewCDPSessionAsync(_page);
            try
            {
                var history = await session.SendAsync("Page.getNavigationHistory");
                if (history is not { } element || !element.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return [];
                return entries.EnumerateArray()
                    .Select(e => e.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null)
                    .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null)
                    .Where(o => o is not null).Select(o => o!).ToArray();
            }
            finally { await session.DetachAsync(); }
        }

        // Playwright .NET cannot materialize a positional record from a JS object; read a JsonElement and map the three allowed fields only.
        public async Task<ManagedEdgeProbeResult> FetchAsync(string origin, string path, string? query)
        {
            var raw = await _page.EvaluateAsync<JsonElement>(FetchScript, new { origin, path, query });
            if (raw.ValueKind != JsonValueKind.Object ||
                !raw.TryGetProperty("StatusCode", out var status) || !status.TryGetInt32(out var statusCode) ||
                !raw.TryGetProperty("ContentType", out var contentType) || contentType.ValueKind != JsonValueKind.String ||
                !raw.TryGetProperty("ElapsedMs", out var elapsed) || !elapsed.TryGetDouble(out var elapsedMs))
                throw new InvalidOperationException("Probe result has an unexpected shape.");
            var mime = contentType.GetString()!;
            return new ManagedEdgeProbeResult(statusCode, mime.Length <= 64 ? mime : "other", elapsedMs);
        }

        private const string FetchScript =
            """
            async ({origin, path, query}) => {
                if (location.origin !== origin) throw new Error('Origin changed');
                const target = new URL(path, origin);
                if (target.origin !== origin) throw new Error('Cross-origin request rejected');
                const start = performance.now();
                const response = await fetch(target.href, {
                    method: query === null ? 'GET' : 'POST',
                    ...(query === null ? {} : {headers: {'Content-Type':'application/json'}, body: JSON.stringify({query})}),
                    credentials: 'same-origin', mode: 'same-origin', redirect: 'error', cache: 'no-store',
                    referrerPolicy: 'no-referrer', signal: AbortSignal.timeout(5000)
                });
                const mime = (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
                const safeMime = ['application/json','application/graphql-response+json','text/html','text/plain'].includes(mime) ? mime : 'other';
                if (response.body) await response.body.cancel();
                return {StatusCode: response.status, ContentType: safeMime, ElapsedMs: Math.round(performance.now()-start)};
            }
            """;
    }
}
