using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BirkNext.Api.Services.ManagedEdge;
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
    Func<BrowserAutomationDiagnosticMode, string>? profileDirectory = null,
    string controlUrl = BrowserAutomationDiagnosticPolicy.DefaultControlUrl)
    : IBrowserAutomationDiagnosticService
{
    private readonly Func<bool> _isLocalWorkstation = isLocalWorkstation ?? (() => true);
    private readonly Func<BrowserAutomationDiagnosticMode, string> _profileDirectory =
        profileDirectory ?? BrowserAutomationDiagnosticPolicy.ProfileDirectory;

    /// <summary>
    /// One run at a time per Target Environment. Two concurrent runs would share the two mode profiles and spend their
    /// time reporting a profile lock to each other instead of the target's behaviour.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.Ordinal);

    /// <summary>Headed first: if a human is watching, they see the visible run before the invisible one.</summary>
    private static readonly BrowserAutomationDiagnosticMode[] ModeOrder =
        [BrowserAutomationDiagnosticMode.Headed, BrowserAutomationDiagnosticMode.Headless];

    public async Task<BrowserAutomationDiagnosticReport> RunAsync(
        BrowserAutomationDiagnosticRequest request, CancellationToken ct = default)
    {
        var run = new Run(request, controlUrl);
        logger.LogInformation("BrowserAutomationDiagnosticStarted {DiagnosticId} {TargetEnvironmentId}",
            run.DiagnosticId, request.TargetEnvironmentId);

        var profiles = ModeOrder.Select(_profileDirectory).ToList();
        if (BrowserAutomationDiagnosticPolicy.BlockedReason(request, profiles, _isLocalWorkstation()) is { } blocked)
            return Publish(run.Blocked(blocked));

        var lockKey = request.TargetEnvironmentId;
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
        logger.LogInformation("BrowserAutomationComparisonCalculated {DiagnosticId} {Comparison} {HeadlessTargetControl}",
            report.DiagnosticId, report.Result, report.HeadlessTargetControlAvailable);
        return report;
    }

    // ── One mode ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<BrowserAutomationDiagnosticModeReport> RunModeAsync(
        Run run, BrowserAutomationDiagnosticMode mode, EdgeInstallation? edge, CancellationToken ct)
    {
        var modeRun = new ModeRun(mode, _profileDirectory(mode));
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
            // mode report is built after disposal, so Cleanup reflects what happened rather than an intention.
            await browser.DisposeAsync();
            modeRun.Pass(BrowserAutomationDiagnosticStage.Cleanup, "Diagnostic browser closed.");
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
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetNavigation;
        try
        {
            await browser.NavigateAsync(new Uri(run.Request.TargetUrl), BrowserAutomationDiagnosticPolicy.TargetNavigationTimeout, ct);
            modeRun.Pass(BrowserAutomationDiagnosticStage.TargetNavigation,
                "Navigation to the target application was issued.", run.Request.TargetUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            switch (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex))
            {
                case BrowserAutomationFailureKind.TargetClosed:
                    return modeRun.TargetRestricted(BrowserAutomationDiagnosticStage.TargetNavigation, type, run, mode, logger);
                case BrowserAutomationFailureKind.Timeout:
                    return modeRun.TargetTimedOut(BrowserAutomationDiagnosticStage.TargetNavigation, type, run.Request.TargetUrl);
                default:
                    modeRun.Fail(BrowserAutomationDiagnosticStage.TargetNavigation,
                        "Navigation to the target application failed before automation control could be tested.",
                        type, run.Request.TargetUrl);
                    return BrowserAutomationDiagnosticModeResult.Failed;
            }
        }

        // ── Target control ────────────────────────────────────────────────────────────────────────────────────────
        // Navigation returning is not the finding. The spike's browser accepted the navigation and then closed the
        // page, so control is asserted separately, after the fact.
        modeRun.CurrentStage = BrowserAutomationDiagnosticStage.TargetControl;
        try
        {
            if (!await browser.IsControllableAsync(BrowserAutomationDiagnosticPolicy.ControlCheckTimeout, ct))
                return modeRun.TargetRestricted(BrowserAutomationDiagnosticStage.TargetControl, null, run, mode, logger);

            modeRun.Pass(BrowserAutomationDiagnosticStage.TargetControl,
                "Playwright retained control of the target page.", run.Request.TargetUrl);
            logger.LogInformation("{Mode}TargetControlPassed {DiagnosticId}", mode, run.DiagnosticId);
            return BrowserAutomationDiagnosticModeResult.Available;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            if (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex) == BrowserAutomationFailureKind.TargetClosed)
                return modeRun.TargetRestricted(BrowserAutomationDiagnosticStage.TargetControl, type, run, mode, logger);

            modeRun.Fail(BrowserAutomationDiagnosticStage.TargetControl,
                "Automation control of the target page could not be verified.", type, run.Request.TargetUrl);
            return BrowserAutomationDiagnosticModeResult.Failed;
        }
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
        public BrowserAutomationDiagnosticModeResult TargetRestricted(
            BrowserAutomationDiagnosticStage stage, string? exceptionType, Run run,
            BrowserAutomationDiagnosticMode currentMode, ILogger log)
        {
            var type = exceptionType ?? BrowserAutomationDiagnosticExceptionClassifier.TargetClosedExceptionName;
            if (_stages.FirstOrDefault(s => s.Stage == BrowserAutomationDiagnosticStage.ControlPage) is not
                { State: BrowserAutomationDiagnosticStageState.Passed })
            {
                Fail(stage, "Automation control was lost, but the control page had not passed, so this says nothing "
                          + "specific about the target.", type, run.Request.TargetUrl);
                return BrowserAutomationDiagnosticModeResult.ControlFailure;
            }

            ObservedExceptionType ??= type;
            Record(stage, BrowserAutomationDiagnosticStageState.Blocked,
                "The Playwright-controlled page or context was closed while navigating to the target application.",
                run.Request.TargetUrl, type);
            log.LogInformation("{Mode}TargetControlBlocked {DiagnosticId} {Stage} {ExceptionType}",
                currentMode, run.DiagnosticId, stage, type);
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

        private void Record(BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
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
            };
        }
    }

    /// <summary>Accumulates the whole run: both modes, and the comparison they produce.</summary>
    private sealed class Run(BrowserAutomationDiagnosticRequest request, string controlUrl)
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

        private BrowserAutomationDiagnosticReport Build(BrowserAutomationDiagnosticComparison result, string? blockedReason) => new()
        {
            DiagnosticId = DiagnosticId,
            StartedAt = StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Result = result,
            ResultLabel = BrowserAutomationDiagnosticPolicy.ResultLabel(result),
            Interpretation = BrowserAutomationDiagnosticPolicy.Interpretation(result),
            BlockedReason = blockedReason,
            TargetEnvironmentId = Request.TargetEnvironmentId,
            TargetEnvironmentName = Request.TargetEnvironmentName,
            TargetEnvironmentType = Request.EnvironmentType,
            TargetUrl = Request.TargetUrl,
            ControlUrl = ControlUrl,
            EdgeVersion = EdgeVersion ?? _modes.Select(m => m.EdgeVersion).FirstOrDefault(v => v is not null),
            PlaywrightVersion = typeof(IPlaywright).Assembly.GetName().Version?.ToString(),
            OperatingSystem = RuntimeInformation.OSDescription,
            Modes = _modes.Count > 0
                ? _modes
                : [Empty(BrowserAutomationDiagnosticMode.Headed), Empty(BrowserAutomationDiagnosticMode.Headless)],
        };
    }
}
