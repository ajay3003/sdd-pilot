using System.Collections.Concurrent;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.HeadlessAuthDiagnostic;

internal sealed record NavigationSignal(HeadlessNavigation Navigation, bool Authentication, bool Entra, bool Application, bool SessionControl);
internal sealed record HeadlessObservation
{
    public List<NavigationSignal> Navigations { get; init; } = [];
    public bool AtApplication { get; init; }
    public bool AtIdentityProvider { get; init; }
    public bool Entra { get; init; }
    public bool InteractiveLogin { get; init; }
    public bool MfaChallenge { get; init; }
    public bool ConditionalAccessSignal { get; init; }
    public bool ConditionalAccessBlock { get; init; }
    public bool StrongSessionControl { get; init; }
    public bool PossibleSessionControl { get; init; }
    public bool ExplicitHeadlessRestriction { get; init; }
    public bool AuthenticatedShell { get; init; }
    public bool VerificationConfigured { get; init; }
    public bool ProductionNavigationBlocked { get; init; }
}
internal interface IHeadlessBrowser : IAsyncDisposable
{
    string? EdgeVersion { get; }
    Task LaunchAsync(string profile, CancellationToken ct);
    Task<bool> ControlAsync(CancellationToken ct);
    Task NavigateAsync(CancellationToken ct);
    Task<HeadlessObservation> ObserveAsync(CancellationToken ct);
    List<NavigationSignal> DrainNavigation() => [];
}
internal interface IHeadlessBrowserFactory { IHeadlessBrowser Create(HeadlessDiagnosticRequest request); }
internal sealed class PlaywrightHeadlessBrowserFactory(Microsoft.Extensions.Options.IOptions<HeadlessDiagnosticOptions> options) : IHeadlessBrowserFactory
{
    public IHeadlessBrowser Create(HeadlessDiagnosticRequest request) => new PlaywrightHeadlessBrowser(request, options.Value.Verification.GetValueOrDefault(request.TargetEnvironmentId));
}

