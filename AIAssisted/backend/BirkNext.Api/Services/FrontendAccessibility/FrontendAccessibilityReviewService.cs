using System.Text.Json;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Deque.AxeCore.Commons;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.FrontendAccessibility;

public sealed class FrontendAccessibilityReviewService : IFrontendAccessibilityReviewService
{
    public static readonly List<string> ExecutedRuleTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
    public const string ManualTestingLimitation = "Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.";

    private readonly ILogger<FrontendAccessibilityReviewService> _logger;
    private readonly BrowserTargetValidator _targetValidator;
    private readonly AccessibilityNormalizer _normalizer;
    private readonly IAxeScriptProvider _axeScriptProvider;

    public FrontendAccessibilityReviewService(
        ILogger<FrontendAccessibilityReviewService> logger,
        BrowserTargetValidator targetValidator,
        AccessibilityNormalizer normalizer)
        : this(logger, targetValidator, normalizer, new BundledAxeScriptProvider()) { }

    internal FrontendAccessibilityReviewService(
        ILogger<FrontendAccessibilityReviewService> logger,
        BrowserTargetValidator targetValidator,
        AccessibilityNormalizer normalizer,
        IAxeScriptProvider axeScriptProvider)
    {
        _logger = logger;
        _targetValidator = targetValidator;
        _normalizer = normalizer;
        _axeScriptProvider = axeScriptProvider;
    }

    public async Task<AccessibilityReviewResult> ReviewAsync(
        string targetUrl,
        AccessibilityReviewOptions? options = null,
        bool requiresAuthentication = false,
        CancellationToken cancellationToken = default)
    {
        options ??= new AccessibilityReviewOptions();
        var startedAt = DateTime.UtcNow;
        if (requiresAuthentication)
            return Failure(AccessibilityExecutionStatus.AuthenticationRequired, targetUrl, startedAt,
                "Anonymous Phase 2B accessibility assessment cannot review an authenticated target.");

        var validation = _targetValidator.ValidateTarget(targetUrl, options.EnvironmentType);
        if (!validation.IsValid)
            return Failure(AccessibilityExecutionStatus.Skipped, targetUrl, startedAt, validation.BlockReason ?? "Target blocked by safety policy.");

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        try
        {
            var axeScript = _axeScriptProvider.GetScript();
            if (string.IsNullOrWhiteSpace(axeScript))
                return Failure(AccessibilityExecutionStatus.EngineError, targetUrl, startedAt, "axe-core bundled asset is unavailable.");

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                Args = ["--no-sandbox", "--disable-dev-shm-usage"]
            });
            context = await browser.NewContextAsync();
            page = await context.NewPageAsync();
            var response = await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = options.NavigationTimeoutMs
            });
            var finalValidation = _targetValidator.ValidateRedirectTarget(page.Url, new Uri(targetUrl).Host, options.EnvironmentType);
            if (!finalValidation.IsValid)
                return Failure(AccessibilityExecutionStatus.Skipped, targetUrl, startedAt, finalValidation.BlockReason ?? "Redirect blocked by safety policy.");
            if (response is null || !response.Ok)
                return Failure(AccessibilityExecutionStatus.EngineError, targetUrl, startedAt, $"Target navigation failed before axe execution (HTTP {response?.Status}).");

            await Task.Delay(options.StabilizationMs, cancellationToken);
            await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = axeScript });
            var axeVersion = await page.EvaluateAsync<string>("() => window.axe && window.axe.version");
            var raw = await page.EvaluateAsync<JsonElement>(
                "tags => axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations','incomplete','passes','inapplicable'] })",
                ExecutedRuleTags);

            if (!raw.TryGetProperty("violations", out var violations))
                return Failure(AccessibilityExecutionStatus.EngineError, targetUrl, startedAt, "axe-core returned no assessment result.");

            var incomplete = raw.GetProperty("incomplete");
            var findings = _normalizer.Normalize(violations, AccessibilityFindingKind.Violation)
                .Concat(_normalizer.Normalize(incomplete, AccessibilityFindingKind.NeedsManualReview))
                .ToList();
            var completedAt = DateTime.UtcNow;
            return new AccessibilityReviewResult(
                ExecutionStatus: AccessibilityExecutionStatus.Assessed,
                AxeVersion: axeVersion,
                BrowserName: "Chromium",
                BrowserVersion: browser.Version,
                RequestedUrl: targetUrl,
                FinalUrl: page.Url,
                StartedAt: startedAt,
                CompletedAt: completedAt,
                DurationMs: (long)(completedAt - startedAt).TotalMilliseconds,
                RuleTags: [.. ExecutedRuleTags],
                ViolationCount: violations.GetArrayLength(),
                IncompleteCount: incomplete.GetArrayLength(),
                PassCount: raw.GetProperty("passes").GetArrayLength(),
                InapplicableCount: raw.GetProperty("inapplicable").GetArrayLength(),
                Findings: findings,
                Limitations: [ManualTestingLimitation]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Accessibility review engine failed for {TargetUrl}", targetUrl);
            return Failure(AccessibilityExecutionStatus.EngineError, targetUrl, startedAt, ex.Message);
        }
        finally
        {
            if (page is not null) await page.CloseAsync();
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    public async Task<AccessibilityReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_axeScriptProvider.GetScript()))
                return new(AccessibilityReadinessState.AxeUnavailable, false, Error: "axe-core bundled asset unavailable.");
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync("<html><title>readiness</title></html>");
            await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = _axeScriptProvider.GetScript() });
            var axeVersion = await page.EvaluateAsync<string>("() => axe.version");
            var browserVersion = browser.Version;
            await browser.CloseAsync();
            return new(AccessibilityReadinessState.Ready, true, axeVersion, "Chromium", browserVersion);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("executable", StringComparison.OrdinalIgnoreCase))
        {
            return new(AccessibilityReadinessState.ChromiumUnavailable, false, BrowserName: "Chromium", Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new(AccessibilityReadinessState.LaunchFailed, false, BrowserName: "Chromium", Error: ex.Message);
        }
    }

    private static AccessibilityReviewResult Failure(AccessibilityExecutionStatus status, string url, DateTime startedAt, string error) =>
        new(status, RequestedUrl: url, StartedAt: startedAt, CompletedAt: DateTime.UtcNow,
            RuleTags: [.. ExecutedRuleTags], Limitations: [ManualTestingLimitation], EngineError: error);
}
