using System.Diagnostics;
using Path = System.IO.Path;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.HeadlessAuthDiagnostic;

public interface IHeadlessDiagnosticService
{
    Task<HeadlessDiagnosticReport> RunAsync(HeadlessDiagnosticRequest request, CancellationToken ct = default);
}

/// <summary>Chronological reducer. Once Block is called, no later observation can replace the first blocker.</summary>
internal sealed class HeadlessEvidenceRun(HeadlessDiagnosticReport report, ILogger logger)
{
    private readonly HashSet<string> _logged = [];
    private long _observationDurationMs;
    public bool AuthenticationObserved { get; private set; }
    public bool SessionControlObserved { get; private set; }
    public bool ReturnObserved { get; private set; }
    public bool SessionVerified { get; private set; }
    public bool Terminal => report.PrimaryBlocker != HeadlessBlocker.None;
    public void Stage(HeadlessStage stage, HeadlessStageState state, string detail, long? duration = null, string? exception = null)
    {
        report.Stages[(int)stage] = new(stage, state, detail, duration ?? _observationDurationMs, exception, DateTimeOffset.UtcNow);
    }
    public void Block(HeadlessBlocker blocker, HeadlessStage stage, string reason, bool uncertain = false)
    {
        if (Terminal) return;
        report.PrimaryBlocker = blocker;
        report.Readiness = uncertain ? HeadlessReadiness.Unknown : HeadlessReadiness.NotReady;
        report.Interpretation = reason;
        Stage(stage, uncertain ? HeadlessStageState.Unknown : HeadlessStageState.Blocked, reason);
        logger.LogInformation("HeadlessPrimaryBlockerDetermined {DiagnosticId} {Stage} {Blocker}", report.DiagnosticId, stage, blocker);
    }
    private void Event(string name)
    {
        if (_logged.Add(name)) logger.LogInformation("{DiagnosticEvent} {DiagnosticId}", name, report.DiagnosticId);
    }
    public void Observe(HeadlessObservation o, long durationMs = 0)
    {
        if (Terminal) return;
        _observationDurationMs = durationMs;
        foreach (var nav in o.Navigations)
        {
            if (report.Navigation.Count < 100) report.Navigation.Add(nav.Navigation);
            if (nav.Authentication) AuthenticationObserved = true;
            if (nav.Entra) report.IdentityProvider = "Microsoft Entra ID";
            if (nav.SessionControl) SessionControlObserved = true;
            if (nav.Application && AuthenticationObserved) ReturnObserved = true;
        }
        AuthenticationObserved |= o.AtIdentityProvider;
        if (o.Entra) report.IdentityProvider = "Microsoft Entra ID";
        else if (o.AtIdentityProvider && report.IdentityProvider == "Unknown") report.IdentityProvider = "Configured identity provider";
        SessionControlObserved |= o.StrongSessionControl;
        if (AuthenticationObserved)
        {
            Stage(HeadlessStage.AuthenticationDetection, HeadlessStageState.Passed, "Authentication redirect observed.");
            Stage(HeadlessStage.IdentityProviderDetection, report.IdentityProvider == "Unknown" ? HeadlessStageState.Unknown : HeadlessStageState.Passed, report.IdentityProvider);
            Event("AuthenticationRedirectDetected"); Event("IdentityProviderDetected");
        }
        else
        {
            Stage(HeadlessStage.AuthenticationDetection, HeadlessStageState.Unknown, "No authentication redirect observed.");
            Stage(HeadlessStage.IdentityProviderDetection, HeadlessStageState.Unknown, "Identity provider not yet observed.");
        }
        if (o.ProductionNavigationBlocked)
        {
            Block(HeadlessBlocker.TargetNavigationBlocked, HeadlessStage.TargetNavigation, "Navigation to a hostname indicating Production was blocked by the diagnostic guard.");
            return;
        }
        // Entra error identifiers count only when the page was actually Microsoft Entra: "53003" on some other page
        // is just a number. The code is kept for every Entra error; only 53003 is an explicit CA block.
        var entraError = o.Entra ? o.EntraError : null;
        if (entraError is not null)
        {
            report.EntraErrorCode ??= entraError.Code;
            report.EntraCorrelationId ??= entraError.CorrelationId;
            report.EntraRequestId ??= entraError.RequestId;
            report.EntraErrorTimestamp ??= entraError.Timestamp;
        }
        var caBlock = o.Entra && (o.ConditionalAccessBlock || entraError?.Code == "AADSTS53003");
        var caCodeSignal = entraError?.Code is { } code && code.StartsWith("AADSTS530", StringComparison.Ordinal) && code != "AADSTS53003";
        // Stronger explicit CA evidence takes precedence over ordinary login controls on the SAME error page.
        // Across pages, the first terminal observation wins, without observing later pages.
        report.ConditionalAccess = caBlock ? "Explicit block observed"
            : caCodeSignal ? $"Conditional Access error observed ({entraError!.Code})"
            : o.ConditionalAccessSignal || report.ConditionalAccess == "Signal observed" ? "Signal observed"
            : report.ConditionalAccess.StartsWith("Conditional Access error observed", StringComparison.Ordinal) ? report.ConditionalAccess
            : "No observable signal";
        Stage(HeadlessStage.ConditionalAccessObservation, caBlock ? HeadlessStageState.Blocked : o.ConditionalAccessSignal || caCodeSignal ? HeadlessStageState.Passed : HeadlessStageState.Unknown, report.ConditionalAccess);
        if (o.ConditionalAccessSignal || caBlock || caCodeSignal) Event("ConditionalAccessSignalDetected");
        report.SessionControl = o.ExplicitHeadlessRestriction ? "Explicit session-control restriction observed" : SessionControlObserved ? "Session-control signal observed" : o.PossibleSessionControl || report.SessionControl == "Possible session-control signal" ? "Possible session-control signal" : "No observable signal";
        Stage(HeadlessStage.SessionControlObservation, SessionControlObserved ? HeadlessStageState.Passed : HeadlessStageState.Unknown, report.SessionControl);
        if (SessionControlObserved || o.PossibleSessionControl) Event("SessionControlSignalDetected");
        if (caBlock)
        {
            report.ConditionalAccessErrorCode = "AADSTS53003";
            report.AuthenticatedSession = "Not established";
            Block(HeadlessBlocker.ConditionalAccessBlocked, HeadlessStage.ConditionalAccessObservation, "Entra displayed an explicit Conditional Access block (AADSTS53003). IT can correlate the diagnostic timestamp with sign-in logs.");
            return;
        }
        if (o.MfaChallenge)
        {
            report.Mfa = "Required";
            report.InteractiveAuthentication = "Required (human MFA action)";
            report.NonInteractiveAuthentication = "Unavailable";
            report.AuthenticatedSession = "Not established";
            Stage(HeadlessStage.NonInteractiveContinuation, HeadlessStageState.Blocked, "Human MFA action is required.");
            Event("MfaChallengeDetected");
            Block(HeadlessBlocker.InteractiveMfaRequired, HeadlessStage.MfaDetection, "The observed MFA challenge requires human action, so this flow cannot complete unattended in Playwright headless mode.");
            return;
        }
        if (o.InteractiveLogin)
        {
            Stage(HeadlessStage.AuthenticationDetection, HeadlessStageState.Passed, "Interactive sign-in controls observed.");
            report.InteractiveAuthentication = "Required";
            report.NonInteractiveAuthentication = "Unavailable";
            report.AuthenticatedSession = "Not established";
            report.Mfa = "Unknown / not reached";
            Event("InteractiveAuthenticationDetected");
            Block(HeadlessBlocker.InteractiveAuthenticationRequired, HeadlessStage.NonInteractiveContinuation, "The current flow requires interactive credential entry or account selection. No credentials were entered; later MFA and session behavior cannot be assessed.");
            return;
        }
        if (o.ExplicitHeadlessRestriction)
        {
            report.SessionControlCompatibility = "Explicit headless restriction observed";
            Block(HeadlessBlocker.SessionControlHeadlessRestriction, HeadlessStage.SessionControlObservation, "A session-control host displayed an explicit restriction on headless or automated browsers. IT/security confirmation is required.");
            return;
        }
        report.Mfa = "Unknown / no recognized challenge";
        Stage(HeadlessStage.MfaDetection, HeadlessStageState.Unknown, "No recognized MFA challenge; this does not establish whether MFA applies.");
        Stage(HeadlessStage.NonInteractiveContinuation, HeadlessStageState.Running, "Observing automatic continuation without entering credentials or approving prompts.");
        ReturnObserved |= AuthenticationObserved && o.AtApplication;
        if (ReturnObserved)
        {
            report.AuthenticatedReturn = "Detected (does not verify the session)";
            Stage(HeadlessStage.AuthenticatedReturn, HeadlessStageState.Passed, report.AuthenticatedReturn);
            Event("AuthenticatedReturnDetected");
        }
        if (o.AtApplication && o.VerificationConfigured && o.AuthenticatedShell)
        {
            SessionVerified = true;
            report.AuthenticatedSession = "Established";
            Stage(HeadlessStage.AuthenticatedSessionVerification, HeadlessStageState.Passed, "Configured authenticated application shell is visible at the exact target origin.");
            Event("AuthenticatedSessionVerified");
        }
        else if (o.AtApplication && o.VerificationConfigured)
        {
            report.AuthenticatedSession = "Not established";
            Stage(HeadlessStage.AuthenticatedSessionVerification, HeadlessStageState.Unknown, "The configured authenticated application shell is not visible.");
        }
    }
    public void ControlLost()
    {
        report.PostAuthenticationControl = ReturnObserved || SessionVerified || SessionControlObserved ? "Lost" : "Not tested";
        if (SessionControlObserved)
        {
            report.SessionControlCompatibility = "Automation lost after observed session-control signal; requires IT confirmation";
            // Sequence, not cause: the blocker says what happened in what order, and nothing about why.
            Block(HeadlessBlocker.AutomationControlLostAfterObservedSessionControl, HeadlessStage.PostAuthenticationAutomationControl,
                "Control was lost after a session-control signal was observed. This establishes sequence, not causality: it "
                + "does not establish MCAS as the cause, and IT/security investigation is required.");
        }
        else if (ReturnObserved || SessionVerified)
            Block(HeadlessBlocker.AutomationControlLostAfterAuthentication, HeadlessStage.PostAuthenticationAutomationControl, "Playwright control was lost after authentication return or session verification.");
        else Block(HeadlessBlocker.Unknown, HeadlessStage.NonInteractiveContinuation, "Browser control was lost during authentication before an authenticated session could be verified. Cause unknown.", true);
    }
    /// <summary>
    /// Each headline value with where it came from. Observed: seen on a page in this run. Derived: read from what was
    /// seen (including "nothing was seen"). Configured: from settings. Unknown: not reached.
    /// </summary>
    public static List<HeadlessEvidenceItem> Provenance(HeadlessDiagnosticReport r, HeadlessDiagnosticRequest request)
    {
        static HeadlessEvidenceProvenance Of(string value, params string[] observedPrefixes) =>
            value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Not tested", StringComparison.OrdinalIgnoreCase)
                ? HeadlessEvidenceProvenance.Unknown
                : observedPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    ? HeadlessEvidenceProvenance.Observed : HeadlessEvidenceProvenance.Derived;
        return
        [
            new("Target URL", r.TargetUrl, HeadlessEvidenceProvenance.Configured),
            new("Configured authority", string.IsNullOrWhiteSpace(request.Authority) ? "Not configured" : HeadlessEvidenceSanitizer.Url(request.Authority),
                string.IsNullOrWhiteSpace(request.Authority) ? HeadlessEvidenceProvenance.Unknown : HeadlessEvidenceProvenance.Configured),
            new("Identity provider", r.IdentityProvider, r.IdentityProvider == "Unknown" ? HeadlessEvidenceProvenance.Unknown : HeadlessEvidenceProvenance.Observed),
            new("Interactive authentication", r.InteractiveAuthentication, Of(r.InteractiveAuthentication, "Required", "No interaction")),
            new("MFA", r.Mfa, Of(r.Mfa, "Required")),
            new("Conditional Access", r.ConditionalAccess, Of(r.ConditionalAccess, "Explicit", "Signal", "Conditional Access error")),
            new("MCAS / session control", r.SessionControl, Of(r.SessionControl, "Explicit", "Session-control signal observed")),
            new("Authenticated session", r.AuthenticatedSession, Of(r.AuthenticatedSession, "Established")),
            new("Post-authentication browser control", r.PostAuthenticationControl, Of(r.PostAuthenticationControl, "Available", "Lost")),
            new("Primary blocker", HeadlessItReport.BlockerLabel(r.PrimaryBlocker), HeadlessEvidenceProvenance.Derived),
        ];
    }

