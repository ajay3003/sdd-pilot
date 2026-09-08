using Microsoft.Playwright;

namespace BirkNext.Api.Services.AuthenticatedReview;

/// <summary>
/// Launches Microsoft Edge (via Playwright) for interactive authentication.
///
/// Policy Requirement: Interactive authentication MUST use Microsoft Edge, not bundled Chromium.
///
/// Current implementation:
/// - Launches Edge through Playwright's Chromium.LaunchAsync with Channel = "msedge"
/// - Uses isolated browser context (no personal Edge profile reuse)
/// - No fallback to Chromium if Edge is unavailable (fails closed)
/// - Preserves current navigation/observer lifecycle
/// </summary>
internal sealed class PlaywrightAuthenticatedBrowserHost : IAuthenticatedBrowserHost
{
    public async Task<IAuthenticatedBrowserResources> LaunchAsync(Uri target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        IBrowserContext? context = null;
        try
        {
            // Launch Microsoft Edge through Playwright (not bundled Chromium)
            // Policy: Only Microsoft Edge is acceptable for interactive authentication
            browser = await playwright.Chromium.LaunchAsync(CreateLaunchOptionsForEdge());

            context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            // Do NOT navigate here; navigation will happen in BeginAuthenticationAsync.
            // This avoids double-GotoAsync conflicts when Blazor WASM app is starting up.
            return new Resources(playwright, browser, context, page);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("msedge", StringComparison.OrdinalIgnoreCase))
        {
            // Edge launch failed - fail closed rather than fall back to Chromium
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright.Dispose();

            throw new InvalidOperationException(
                "Microsoft Edge is required for interactive authentication but could not be launched. " +
                "Ensure Microsoft Edge is installed and accessible. " +
                "Bundled Chromium fallback is not permitted.",
                ex);
        }
        catch
        {
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates launch options for Microsoft Edge via Playwright.
    ///
    /// Channel = "msedge" directs Playwright to launch the installed Microsoft Edge browser
    /// instead of bundled Chromium.
    /// </summary>
    internal static BrowserTypeLaunchOptions CreateLaunchOptionsForEdge() => new()
    {
        Channel = "msedge",
        Headless = false,
        Args = ["--no-sandbox", "--disable-dev-shm-usage"]
    };

    private sealed class Resources : IAuthenticatedBrowserResources
    {
        private readonly IPlaywright _playwright;
        private int _disposed;

        public Resources(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
        {
            _playwright = playwright;
            Browser = browser;
            Context = context;
            Page = page;
            Browser.Disconnected += OnDisconnected;
        }

        public IBrowser Browser { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public event EventHandler? BrowserDisconnected;

        private void OnDisconnected(object? sender, IBrowser browser) => BrowserDisconnected?.Invoke(this, EventArgs.Empty);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Browser.Disconnected -= OnDisconnected;
            try { if (!Page.IsClosed) await Page.CloseAsync(); } catch { }
            try { await Context.CloseAsync(); } catch { }
            try { if (Browser.IsConnected) await Browser.CloseAsync(); } catch { }
            _playwright.Dispose();
        }
    }
}
