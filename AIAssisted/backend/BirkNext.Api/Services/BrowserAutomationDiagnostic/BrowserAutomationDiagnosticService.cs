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
/// Runs the browser automation diagnostic: Playwright launches its own Microsoft Edge, proves it can control a neutral
/// page, then tries the configured target and reports whether control survived.
///
/// The value of the run is the CONTRAST, so the order is not negotiable: a target failure is only meaningful after the
/// control page has passed, and this class refuses to draw a target-specific conclusion otherwise. It also refuses to
/// name a cause. BirkNext can see that a page closed; it cannot see which control closed it.
/// </summary>
internal sealed class BrowserAutomationDiagnosticService(
    IDiagnosticBrowserFactory browserFactory,
    IEdgeInstallationLocator edgeLocator,
    ILogger<BrowserAutomationDiagnosticService> logger,
    Func<bool>? isLocalWorkstation = null,
    Func<string>? profileDirectory = null,
    string controlUrl = BrowserAutomationDiagnosticPolicy.DefaultControlUrl)
    : IBrowserAutomationDiagnosticService
{
    private readonly Func<bool> _isLocalWorkstation = isLocalWorkstation ?? (() => true);
    private readonly Func<string> _profileDirectory = profileDirectory ?? BrowserAutomationDiagnosticPolicy.DefaultProfileDirectory;

    public async Task<BrowserAutomationDiagnosticReport> RunAsync(
        BrowserAutomationDiagnosticRequest request, CancellationToken ct = default)
    {
        var run = new Run(request, _profileDirectory(), controlUrl);
        logger.LogInformation("BrowserAutomationDiagnosticStarted {DiagnosticId} {TargetEnvironmentId}",
            run.DiagnosticId, request.TargetEnvironmentId);

        if (BrowserAutomationDiagnosticPolicy.BlockedReason(request, run.ProfileDirectory, _isLocalWorkstation()) is { } blocked)
        {
            logger.LogInformation("DiagnosticFailed {DiagnosticId} {Stage} Blocked", run.DiagnosticId, BrowserAutomationDiagnosticStage.Runtime);
            return run.Blocked(blocked);
        }

        var edge = SafeLocateEdge();
        if (edge is null)
        {
            run.Fail(BrowserAutomationDiagnosticStage.Runtime,
                "Microsoft Edge was not found in the standard installation locations.");
            return run.Complete(BrowserAutomationDiagnosticResult.RuntimeUnavailable);
        }
        run.EdgeVersion = edge.Version;
        run.Pass(BrowserAutomationDiagnosticStage.Runtime, $"Microsoft Edge {edge.Version ?? "(version unknown)"} found.");

        // Cleanup is the one thing that must happen on every path, including the one where the target kills the page.
        // The stages decide the OUTCOME; the report is built once, at the end, after the browser is actually gone —
        // otherwise the Cleanup stage would be recorded after the report that is supposed to contain it.
        var browser = browserFactory.Create();
        BrowserAutomationDiagnosticResult outcome;
        try
        {
            outcome = await RunStagesAsync(run, browser, ct);
        }
        catch (OperationCanceledException)
        {
            outcome = BrowserAutomationDiagnosticResult.Cancelled;
        }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            run.Fail(run.CurrentStage, "The diagnostic did not complete.", type);
            logger.LogWarning("DiagnosticFailed {DiagnosticId} {Stage} {ExceptionType}", run.DiagnosticId, run.CurrentStage, type);
            outcome = BrowserAutomationDiagnosticResult.Failed;
        }
        finally
        {
            await browser.DisposeAsync();
            run.Pass(BrowserAutomationDiagnosticStage.Cleanup, "Diagnostic browser closed.");
            logger.LogInformation("DiagnosticCleanupCompleted {DiagnosticId}", run.DiagnosticId);
        }

        return run.Complete(outcome);
    }

    private async Task<BrowserAutomationDiagnosticResult> RunStagesAsync(Run run, IDiagnosticBrowser browser, CancellationToken ct)
    {
        // ── Launch ────────────────────────────────────────────────────────────────────────────────────────────────
        run.CurrentStage = BrowserAutomationDiagnosticStage.EdgeLaunch;
        try
        {
            await browser.LaunchAsync(run.ProfileDirectory, BrowserAutomationDiagnosticPolicy.LaunchTimeout, ct);
            run.EdgeVersion ??= browser.EdgeVersion;
            run.Pass(BrowserAutomationDiagnosticStage.EdgeLaunch, "Playwright launched a persistent Microsoft Edge context.");
            logger.LogInformation("DiagnosticEdgeLaunchSucceeded {DiagnosticId}", run.DiagnosticId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            run.Fail(BrowserAutomationDiagnosticStage.EdgeLaunch,
                "Playwright could not launch Microsoft Edge with a persistent context.",
                BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex));
            return BrowserAutomationDiagnosticResult.RuntimeUnavailable;
        }

        // ── about:blank ───────────────────────────────────────────────────────────────────────────────────────────
        // A page that closes HERE is a runtime or control problem. It is never a statement about the target, which
        // has not been contacted — the contrast the diagnostic exists to draw does not exist yet.
        run.CurrentStage = BrowserAutomationDiagnosticStage.BlankPage;
        try
        {
            if (!await browser.IsControllableAsync(ct))
            {
                run.Fail(BrowserAutomationDiagnosticStage.BlankPage, "The blank page was not under automation control.");
                return BrowserAutomationDiagnosticResult.ControlFailure;
            }
            run.Pass(BrowserAutomationDiagnosticStage.BlankPage, "about:blank is under automation control.");
            logger.LogInformation("DiagnosticBlankPageSucceeded {DiagnosticId}", run.DiagnosticId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A closed page HERE is the runtime failing, not the target: nothing has touched the target yet.
            run.Fail(BrowserAutomationDiagnosticStage.BlankPage,
                "Automation control was lost on about:blank.", BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex));
            return BrowserAutomationDiagnosticResult.ControlFailure;
        }

        // ── Control page ──────────────────────────────────────────────────────────────────────────────────────────
        run.CurrentStage = BrowserAutomationDiagnosticStage.ControlPage;
        try
        {
            await browser.NavigateAsync(new Uri(run.ControlUrl), BrowserAutomationDiagnosticPolicy.ControlNavigationTimeout, ct);
            if (!await browser.IsControllableAsync(ct))
            {
                run.Fail(BrowserAutomationDiagnosticStage.ControlPage,
                    "The control page was not under automation control after navigating.", url: run.ControlUrl);
                return BrowserAutomationDiagnosticResult.ControlFailure;
            }
            run.Pass(BrowserAutomationDiagnosticStage.ControlPage, "The control page stayed under automation control.", run.ControlUrl);
            logger.LogInformation("DiagnosticControlPageSucceeded {DiagnosticId}", run.DiagnosticId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A control page that cannot be REACHED is a network problem, not an automation one, and saying so is the
            // difference between a useful report and one that blames Playwright for a proxy.
            run.Fail(BrowserAutomationDiagnosticStage.ControlPage,
                "The control page could not be reached or kept under control. This is not evidence about the target: "
                + "it may be network, TLS or proxy restrictions on the control URL.",
                BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex), run.ControlUrl);
            return BrowserAutomationDiagnosticResult.ControlFailure;
        }

        // ── Target ────────────────────────────────────────────────────────────────────────────────────────────────
        run.CurrentStage = BrowserAutomationDiagnosticStage.TargetNavigation;
        logger.LogInformation("DiagnosticTargetNavigationStarted {DiagnosticId} {TargetEnvironmentId}",
            run.DiagnosticId, run.Request.TargetEnvironmentId);
        try
        {
            await browser.NavigateAsync(new Uri(run.Request.TargetUrl), BrowserAutomationDiagnosticPolicy.TargetNavigationTimeout, ct);
            run.Pass(BrowserAutomationDiagnosticStage.TargetNavigation, "Navigation to the target application was issued.", run.Request.TargetUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            switch (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex))
            {
                case BrowserAutomationFailureKind.TargetClosed:
                    return run.TargetRestricted(BrowserAutomationDiagnosticStage.TargetNavigation, type, logger);
                case BrowserAutomationFailureKind.Timeout:
                    return run.TargetTimedOut(BrowserAutomationDiagnosticStage.TargetNavigation, type);
                default:
                    run.Fail(BrowserAutomationDiagnosticStage.TargetNavigation,
                        "Navigation to the target application failed before automation control could be tested.",
                        type, run.Request.TargetUrl);
                    return BrowserAutomationDiagnosticResult.Failed;
            }
        }

        // ── Target control ────────────────────────────────────────────────────────────────────────────────────────
        // Navigation returning is not the finding. The spike's browser accepted the navigation and then closed the
        // page, so control is asserted separately, after the fact.
        run.CurrentStage = BrowserAutomationDiagnosticStage.TargetControl;
        try
        {
            if (!await browser.IsControllableAsync(ct))
                return run.TargetRestricted(BrowserAutomationDiagnosticStage.TargetControl, null, logger);

            run.Pass(BrowserAutomationDiagnosticStage.TargetControl, "Playwright retained control of the target page.", run.Request.TargetUrl);
            logger.LogInformation("DiagnosticTargetSucceeded {DiagnosticId}", run.DiagnosticId);
            return BrowserAutomationDiagnosticResult.Passed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var type = BrowserAutomationDiagnosticExceptionClassifier.TypeName(ex);
            if (BrowserAutomationDiagnosticExceptionClassifier.Classify(ex) == BrowserAutomationFailureKind.TargetClosed)
                return run.TargetRestricted(BrowserAutomationDiagnosticStage.TargetControl, type, logger);

            run.Fail(BrowserAutomationDiagnosticStage.TargetControl,
                "Automation control of the target page could not be verified.", type, run.Request.TargetUrl);
            return BrowserAutomationDiagnosticResult.Failed;
        }
    }

    private EdgeInstallation? SafeLocateEdge()
    {
        try { return edgeLocator.Locate(); }
        catch { return null; }
    }

    /// <summary>Accumulates the stage results of one run and turns them into the report.</summary>
    private sealed class Run(BrowserAutomationDiagnosticRequest request, string profileDirectory, string controlUrl)
    {
        private readonly List<BrowserAutomationDiagnosticStageResult> _stages = [];
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public string DiagnosticId { get; } = Guid.NewGuid().ToString("N")[..12];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public BrowserAutomationDiagnosticRequest Request { get; } = request;
        public string ProfileDirectory { get; } = profileDirectory;
        public string ControlUrl { get; } = controlUrl;
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

        public void Block(BrowserAutomationDiagnosticStage stage, string detail, string? exceptionType = null, string? url = null)
        {
            ObservedExceptionType ??= exceptionType;
            Record(stage, BrowserAutomationDiagnosticStageState.Blocked, detail, url, exceptionType);
        }

        /// <summary>
        /// The one conclusion the diagnostic exists to reach — and the one it is most tempting to reach too early.
        /// It requires the control page to have passed, because without that contrast a closed target page is just a
        /// browser that cannot be automated at all.
        /// </summary>
        public BrowserAutomationDiagnosticResult TargetRestricted(
            BrowserAutomationDiagnosticStage stage, string? exceptionType, ILogger log)
        {
            if (_stages.FirstOrDefault(s => s.Stage == BrowserAutomationDiagnosticStage.ControlPage) is not
                { State: BrowserAutomationDiagnosticStageState.Passed })
            {
                Fail(stage, "Automation control was lost, but the control page had not passed, so this says nothing "
                          + "specific about the target.",
                exceptionType ?? BrowserAutomationDiagnosticExceptionClassifier.TargetClosedExceptionName, Request.TargetUrl);
                return BrowserAutomationDiagnosticResult.ControlFailure;
            }

            Block(stage, "The Playwright-controlled page or context was closed while navigating to the target application.",
                exceptionType ?? BrowserAutomationDiagnosticExceptionClassifier.TargetClosedExceptionName, Request.TargetUrl);
            log.LogInformation("DiagnosticTargetRestricted {DiagnosticId} {Stage} {ExceptionType}",
                DiagnosticId, stage, exceptionType ?? BrowserAutomationDiagnosticExceptionClassifier.TargetClosedExceptionName);
            return BrowserAutomationDiagnosticResult.TargetRestricted;
        }

        /// <summary>
        /// A target that did not answer in time. Kept apart from the restricted case on purpose: a timeout is an
        /// absence of a response, not an observation that automation was terminated, and reporting it as a restriction
        /// would hand IT a conclusion the run does not support.
        /// </summary>
        public BrowserAutomationDiagnosticResult TargetTimedOut(BrowserAutomationDiagnosticStage stage, string exceptionType)
        {
            Fail(stage, "The target application did not respond within the navigation timeout. This is not evidence "
                      + "of an automation restriction: nothing was observed about automation control.",
                exceptionType, Request.TargetUrl);
            return BrowserAutomationDiagnosticResult.Failed;
        }

        public BrowserAutomationDiagnosticReport Blocked(string reason)
        {
            Block(BrowserAutomationDiagnosticStage.Runtime, reason);
            return Build(BrowserAutomationDiagnosticResult.Blocked, reason);
        }

        public BrowserAutomationDiagnosticReport Complete(BrowserAutomationDiagnosticResult result) => Build(result, null);

        private void Record(BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
            string? detail, string? url, string? exceptionType = null)
        {
            if (_stages.Any(s => s.Stage == stage)) return;   // a stage reports once
            _stages.Add(new BrowserAutomationDiagnosticStageResult(stage, state, detail, url, exceptionType, _stopwatch.ElapsedMilliseconds));
        }

        private BrowserAutomationDiagnosticReport Build(BrowserAutomationDiagnosticResult result, string? blockedReason)
        {
            // Every stage appears, in order, so "not run" is visible rather than absent.
            var stages = Enum.GetValues<BrowserAutomationDiagnosticStage>()
                .Select(stage => _stages.FirstOrDefault(s => s.Stage == stage)
                                 ?? new BrowserAutomationDiagnosticStageResult(stage, BrowserAutomationDiagnosticStageState.NotRun))
                .ToList();

            return new BrowserAutomationDiagnosticReport
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
                EdgeVersion = EdgeVersion,
                PlaywrightVersion = typeof(IPlaywright).Assembly.GetName().Version?.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription,
                ProfileDescription = BrowserAutomationDiagnosticPolicy.ProfileDescription,
                Stages = stages,
                ObservedExceptionType = ObservedExceptionType,
            };
        }
    }
}