    public void Ready()
    {
        if (Terminal || !SessionVerified) return;
        report.PostAuthenticationControl = "Available";
        report.Readiness = HeadlessReadiness.Ready;
        report.InteractiveAuthentication = "No interaction encountered";
        report.NonInteractiveAuthentication = "Available in current diagnostic context";
        report.Mfa = "No interactive MFA challenge was observed during this diagnostic";
        report.SessionControlCompatibility = "No blocking behavior observed in this run";
        report.Interpretation = "A verified authenticated application session retained Playwright control without human interaction. This result applies only to the current diagnostic context; it does not configure a QA identity or Critical E2E.";
        Stage(HeadlessStage.NonInteractiveContinuation, HeadlessStageState.Passed, "No interaction was performed.");
        Stage(HeadlessStage.MfaDetection, HeadlessStageState.Passed, report.Mfa);
        Stage(HeadlessStage.PostAuthenticationAutomationControl, HeadlessStageState.Passed, "Safe title and DOM reads succeeded.");
        Event("PostAuthAutomationControlVerified");
    }
}

internal sealed class HeadlessDiagnosticService(IHeadlessBrowserFactory factory, BrowserAutomationEvidenceStore prerequisites,
    IOptions<HeadlessDiagnosticOptions> options, ILogger<HeadlessDiagnosticService> logger) : IHeadlessDiagnosticService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Configured observation window (HeadlessAuthDiagnostic:ObservationTimeoutSeconds); tests may override it.</summary>
    internal TimeSpan? ObservationTimeoutOverride { get; init; }
    internal TimeSpan ObservationTimeout
    {
        get => ObservationTimeoutOverride ?? options.Value.ObservationTimeout;
        init => ObservationTimeoutOverride = value;
    }
    public async Task<HeadlessDiagnosticReport> RunAsync(HeadlessDiagnosticRequest request, CancellationToken ct = default)
    {
        var report = new HeadlessDiagnosticReport { TargetEnvironmentName = HeadlessEvidenceSanitizer.Label(request.TargetEnvironmentName),
            TargetEnvironmentType = HeadlessEvidenceSanitizer.Label(request.EnvironmentType), TargetUrl = HeadlessEvidenceSanitizer.Url(request.TargetUrl),
            PlaywrightVersion = typeof(Microsoft.Playwright.IPlaywright).Assembly.GetName().Version?.ToString() };
        var run = new HeadlessEvidenceRun(report, logger);
        var profile = Path.Combine(HeadlessDiagnosticPolicy.ProfileRoot, report.DiagnosticId);
        var current = HeadlessStage.Runtime;
        var timer = Stopwatch.StartNew();
        var entered = false;
        IHeadlessBrowser? browser = null;
        logger.LogInformation("HeadlessAuthDiagnosticStarted {DiagnosticId}", report.DiagnosticId);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (HeadlessDiagnosticPolicy.BlockedReason(request, profile, options.Value) is { } reason)
            {
                report.Status = HeadlessRunStatus.Blocked;
                run.Block(HeadlessBlocker.TargetNavigationBlocked, current, reason);
                return report;
            }
            var proof = prerequisites.Check(request);
            report.PrerequisiteDiagnosticId = proof.DiagnosticId;
            report.BrowserAutomation = proof.Available ? "Available" : "Blocked / not demonstrated";
            if (!proof.Available)
            {
                report.Status = HeadlessRunStatus.Blocked;
                run.Block(HeadlessBlocker.BrowserAutomationBlocked, current, proof.Reason);
                return report;
            }
            entered = await _gate.WaitAsync(0, ct);
            if (!entered) { run.Block(HeadlessBlocker.Unknown, current, "Another headless diagnostic is running. Try again after it completes.", true); return report; }
            run.Stage(current, HeadlessStageState.Passed, "Prerequisite target control demonstrated; fresh dedicated profile selected.");
            browser = factory.Create(request);
            current = HeadlessStage.HeadlessEdgeLaunch; timer.Restart();
            run.Stage(current, HeadlessStageState.Running, "Launching Microsoft Edge headless.");
            await browser.LaunchAsync(profile, ct);
            report.EdgeVersion = browser.EdgeVersion;
            report.HeadlessBrowser = "Available";
            run.Stage(current, HeadlessStageState.Passed, "Microsoft Edge / msedge / Headless = true / fresh persistent context.", timer.ElapsedMilliseconds);
            logger.LogInformation("HeadlessEdgeStarted {DiagnosticId}", report.DiagnosticId);
            current = HeadlessStage.HeadlessBrowserControl; timer.Restart();
            if (!await browser.ControlAsync(ct)) { run.Block(HeadlessBlocker.BrowserAutomationBlocked, current, "Headless browser control could not be proven on about:blank."); return report; }
            run.Stage(current, HeadlessStageState.Passed, "Safe title and DOM reads on about:blank succeeded.", timer.ElapsedMilliseconds);
            current = HeadlessStage.TargetNavigation; timer.Restart();
            await browser.NavigateAsync(ct);
            if (!await browser.ControlAsync(ct)) { report.TargetControl = "Lost"; run.Block(HeadlessBlocker.BrowserAutomationBlocked, current, "Playwright could not retain target control before authentication assessment. MFA and session control were not tested."); return report; }
            report.TargetControl = "Available";
            run.Stage(current, HeadlessStageState.Passed, "Navigation and safe target automation control succeeded.", timer.ElapsedMilliseconds);
            logger.LogInformation("HeadlessTargetReached {DiagnosticId}", report.DiagnosticId);
            current = HeadlessStage.NonInteractiveContinuation; timer.Restart();
            using var ticks = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            while (timer.Elapsed < ObservationTimeout)
            {
                ct.ThrowIfCancellationRequested();
                var observationTimer = Stopwatch.StartNew();
                var observation = await browser.ObserveAsync(ct);
                run.Observe(observation, observationTimer.ElapsedMilliseconds);
                if (run.Terminal) break;
                if (run.SessionVerified)
                {
                    current = HeadlessStage.PostAuthenticationAutomationControl;
                    if (await browser.ControlAsync(ct)) run.Ready(); else run.ControlLost();
                    break;
                }
                await ticks.WaitForNextTickAsync(ct);
            }
            if (!run.Terminal && report.Readiness != HeadlessReadiness.Ready)
            {
                run.Stage(HeadlessStage.AuthenticatedSessionVerification, HeadlessStageState.Unknown, "No explicit authenticated application evidence was verified; cookies or an application return alone are insufficient.");
                run.Block(HeadlessBlocker.Unknown, current, $"Observation timed out after {ObservationTimeout.TotalSeconds:0} s before unattended authentication and a usable session could be established. Configure an explicit authenticated application shell contract if needed; no authentication failure or policy cause is inferred.", true);
                // Where the flow was when observation stopped: a sanitized location, not a cause.
                if (report.Navigation.LastOrDefault() is { } last)
                    report.Observations.Add($"Last observed page when observation stopped: {last.Url}{(last.SecondaryPage ? " (sign-in popup)" : "")}. No interaction was performed there.");
            }
            // Finished reports never leave an observational stage marked Running.
            if (report.Stage(HeadlessStage.NonInteractiveContinuation).State == HeadlessStageState.Running)
                run.Stage(HeadlessStage.NonInteractiveContinuation, HeadlessStageState.Unknown, "Continuation did not reach a verified unattended session.", timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            report.Status = HeadlessRunStatus.Cancelled; report.Readiness = HeadlessReadiness.Unknown;
            report.Interpretation = "Diagnostic cancelled. Unobserved stages were not evaluated.";
            run.Stage(current, HeadlessStageState.Unknown, "Cancelled.", timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            var kind = BrowserAutomationDiagnosticExceptionClassifier.Classify(ex);
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            if (current == HeadlessStage.HeadlessEdgeLaunch) report.HeadlessBrowser = "Unavailable";
            if (current == HeadlessStage.TargetNavigation) report.TargetControl = kind == BrowserAutomationFailureKind.TargetClosed ? "Lost" : "Unknown";
            // Preserve redirect evidence even when the next DOM operation loses its automation channel.
            // This never evaluates authentication after a pre-auth browser/target-control failure.
            if (current > HeadlessStage.TargetNavigation && browser is not null)
                run.Observe(new() { Navigations = browser.DrainNavigation() });
            if (kind == BrowserAutomationFailureKind.Timeout)
                run.Block(HeadlessBlocker.Unknown, current, $"Timeout at {current}; no policy or authentication cause can be concluded.", true);
            else if (current <= HeadlessStage.TargetNavigation)
                run.Block(current == HeadlessStage.TargetNavigation && kind != BrowserAutomationFailureKind.TargetClosed ? HeadlessBlocker.TargetNavigationBlocked : HeadlessBlocker.BrowserAutomationBlocked,
                    current, "Browser launch, navigation or control stopped before authentication could be assessed.");
            else if (kind == BrowserAutomationFailureKind.TargetClosed) run.ControlLost();
            else run.Block(HeadlessBlocker.Unknown, current, "An unexpected browser operation stopped observation. Only the exception type is retained.", true);
            var stage = report.Stage(current);
            run.Stage(current, stage.State == HeadlessStageState.NotRun ? HeadlessStageState.Unknown : stage.State, stage.Detail ?? report.Interpretation, timer.ElapsedMilliseconds, type);
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            try
            {
                if (browser is not null)
                    foreach (var nav in browser.DrainNavigation())
                        if (report.Navigation.Count < 100) report.Navigation.Add(nav.Navigation);
                if (browser is not null) await browser.DisposeAsync();
                // Only the exact fresh run directory, validated again, may be removed.
                if (Directory.Exists(profile))
                {
                    if (!HeadlessDiagnosticPolicy.IsDedicatedProfile(profile)) throw new IOException("Unsafe profile cleanup path.");
                    Directory.Delete(profile, recursive: true);
                }
                run.Stage(HeadlessStage.Cleanup, browser is null ? HeadlessStageState.NotApplicable : HeadlessStageState.Passed, "Only diagnostic-owned browser resources and fresh profile were closed/removed.", cleanup.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                run.Stage(HeadlessStage.Cleanup, HeadlessStageState.Unknown, "Diagnostic cleanup could not be fully confirmed; normal Edge processes were not touched.", cleanup.ElapsedMilliseconds, ex.GetType().Name);
                report.Observations.Add("Diagnostic profile cleanup could not be confirmed. This profile will never be reused.");
            }
            if (entered) _gate.Release();
            report.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var pending in report.Stages.Where(s => s.State == HeadlessStageState.Running).ToList())
                run.Stage(pending.Stage, HeadlessStageState.Unknown, "Observation stopped before this stage completed.", pending.DurationMs, pending.ExceptionType);
            run.Stage(HeadlessStage.HeadlessReadiness, report.Readiness == HeadlessReadiness.Ready ? HeadlessStageState.Passed : report.Readiness == HeadlessReadiness.NotReady ? HeadlessStageState.Blocked : HeadlessStageState.Unknown, report.Interpretation);
            report.Evidence = HeadlessEvidenceRun.Provenance(report, request);
            logger.LogInformation("HeadlessAuthDiagnosticCleanupCompleted {DiagnosticId} {State}", report.DiagnosticId, report.Stage(HeadlessStage.Cleanup).State);
            logger.LogInformation("HeadlessAuthDiagnosticCompleted {DiagnosticId} {Readiness} {Blocker}", report.DiagnosticId, report.Readiness, report.PrimaryBlocker);
        }
        return report;
    }
}
