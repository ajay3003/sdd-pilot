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

        public Task<ManagedEdgeProbeResult> FetchAsync(string origin, string path, string? query) => _page.EvaluateAsync<ManagedEdgeProbeResult>(
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
            """, new { origin, path, query });
    }
}
