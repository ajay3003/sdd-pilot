using Microsoft.Playwright;
using System.Diagnostics;

namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>
/// Production Chromium-based browser runtime engine for Frontend Quality Review.
/// Executes single-target startup/runtime observation without crawling or active scanning.
/// </summary>
public sealed class FrontendBrowserRuntimeReviewService : IFrontendBrowserRuntimeReviewService
{
    private readonly ILogger<FrontendBrowserRuntimeReviewService> _logger;
    private readonly BrowserTargetValidator _targetValidator;
    private readonly BrowserRuntimeFindingClassifier _findingClassifier;
    private readonly BrowserResourceClassifier _resourceClassifier;
    private readonly BrowserEvidenceSanitizer _sanitizer;

    public FrontendBrowserRuntimeReviewService(
        ILogger<FrontendBrowserRuntimeReviewService> logger,
        BrowserTargetValidator targetValidator,
        BrowserRuntimeFindingClassifier findingClassifier,
        BrowserResourceClassifier resourceClassifier,
        BrowserEvidenceSanitizer sanitizer)
    {
        _logger = logger;
        _targetValidator = targetValidator;
        _findingClassifier = findingClassifier;
        _resourceClassifier = resourceClassifier;
        _sanitizer = sanitizer;
    }

    public async Task<BrowserRuntimeResult> ReviewAsync(
        string targetUrl,
        BrowserRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        options ??= new BrowserRuntimeOptions();

        // ── Validate target ─────────────────────────────────────────
        var validation = _targetValidator.ValidateTarget(targetUrl);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Browser runtime target validation failed: {BlockReason}", validation.BlockReason);
            return new BrowserRuntimeResult(
                Status: BrowserRuntimeEngineStatus.Skipped,
                EngineError: validation.BlockReason,
                RequestedUrl: targetUrl);
        }

