using System.Diagnostics;
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
/// Nothing here signs in, and nothing here changes a browser or system setting. It opens pages and reads from them,
/// and it LISTENS: page close, page crash, context close and browser disconnect are recorded as they happen, because
/// a page that closes a few seconds after it loaded is the failure a single immediate probe cannot see.
/// </summary>
internal sealed class PlaywrightDiagnosticBrowser : IDiagnosticBrowser
{
    /// <summary>A redirect loop must not grow the trace without bound.</summary>
    private const int MaxTraceEntries = 50;

    private readonly object _gate = new();
    private readonly List<DiagnosticNavigationObservation> _trace = [];
    private readonly List<DiagnosticLifecycleObservation> _events = [];
    private readonly List<IPage> _secondaryPages = [];
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stopwatch _clock = Stopwatch.StartNew();
    private long _lastNavigationAtMs;

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private int _disposed;

    public string? EdgeVersion { get; private set; }
    public bool HasPersistentContext => _context is not null;

    public IReadOnlyList<DiagnosticNavigationObservation> NavigationTrace { get { lock (_gate) return [.. _trace]; } }
    public IReadOnlyList<DiagnosticLifecycleObservation> LifecycleEvents { get { lock (_gate) return [.. _events]; } }
    public long ObservationElapsedMs { get { lock (_gate) return _clock.ElapsedMilliseconds; } }

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

        _page.FrameNavigated += OnFrameNavigated;
        _page.Close += OnPageClose;
        _page.Crash += OnPageCrash;
        _context.Close += OnContextClose;
        _context.Page += OnPageOpened;
        // A persistent context may not expose its Browser; when it does, a disconnect is recorded too.
        if (_context.Browser is { } browser) browser.Disconnected += OnBrowserDisconnected;

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
            // Commit, not load: the navigation call answers "did the browser accept it". What happens next — a
            // script redirect, a close — is observed separately by the settle and stability steps.
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

    public void BeginTargetObservation()
    {
        lock (_gate)
        {
            _trace.Clear();
            _events.Clear();
            _clock = Stopwatch.StartNew();
            _lastNavigationAtMs = 0;
        }
    }

    public async Task<DiagnosticSettleResult> WaitForNavigationToSettleAsync(TimeSpan quietPeriod, TimeSpan bound, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var page = _page;
        if (page is null || page.IsClosed || _terminated.Task.IsCompleted)
            return new(DiagnosticSettleOutcome.Terminated, started.ElapsedMilliseconds);

        // The document the navigation committed to should at least finish loading before quiet is measured; a
        // script-driven identity redirect cannot start before that. Best effort and bounded: a page that never
        // reaches "load" is still observed, and a closed page throws out of here as evidence.
        await WaitForLoadAsync(page, LoadState.Load, Remaining(started, bound));

        var observedNavigation = LastNavigationAtMs();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed || _terminated.Task.IsCompleted)
                return new(DiagnosticSettleOutcome.Terminated, started.ElapsedMilliseconds);

            // The bound is checked first, so a page that keeps navigating (a redirect loop) cannot hold the loop open.
            if (started.Elapsed >= bound) return new(DiagnosticSettleOutcome.BoundReached, started.ElapsedMilliseconds);

            var last = LastNavigationAtMs();
            if (last != observedNavigation)
            {
                // A new document: give it the same bounded chance to load before measuring quiet again.
                observedNavigation = last;
                await WaitForLoadAsync(page, LoadState.Load, Remaining(started, bound));
                continue;
            }

            var quietFor = TimeSpan.FromMilliseconds(ObservationElapsedMs - last);
            if (quietFor >= quietPeriod) return new(DiagnosticSettleOutcome.Settled, started.ElapsedMilliseconds);

