using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.ManagedEdge;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

public interface IBrowserAutomationDiagnosticService
{
    Task<BrowserAutomationDiagnosticReport> RunAsync(BrowserAutomationDiagnosticRequest request, CancellationToken ct = default);
}

/// <summary>
/// Runs the browser automation diagnostic in BOTH modes: Playwright launches its own Microsoft Edge headed, proves it
/// can control a neutral page, tries the configured target — then does the whole thing again headless.
///
/// Neither mode is inferred from the other, because whether they differ IS the question. Headless is what unattended
/// CI would use, and "it worked when I watched it" is not an answer for CI.
///
/// Within a mode the value is the CONTRAST, so the order is not negotiable: a target failure is only meaningful after
/// the control page has passed, and this class refuses to draw a target-specific conclusion otherwise. It also refuses
/// to name a cause. BirkNext can see that a page closed; it cannot see which control closed it.
/// </summary>
internal sealed class BrowserAutomationDiagnosticService(
    IDiagnosticBrowserFactory browserFactory,
    IEdgeInstallationLocator edgeLocator,
    ILogger<BrowserAutomationDiagnosticService> logger,
    Func<bool>? isLocalWorkstation = null,
    Func<BrowserAutomationDiagnosticRequest, BrowserAutomationDiagnosticMode, string>? profileDirectory = null,
    string controlUrl = BrowserAutomationDiagnosticPolicy.DefaultControlUrl,
    BrowserAutomationTargetObservationTiming? observationTiming = null,
    Func<BrowserAutomationDiagnosticRequest, string?>? applicationMarker = null,
    IEdgePolicyReader? policyReader = null)
    : IBrowserAutomationDiagnosticService
{
    private readonly BrowserAutomationTargetObservationTiming timing =
        observationTiming ?? BrowserAutomationTargetObservationTiming.Default;
    /// <summary>The structural application marker configured for a Target Environment, if any. Never guessed.</summary>
    private readonly Func<BrowserAutomationDiagnosticRequest, string?> _applicationMarker = applicationMarker ?? (_ => null);
    private readonly Func<bool> _isLocalWorkstation = isLocalWorkstation ?? (() => true);
    private readonly Func<BrowserAutomationDiagnosticRequest, BrowserAutomationDiagnosticMode, string> _profileDirectory =
        profileDirectory ?? BrowserAutomationDiagnosticPolicy.ProfileDirectory;

    /// <summary>
    /// One run at a time per PHYSICAL PROFILE SET. Profiles are per target (see
    /// <see cref="BrowserAutomationDiagnosticPolicy.ProfileDirectory"/>), so two different targets run independently,
    /// while two runs that would share a user-data directory can never overlap and report each other's lock.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.Ordinal);

    /// <summary>Headed first: if a human is watching, they see the visible run before the invisible one.</summary>
    private static readonly BrowserAutomationDiagnosticMode[] ModeOrder =
        [BrowserAutomationDiagnosticMode.Headed, BrowserAutomationDiagnosticMode.Headless];

    public async Task<BrowserAutomationDiagnosticReport> RunAsync(
        BrowserAutomationDiagnosticRequest request, CancellationToken ct = default)
    {
        var run = new Run(request, controlUrl, ReadPolicies());
        logger.LogInformation("BrowserAutomationDiagnosticStarted {DiagnosticId} {TargetEnvironmentId}",
            run.DiagnosticId, request.TargetEnvironmentId);

        var profiles = ModeOrder.Select(mode => _profileDirectory(request, mode)).ToList();
        if (BrowserAutomationDiagnosticPolicy.BlockedReason(request, profiles, _isLocalWorkstation()) is { } blocked)
            return Publish(run.Blocked(blocked));

        var lockKey = string.Join("|", profiles.Select(p => p.ToUpperInvariant()));
        if (!Running.TryAdd(lockKey, 0))
            return Publish(run.Blocked(BrowserAutomationDiagnosticPolicy.AlreadyRunningReason));

        try
        {
            var edge = SafeLocateEdge();
            run.EdgeVersion = edge?.Version;

            // Sequential, never parallel: two Edge launches at once on one workstation make the timings and the
            // failures belong to the contention rather than to the target.
            foreach (var mode in ModeOrder)
                run.Add(await RunModeAsync(run, mode, edge, ct));

            return Publish(run.Complete());
        }
        finally
        {
            Running.TryRemove(lockKey, out _);
            logger.LogInformation("BrowserAutomationDiagnosticCleanupCompleted {DiagnosticId}", run.DiagnosticId);
        }
    }

    /// <summary>
    /// Records the comparison, including the single fact the authentication prerequisite is built on. The report is
    /// then returned to the caller; the API surface is what hands it to the authentication diagnostic's evidence
    /// store, so browser capability is established here and consumed there, one way only.
    /// </summary>
    private BrowserAutomationDiagnosticReport Publish(BrowserAutomationDiagnosticReport report)
    {
        logger.LogInformation("BrowserAutomationComparisonCalculated {DiagnosticId} {Comparison} {HeadlessControlAfterTargetNavigation}",
            report.DiagnosticId, report.Result, report.HeadlessAutomationControlAfterTargetNavigation);
        return report;
    }

    // ── One mode ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<BrowserAutomationDiagnosticModeReport> RunModeAsync(
        Run run, BrowserAutomationDiagnosticMode mode, EdgeInstallation? edge, CancellationToken ct)
    {
        var modeRun = new ModeRun(mode, _profileDirectory(run.Request, mode));
        logger.LogInformation("{Mode}DiagnosticStarted {DiagnosticId}", mode, run.DiagnosticId);

        if (edge is null)
        {
            modeRun.Fail(BrowserAutomationDiagnosticStage.Runtime,
                "Microsoft Edge was not found in the standard installation locations.");
            return modeRun.Complete(BrowserAutomationDiagnosticModeResult.RuntimeUnavailable);
        }
        modeRun.Pass(BrowserAutomationDiagnosticStage.Runtime, $"Microsoft Edge {edge.Version ?? "(version unknown)"} found.");

        var browser = browserFactory.Create(mode);
        BrowserAutomationDiagnosticModeResult outcome;
        try
        {
            outcome = await RunStagesAsync(run, modeRun, browser, mode, ct);
        }
        catch (OperationCanceledException)
        {
            outcome = BrowserAutomationDiagnosticModeResult.Cancelled;
        }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            modeRun.Fail(modeRun.CurrentStage, "The diagnostic did not complete in this mode.", type);
            logger.LogWarning("DiagnosticFailed {DiagnosticId} {Mode} {Stage} {ExceptionType}",
                run.DiagnosticId, mode, modeRun.CurrentStage, type);
            outcome = BrowserAutomationDiagnosticModeResult.Failed;
        }
        finally
        {
            // The one thing that must happen on every path, including the one where the target kills the page. The
            // mode report is built after closing, so Cleanup reflects what happened rather than an intention — and a
            // cleanup problem is a WARNING beside the result, never a replacement for it.
            var cleanupFailure = await SafeCloseAsync(browser);
            await browser.DisposeAsync();
            if (cleanupFailure is null)
                modeRun.Pass(BrowserAutomationDiagnosticStage.Cleanup, "Diagnostic browser closed.");
            else
            {
                modeRun.Record(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Warning,
                    "Cleanup warning: closing the diagnostic browser reported an error. The result above is unaffected; "
                    + "only this diagnostic's own browser was closed, and no other Edge process was touched.",
                    null, cleanupFailure);
                logger.LogWarning("{Mode}DiagnosticCleanupWarning {DiagnosticId} {ExceptionType}", mode, run.DiagnosticId, cleanupFailure);
            }
        }

        modeRun.EdgeVersion ??= edge.Version;
        return modeRun.Complete(outcome);
    }

    private async Task<BrowserAutomationDiagnosticModeResult> RunStagesAsync(
        Run run, ModeRun modeRun, IDiagnosticBrowser browser, BrowserAutomationDiagnosticMode mode, CancellationToken ct)
    {
        // ── Launch ────────────────────────────────────────────────────────────────────────────────────────────────
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.EdgeLaunch;
        try
        {
            await browser.LaunchAsync(new BrowserAutomationDiagnosticLaunchOptions(
                mode, modeRun.ProfileDirectory,
                Headless: mode == BrowserAutomationDiagnosticMode.Headless,
                BrowserAutomationDiagnosticPolicy.LaunchTimeout), ct);
            modeRun.EdgeVersion = browser.EdgeVersion;
            modeRun.Pass(BrowserAutomationDiagnosticStage.EdgeLaunch,
                $"Playwright launched Microsoft Edge ({(mode == BrowserAutomationDiagnosticMode.Headless ? "headless" : "headed")}).");
            logger.LogInformation("{Mode}EdgeStarted {DiagnosticId}", mode, run.DiagnosticId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            modeRun.Fail(BrowserAutomationDiagnosticStage.EdgeLaunch,
                "Playwright could not launch Microsoft Edge in this mode.",
                BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex));
            return BrowserAutomationDiagnosticModeResult.RuntimeUnavailable;
        }

        // ── Persistent context ────────────────────────────────────────────────────────────────────────────────────
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.PersistentContext;
        if (!browser.HasPersistentContext)
        {
            modeRun.Fail(BrowserAutomationDiagnosticStage.PersistentContext,
                "Microsoft Edge started but no persistent context was available to automate.");
            return BrowserAutomationDiagnosticModeResult.RuntimeUnavailable;
        }
        modeRun.Pass(BrowserAutomationDiagnosticStage.PersistentContext,
            "A persistent context is open against the dedicated diagnostic profile.");

        // ── about:blank ───────────────────────────────────────────────────────────────────────────────────────────
        // A page that closes HERE is a runtime or control problem. It is never a statement about the target, which has
        // not been contacted — the contrast the diagnostic exists to draw does not exist yet.
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.BlankPage;
        try
        {
            if (!await browser.IsControllableAsync(BrowserAutomationDiagnosticPolicy.ControlCheckTimeout, ct))
            {
                modeRun.Fail(BrowserAutomationDiagnosticStage.BlankPage, "The blank page was not under automation control.");
                return BrowserAutomationDiagnosticModeResult.ControlFailure;
            }
            modeRun.Pass(BrowserAutomationDiagnosticStage.BlankPage, "about:blank is under automation control.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            modeRun.Fail(BrowserAutomationDiagnosticStage.BlankPage, "Automation control was lost on about:blank.",
                BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex));
            return BrowserAutomationDiagnosticModeResult.ControlFailure;
        }

        // ── Control page ──────────────────────────────────────────────────────────────────────────────────────────
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.ControlPage;
        try
        {
            await browser.NavigateAsync(new Uri(run.ControlUrl), BrowserAutomationDiagnosticPolicy.ControlNavigationTimeout, ct);
            if (!await browser.IsControllableAsync(BrowserAutomationDiagnosticPolicy.ControlCheckTimeout, ct))
            {
                modeRun.Fail(BrowserAutomationDiagnosticStage.ControlPage,
                    "The control page was not under automation control after navigating.", url: run.ControlUrl);
                return BrowserAutomationDiagnosticModeResult.ControlFailure;
            }
            modeRun.Pass(BrowserAutomationDiagnosticStage.ControlPage,
                "The control page stayed under automation control.", run.ControlUrl);
            logger.LogInformation("{Mode}ControlPagePassed {DiagnosticId}", mode, run.DiagnosticId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A control page that cannot be REACHED is a network problem, not an automation one, and saying so is the
            // difference between a useful report and one that blames Playwright for a proxy.
            modeRun.Fail(BrowserAutomationDiagnosticStage.ControlPage,
                "The control page could not be reached or kept under control. This is not evidence about the target: "
                + "it may be network, TLS or proxy restrictions on the control URL.",
                BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex), run.ControlUrl);
            return BrowserAutomationDiagnosticModeResult.ControlFailure;
        }

        // ── Target ────────────────────────────────────────────────────────────────────────────────────────────────
        // Four questions, answered separately because each can be true while the next is false: did the browser
        // accept the navigation; is the browser still controllable once it has settled wherever the target sent it;
        // does it STAY controllable; and is the page it controls actually the target application.
        var target = new TargetObservation(run.Request, _applicationMarker(run.Request), timing);
        browser.BeginTargetObservation();

        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetNavigation;
        try
        {
            await browser.NavigateAsync(new Uri(run.Request.TargetUrl), BrowserAutomationDiagnosticPolicy.TargetNavigationTimeout, ct);
            target.NavigationReturnedAtMs = browser.ObservationElapsedMs;
            modeRun.Pass(BrowserAutomationDiagnosticStage.TargetNavigation,
                "The browser accepted the navigation to the target URL. This says nothing yet about where the page ended up.",
                target.RequestedUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            // The message is read once, here, by the classifier — and only its anchored browser error code leaves.
            var failure = BrowserNavigationFailureClassifier.Describe(ex, BrowserAutomationDiagnosticStage.TargetNavigation,
                BrowserAutomationDiagnosticPolicy.TargetNavigationTimeout);
            modeRun.Target = target.Build(browser, BrowserAutomationFailurePhase.DuringTargetNavigation, type) with
            {
                NavigationFailure = failure,
            };
            logger.LogWarning("{Mode}TargetNavigationFailed {DiagnosticId} {TargetEnvironmentId} {Stage} {ExceptionType} {BrowserErrorCode} {FailureCategory}",
                mode, run.DiagnosticId, run.Request.TargetEnvironmentId, failure.ObservedAtStage, failure.ExceptionType,
                failure.BrowserErrorCode ?? "none", failure.Category);
            switch (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex))
            {
                case BrowserAutomationFailureKind.TargetClosed:
                    return modeRun.TargetRestricted(BrowserAutomationDiagnosticStage.TargetNavigation, type,
                        BrowserAutomationFailurePhase.DuringTargetNavigation, run, mode, logger);
                case BrowserAutomationFailureKind.Timeout:
                    return modeRun.TargetTimedOut(BrowserAutomationDiagnosticStage.TargetNavigation, type, target.RequestedUrl);
                default:
                    modeRun.Fail(BrowserAutomationDiagnosticStage.TargetNavigation,
                        "Navigation to the target application failed before automation control could be tested.",
                        type, target.RequestedUrl);
                    return BrowserAutomationDiagnosticModeResult.Failed;
            }
        }

        // ── Browser control after navigation ──────────────────────────────────────────────────────────────────────
        // Not read at commit: an MSAL single-page app redirects to its identity provider from script after it boots,
        // so the URL at commit is the app shell and says nothing about where the browser will be a moment later.
        var phase = BrowserAutomationFailurePhase.AfterTargetNavigation;
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetControl;
        try
        {
            target.Settle = await browser.WaitForNavigationToSettleAsync(timing.QuietPeriod, timing.SettleBound, ct);
            if (target.Settle.Outcome == DiagnosticSettleOutcome.Terminated)
                return target.Restricted(modeRun, browser, BrowserAutomationDiagnosticStage.TargetControl, null, phase, run, mode, logger);

            var first = await browser.ProbeAsync(target.MarkerSelector, BrowserAutomationDiagnosticPolicy.ControlCheckTimeout, ct);
            if (first.PageClosed)
                return target.Restricted(modeRun, browser, BrowserAutomationDiagnosticStage.TargetControl, null, phase, run, mode, logger);
            target.Observe(first);
            target.ControlProvenAtMs = browser.ObservationElapsedMs;

            modeRun.Pass(BrowserAutomationDiagnosticStage.TargetControl,
                "After navigation settled, Playwright read the page title and document element. This is about the "
                + "browser, wherever it ended up — not about which application the page is.", target.LastUrl);

            // ── Stability ─────────────────────────────────────────────────────────────────────────────────────────
            phase = BrowserAutomationFailurePhase.DuringStabilityWindow;
            modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetStability;
            if (!await browser.ObserveStabilityAsync(timing.StabilityWindow, ct))
                return target.Restricted(modeRun, browser, BrowserAutomationDiagnosticStage.TargetStability, null, phase, run, mode, logger);

            // A navigation during the window (a late redirect) gets the same bounded settle before the final read.
            var late = await browser.WaitForNavigationToSettleAsync(timing.QuietPeriod, timing.SettleBound, ct);
            if (late.Outcome == DiagnosticSettleOutcome.Terminated)
                return target.Restricted(modeRun, browser, BrowserAutomationDiagnosticStage.TargetStability, null, phase, run, mode, logger);

            var second = await browser.ProbeAsync(target.MarkerSelector, BrowserAutomationDiagnosticPolicy.ControlCheckTimeout, ct);
            if (second.PageClosed)
                return target.Restricted(modeRun, browser, BrowserAutomationDiagnosticStage.TargetStability, null, phase, run, mode, logger);
            target.Observe(second);

            modeRun.Pass(BrowserAutomationDiagnosticStage.TargetStability,
                $"No page close, crash, context close or disconnect during the {timing.StabilityWindow.TotalSeconds:0.#} s "
                + "observation window, and a second probe succeeded afterwards. A close after the window cannot be excluded.",
                target.LastUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            var stage = modeRun.CurrentStage;
            if (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex) == BrowserAutomationFailureKind.TargetClosed)
                return target.Restricted(modeRun, browser, stage, type, phase, run, mode, logger);

            modeRun.Target = target.Build(browser, phase, type);
            modeRun.Fail(stage, "Automation control after the target navigation could not be verified.", type, target.LastUrl);
            return BrowserAutomationDiagnosticModeResult.Failed;
        }

        // ── Target application ────────────────────────────────────────────────────────────────────────────────────
        // Control is proven. Which page is being controlled is a separate question, answered from where the browser
        // actually is — never from where it was asked to go.
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetApplication;
        var evidence = target.Build(browser, BrowserAutomationFailurePhase.None, null);
        modeRun.Target = evidence;
        var (state, detail) = BrowserAutomationDiagnosticPolicy.TargetApplicationStage(evidence);
        modeRun.Record(BrowserAutomationDiagnosticStage.TargetApplication, state, detail, evidence.FinalUrl);
        logger.LogInformation("{Mode}TargetObserved {DiagnosticId} {FinalLocation} {FinalHost} {AuthenticationRedirect} {Identified}",
            mode, run.DiagnosticId, evidence.FinalLocation, evidence.FinalHost, evidence.AuthenticationRedirect,
            evidence.TargetApplicationIdentified);

        return evidence switch
        {
            { TargetApplicationIdentified: BrowserAutomationEvidenceAnswer.Yes } => BrowserAutomationDiagnosticModeResult.Available,
            { FinalLocation: BrowserAutomationFinalLocation.AuthenticationAuthority } =>
                BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary,
            _ => BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed,
        };
    }

    /// <summary>
    /// Accumulates what one mode sees at the target and turns it into sanitized evidence. Raw URLs live only in here
    /// and in the browser; nothing leaves without going through <see cref="BrowserAutomationTargetLocationPolicy.Sanitize"/>.
    /// </summary>
    private sealed class TargetObservation(
        BrowserAutomationDiagnosticRequest request, string? markerSelector, BrowserAutomationTargetObservationTiming timing)
    {
        private readonly Uri _target = new(request.TargetUrl);
        private DiagnosticPageProbe? _last;

        public string? MarkerSelector { get; } = string.IsNullOrWhiteSpace(markerSelector) ? null : markerSelector;
        public DiagnosticSettleResult? Settle { get; set; }
        public string RequestedUrl => BrowserAutomationTargetLocationPolicy.Sanitize(request.TargetUrl);
        public string? LastUrl => _last is null ? null : SanitizeObserved(_last.Url);

        /// <summary>When, on the observation clock, the navigation call returned and the first probe succeeded.</summary>
        public long? NavigationReturnedAtMs { get; set; }
        public long? ControlProvenAtMs { get; set; }

        public void Observe(DiagnosticPageProbe probe) => _last = probe;

        /// <summary>Which part of the target observation a lifecycle signal arrived in, from its timestamp.</summary>
        private BrowserAutomationFailurePhase PhaseAt(long atMs) =>
            NavigationReturnedAtMs is not { } returned || atMs <= returned ? BrowserAutomationFailurePhase.DuringTargetNavigation
            : ControlProvenAtMs is not { } proven || atMs <= proven ? BrowserAutomationFailurePhase.AfterTargetNavigation
            : BrowserAutomationFailurePhase.DuringStabilityWindow;

        public BrowserAutomationDiagnosticModeResult Restricted(
            ModeRun modeRun, IDiagnosticBrowser browser, BrowserAutomationDiagnosticStage stage, string? exceptionType,
            BrowserAutomationFailurePhase phase, Run run, BrowserAutomationDiagnosticMode mode, ILogger log)
        {
            modeRun.Target = Build(browser, phase, exceptionType);
            return modeRun.TargetRestricted(stage, exceptionType, phase, run, mode, log);
        }

        public BrowserAutomationTargetEvidence Build(IDiagnosticBrowser browser, BrowserAutomationFailurePhase phase, string? exceptionType)
        {
            var raw = browser.NavigationTrace;
            var trace = raw
                .Select((n, i) => new BrowserAutomationNavigationStep(i + 1, n.AtMs, SanitizeObserved(n.Url), Classify(n.Url), n.SecondaryPage))
                .ToList();

            // The final location is the last thing the diagnostic READ from a live page. When the page died before it
            // could be read, the last navigation the browser reported is the best available answer — and it is
            // labelled as such by FinalLocation being taken from the trace, never from the requested URL.
            // The final location is always the diagnostic's OWN page; a popup is reported in the trace, not as "final".
            // The browser's own error page is not a final location either: after a failed navigation Edge may commit
            // it before the navigation call throws, and reading it as "final" would report an origin nobody reached.
            var finalRaw = _last?.Url ?? raw.LastOrDefault(n => !n.SecondaryPage
                && !BrowserAutomationTargetLocationPolicy.IsBrowserErrorPage(n.Url))?.Url;
            Uri.TryCreate(finalRaw, UriKind.Absolute, out var final);
            var location = Classify(finalRaw);
            var observed = location != BrowserAutomationFinalLocation.Unknown;

            var authStep = trace.FirstOrDefault(s => s.Location == BrowserAutomationFinalLocation.AuthenticationAuthority);
            var authHost = location == BrowserAutomationFinalLocation.AuthenticationAuthority ? final!.IdnHost
                : authStep is not null && Uri.TryCreate(raw[authStep.Sequence - 1].Url, UriKind.Absolute, out var a) ? a.IdnHost
                : null;
            var authInPopup = location != BrowserAutomationFinalLocation.AuthenticationAuthority && authStep is { SecondaryPage: true };
            var sessionHost = raw.Select(n => Uri.TryCreate(n.Url, UriKind.Absolute, out var u) ? u : null)
                .FirstOrDefault(u => u is not null && BrowserAutomationTargetLocationPolicy.IsSessionControlProxy(u))?.IdnHost;

            var markerFound = location == BrowserAutomationFinalLocation.TargetOrigin ? _last?.MarkerFound : null;
            var controlRetained = phase == BrowserAutomationFailurePhase.None && _last is not null;
            var (identified, basis) = Identify(location, controlRetained, markerFound);

            return new BrowserAutomationTargetEvidence
            {
                RequestedUrl = RequestedUrl,
                ExpectedOrigin = BrowserAutomationTargetLocationPolicy.CanonicalOrigin(_target) ?? "",
                FinalUrl = finalRaw is null ? null : SanitizeObserved(finalRaw),
                FinalScheme = final?.Scheme,
                FinalHost = final is { Scheme: "http" or "https" } ? final.IdnHost : null,
                FinalOrigin = final is null ? null : BrowserAutomationTargetLocationPolicy.CanonicalOrigin(final),
                FinalLocation = location,
                ExpectedOriginReached = !observed ? BrowserAutomationEvidenceAnswer.Unknown
                    : location == BrowserAutomationFinalLocation.TargetOrigin ? BrowserAutomationEvidenceAnswer.Yes
                    : BrowserAutomationEvidenceAnswer.No,
                AuthenticationRedirect = authHost is not null ? BrowserAutomationEvidenceAnswer.Yes
                    : observed ? BrowserAutomationEvidenceAnswer.No : BrowserAutomationEvidenceAnswer.Unknown,
                AuthenticationHost = authHost,
                AuthenticationInSecondaryPage = authInPopup,
                SessionControlHost = sessionHost,
                TargetApplicationIdentified = identified,
                IdentificationEvidence = basis,
                ApplicationMarkerConfigured = MarkerSelector is not null,
                ApplicationMarkerFound = markerFound,
                NavigationSettled = Settle is null ? null : Settle.Outcome == DiagnosticSettleOutcome.Settled,
                SettleDurationMs = Settle?.DurationMs,
                StabilityWindowMs = (long)timing.StabilityWindow.TotalMilliseconds,
                NavigationTrace = trace,
                LifecycleEvents = browser.LifecycleEvents
                    .Select(e => new BrowserAutomationLifecycleEvent(e.Kind, e.AtMs, PhaseAt(e.AtMs)))
                    .ToList(),
                ExceptionType = exceptionType,
                FailurePhase = phase,
            };
        }

        /// <summary>
        /// The evidence hierarchy: the expected origin first, then — when one is configured for this Target
        /// Environment — a structural application marker. Authenticated content is never required: before sign-in it
        /// cannot exist, and demanding it would make every pre-authentication run "not identified".
        /// </summary>
        private (BrowserAutomationEvidenceAnswer, string) Identify(
            BrowserAutomationFinalLocation location, bool controlRetained, bool? markerFound)
        {
            if (!controlRetained) return (BrowserAutomationEvidenceAnswer.Unknown, "Not assessed: automation control was not retained.");
            return location switch
            {
                BrowserAutomationFinalLocation.TargetOrigin when MarkerSelector is null =>
                    (BrowserAutomationEvidenceAnswer.Yes,
                        "Expected target origin. No application marker is configured for this Target Environment, so the origin is the only evidence."),
                BrowserAutomationFinalLocation.TargetOrigin when markerFound == true =>
                    (BrowserAutomationEvidenceAnswer.Yes, "Expected target origin and the configured application marker."),
                BrowserAutomationFinalLocation.TargetOrigin =>
                    (BrowserAutomationEvidenceAnswer.Unknown,
                        "Expected target origin, but the configured application marker was not found."),
                BrowserAutomationFinalLocation.AuthenticationAuthority =>
                    (BrowserAutomationEvidenceAnswer.No, "Not yet reached: the target redirected to authentication first."),
                BrowserAutomationFinalLocation.SessionControlProxy =>
                    (BrowserAutomationEvidenceAnswer.No, "The page is delivered through a session-control proxy host, not the target origin."),
                BrowserAutomationFinalLocation.OtherOrigin =>
                    (BrowserAutomationEvidenceAnswer.No, "The final location is outside the expected origin and is not a recognised authentication authority."),
                _ => (BrowserAutomationEvidenceAnswer.Unknown, "The final location could not be read."),
            };
        }

        private BrowserAutomationFinalLocation Classify(string? url) =>
            BrowserAutomationTargetLocationPolicy.Classify(url, _target, request.Authority);

        private string SanitizeObserved(string url) =>
            BrowserAutomationTargetLocationPolicy.Sanitize(url, Classify(url) == BrowserAutomationFinalLocation.AuthenticationAuthority);
    }

    /// <summary>Closes the browser and reports the exception TYPE if closing failed. Never throws.</summary>
    private static async Task<string?> SafeCloseAsync(IDiagnosticBrowser browser)
    {
        try { return await browser.CloseAsync(); }
        catch (Exception ex) { return BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex); }
    }

    /// <summary>Read-only Edge policy values, read once per run. A reader failure is "Unknown", never a guess.</summary>
    private BrowserAutomationPolicySnapshot ReadPolicies()
    {
        if (policyReader is null) return BrowserAutomationPolicySnapshot.Unknown;
        EdgeRemoteDebuggingPolicyStatus remote;
        EdgeDeveloperToolsPolicyStatus devTools;
        try { remote = policyReader.ReadRemoteDebuggingPolicy(); } catch { remote = EdgeRemoteDebuggingPolicyStatus.Unknown; }
        try { devTools = policyReader.ReadDeveloperToolsPolicy(); } catch { devTools = EdgeDeveloperToolsPolicyStatus.Unknown; }
        return new(remote, devTools);
    }

    private EdgeInstallation? SafeLocateEdge()
    {
        try { return edgeLocator.Locate(); }
        catch { return null; }
    }

    // ── Accumulators ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Accumulates one MODE's stage results and turns them into that mode's report.</summary>
    private sealed class ModeRun(BrowserAutomationDiagnosticMode mode, string profileDirectory)
    {
        private readonly List<BrowserAutomationDiagnosticStageResult> _stages = [];
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public string ProfileDirectory { get; } = profileDirectory;
        public string? EdgeVersion { get; set; }
        public string? ObservedExceptionType { get; private set; }
        public BrowserAutomationTargetEvidence? Target { get; set; }
        public BrowserAutomationDiagnosticStage CurrentStage { get; set; } = BrowserAutomationDiagnosticStage.Runtime;

        public void Pass(BrowserAutomationDiagnosticStage stage, string detail, string? url = null) =>
            Record(stage, BrowserAutomationDiagnosticStageState.Passed, detail, url);

        public void Fail(BrowserAutomationDiagnosticStage stage, string detail, string? exceptionType = null, string? url = null)
        {
            ObservedExceptionType ??= exceptionType;
            Record(stage, BrowserAutomationDiagnosticStageState.Failed, detail, url, exceptionType);
        }

        /// <summary>
        /// The one conclusion a mode exists to reach — and the one it is most tempting to reach too early. It requires
        /// the control page to have passed, because without that contrast a closed target page is just a browser that
        /// cannot be automated at all.
        /// </summary>
        /// <para>
        /// The exception type is reported only when there WAS one. A close observed through Page.Close with no
        /// exception is reported as the event it was, not relabelled as a TargetClosedException nobody threw.
        /// </para>
        public BrowserAutomationDiagnosticModeResult TargetRestricted(
            BrowserAutomationDiagnosticStage stage, string? exceptionType, BrowserAutomationFailurePhase phase, Run run,
            BrowserAutomationDiagnosticMode currentMode, ILogger log)
        {
            var url = Target?.FinalUrl ?? Target?.RequestedUrl;
            if (_stages.FirstOrDefault(s => s.Stage == BrowserAutomationDiagnosticStage.ControlPage) is not
                { State: BrowserAutomationDiagnosticStageState.Passed })
            {
                Fail(stage, "Automation control was lost, but the control page had not passed, so this says nothing "
                          + "specific about the target.", exceptionType, url);
                return BrowserAutomationDiagnosticModeResult.ControlFailure;
            }

            ObservedExceptionType ??= exceptionType;
            Record(stage, BrowserAutomationDiagnosticStageState.Blocked,
                BrowserAutomationDiagnosticPolicy.RestrictionDetail(phase, exceptionType, Target), url, exceptionType);
            log.LogInformation("{Mode}TargetControlBlocked {DiagnosticId} {Stage} {Phase} {ExceptionType}",
                currentMode, run.DiagnosticId, stage, phase, exceptionType);
            return BrowserAutomationDiagnosticModeResult.TargetRestricted;
        }

        /// <summary>
        /// A target that did not answer in time. Kept apart from the restricted case on purpose: a timeout is an
        /// absence of a response, not an observation that automation was terminated, and reporting it as a restriction
        /// would hand IT a conclusion the run does not support.
        /// </summary>
        public BrowserAutomationDiagnosticModeResult TargetTimedOut(
            BrowserAutomationDiagnosticStage stage, string exceptionType, string targetUrl)
        {
            Fail(stage, "The target application did not respond within the navigation timeout. This is not evidence "
                      + "of an automation restriction: nothing was observed about automation control.",
                exceptionType, targetUrl);
            return BrowserAutomationDiagnosticModeResult.Failed;
        }

        public void Record(BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
            string? detail, string? url, string? exceptionType = null)
        {
            if (_stages.Any(s => s.Stage == stage)) return;   // a stage reports once
            _stages.Add(new BrowserAutomationDiagnosticStageResult(stage, state, detail, url, exceptionType, _stopwatch.ElapsedMilliseconds));
        }

        public BrowserAutomationDiagnosticModeReport Complete(BrowserAutomationDiagnosticModeResult result)
        {
            // Every stage appears, in order, so "not run" is visible rather than absent.
            var stages = Enum.GetValues<BrowserAutomationDiagnosticStage>()
                .Select(stage => _stages.FirstOrDefault(s => s.Stage == stage)
                                 ?? new BrowserAutomationDiagnosticStageResult(stage, BrowserAutomationDiagnosticStageState.NotRun))
                .ToList();

            return new BrowserAutomationDiagnosticModeReport
            {
                Mode = mode,
                Result = result,
                Headless = mode == BrowserAutomationDiagnosticMode.Headless,
                EdgeVersion = EdgeVersion,
                ProfileDescription = BrowserAutomationDiagnosticPolicy.ProfileDescription(mode),
                Stages = stages,
                ObservedExceptionType = ObservedExceptionType,
                Target = Target,
            };
        }
    }

    /// <summary>Accumulates the whole run: both modes, and the comparison they produce.</summary>
    private sealed class Run(BrowserAutomationDiagnosticRequest request, string controlUrl, BrowserAutomationPolicySnapshot policies)
    {
        private readonly List<BrowserAutomationDiagnosticModeReport> _modes = [];

        public string DiagnosticId { get; } = Guid.NewGuid().ToString("N")[..12];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public BrowserAutomationDiagnosticRequest Request { get; } = request;
        public string ControlUrl { get; } = controlUrl;
        public string? EdgeVersion { get; set; }

        public void Add(BrowserAutomationDiagnosticModeReport mode) => _modes.Add(mode);

        public BrowserAutomationDiagnosticReport Blocked(string reason) => Build(BrowserAutomationDiagnosticComparison.Blocked, reason);

        public BrowserAutomationDiagnosticReport Complete()
        {
            var headed = _modes.FirstOrDefault(m => m.Mode == BrowserAutomationDiagnosticMode.Headed)
                         ?? Empty(BrowserAutomationDiagnosticMode.Headed);
            var headless = _modes.FirstOrDefault(m => m.Mode == BrowserAutomationDiagnosticMode.Headless)
                           ?? Empty(BrowserAutomationDiagnosticMode.Headless);
            return Build(BrowserAutomationDiagnosticPolicy.Compare(headed, headless), null);
        }

        private static BrowserAutomationDiagnosticModeReport Empty(BrowserAutomationDiagnosticMode mode) => new()
        {
            Mode = mode,
            Headless = mode == BrowserAutomationDiagnosticMode.Headless,
            ProfileDescription = BrowserAutomationDiagnosticPolicy.ProfileDescription(mode),
            Stages = Enum.GetValues<BrowserAutomationDiagnosticStage>()
                .Select(stage => new BrowserAutomationDiagnosticStageResult(stage, BrowserAutomationDiagnosticStageState.NotRun))
                .ToList(),
        };

        private BrowserAutomationDiagnosticReport Build(BrowserAutomationDiagnosticComparison result, string? blockedReason)
        {
            var modes = _modes.Count > 0
                ? _modes
                : [Empty(BrowserAutomationDiagnosticMode.Headed), Empty(BrowserAutomationDiagnosticMode.Headless)];
            var headed = modes.FirstOrDefault(m => m.Mode == BrowserAutomationDiagnosticMode.Headed);
            var headless = modes.FirstOrDefault(m => m.Mode == BrowserAutomationDiagnosticMode.Headless);
            return new()
        {
            DiagnosticId = DiagnosticId,
            StartedAt = StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Result = result,
            ResultLabel = BrowserAutomationDiagnosticPolicy.ResultLabel(result),
            Interpretation = BrowserAutomationDiagnosticPolicy.Interpretation(result, headed, headless),
            BlockedReason = blockedReason,
            TargetEnvironmentId = Request.TargetEnvironmentId,
            TargetEnvironmentName = Request.TargetEnvironmentName,
            TargetEnvironmentType = Request.EnvironmentType,
            // Sanitized: a configured URL is not assumed to be free of query parameters worth protecting.
            TargetUrl = BrowserAutomationTargetLocationPolicy.Sanitize(Request.TargetUrl),
            ControlUrl = ControlUrl,
            EdgeVersion = EdgeVersion ?? _modes.Select(m => m.EdgeVersion).FirstOrDefault(v => v is not null),
            PlaywrightVersion = typeof(IPlaywright).Assembly.GetName().Version?.ToString(),
            OperatingSystem = RuntimeInformation.OSDescription,
            Modes = modes,
            ControlDimensions = BrowserAutomationDiagnosticPolicy.ControlDimensions(result, headed, headless, policies),
            CorrelationTargetUrl = Request.TargetUrl,
        };
        }
    }
}