        _logger.LogInformation("Starting browser runtime review for {TargetUrl}", targetUrl);

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            // ── Initialize Playwright ───────────────────────────────
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.HeadlessMode,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
            });

            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = int.Parse(options.ViewportWidth),
                    Height = int.Parse(options.ViewportHeight)
                }
            });

            page = await context.NewPageAsync();

            // ── Capture observations ────────────────────────────────
            var observation = new BrowserStartupObservationCollector();

            EventHandler<string> pageErrorHandler = (sender, errorMessage) =>
            {
                observation.AddPageError(new BrowserPageError(
                    errorMessage,
                    page.Url,
                    null));
            };
            page.PageError += pageErrorHandler;

            try
            {
                EventHandler<IConsoleMessage> consoleHandler = (sender, consoleMessage) =>
                {
                    if (consoleMessage.Type == "error" || consoleMessage.Type == "warning")
                    {
                        observation.AddConsoleEvent(new BrowserConsoleEvent(
                            consoleMessage.Type,
                            consoleMessage.Text,
                            null,
                            null,
                            null));
                    }
                };
                page.Console += consoleHandler;
            }
            catch
            {
                // Console event handler registration failed, continue without it
            }

            try
            {
                EventHandler<IRequest> requestFailedHandler = (sender, requestFailed) =>
                {
                    observation.AddResourceFailure(new BrowserResourceFailure(
                        requestFailed.Url,
                        requestFailed.ResourceType,
                        "Request failed",
                        null));
                };
                page.RequestFailed += requestFailedHandler;
            }
            catch
            {
                // Request failed handler registration failed, continue
            }

            try
            {
                EventHandler<IResponse> responseHandler = (sender, responseData) =>
                {
                    if ((responseData.Status >= 400 && responseData.Status < 600) ||
                        (responseData.Request.ResourceType == "fetch" && responseData.Status >= 400))
                    {
                        observation.AddResourceFailure(new BrowserResourceFailure(
                            responseData.Url,
                            responseData.Request.ResourceType,
                            $"HTTP {responseData.Status}",
                            responseData.Status));
                    }
                };
                page.Response += responseHandler;
            }
            catch
            {
                // Response handler registration failed, continue
            }

            try
            {
                // ── Navigate with timeout ───────────────────────────────
                var navigationTask = page.GotoAsync(
                    targetUrl,
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = options.NavigationTimeoutMs });

                var completedNavigation = await navigationTask.ConfigureAwait(false);

                observation.DomContentLoadedReached = true;
                observation.NavigationDurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

                // ── Observe startup state ───────────────────────────────
                await Task.Delay(options.StartupObservationMs, cancellationToken);

                // ── Check Blazor WASM detection ─────────────────────────
                var blazorDetected = await DetectBlazorAsync(page);
                observation.BlazorDetected = blazorDetected;

                if (blazorDetected)
                {
                    observation.BlazorBootstrapCompleted = await IsBlazorBootstrapCompleteAsync(page);
                }

                // ── Determine startup state ─────────────────────────────
                var startupState = DetermineStartupState(observation);

                // ── Classify findings ───────────────────────────────────
                var findings = _findingClassifier.ClassifyObservations(new BrowserStartupObservation(
                    observation.DomContentLoadedReached,
                    observation.LoadEventReached,
                    observation.NavigationDurationMs,
                    observation.BlazorDetected,
                    observation.BlazorBootstrapCompleted,
                    observation.ConsoleEvents,
                    observation.PageErrors,
                    observation.ResourceFailures,
                    observation.CriticalResourceFailureCount));

                // ── Sanitize evidence ───────────────────────────────────
                var sanitizedFindings = _sanitizer.SanitizeFindings(findings);
                var sanitizedConsoleEvents = _sanitizer.SanitizeConsoleEvents(observation.ConsoleEvents);
                var sanitizedPageErrors = _sanitizer.SanitizePageErrors(observation.PageErrors);
                var sanitizedResourceFailures = _sanitizer.SanitizeResourceFailures(observation.ResourceFailures);

                var completedAt = DateTime.UtcNow;
                var durationMs = (completedAt - startedAt).TotalMilliseconds;

                return new BrowserRuntimeResult(
                    Status: BrowserRuntimeEngineStatus.Assessed,
                    EngineName: "Browser Runtime",
                    BrowserName: "Chromium",
                    BrowserVersion: GetChromiumVersion(),
                    RequestedUrl: targetUrl,
                    FinalUrl: page.Url,
                    StartedAt: startedAt,
                    CompletedAt: completedAt,
                    DurationMs: (long)durationMs,
                    StartupState: startupState,
                    ConsoleErrorCount: observation.ConsoleEvents.Count(e => e.Type == "error"),
                    PageErrorCount: observation.PageErrors.Count,
                    CriticalResourceFailureCount: observation.CriticalResourceFailureCount,
                    Findings: sanitizedFindings,
                    EngineError: null,
                    Limitations: new List<string>
                    {
                        "Phase 2A: Single-target startup observation only, no crawling",
                        "No Lighthouse, Core Web Vitals, or active security scanning",
                        "Authentication not supported in Phase 2A"
                    });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout"))
            {
                _logger.LogWarning("Browser runtime navigation timeout: {Message}", ex.Message);
                return new BrowserRuntimeResult(
                    Status: BrowserRuntimeEngineStatus.Assessed,
                    StartupState: BrowserStartupState.TimedOut,
                    EngineError: "Navigation timeout exceeded",
                    RequestedUrl: targetUrl,
                    FinalUrl: page?.Url,
                    CompletedAt: DateTime.UtcNow);
            }
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Chromium") || ex.Message.Contains("executable"))
        {
            _logger.LogError("Chromium not available: {Message}", ex.Message);
            return new BrowserRuntimeResult(
                Status: BrowserRuntimeEngineStatus.EngineError,
                EngineError: "Chromium browser not available",
                RequestedUrl: targetUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browser runtime error: {Message}", ex.Message);
            return new BrowserRuntimeResult(
                Status: BrowserRuntimeEngineStatus.EngineError,
                EngineError: $"Runtime error: {ex.Message}",
                RequestedUrl: targetUrl);
        }
        finally
        {
            // ── Cleanup resources ───────────────────────────────────────
            if (page != null)
                await page.CloseAsync();
            if (context != null)
                await context.CloseAsync();
            if (browser != null)
                await browser.CloseAsync();
            playwright?.Dispose();

            _logger.LogInformation("Browser runtime review completed for {TargetUrl}", targetUrl);
        }
    }

    public async Task<BrowserRuntimeReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var playwright = await Playwright.CreateAsync();
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
                    });

                await browser.CloseAsync();
                return new BrowserRuntimeReadinessResult(IsAvailable: true);
            }
            finally
            {
                playwright?.Dispose();
            }
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Chromium") || ex.Message.Contains("executable"))
        {
            _logger.LogWarning("Chromium not available: {Message}", ex.Message);
            return new BrowserRuntimeReadinessResult(
                IsAvailable: false,
                ErrorMessage: "Chromium executable not available");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser readiness check failed");
            return new BrowserRuntimeReadinessResult(
                IsAvailable: false,
                ErrorMessage: ex.Message);
        }
    }

    private async Task<bool> DetectBlazorAsync(IPage page)
    {
        try
        {
            var hasBlazorScript = await page.EvaluateAsync<bool>(
                "() => !!document.querySelector('script[src*=\"blazor\"]')");

            var hasFrameworkScript = await page.EvaluateAsync<bool>(
                "() => !!document.querySelector('script[src*=\"_framework\"]')");

            return hasBlazorScript || hasFrameworkScript;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsBlazorBootstrapCompleteAsync(IPage page)
    {
        try
        {
            var isComplete = await page.EvaluateAsync<bool>(
                "() => window.Blazor?.started ?? false");

            return isComplete;
        }
        catch
        {
            return false;
        }
    }

    private static BrowserStartupState DetermineStartupState(BrowserStartupObservationCollector obs)
    {
        // Failed: critical resources unavailable or uncaught bootstrap exception
        var criticalResourceFailures = obs.ResourceFailures
            .Where(f => _IsCriticalResource(f.Url))
            .ToList();

        if (criticalResourceFailures.Count > 0)
            return BrowserStartupState.Failed;

        // Check for unrecoverable known failures
        var hasCriticalPageError = obs.PageErrors
            .Any(e => e.Message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase));

        if (hasCriticalPageError)
            return BrowserStartupState.Failed;

        // StartedWithErrors: page rendered but runtime/page errors observed
        var hasErrors = obs.PageErrors.Count > 0 ||
                       obs.ConsoleEvents.Any(e => e.Type == "error");

        if (hasErrors)
            return BrowserStartupState.StartedWithErrors;

        // Started: usable state reached with no critical failure
        return BrowserStartupState.Started;
    }

    private static bool _IsCriticalResource(string url)
    {
        var critical = new[] { "_framework", "blazor", ".wasm", "app.js", "app.css" };
        return critical.Any(c => url.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetChromiumVersion()
    {
        try
        {
            var version = typeof(BrowserType).Assembly.GetName().Version;
            return version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    // Helper class for collecting observations during page load
    private sealed class BrowserStartupObservationCollector
    {
        public bool DomContentLoadedReached { get; set; }
        public bool LoadEventReached { get; set; }
        public long NavigationDurationMs { get; set; }
        public bool BlazorDetected { get; set; }
        public bool BlazorBootstrapCompleted { get; set; }
        public List<BrowserConsoleEvent> ConsoleEvents { get; } = new();
        public List<BrowserPageError> PageErrors { get; } = new();
        public List<BrowserResourceFailure> ResourceFailures { get; } = new();
        public int CriticalResourceFailureCount => ResourceFailures
            .Count(f => f.Url.Contains("_framework", StringComparison.OrdinalIgnoreCase) ||
                       f.Url.Contains(".wasm", StringComparison.OrdinalIgnoreCase));

        public void AddConsoleEvent(BrowserConsoleEvent evt) => ConsoleEvents.Add(evt);
        public void AddPageError(BrowserPageError err) => PageErrors.Add(err);
        public void AddResourceFailure(BrowserResourceFailure failure) => ResourceFailures.Add(failure);
    }
}