            var wait = Min(quietPeriod - quietFor, Remaining(started, bound));
            // Event-driven: a termination ends the wait immediately rather than at the next poll.
            await Task.WhenAny(Task.Delay(wait, ct), _terminated.Task);
        }
    }

    public async Task<bool> ObserveStabilityAsync(TimeSpan window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = _page;
        if (page is null || page.IsClosed || _terminated.Task.IsCompleted) return false;
        await Task.WhenAny(Task.Delay(window, ct), _terminated.Task);
        ct.ThrowIfCancellationRequested();
        return !page.IsClosed && !_terminated.Task.IsCompleted;
    }

    public async Task<DiagnosticPageProbe> ProbeAsync(string? markerSelector, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = _page;
        if (page is null || page.IsClosed) return new(page?.Url ?? "", PageClosed: true, MarkerFound: null);

        // Two reads through the automation channel: the title, and the document element. Neither value is kept.
        await page.TitleAsync().WaitAsync(timeout, ct);
        await page.Locator("html").CountAsync().WaitAsync(timeout, ct);

        bool? marker = null;
        if (!string.IsNullOrWhiteSpace(markerSelector))
        {
            try { marker = await page.Locator(markerSelector).CountAsync().WaitAsync(timeout, ct) > 0; }
            // A marker that cannot be evaluated is an unanswered question, not a control failure. A closed page is
            // still a closed page, though, so that one is allowed to propagate.
            catch (PlaywrightException ex) when (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex)
                                                 != BrowserAutomationFailureKind.TargetClosed) { marker = null; }
        }

        return new(page.Url, page.IsClosed, marker);
    }

    // ── Event handlers: record the kind and the time, never anything from the page ────────────────────────────────

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        if (_disposed != 0 || _page is not { } page || frame != page.MainFrame) return;
        lock (_gate)
        {
            _lastNavigationAtMs = _clock.ElapsedMilliseconds;
            if (_trace.Count < MaxTraceEntries) _trace.Add(new(_lastNavigationAtMs, frame.Url));
        }
    }

    private void OnPageClose(object? sender, IPage page) => Terminated(BrowserAutomationLifecycleEventKind.PageClosed);
    private void OnPageCrash(object? sender, IPage page) => Terminated(BrowserAutomationLifecycleEventKind.PageCrashed);
    private void OnContextClose(object? sender, IBrowserContext context) => Terminated(BrowserAutomationLifecycleEventKind.ContextClosed);
    private void OnBrowserDisconnected(object? sender, IBrowser browser) => Terminated(BrowserAutomationLifecycleEventKind.BrowserDisconnected);
    /// <summary>
    /// A page the target opened — typically an MSAL sign-in popup. Its navigations are traced too (flagged as a
    /// secondary page), because an authentication handoff that happens in a popup is still the handoff, and a trace
    /// that only follows the diagnostic's own page would report "no authentication redirect" while one is on screen.
    /// </summary>
    private void OnPageOpened(object? sender, IPage page)
    {
        Record(BrowserAutomationLifecycleEventKind.PageOpened);
        if (_disposed != 0 || ReferenceEquals(page, _page)) return;
        lock (_gate) _secondaryPages.Add(page);
        page.FrameNavigated += OnSecondaryFrameNavigated;
        TraceSecondary(page.Url);
    }

    private void OnSecondaryFrameNavigated(object? sender, IFrame frame)
    {
        if (_disposed != 0 || frame.ParentFrame is not null) return;
        TraceSecondary(frame.Url);
    }

    private void TraceSecondary(string url)
    {
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        lock (_gate)
            if (_trace.Count < MaxTraceEntries) _trace.Add(new(_clock.ElapsedMilliseconds, url, SecondaryPage: true));
    }

    private void Terminated(BrowserAutomationLifecycleEventKind kind)
    {
        // Our own cleanup closes the page too; that is not evidence about the target.
        if (_disposed != 0) return;
        Record(kind);
        _terminated.TrySetResult();
    }

    private void Record(BrowserAutomationLifecycleEventKind kind)
    {
        if (_disposed != 0) return;
        lock (_gate) _events.Add(new(kind, _clock.ElapsedMilliseconds));
    }

    private long LastNavigationAtMs() { lock (_gate) return _lastNavigationAtMs; }

    private static async Task WaitForLoadAsync(IPage page, LoadState state, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return;
        try { await page.WaitForLoadStateAsync(state, new() { Timeout = (float)remaining.TotalMilliseconds }); }
        catch (Exception ex) when (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex) == BrowserAutomationFailureKind.Timeout)
        {
            // Bounded, not a verdict: a page that never reaches "load" is still observed.
        }
    }

    private static TimeSpan Remaining(Stopwatch started, TimeSpan bound) =>
        bound - started.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Closes what this diagnostic created, and only that. No process is killed by name: a stray
    /// <c>msedge.exe</c> sweep would take the user's own browser, the Local HTTPS proxy's Edge, the Browser
    /// Companion's session and the other mode's browser with it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_page is not null)
        {
            _page.FrameNavigated -= OnFrameNavigated;
            _page.Close -= OnPageClose;
            _page.Crash -= OnPageCrash;
        }
        if (_context is not null)
        {
            _context.Close -= OnContextClose;
            _context.Page -= OnPageOpened;
            lock (_gate) foreach (var secondary in _secondaryPages) secondary.FrameNavigated -= OnSecondaryFrameNavigated;
            if (_context.Browser is { } browser) browser.Disconnected -= OnBrowserDisconnected;
        }
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