internal sealed class PlaywrightHeadlessBrowser(HeadlessDiagnosticRequest request, HeadlessVerificationContract? verification) : IHeadlessBrowser
{
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly ConcurrentQueue<NavigationSignal> _navigation = new();
    private readonly Uri _target = new(request.TargetUrl);
    private bool _productionBlocked;
    private int _navigationCount;
    public string? EdgeVersion => _context?.Browser?.Version;
    internal static BrowserTypeLaunchPersistentContextOptions LaunchOptions => new()
    {
        Channel = "msedge", Headless = true, ChromiumSandbox = true, Timeout = 30000
    };
    public async Task LaunchAsync(string profile, CancellationToken ct)
    {
        if (!HeadlessDiagnosticPolicy.IsDedicatedProfile(profile) || Directory.Exists(profile))
            throw new InvalidOperationException("Fresh diagnostic profile required.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(profile);
        var creation = Playwright.CreateAsync();
        try { _playwright = await creation.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch
        {
            // If driver startup finishes after cancellation/timeout, dispose that owned driver too.
            _ = creation.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(profile, LaunchOptions);
        _context.SetDefaultTimeout(5000);
        _context.SetDefaultNavigationTimeout(20000);
        await _context.RouteAsync("**/*", async route =>
        {
            if (route.Request.IsNavigationRequest && Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var destination)
                && TargetEnvironmentDetection.TargetEnvironmentTypeClassifier.Infer(destination.IdnHost) == Models.FrontendEnvironmentType.Production)
            {
                _productionBlocked = true;
                await route.AbortAsync();
            }
            else await route.ContinueAsync();
        });
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        _page.FrameNavigated += OnNavigation;
        await _page.GotoAsync("about:blank");
        ct.ThrowIfCancellationRequested();
    }
    private bool IsEntra(Uri uri) => uri.Scheme == "https" && uri.IdnHost.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
    private bool IsAuthority(Uri uri) => IsEntra(uri) ||
        (Uri.TryCreate(request.Authority, UriKind.Absolute, out var authority) && AuthenticationOriginPolicy.SameOrigin(uri, authority));
    internal static bool IsSessionHost(Uri uri) => uri.Scheme == "https" &&
        new[] { ".mcas.ms", ".mcas-gov.us", ".mcas-gov.ms" }.Any(s => uri.IdnHost.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    private void OnNavigation(object? sender, IFrame frame)
    {
        if (_page is null || frame != _page.MainFrame || !Uri.TryCreate(frame.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return;
        if (Interlocked.Increment(ref _navigationCount) <= 100)
            _navigation.Enqueue(new(new(DateTimeOffset.UtcNow, HeadlessEvidenceSanitizer.Url(frame.Url)), IsAuthority(url), IsEntra(url), AuthenticationOriginPolicy.SameOrigin(url, _target), IsSessionHost(url)));
    }
    public async Task<bool> ControlAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_page is null || _page.IsClosed) return false;
        await _page.TitleAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await _page.Locator("html").CountAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        return !_page.IsClosed;
    }
    public async Task NavigateAsync(CancellationToken ct)
    {
        await _page!.GotoAsync(request.TargetUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 20000 }).WaitAsync(ct);
    }
    public async Task<HeadlessObservation> ObserveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = _page!;
        var url = new Uri(page.Url);
        var entra = IsEntra(url);
        var identity = IsAuthority(url);
        var application = AuthenticationOriginPolicy.SameOrigin(url, _target);
        var strongSession = IsSessionHost(url);
        // Only boolean evidence leaves the page. No values, HTML, text, headers, cookies, bodies or storage are collected.
        var signals = await page.EvaluateAsync<bool[]>("""
            () => {
              const visible = e => !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
              const has = s => [...document.querySelectorAll(s)].some(visible);
              const text = document.body?.innerText || '';
              return [
                has('input[type=password], input[name=loginfmt], input[autocomplete=username], #tilesHolder .tile'),
                has('input[name=otc], input[autocomplete=one-time-code], #idDiv_SAOTCAS_Description, #idDiv_SAOTCC_Description') || /approve (the )?sign[ -]in request|enter (the )?code (from|displayed in) (your |the )?(microsoft )?authenticator/i.test(text),
                /conditional access/i.test(text),
                /AADSTS53003\b|error code\s*:\s*53003\b/i.test(text),
                /defender for cloud apps|conditional access app control/i.test(text),
                /(?:headless|automated) browser.{0,60}(?:not supported|not allowed|blocked)/i.test(text)
              ];
            }
            """).WaitAsync(TimeSpan.FromSeconds(5), ct);
        var configured = verification is { AuthenticatedSelector.Length: > 0 }
            && Uri.TryCreate(verification.TargetOrigin, UriKind.Absolute, out var expected)
            && AuthenticationOriginPolicy.SameOrigin(expected, _target);
        var shell = false;
        if (application && !identity && !signals[0] && configured)
            shell = await page.Locator(verification!.AuthenticatedSelector).First.IsVisibleAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        var trace = DrainNavigation();
        return new()
        {
            Navigations = trace, AtApplication = application, AtIdentityProvider = identity,
            Entra = entra || (identity && request.AuthenticationType == "MicrosoftEntraId"),
            InteractiveLogin = signals[0] && (identity || application), MfaChallenge = signals[1] && identity,
            ConditionalAccessSignal = signals[2] && entra, ConditionalAccessBlock = signals[3] && entra,
            StrongSessionControl = strongSession, PossibleSessionControl = signals[4],
            ExplicitHeadlessRestriction = strongSession && signals[5], AuthenticatedShell = shell,
            VerificationConfigured = configured, ProductionNavigationBlocked = _productionBlocked
        };
    }
    public List<NavigationSignal> DrainNavigation()
    {
        var trace = new List<NavigationSignal>();
        while (_navigation.TryDequeue(out var navigation)) trace.Add(navigation);
        return trace;
    }
    public async ValueTask DisposeAsync()
    {
        if (_page is not null) _page.FrameNavigated -= OnNavigation;
        try { if (_context is not null) await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { _playwright?.Dispose(); _context = null; _page = null; _playwright = null; }
    }
}
