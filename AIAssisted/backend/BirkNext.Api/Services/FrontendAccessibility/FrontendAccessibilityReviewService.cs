using System.Text.Json;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Deque.AxeCore.Commons;
using Microsoft.Playwright;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.FrontendAccessibility;

public sealed class FrontendAccessibilityReviewService : IFrontendAccessibilityReviewService
{
    public static readonly List<string> ExecutedRuleTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
    public const string ManualTestingLimitation = "Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.";

    private readonly ILogger<FrontendAccessibilityReviewService> _logger;
    private readonly BrowserTargetValidator _targetValidator;
    private readonly AccessibilityNormalizer _normalizer;
    private readonly IAxeScriptProvider _axeScriptProvider;
    private readonly AccessibilityEvidenceSanitizer _sanitizer;
    private readonly IAuthenticatedBrowserSessionManager? _authenticatedSessions;
    private readonly bool _enabled;

    public FrontendAccessibilityReviewService(
        ILogger<FrontendAccessibilityReviewService> logger,
        BrowserTargetValidator targetValidator,
        AccessibilityNormalizer normalizer,
        IOptions<FrontendAccessibilityOptions> options)
        : this(logger, targetValidator, normalizer, new BundledAxeScriptProvider(), new AccessibilityEvidenceSanitizer(), null, options.Value.Enabled) { }

    internal FrontendAccessibilityReviewService(
        ILogger<FrontendAccessibilityReviewService> logger,
        BrowserTargetValidator targetValidator,
        AccessibilityNormalizer normalizer,
        IAxeScriptProvider axeScriptProvider,
        AccessibilityEvidenceSanitizer sanitizer,
        IAuthenticatedBrowserSessionManager? authenticatedSessions = null,
        bool enabled = true)
    {
        _logger = logger;
        _targetValidator = targetValidator;
        _normalizer = normalizer;
        _axeScriptProvider = axeScriptProvider;
        _sanitizer = sanitizer;
        _authenticatedSessions = authenticatedSessions;
        _enabled = enabled;
    }

    public async Task<AccessibilityReviewResult> ReviewAsync(
        string targetUrl,
        AccessibilityReviewOptions? options = null,
        bool requiresAuthentication = false,
        CancellationToken cancellationToken = default)
    {
        if (requiresAuthentication)
            return new AccessibilityReviewResult(
                AccessibilityExecutionStatus.AuthenticationRequired,
                RequestedUrl: targetUrl,
                ExecutionMode: AccessibilityExecutionMode.AnonymousOwnedBrowser,
                OutcomeReason: AccessibilityOutcomeReason.AuthenticationRequired);

        options ??= new AccessibilityReviewOptions();
        return await ReviewAsync(new AccessibilityExecutionRequest(
            targetUrl,
            AccessibilityExecutionMode.AnonymousOwnedBrowser,
            null, null, null, options), cancellationToken);
    }

    public async Task<AccessibilityReviewResult> ReviewAsync(
        AccessibilityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var options = request.Options ?? new AccessibilityReviewOptions();

        if (!_enabled)
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "Accessibility review engine is disabled.", AccessibilityExecutionMode.AnonymousOwnedBrowser);

