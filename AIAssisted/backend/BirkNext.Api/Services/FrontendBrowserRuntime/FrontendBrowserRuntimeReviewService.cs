using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>Single-target runtime observation. Anonymous resources are owned here;
/// authenticated resources are borrowed through a non-owning session lease.</summary>
public sealed class FrontendBrowserRuntimeReviewService : IFrontendBrowserRuntimeReviewService
{
    private readonly ILogger<FrontendBrowserRuntimeReviewService> _logger;
    private readonly BrowserTargetValidator _targetValidator;
    private readonly BrowserRuntimeFindingClassifier _findingClassifier;
    private readonly BrowserEvidenceSanitizer _sanitizer;
    private readonly IAuthenticatedBrowserSessionManager? _authenticatedSessions;

    public FrontendBrowserRuntimeReviewService(ILogger<FrontendBrowserRuntimeReviewService> logger,
        BrowserTargetValidator targetValidator, BrowserRuntimeFindingClassifier findingClassifier,
        BrowserResourceClassifier resourceClassifier, BrowserEvidenceSanitizer sanitizer,
        IAuthenticatedBrowserSessionManager? authenticatedSessions = null)
    {
        _logger = logger; _targetValidator = targetValidator; _findingClassifier = findingClassifier;
        _sanitizer = sanitizer; _authenticatedSessions = authenticatedSessions;
    }

    public Task<BrowserRuntimeResult> ReviewAsync(string targetUrl, BrowserRuntimeOptions? options = null, CancellationToken cancellationToken = default) =>
        ReviewAsync(new BrowserRuntimeExecutionRequest(targetUrl, Options: options), cancellationToken);

    public async Task<BrowserRuntimeResult> ReviewAsync(BrowserRuntimeExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var validation = _targetValidator.ValidateTarget(request.TargetUrl);
        if (!validation.IsValid) return Rejected(request, BrowserRuntimeOutcomeReason.SessionUnavailable, validation.BlockReason ?? "Target validation failed.");
        return request.ExecutionMode == BrowserRuntimeExecutionMode.AuthenticatedSessionPage
            ? await ReviewAuthenticatedAsync(request, cancellationToken)
            : await ReviewAnonymousAsync(request, cancellationToken);
    }

