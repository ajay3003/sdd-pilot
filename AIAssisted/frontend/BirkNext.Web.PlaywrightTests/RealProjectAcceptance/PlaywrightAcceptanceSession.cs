using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance;

/// <summary>
/// Browser + backend driver for real-project acceptance against PRE-STARTED production servers (the run script starts an isolated
/// backend with a fresh database and the frontend). One fresh browser context per run: no developer storage, no previous import.
/// When the frontend is configured for a different backend origin than the acceptance backend, requests are rewritten in the browser
/// (route + fetch), so the production frontend build is used unchanged.
/// </summary>
public sealed class PlaywrightAcceptanceSession : IAsyncDisposable
{
    /// <summary>Texts that would mean an imported project is ignored in favour of the Sample Project catalog.</summary>
    public static readonly string[] SampleProjectCouplingTexts = ["No Sample Project selected", "Select a Sample Project"];

    private readonly IPlaywright playwright;
    public IBrowser Browser { get; }
    public IBrowserContext Context { get; }
    public IPage Page { get; }
    public HttpClient Backend { get; }
    public string FrontendUrl { get; }
    public string ArtifactsDirectory { get; }
    public List<string> PageErrors { get; } = [];
    public List<string> ConsoleErrors { get; } = [];
    public List<string> FailedRequests { get; } = [];
    public string? AxeScriptPath { get; }

    private PlaywrightAcceptanceSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, HttpClient backend, string frontendUrl, string artifacts, string? axe)
    {
        this.playwright = playwright; Browser = browser; Context = context; Page = page; Backend = backend; FrontendUrl = frontendUrl.TrimEnd('/'); ArtifactsDirectory = artifacts; AxeScriptPath = axe;
    }

    public static async Task<PlaywrightAcceptanceSession> StartAsync(string frontendUrl, string backendUrl, string? frontendConfiguredBackendUrl, string artifactsDirectory, string? axeScriptPath)
    {
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new() { Width = 1440, Height = 1000 }, AcceptDownloads = true });
        var backend = new HttpClient { BaseAddress = new Uri(backendUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        var page = await context.NewPageAsync();
        var session = new PlaywrightAcceptanceSession(playwright, browser, context, page, backend, frontendUrl, artifactsDirectory, axeScriptPath);
        page.PageError += (_, error) => session.PageErrors.Add(error.Split('\n')[0]);
        page.Console += (_, message) => { if (message.Type == "error") session.ConsoleErrors.Add(message.Text.Split('\n')[0]); };
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        page.SetDefaultTimeout(60_000);

        if (frontendConfiguredBackendUrl is { Length: > 0 } configured && !SameOrigin(configured, backendUrl))
        {
            var from = new Uri(configured).GetLeftPart(UriPartial.Authority);
            var to = new Uri(backendUrl).GetLeftPart(UriPartial.Authority);
            var frontendOrigin = new Uri(frontendUrl).GetLeftPart(UriPartial.Authority);
            await context.RouteAsync(from + "/**", async route =>
            {
                if (route.Request.Method == "OPTIONS")
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 204, Headers = Cors(frontendOrigin, route.Request) });
                    return;
                }
                var target = to + route.Request.Url[from.Length..];
                try
                {
                    var response = await route.FetchAsync(new RouteFetchOptions { Url = target, Timeout = 600_000 });
                    var headers = response.Headers.ToDictionary(h => h.Key, h => h.Value);
                    foreach (var (k, v) in Cors(frontendOrigin, route.Request)) headers[k] = v;
                    await route.FulfillAsync(new RouteFulfillOptions { Response = response, Headers = headers });
                }
                catch (PlaywrightException ex)
                {
                    session.FailedRequests.Add($"{route.Request.Method} {new Uri(target).AbsolutePath}: {ex.Message.Split('\n')[0]}");
                    await route.AbortAsync();
                }
            });
        }
        return session;
    }

    private static Dictionary<string, string> Cors(string origin, IRequest request) => new()
    {
        ["access-control-allow-origin"] = origin, ["access-control-allow-credentials"] = "true",
        ["access-control-allow-methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS",
        ["access-control-allow-headers"] = request.Headers.TryGetValue("access-control-request-headers", out var h) ? h : "*",
    };

    private static bool SameOrigin(string a, string b) => string.Equals(new Uri(a).GetLeftPart(UriPartial.Authority), new Uri(b).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    // ── navigation & DOM helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Client-side navigation (keeps the WASM app and its in-memory workspace state, like a user clicking the sidebar).</summary>
    public async Task NavigateAsync(string route)
    {
        var path = "/" + route.TrimStart('/');
        var hasBlazor = await Page.EvaluateAsync<bool>("() => !!(window.Blazor && window.Blazor.navigateTo)");
        if (hasBlazor) await Page.EvaluateAsync("p => window.Blazor.navigateTo(p)", path);
        else await Page.GotoAsync(FrontendUrl + path, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.WaitForFunctionAsync("p => location.pathname + location.search === p || location.pathname === p.split('?')[0]", path);
        await Page.WaitForTimeoutAsync(300);
    }

    public async Task<bool> ExistsAsync(string selector, int timeoutMs = 0)
    {
        if (timeoutMs <= 0) return await Page.Locator(selector).CountAsync() > 0;
        try { await Page.Locator(selector).First.WaitForAsync(new() { Timeout = timeoutMs, State = WaitForSelectorState.Attached }); return true; }
        catch (TimeoutException) { return false; }
    }

    public async Task<string?> WaitAnyAsync(int timeoutMs, params string[] selectors)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var selector in selectors)
                if (await Page.Locator(selector).CountAsync() > 0) return selector;
            await Page.WaitForTimeoutAsync(250);
        }
        return null;
    }

    public async Task<string> TextAsync(string selector) => await Page.Locator(selector).CountAsync() > 0 ? (await Page.Locator(selector).First.InnerTextAsync()).Trim() : "";
    public Task<int> CountAsync(string selector) => Page.Locator(selector).CountAsync();
    public async Task<string?> AttrAsync(string selector, string name) => await Page.Locator(selector).CountAsync() > 0 ? await Page.Locator(selector).First.GetAttributeAsync(name) : null;

    /// <summary>Visible text of the main content, used for cross-feature wording checks (Sample Project coupling).</summary>
    public Task<string> BodyTextAsync() => Page.Locator("body").InnerTextAsync();

    public async Task<string?> SampleProjectCouplingAsync()
    {
        var text = await BodyTextAsync();
        return SampleProjectCouplingTexts.FirstOrDefault(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs an export that downloads a file and returns its size and content (for privacy checks); null when no download happened.</summary>
    public async Task<(string FileName, string Content)?> CaptureDownloadAsync(Func<Task> trigger, int timeoutMs = 60_000)
    {
        try
        {
            var download = await Page.RunAndWaitForDownloadAsync(trigger, new() { Timeout = timeoutMs });
            var path = Path.Combine(ArtifactsDirectory, "exports", download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await download.SaveAsAsync(path);
            return (download.SuggestedFilename, await File.ReadAllTextAsync(path));
        }
        catch (TimeoutException) { return null; }
    }

    public async Task<string> ScreenshotAsync(string name)
    {
        var path = Path.Combine(ArtifactsDirectory, "browser", $"{name}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
        return Path.GetRelativePath(ArtifactsDirectory, path);
    }

    public async Task<(int Client, int Scroll)> HorizontalExtentAsync() =>
        await Page.EvaluateAsync<int[]>("() => [document.documentElement.clientWidth, document.documentElement.scrollWidth]") is { Length: 2 } v ? (v[0], v[1]) : (0, 0);

    /// <summary>axe-core WCAG 2.x A/AA violations within a region (ids + node count). References only; never a conformance claim.</summary>
    public async Task<IReadOnlyList<string>> AxeAsync(string include)
    {
        if (AxeScriptPath is null || !File.Exists(AxeScriptPath)) return ["axe-unavailable"];
        if (!await Page.EvaluateAsync<bool>("() => !!window.axe")) await Page.AddScriptTagAsync(new() { Path = AxeScriptPath });
        var json = await Page.EvaluateAsync<JsonElement>(
            "async inc => (await axe.run({ include: [[inc]] }, { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa'] } })).violations.map(v => v.id + ' (' + v.impact + ', ' + v.nodes.length + '): ' + v.nodes.slice(0, 2).map(n => n.target.join(' ')).join('; '))", include);
        return json.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
    }

    // ── backend helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct = default)
    {
        using var response = await Backend.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<(int Status, JsonElement? Body)> PostJsonAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = body is null ? await Backend.PostAsync(path, null, ct) : await Backend.PostAsJsonAsync(path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement? json = null;
        try { if (text.Length > 0) json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
        return ((int)response.StatusCode, json);
    }

    public async ValueTask DisposeAsync()
    {
        Backend.Dispose();
        await Context.CloseAsync();
        await Browser.CloseAsync();
        playwright.Dispose();
    }
}