        return request.ExecutionMode == AccessibilityExecutionMode.AuthenticatedSessionPage
            ? await ReviewAuthenticatedAsync(request, startedAt, cancellationToken)
            : await ReviewAnonymousAsync(request, startedAt, options, cancellationToken);
    }

    private async Task<AccessibilityReviewResult> ReviewAnonymousAsync(
        AccessibilityExecutionRequest request,
        DateTime startedAt,
        AccessibilityReviewOptions options,
        CancellationToken cancellationToken)
    {
        var validation = _targetValidator.ValidateTarget(request.TargetUrl, options.EnvironmentType);
        if (!validation.IsValid)
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                validation.BlockReason ?? "Target blocked by safety policy.", AccessibilityExecutionMode.AnonymousOwnedBrowser);

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        try
        {
            var axeScript = _axeScriptProvider.GetScript();
            if (string.IsNullOrWhiteSpace(axeScript))
                return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt,
                    "axe-core bundled asset is unavailable.", AccessibilityExecutionMode.AnonymousOwnedBrowser);

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                Args = ["--no-sandbox", "--disable-dev-shm-usage"]
            });
            context = await browser.NewContextAsync();
            page = await context.NewPageAsync();
            var response = await page.GotoAsync(request.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = options.NavigationTimeoutMs
            });
            var finalValidation = _targetValidator.ValidateRedirectTarget(page.Url, new Uri(request.TargetUrl).Host, options.EnvironmentType);
            if (!finalValidation.IsValid)
                return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                    finalValidation.BlockReason ?? "Redirect blocked by safety policy.", AccessibilityExecutionMode.AnonymousOwnedBrowser);
            if (response is null || !response.Ok)
                return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt,
                    $"Target navigation failed before axe execution (HTTP {response?.Status}).", AccessibilityExecutionMode.AnonymousOwnedBrowser);

            var result = await AnalyzePageAsync(page, browser.Version, request, options, false, cancellationToken);
            return result with { ExecutionMode = AccessibilityExecutionMode.AnonymousOwnedBrowser };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Accessibility review engine failed for {TargetUrl}", request.TargetUrl);
            return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt, ex.Message,
                AccessibilityExecutionMode.AnonymousOwnedBrowser);
        }
        finally
        {
            if (page is not null) await page.CloseAsync();
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    private async Task<AccessibilityReviewResult> ReviewAuthenticatedAsync(
        AccessibilityExecutionRequest request,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        if (_authenticatedSessions is null || string.IsNullOrWhiteSpace(request.AuthenticatedSessionId) ||
            string.IsNullOrWhiteSpace(request.ReviewSessionId) || string.IsNullOrWhiteSpace(request.ProfileId))
            return Failure(AccessibilityExecutionStatus.AuthenticationRequired, request.TargetUrl, startedAt,
                "Authenticated session identifiers are required.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationRequired);

        try
        {
            await using var lease = await _authenticatedSessions.AcquirePageLeaseAsync(
                request.AuthenticatedSessionId, request.ReviewSessionId, request.ProfileId, request.TargetUrl, cancellationToken);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.SessionCancellation);
            var options = request.Options ?? new AccessibilityReviewOptions();
            var result = await AnalyzePageAsync(lease.Page, lease.Context.Browser?.Version, request, options, true, execution.Token);
            return result with { ExecutionMode = AccessibilityExecutionMode.AuthenticatedSessionPage };
        }
        catch (AuthenticatedSessionExpiredException)
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "The authenticated session expired.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationExpired);
        }
        catch (AuthenticatedSessionNotEligibleException)
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "The authenticated application page is not currently eligible for review.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationRequired);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "The authenticated session is unavailable.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationRequired);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "Authenticated session ownership validation failed.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationRequired);
        }
        catch (ObjectDisposedException)
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "The authenticated session is closed.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationCancelled);
        }
        catch (OperationCanceledException) when (request.ExecutionMode == AccessibilityExecutionMode.AuthenticatedSessionPage)
        {
            return await MapCancelledSessionOutcome(request, startedAt, cancellationToken);
        }
        catch (Microsoft.Playwright.PlaywrightException) when (request.ExecutionMode == AccessibilityExecutionMode.AuthenticatedSessionPage)
        {
            // Navigation mid-execution (redirect, unexpected origin) throws PlaywrightException
            // Check session status to determine the auth-specific outcome
            return await MapCancelledSessionOutcome(request, startedAt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authenticated accessibility review failed for {TargetUrl}", request.TargetUrl);

            // Before returning generic EngineError, check if session eligibility changed
            // (e.g., redirect during axe execution caused auth/origin loss)
            try
            {
                var status = await _authenticatedSessions.GetStatusAsync(
                    request.AuthenticatedSessionId, request.ReviewSessionId, request.ProfileId,
                    CancellationToken.None);

                if (status?.Status is AuthenticatedBrowserSessionStatus.AuthenticationExpired or
                    AuthenticatedBrowserSessionStatus.UnexpectedOrigin or
                    AuthenticatedBrowserSessionStatus.AuthenticationCancelled)
                {
                    var reason = status.Status switch
                    {
                        AuthenticatedBrowserSessionStatus.AuthenticationExpired => AccessibilityOutcomeReason.AuthenticationExpired,
                        AuthenticatedBrowserSessionStatus.UnexpectedOrigin => AccessibilityOutcomeReason.UnexpectedOrigin,
                        AuthenticatedBrowserSessionStatus.AuthenticationCancelled => AccessibilityOutcomeReason.AuthenticationCancelled,
                        _ => AccessibilityOutcomeReason.AuthenticationRequired
                    };

                    return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                        "Authenticated session became ineligible during analysis.",
                        AccessibilityExecutionMode.AuthenticatedSessionPage, reason);
                }
            }
            catch { }

            // Session still valid, so this is a genuine execution error
            return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt, ex.Message,
                AccessibilityExecutionMode.AuthenticatedSessionPage);
        }
    }

    private async Task<AccessibilityReviewResult> AnalyzePageAsync(
        IPage page,
        string? browserVersion,
        AccessibilityExecutionRequest request,
        AccessibilityReviewOptions options,
        bool isAuthenticated,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var axeScript = _axeScriptProvider.GetScript();
            if (string.IsNullOrWhiteSpace(axeScript))
                return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt,
                    "axe-core bundled asset is unavailable.", request.ExecutionMode);

            await Task.Delay(options.StabilizationMs, cancellationToken);
            await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = axeScript });
            var axeVersion = await page.EvaluateAsync<string>("() => window.axe && window.axe.version");
            var raw = await page.EvaluateAsync<JsonElement>(
                "tags => axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations','incomplete','passes','inapplicable'] })",
                ExecutedRuleTags);

            if (!raw.TryGetProperty("violations", out var violations))
                return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt,
                    "axe-core returned no assessment result.", request.ExecutionMode);

            var incomplete = raw.GetProperty("incomplete");
            var findings = _normalizer.Normalize(violations, AccessibilityFindingKind.Violation)
                .Concat(_normalizer.Normalize(incomplete, AccessibilityFindingKind.NeedsManualReview))
                .ToList();

            if (isAuthenticated)
            {
                findings = _sanitizer.SanitizeAuthenticatedFindings(findings);
            }

            var completedAt = DateTime.UtcNow;
            return new AccessibilityReviewResult(
                ExecutionStatus: AccessibilityExecutionStatus.Assessed,
                AxeVersion: axeVersion,
                BrowserName: "Chromium",
                BrowserVersion: browserVersion,
                RequestedUrl: request.TargetUrl,
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
                Limitations: isAuthenticated
                    ? ["Authenticated single-page accessibility analysis", "No DOM, response body, or sensitive element evidence collected"]
                    : [ManualTestingLimitation],
                ExecutionMode: request.ExecutionMode);
        }
        catch (Microsoft.Playwright.PlaywrightException) when (isAuthenticated)
        {
            // Navigation during axe.run (redirect, unexpected origin) throws PlaywrightException
            // Let it bubble up to ReviewAuthenticatedAsync for proper session-state mapping
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "axe-core analysis failed for {TargetUrl}", request.TargetUrl);
            return Failure(AccessibilityExecutionStatus.EngineError, request.TargetUrl, startedAt, ex.Message, request.ExecutionMode);
        }
    }

    public async Task<AccessibilityReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return new(AccessibilityReadinessState.Disabled, false, BrowserName: "Chromium", Error: "Accessibility review engine is disabled.");
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

    private async Task<AccessibilityReviewResult> MapCancelledSessionOutcome(
        AccessibilityExecutionRequest request,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await _authenticatedSessions!.GetStatusAsync(
                request.AuthenticatedSessionId, request.ReviewSessionId, request.ProfileId, cancellationToken);
            var reason = status?.Status switch
            {
                AuthenticatedBrowserSessionStatus.AuthenticationExpired => AccessibilityOutcomeReason.AuthenticationExpired,
                AuthenticatedBrowserSessionStatus.UnexpectedOrigin => AccessibilityOutcomeReason.UnexpectedOrigin,
                AuthenticatedBrowserSessionStatus.AuthenticationCancelled => AccessibilityOutcomeReason.AuthenticationCancelled,
                _ => AccessibilityOutcomeReason.AuthenticationRequired
            };
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "Authenticated application eligibility ended during Accessibility analysis.", AccessibilityExecutionMode.AuthenticatedSessionPage, reason);
        }
        catch
        {
            return Failure(AccessibilityExecutionStatus.Skipped, request.TargetUrl, startedAt,
                "Authenticated session state unknown during Accessibility analysis.", AccessibilityExecutionMode.AuthenticatedSessionPage,
                AccessibilityOutcomeReason.AuthenticationRequired);
        }
    }

    private static AccessibilityReviewResult Failure(
        AccessibilityExecutionStatus status,
        string url,
        DateTime startedAt,
        string error,
        AccessibilityExecutionMode executionMode = AccessibilityExecutionMode.AnonymousOwnedBrowser,
        AccessibilityOutcomeReason outcomeReason = AccessibilityOutcomeReason.None) =>
        new(status, RequestedUrl: url, StartedAt: startedAt, CompletedAt: DateTime.UtcNow,
            RuleTags: [.. ExecutedRuleTags], Limitations: [ManualTestingLimitation], EngineError: error,
            ExecutionMode: executionMode, OutcomeReason: outcomeReason);
}
