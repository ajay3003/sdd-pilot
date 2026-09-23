using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// Approach 2, exactly: Playwright owns the browser.
///
/// <c>Chromium.LaunchPersistentContextAsync</c> with <c>Channel = "msedge"</c> starts the installed Microsoft Edge
/// against a dedicated profile directory and hands Playwright the context. This is deliberately NOT
/// <c>ConnectOverCDPAsync</c>: attaching to a browser somebody else started is a different question with a different
/// failure mode, and mixing the two would make the result unreadable.
///
/// The only difference between headed and headless is <c>Headless</c>. No extra Chromium flags are invented for the
/// headless case — the point of the run is to observe the default behaviour of an ordinary automated Edge, not to
/// coax a particular outcome out of it.
///
/// Nothing here signs in, and nothing here changes a browser or system setting. It opens pages and reads from them.
/// </summary>
internal sealed class PlaywrightDiagnosticBrowser : IDiagnosticBrowser
{
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private int _disposed;

    public string? EdgeVersion { get; private set; }
    public bool HasPersistentContext => _context is not null;

    public async Task LaunchAsync(BrowserAutomationDiagnosticLaunchOptions options, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(options.ProfileDirectory);
        _playwright = await Playwright.CreateAsync();

        _context = await _playwright.Chromium.LaunchPersistentContextAsync(options.ProfileDirectory, new()
        {
            Channel = "msedge",
            Headless = options.Headless,
            Timeout = (float)options.Timeout.TotalMilliseconds,
            // First-run and default-browser prompts only; nothing that weakens a browser protection.
            Args = ["--no-first-run", "--no-default-browser-check"],
        });

        EdgeVersion = _context.Browser?.Version;
        // A persistent context opens with one page; use it rather than adding a second.
        _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
        await _page.GotoAsync("about:blank", new() { Timeout = (float)options.Timeout.TotalMilliseconds });
        ct.ThrowIfCancellationRequested();
    }

    public async Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = _page ?? throw new InvalidOperationException("The diagnostic browser has not been launched.");
        await page.GotoAsync(url.AbsoluteUri, new()
        {
            Timeout = (float)timeout.TotalMilliseconds,
            // Commit, not load: the question is whether automation SURVIVES the navigation, and waiting for a full
            // load would attribute a slow target to the automation restriction being tested for.
            WaitUntil = WaitUntilState.Commit,
        });
    }

    /// <summary>
    /// Reads the page title. Read-only, needs no DOM contract with the application, and — the point — it goes through
    /// the automation channel, so a page or context that has been closed underneath us throws instead of lying.
    /// </summary>
    public async Task<bool> IsControllableAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = _page;
        if (page is null || page.IsClosed) return false;
        // TitleAsync takes no timeout of its own, and a probe that hangs is the failure mode this stage exists to
        // avoid — so the wait is bounded here instead.
        await page.TitleAsync().WaitAsync(timeout, ct);
        return !page.IsClosed;
    }

    /// <summary>
    /// Closes what this diagnostic created, and only that. No process is killed by name: a stray
    /// <c>msedge.exe</c> sweep would take the user's own browser, the Local HTTPS proxy's Edge, the Browser
    /// Companion's session and the other mode's browser with it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (_page is { IsClosed: false }) await _page.CloseAsync(); } catch { }
        try { if (_context is not null) await _context.CloseAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        _page = null;
        _context = null;
        _playwright = null;
    }
}

internal sealed class PlaywrightDiagnosticBrowserFactory : IDiagnosticBrowserFactory
{
    public IDiagnosticBrowser Create(BrowserAutomationDiagnosticMode mode) => new PlaywrightDiagnosticBrowser();
}
