using System.Collections.Concurrent;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.HeadlessAuthDiagnostic;

internal sealed record NavigationSignal(HeadlessNavigation Navigation, bool Authentication, bool Entra, bool Application, bool SessionControl);

/// <summary>Safe identifiers parsed from a Microsoft Entra error page. Only these values ever leave the page text.</summary>
internal sealed record EntraErrorEvidence(string? Code, string? CorrelationId, string? RequestId, string? Timestamp)
{
    private static readonly System.Text.RegularExpressions.Regex CodePattern = new(@"\bAADSTS(\d{5,6})\b");
    private const string Guid = @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})";
    private static readonly System.Text.RegularExpressions.Regex Correlation = new(@"Correlation\s*ID\s*:\s*" + Guid, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex Request = new(@"Request\s*ID\s*:\s*" + Guid, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex Time = new(@"Timestamp\s*:\s*(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses only fixed-shape values: an AADSTS number, GUIDs and an ISO timestamp. The AADSTS code is language
    /// independent; the id labels are the English ones Entra prints, so a localised page may yield the code only —
    /// and then the ids are Unknown rather than guessed. Null when nothing recognisable is present.
    /// </summary>
    public static EntraErrorEvidence? Parse(string? pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText)) return null;
        var code = CodePattern.Match(pageText) is { Success: true } c ? "AADSTS" + c.Groups[1].Value : null;
        var correlation = Correlation.Match(pageText) is { Success: true } k ? k.Groups[1].Value.ToLowerInvariant() : null;
        var request = Request.Match(pageText) is { Success: true } r ? r.Groups[1].Value.ToLowerInvariant() : null;
        var time = Time.Match(pageText) is { Success: true } t ? t.Groups[1].Value : null;
        return code is null && correlation is null && request is null ? null : new(code, correlation, request, time);
    }
}
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
    /// <summary>Identifiers from an Entra error page. Accepted by the reducer only when the page was on Microsoft Entra.</summary>
    public EntraErrorEvidence? EntraError { get; init; }
}

/// <summary>
/// The Entra DOM markers the headless diagnostic recognises, by id and input semantics first — they survive
/// translation — with narrow English text only as a fallback. Kept as data so the list is reviewable and tested.
/// </summary>
internal static class EntraSignals
{
    /// <summary>First-factor / account-selection controls: interactive sign-in, NOT MFA.</summary>
    public static readonly string[] InteractiveSignIn =
        ["input[type=password]", "input[name=loginfmt]", "input[autocomplete=username]", "#tilesHolder .tile"];

