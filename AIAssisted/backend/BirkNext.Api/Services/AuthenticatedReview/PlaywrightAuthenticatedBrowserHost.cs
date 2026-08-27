using Microsoft.Playwright;

namespace BirkNext.Api.Services.AuthenticatedReview;

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
            browser = await playwright.Chromium.LaunchAsync(CreateLaunchOptions());
            context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync(target.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
            return new Resources(playwright, browser, context, page);
        }
        catch
        {
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright.Dispose();
            throw;
        }
    }

    internal static BrowserTypeLaunchOptions CreateLaunchOptions() => new()
    {
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