    private async Task<BrowserRuntimeResult> ReviewAnonymousAsync(BrowserRuntimeExecutionRequest request, CancellationToken cancellationToken)
    {
        IPlaywright? playwright = null; IBrowser? browser = null; IBrowserContext? context = null; IPage? page = null;
        try
        {
            var options = request.Options ?? new BrowserRuntimeOptions();
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = options.HeadlessMode, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
            context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = int.Parse(options.ViewportWidth), Height = int.Parse(options.ViewportHeight) } });
            page = await context.NewPageAsync();
            return await ObserveAsync(page, browser.Version, request, navigate: true, "None", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PlaywrightException ex) when (ex.Message.Contains("Chromium") || ex.Message.Contains("executable"))
        { return Rejected(request, BrowserRuntimeOutcomeReason.SessionUnavailable, "Chromium browser not available", BrowserRuntimeEngineStatus.EngineError); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Anonymous browser runtime failed for {TargetOrigin}", SafeOrigin(request.TargetUrl));
            return Rejected(request, BrowserRuntimeOutcomeReason.SessionUnavailable, "Browser Runtime failed.", BrowserRuntimeEngineStatus.EngineError);
        }
        finally
        {
            if (page is not null) await page.CloseAsync();
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    private async Task<BrowserRuntimeResult> ReviewAuthenticatedAsync(BrowserRuntimeExecutionRequest request, CancellationToken cancellationToken)
    {
        if (_authenticatedSessions is null || string.IsNullOrWhiteSpace(request.AuthenticatedSessionId) || string.IsNullOrWhiteSpace(request.ReviewSessionId) || string.IsNullOrWhiteSpace(request.ProfileId))
            return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationRequired, "An authenticated browser session is required.");
        try
        {
            await using var lease = await _authenticatedSessions.AcquirePageLeaseAsync(request.AuthenticatedSessionId, request.ReviewSessionId, request.ProfileId, request.TargetUrl, cancellationToken);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.SessionCancellation);
            var status = await _authenticatedSessions.GetStatusAsync(request.AuthenticatedSessionId, request.ReviewSessionId, request.ProfileId, cancellationToken);
            return await ObserveAsync(lease.Page, lease.Context.Browser?.Version, request, navigate: false, status?.DeliveryContext.ToString() ?? "None", execution.Token);
        }
        catch (AuthenticatedSessionExpiredException) { return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationExpired, "The authenticated session expired."); }
        catch (AuthenticatedSessionNotEligibleException) { return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationRequired, "The authenticated application page is not currently eligible for review."); }
        catch (System.Collections.Generic.KeyNotFoundException) { return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationRequired, "The authenticated session is unavailable."); }
        catch (UnauthorizedAccessException) { return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationRequired, "Authenticated session ownership validation failed."); }
        catch (ObjectDisposedException) { return Rejected(request, BrowserRuntimeOutcomeReason.AuthenticationCancelled, "The authenticated session is closed."); }
    }

    private async Task<BrowserRuntimeResult> ObserveAsync(IPage page, string? browserVersion, BrowserRuntimeExecutionRequest request, bool navigate, string deliveryContext, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow; var options = request.Options ?? new BrowserRuntimeOptions(); var observation = new Observation();
        EventHandler<string> pageError = (_, message) => observation.PageErrors.Add(new(message, page.Url, null));
        EventHandler<IConsoleMessage> console = (_, message) => { if (message.Type is "error" or "warning") observation.ConsoleEvents.Add(new(message.Type, message.Text, null, null, null)); };
        EventHandler<IRequest> failed = (_, req) => observation.ResourceFailures.Add(new(req.Url, req.ResourceType, "Request failed", null));
        EventHandler<IResponse> response = (_, res) => { if (res.Status >= 400) observation.ResourceFailures.Add(new(res.Url, res.Request.ResourceType, $"HTTP {res.Status}", res.Status)); };
        page.PageError += pageError; page.Console += console; page.RequestFailed += failed; page.Response += response;
        try
        {
            if (navigate)
            {
                await page.GotoAsync(request.TargetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = options.NavigationTimeoutMs });
                observation.NavigationDurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            }
            observation.DomContentLoadedReached = await page.EvaluateAsync<bool>("() => ['interactive','complete'].includes(document.readyState)");
            await Task.Delay(options.StartupObservationMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            observation.BlazorDetected = await DetectBlazorAsync(page);
            if (observation.BlazorDetected) observation.BlazorBootstrapCompleted = await IsBlazorBootstrapCompleteAsync(page);
            cancellationToken.ThrowIfCancellationRequested();
            var findings = _findingClassifier.ClassifyObservations(observation.Snapshot());
            var authenticated = request.ExecutionMode == BrowserRuntimeExecutionMode.AuthenticatedSessionPage;
            return new BrowserRuntimeResult(Status: BrowserRuntimeEngineStatus.Assessed, BrowserName: "Chromium", BrowserVersion: browserVersion,
                RequestedUrl: authenticated ? _sanitizer.SanitizeAuthenticatedUrl(request.TargetUrl) : _sanitizer.SanitizeUrl(request.TargetUrl),
                FinalUrl: authenticated ? _sanitizer.SanitizeAuthenticatedUrl(page.Url) : _sanitizer.SanitizeUrl(page.Url), StartedAt: startedAt,
                CompletedAt: DateTime.UtcNow, DurationMs: (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                StartupState: DetermineStartupState(observation), ConsoleErrorCount: observation.ConsoleEvents.Count(e => e.Type == "error"),
                PageErrorCount: observation.PageErrors.Count, CriticalResourceFailureCount: observation.CriticalResourceFailureCount,
                Findings: authenticated ? _sanitizer.SanitizeAuthenticatedFindings(findings) : _sanitizer.SanitizeFindings(findings),
                Limitations: authenticated ? ["Authenticated single-page runtime observation", $"Delivery context: {deliveryContext}", "No DOM, response body, request body, cookie, token, or storage evidence collected"] : ["Single-target startup observation only, no crawling", "No Lighthouse, Core Web Vitals, or active security scanning"],
                ExecutionMode: request.ExecutionMode, DeliveryContext: deliveryContext);
        }
        catch (OperationCanceledException) when (request.ExecutionMode == BrowserRuntimeExecutionMode.AuthenticatedSessionPage)
        {
            var state = await SafeStatusAsync(request);
            var reason = state?.Status switch
            {
                AuthenticatedBrowserSessionStatus.AuthenticationExpired => BrowserRuntimeOutcomeReason.AuthenticationExpired,
                AuthenticatedBrowserSessionStatus.UnexpectedOrigin => BrowserRuntimeOutcomeReason.UnexpectedOrigin,
                AuthenticatedBrowserSessionStatus.AuthenticationCancelled => BrowserRuntimeOutcomeReason.AuthenticationCancelled,
                _ => BrowserRuntimeOutcomeReason.AuthenticationRequired
            };
            return Rejected(request, reason, "Authenticated application eligibility ended during Browser Runtime execution.");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout"))
        { return new BrowserRuntimeResult(Status: BrowserRuntimeEngineStatus.Assessed, StartupState: BrowserStartupState.TimedOut, EngineError: "Navigation timeout exceeded", RequestedUrl: SafeOrigin(request.TargetUrl), CompletedAt: DateTime.UtcNow, ExecutionMode: request.ExecutionMode, DeliveryContext: deliveryContext); }
        finally
        { page.PageError -= pageError; page.Console -= console; page.RequestFailed -= failed; page.Response -= response; }
    }

    private async Task<AuthenticatedBrowserSessionDescriptor?> SafeStatusAsync(BrowserRuntimeExecutionRequest request)
    { try { return _authenticatedSessions is null ? null : await _authenticatedSessions.GetStatusAsync(request.AuthenticatedSessionId!, request.ReviewSessionId!, request.ProfileId!); } catch { return null; } }
    private static BrowserRuntimeResult Rejected(BrowserRuntimeExecutionRequest request, BrowserRuntimeOutcomeReason reason, string message, BrowserRuntimeEngineStatus status = BrowserRuntimeEngineStatus.Skipped) =>
        new(Status: status, RequestedUrl: SafeOrigin(request.TargetUrl), EngineError: message, ExecutionMode: request.ExecutionMode, OutcomeReason: reason);
    private static string SafeOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "invalid-target";

    public async Task<BrowserRuntimeReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try { using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] }); return new(true, BrowserName: "Chromium", BrowserVersion: browser.Version); }
        catch { return new(false, "Chromium executable not available", "Chromium", null); }
    }
    private static Task<bool> DetectBlazorAsync(IPage page) => page.EvaluateAsync<bool>("() => !!document.querySelector('script[src*=\"blazor\"],script[src*=\"_framework\"]')");
    private static Task<bool> IsBlazorBootstrapCompleteAsync(IPage page) => page.EvaluateAsync<bool>("() => window.Blazor?.started ?? false");
    private static BrowserStartupState DetermineStartupState(Observation obs) => obs.ResourceFailures.Any(f => IsCritical(f.Url)) || obs.PageErrors.Any(e => e.Message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)) ? BrowserStartupState.Failed : obs.PageErrors.Count > 0 || obs.ConsoleEvents.Any(e => e.Type == "error") ? BrowserStartupState.StartedWithErrors : BrowserStartupState.Started;
    private static bool IsCritical(string url) => new[] { "_framework", "blazor", ".wasm", "app.js", "app.css" }.Any(c => url.Contains(c, StringComparison.OrdinalIgnoreCase));
    private sealed class Observation
    {
        public bool DomContentLoadedReached { get; set; } public bool LoadEventReached { get; set; } public long NavigationDurationMs { get; set; }
        public bool BlazorDetected { get; set; } public bool BlazorBootstrapCompleted { get; set; }
        public List<BrowserConsoleEvent> ConsoleEvents { get; } = []; public List<BrowserPageError> PageErrors { get; } = []; public List<BrowserResourceFailure> ResourceFailures { get; } = [];
        public int CriticalResourceFailureCount => ResourceFailures.Count(f => IsCritical(f.Url));
        public BrowserStartupObservation Snapshot() => new(DomContentLoadedReached, LoadEventReached, NavigationDurationMs, BlazorDetected, BlazorBootstrapCompleted, ConsoleEvents, PageErrors, ResourceFailures, CriticalResourceFailureCount);
    }
}