    /// <summary>Second-factor challenges: OTP entry, Authenticator approval / number matching, proof selection.</summary>
    public static readonly string[] MfaChallenge =
    [
        "input[name=otc]", "input[autocomplete=one-time-code]", "#idTxtBx_SAOTCC_OTC",
        "#idDiv_SAOTCAS_Description", "#idDiv_SAOTCC_Description", "#idRichContext_DisplaySign",
        "#idDiv_SAASDS_Description", "#idDiv_SAASTO_Description", "#idDiv_SAOTCS_Proofs",
    ];
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
/// <summary>The headless target navigation timeout, shared with the failure evidence that reports it.</summary>
internal static class HeadlessBrowserNavigation { public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20); }
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
    /// <summary>Pages the target opened (MSAL uses a sign-in popup). The authentication handoff happens there.</summary>
    private readonly List<IPage> _popups = [];
    private int _disposed;
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
        _context.Page += OnPopup;
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
        if (_page is null || frame != _page.MainFrame) return;
        Enqueue(frame.Url, secondary: false);
    }

    private void OnPopup(object? sender, IPage popup)
    {
        if (_disposed != 0 || ReferenceEquals(popup, _page)) return;
        lock (_popups) _popups.Add(popup);
        popup.FrameNavigated += (_, frame) => { if (frame.ParentFrame is null) Enqueue(frame.Url, secondary: true); };
        Enqueue(popup.Url, secondary: true);
    }

    private void Enqueue(string raw, bool secondary)
    {
        if (_disposed != 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return;
        if (Interlocked.Increment(ref _navigationCount) <= 100)
            _navigation.Enqueue(new(new(DateTimeOffset.UtcNow, HeadlessEvidenceSanitizer.Url(raw), secondary), IsAuthority(url), IsEntra(url),
                // A popup on the target origin is the sign-in callback, not the application.
                !secondary && AuthenticationOriginPolicy.SameOrigin(url, _target), IsSessionHost(url)));
    }

    /// <summary>The page to observe: the most recent open sign-in popup while one exists, otherwise the diagnostic's page.</summary>
    private IPage ActivePage()
    {
        lock (_popups)
            return _popups.LastOrDefault(p => !p.IsClosed) ?? _page!;
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
        await _page!.GotoAsync(request.TargetUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = (float)HeadlessBrowserNavigation.Timeout.TotalMilliseconds }).WaitAsync(ct);
    }
    public async Task<HeadlessObservation> ObserveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = ActivePage();
        try { return await ObservePageAsync(page, ct); }
        // An MSAL popup closes itself when sign-in hands back to the application. That is the flow finishing, not the
        // automation being lost — so it is observed again on the diagnostic's own page, which must still answer.
        catch (PlaywrightException ex) when (!ReferenceEquals(page, _page) && page.IsClosed
            && BrowserAutomationDiagnostic.BrowserAutomationDiagnosticExceptionClassifier.Classify(ex) == BrowserAutomationDiagnostic.BrowserAutomationFailureKind.TargetClosed)
        {
            return await ObservePageAsync(_page!, ct);
        }
        // The page navigated while it was being read (Entra → session control is a chain of redirects). The read is
        // simply repeated on the next tick; the navigations themselves were recorded, and nothing is concluded.
        catch (PlaywrightException ex) when (IsNavigationRace(ex))
        {
            return new HeadlessObservation { Navigations = DrainNavigation(), ProductionNavigationBlocked = _productionBlocked };
        }
    }

    /// <summary>
    /// A read that lost its document to a navigation. Recognised by Playwright's own wording for exactly that race —
    /// never used for a closed page, which remains lost control.
    /// </summary>
    internal static bool IsNavigationRace(Exception ex) =>
        ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("because of a navigation", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase);

    private async Task<HeadlessObservation> ObservePageAsync(IPage page, CancellationToken ct)
    {
        var url = new Uri(page.Url);
        var entra = IsEntra(url);
        var identity = IsAuthority(url);
        var application = ReferenceEquals(page, _page) && AuthenticationOriginPolicy.SameOrigin(url, _target);
        var strongSession = IsSessionHost(url);
        // Only boolean evidence leaves the page. No values, HTML, text, headers, cookies, bodies or storage are collected.
        var signals = await page.EvaluateAsync<bool[]>("""
            ([login, mfa]) => {
              const visible = e => !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
              const has = s => [...document.querySelectorAll(s)].some(visible);
              const text = document.body?.innerText || '';
              return [
                has(login),
                has(mfa) || /approve (the )?sign[ -]in request|enter (the )?code (from|displayed in) (your |the )?(microsoft )?authenticator/i.test(text),
                /conditional access/i.test(text),
                /AADSTS53003\b|error code\s*:\s*53003\b/i.test(text),
                /defender for cloud apps|conditional access app control/i.test(text),
                /(?:headless|automated) browser.{0,60}(?:not supported|not allowed|blocked)/i.test(text)
              ];
            }
            """, new[] { string.Join(", ", EntraSignals.InteractiveSignIn), string.Join(", ", EntraSignals.MfaChallenge) })
            .WaitAsync(TimeSpan.FromSeconds(5), ct);
        // Error identifiers are read ONLY on a Microsoft Entra host, and only the fixed-shape values are kept.
        var entraError = entra
            ? EntraErrorEvidence.Parse(await page.EvaluateAsync<string>("() => (document.body?.innerText || '').slice(0, 8000)").WaitAsync(TimeSpan.FromSeconds(5), ct))
            : null;
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
            // A session-control proxy serves the identity provider's own sign-in and MFA pages under its host, so
            // visible credential or challenge controls there count exactly as they would on Entra itself.
            InteractiveLogin = signals[0] && (identity || application || strongSession), MfaChallenge = signals[1] && (identity || strongSession),
            ConditionalAccessSignal = signals[2] && entra, ConditionalAccessBlock = signals[3] && entra,
            StrongSessionControl = strongSession, PossibleSessionControl = signals[4],
            ExplicitHeadlessRestriction = strongSession && signals[5], AuthenticatedShell = shell,
            VerificationConfigured = configured, ProductionNavigationBlocked = _productionBlocked, EntraError = entraError
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
        Interlocked.Exchange(ref _disposed, 1);
        if (_page is not null) _page.FrameNavigated -= OnNavigation;
        if (_context is not null) _context.Page -= OnPopup;
        try { if (_context is not null) await _context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { _playwright?.Dispose(); _context = null; _page = null; _playwright = null; }
    }
}
