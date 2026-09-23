using System.Reflection;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.ManagedEdge;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// Two independent modes, the per-mode stage flow, the classification, the comparison and the cleanup guarantee.
///
/// These run against a fake browser on purpose. The failures the diagnostic exists to detect — a target that closes
/// the page underneath the automation, and a target that behaves differently headless than headed — cannot be
/// summoned from a real browser on demand. Nor can the ordering rule that makes a result meaningful ("a target
/// conclusion requires a passing control page") be proven by a manual run. The one class that touches Playwright is
/// separated out so everything else is testable.
/// </summary>
public sealed class BrowserAutomationDiagnosticRunTests
{
    // ── A fake browser that fails exactly where, and in whichever mode, a test wants it to ────────────────────────

    private sealed class FakeBrowser(BrowserAutomationDiagnosticMode mode) : IDiagnosticBrowser
    {
        public Func<BrowserAutomationDiagnosticLaunchOptions, Task>? OnLaunch;
        /// <summary>Called for each navigation, with the mode and URL, so a test can fail one mode only.</summary>
        public Func<BrowserAutomationDiagnosticMode, Uri, Task>? OnNavigate;
        /// <summary>Called for each about:blank/control-page check, with the mode and how many have happened before it.</summary>
        public Func<BrowserAutomationDiagnosticMode, int, Task<bool>>? OnIsControllable;
        /// <summary>Where the target navigation ends up, as the browser would report it. Null: it stays where it was sent.</summary>
        public Func<BrowserAutomationDiagnosticMode, Uri, string?>? RedirectTo;
        /// <summary>URLs a second page (a sign-in popup the target opens) navigates through, in order.</summary>
        public Func<BrowserAutomationDiagnosticMode, Uri, string[]?>? PopupNavigates;
        /// <summary>Called for each target probe (0 = after settling, 1 = after the stability window). May throw.</summary>
        public Func<BrowserAutomationDiagnosticMode, int, Task>? OnProbe;
        /// <summary>Whether the page is already closed at a given probe.</summary>
        public Func<BrowserAutomationDiagnosticMode, int, bool>? ProbeFindsPageClosed;
        /// <summary>Whether the page closes during the stability window (Page.Close is then reported, with no exception).</summary>
        public Func<BrowserAutomationDiagnosticMode, bool>? ClosesDuringStability;
        /// <summary>Which termination the stability window reports when it ends early. Page close by default.</summary>
        public BrowserAutomationLifecycleEventKind StabilityTermination = BrowserAutomationLifecycleEventKind.PageClosed;
        /// <summary>What closing the browser reports: null = clean, otherwise the exception type.</summary>
        public Func<string?>? OnClose;
        public bool Closed { get; private set; }
        public Func<BrowserAutomationDiagnosticMode, int, DiagnosticSettleOutcome>? SettleOutcome;
        public bool? MarkerFound;
        public bool PersistentContext = true;
        /// <summary>Real Edge: a failed navigation may commit chrome-error://chromewebdata/ before GotoAsync throws.</summary>
        public bool FailedNavigationCommitsErrorPage;

        private readonly List<DiagnosticNavigationObservation> _trace = [];
        private readonly List<DiagnosticLifecycleObservation> _events = [];
        private string _url = "about:blank";
        private long _clock;
        private int _probes;
        private int _settles;

        public BrowserAutomationDiagnosticMode Mode { get; } = mode;
        public int ControlChecks { get; private set; }
        public List<Uri> Navigations { get; } = [];
        public List<string?> MarkerSelectors { get; } = [];
        public List<TimeSpan> StabilityWindows { get; } = [];
        public bool Disposed { get; private set; }
        public BrowserAutomationDiagnosticLaunchOptions? LaunchOptions { get; private set; }
        public string? EdgeVersion => "153.0.0.0";
        public bool HasPersistentContext => LaunchOptions is not null && PersistentContext;
        public IReadOnlyList<DiagnosticNavigationObservation> NavigationTrace => _trace;
        public IReadOnlyList<DiagnosticLifecycleObservation> LifecycleEvents => _events;
        public long ObservationElapsedMs => _clock;

        public async Task LaunchAsync(BrowserAutomationDiagnosticLaunchOptions options, CancellationToken ct)
        {
            LaunchOptions = options;
            if (OnLaunch is not null) await OnLaunch(options);
        }

        public async Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Navigations.Add(url);
            if (OnNavigate is not null)
            {
                try { await OnNavigate(Mode, url); }
                catch when (FailedNavigationCommitsErrorPage)
                {
                    _trace.Add(new(_clock += 10, "chrome-error://chromewebdata/"));
                    throw;
                }
            }
            _url = url.AbsoluteUri;
            _trace.Add(new(_clock += 10, _url));
            if (RedirectTo?.Invoke(Mode, url) is { } final)
            {
                _url = final;
                _trace.Add(new(_clock += 400, final));
            }
            if (PopupNavigates?.Invoke(Mode, url) is { } popup)
            {
                _events.Add(new(BrowserAutomationLifecycleEventKind.PageOpened, _clock += 50));
                foreach (var step in popup) _trace.Add(new(_clock += 300, step, SecondaryPage: true));
            }
        }

        public async Task<bool> IsControllableAsync(TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = ControlChecks++;
            return OnIsControllable is null || await OnIsControllable(Mode, index);
        }

        public void BeginTargetObservation()
        {
            _trace.Clear();
            _events.Clear();
            _clock = 0;
        }

        public Task<DiagnosticSettleResult> WaitForNavigationToSettleAsync(TimeSpan quietPeriod, TimeSpan bound, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = SettleOutcome?.Invoke(Mode, _settles++) ?? DiagnosticSettleOutcome.Settled;
            if (outcome == DiagnosticSettleOutcome.Terminated) _events.Add(new(BrowserAutomationLifecycleEventKind.PageClosed, _clock += 5));
            return Task.FromResult(new DiagnosticSettleResult(outcome, 1200));
        }

        public Task<bool> ObserveStabilityAsync(TimeSpan window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            StabilityWindows.Add(window);
            _clock += 100;
            if (ClosesDuringStability?.Invoke(Mode) != true) return Task.FromResult(true);
            _events.Add(new(StabilityTermination, _clock += 1500));
            return Task.FromResult(false);
        }

        public async Task<DiagnosticPageProbe> ProbeAsync(string? markerSelector, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = _probes++;
            MarkerSelectors.Add(markerSelector);
            _clock += 20;
            if (OnProbe is not null) await OnProbe(Mode, index);
            if (ProbeFindsPageClosed?.Invoke(Mode, index) == true) return new(_url, PageClosed: true, MarkerFound: null);
            return new(_url, PageClosed: false, markerSelector is null ? null : MarkerFound);
        }

        public Task<string?> CloseAsync()
        {
            Closed = true;
            return Task.FromResult(OnClose?.Invoke());
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePolicyReader(EdgeRemoteDebuggingPolicyStatus remote, EdgeDeveloperToolsPolicyStatus devTools) : IEdgePolicyReader
    {
        public EdgeRemoteDebuggingPolicyStatus ReadRemoteDebuggingPolicy() => remote;
        public EdgeDeveloperToolsPolicyStatus ReadDeveloperToolsPolicy() => devTools;
    }

    /// <summary>Hands out one fake per mode and keeps them, so a test can assert on each mode's browser separately.</summary>
    private sealed class FakeFactory : IDiagnosticBrowserFactory
    {
        public readonly Dictionary<BrowserAutomationDiagnosticMode, FakeBrowser> Browsers = [];
        public Action<FakeBrowser>? Configure;

        public IDiagnosticBrowser Create(BrowserAutomationDiagnosticMode mode)
        {
            var browser = new FakeBrowser(mode);
            Configure?.Invoke(browser);
            Browsers[mode] = browser;
            return browser;
        }

        public FakeBrowser Headed => Browsers[BrowserAutomationDiagnosticMode.Headed];
        public FakeBrowser Headless => Browsers[BrowserAutomationDiagnosticMode.Headless];
    }

    private sealed class FakeEdgeLocator(EdgeInstallation? installation) : IEdgeInstallationLocator
    {
        public EdgeInstallation? Locate() => installation;
    }

    private const string ProfileRoot = @"C:\Users\someone\AppData\Local\BirkNext\BrowserAutomationDiagnostic";
    private const string ControlUrl = "https://example.com/";
    private const string TargetUrl = "https://m2lbdev.example.test/";
    private const string TargetHost = "m2lbdev.example.test";

    /// <summary>The real per-target layout under a fake root: …\&lt;target key&gt;\Headed|Headless.</summary>
    private static string Profile(BrowserAutomationDiagnosticRequest request, BrowserAutomationDiagnosticMode mode) =>
        System.IO.Path.Combine(ProfileRoot, BrowserAutomationDiagnosticPolicy.TargetProfileKey(request),
            mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed");

    private static BrowserAutomationDiagnosticRequest Request(
        string environmentType = "Development", string id = "dev") => new()
        {
            TargetEnvironmentId = id, TargetEnvironmentName = "M2LB DEV",
            EnvironmentType = environmentType, TargetUrl = TargetUrl,
        };

    /// <summary>Tiny, so the suite is fast; the real durations and their rationale live in BrowserAutomationTargetObservationTiming.</summary>
    private static readonly BrowserAutomationTargetObservationTiming FastTiming =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(2));

    private static IBrowserAutomationDiagnosticService Service(
        FakeFactory factory, bool isLocalWorkstation = true, bool edgeFound = true,
        Func<BrowserAutomationDiagnosticRequest, BrowserAutomationDiagnosticMode, string>? profile = null, string? marker = null,
        IEdgePolicyReader? policies = null, List<string>? logLines = null)
    {
        var type = typeof(BrowserAutomationDiagnosticPolicy).Assembly
            .GetType("BirkNext.Api.Services.BrowserAutomationDiagnostic.BrowserAutomationDiagnosticService")!;
        var logger = logLines is not null
            ? Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(type), logLines)
            : typeof(NullLogger<>).MakeGenericType(type).GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        return (IBrowserAutomationDiagnosticService)Activator.CreateInstance(type,
            factory,
            new FakeEdgeLocator(edgeFound ? new EdgeInstallation(@"C:\Program Files\Edge\msedge.exe", "153.0.0.0") : null),
            logger,
            (Func<bool>)(() => isLocalWorkstation),
            profile ?? Profile,
            ControlUrl,
            FastTiming,
            (Func<BrowserAutomationDiagnosticRequest, string?>)(_ => marker),
            policies ?? new FakePolicyReader(EdgeRemoteDebuggingPolicyStatus.NotConfigured, EdgeDeveloperToolsPolicyStatus.NotConfigured))!;
    }

    /// <summary>Playwright's .NET binding does not expose TargetClosedException, so this is what a caller actually sees.</summary>
    private static PlaywrightException TargetClosed() => new("Target page, context or browser has been closed");

    /// <summary>Closes the target in the named mode only; every other navigation succeeds.</summary>
    private static Func<BrowserAutomationDiagnosticMode, Uri, Task> ClosesTargetIn(params BrowserAutomationDiagnosticMode[] modes) =>
        (mode, url) => url.Host == TargetHost && modes.Contains(mode) ? throw TargetClosed() : Task.CompletedTask;

    private static BrowserAutomationDiagnosticStageState State(
        BrowserAutomationDiagnosticModeReport mode, BrowserAutomationDiagnosticStage stage) => mode.Stage(stage)!.State;

    // ── §34. Mode execution ───────────────────────────────────────────────────────────────────────────────────────

    // 1, 2, 4. Both modes run, and each one launches the way its name says, in its own profile.
    [Fact]
    public async Task BothModesRunIndependently_WithTheirOwnHeadlessFlagAndProfile()
    {
        var factory = new FakeFactory();

        var report = await Service(factory).RunAsync(Request());

        report.Modes.Select(m => m.Mode).Should().Equal(
            BrowserAutomationDiagnosticMode.Headed, BrowserAutomationDiagnosticMode.Headless);

        factory.Headed.LaunchOptions!.Headless.Should().BeFalse();
        factory.Headless.LaunchOptions!.Headless.Should().BeTrue();

        // 4. Separate profiles: one shared user-data directory is how two sequential Chromium launches end up
        // reporting a profile lock instead of the target's behaviour.
        factory.Headed.LaunchOptions.ProfileDirectory.Should().EndWith("Headed");
        factory.Headless.LaunchOptions.ProfileDirectory.Should().EndWith("Headless");
        factory.Headed.LaunchOptions.ProfileDirectory.Should().NotBe(factory.Headless.LaunchOptions.ProfileDirectory);
    }

    // 5. Neither mode may ever run in the employee's own Edge profile.
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge\User Data")]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge Beta\User Data")]
    public async Task ANormalEdgeProfileInEitherModeBlocksTheWholeDiagnostic(string normal)
    {
        var factory = new FakeFactory();

        // Only the HEADLESS profile is unsafe; the run must still refuse, rather than doing the headed half.
        var report = await Service(factory, profile: (request, mode) =>
            mode == BrowserAutomationDiagnosticMode.Headless ? normal : Profile(request, mode)).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.Blocked);
        report.BlockedReason.Should().Be(BrowserAutomationDiagnosticPolicy.NormalProfileBlockedReason);
        factory.Browsers.Should().BeEmpty("no browser is created for a blocked diagnostic");
    }

    // 6, 7. The target comes from the request, and Production never reaches a browser.
    [Fact]
    public async Task ProductionIsBlockedBeforeAnyBrowserIsCreated()
    {
        var factory = new FakeFactory();

        var report = await Service(factory).RunAsync(Request("Production"));

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.Blocked);
        report.BlockedReason.Should().Be(BrowserAutomationDiagnosticPolicy.ProductionBlockedReason);
        factory.Browsers.Should().BeEmpty();
    }

    // 29. Two runs for one target would share both profiles and report each other's contention.
    [Fact]
    public async Task ASecondConcurrentRunForTheSameTargetIsRefused()
    {
        var gate = new TaskCompletionSource();
        var factory = new FakeFactory { Configure = b => b.OnLaunch = _ => gate.Task };
        var service = Service(factory);

        var first = service.RunAsync(Request(id: "dev"));
        await Task.Delay(50);
        var second = await service.RunAsync(Request(id: "dev"));

        second.Result.Should().Be(BrowserAutomationDiagnosticComparison.Blocked);
        second.BlockedReason.Should().Be(BrowserAutomationDiagnosticPolicy.AlreadyRunningReason);
        gate.SetResult();
        await first;
    }

    // ── §35. Per-mode control sequence ────────────────────────────────────────────────────────────────────────────

    // 8, 9. about:blank, then the control page, then the target — in every mode.
    [Fact]
    public async Task EachModeProvesTheNeutralPageBeforeTouchingTheTarget()
    {
        var factory = new FakeFactory();

        await Service(factory).RunAsync(Request());

        foreach (var browser in new[] { factory.Headed, factory.Headless })
            browser.Navigations.Should().Equal(new Uri(ControlUrl), new Uri(TargetUrl));
    }

    // 10, 20. A control page that cannot be reached stops that mode, and the target is never contacted.
    [Fact]
    public async Task AControlPageFailureStopsThatModeBeforeTheTarget()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = (_, url) => url.Host == "example.com"
                ? throw new PlaywrightException("net::ERR_NAME_NOT_RESOLVED")
                : Task.CompletedTask,
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.ControlFailure);
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
            mode.Stage(BrowserAutomationDiagnosticStage.ControlPage)!.Detail
                .Should().Contain("not evidence about the target");
        }
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.ControlPageFailure);
        factory.Headed.Navigations.Should().ContainSingle();
    }

    // 11, 12. Navigation returning is not success: the page must still answer afterwards.
    [Fact]
    public async Task ATargetPageThatClosesAfterNavigatingIsRestricted_NotAvailable()
    {
        // The first target probe finds the page already gone.
        var factory = new FakeFactory { Configure = b => b.ProbeFindsPageClosed = (_, index) => index == 0 };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed,
                "navigation itself succeeded — which is exactly why navigation alone is not the check");
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
            mode.BrowserControlRetained.Should().BeFalse();
            mode.TargetApplicationIdentified.Should().BeFalse();
        }
    }

    // A persistent context is its own stage: a launch can return without one.
    [Fact]
    public async Task AMissingPersistentContextIsARuntimeFailureForThatMode()
    {
        var factory = new FakeFactory { Configure = b => b.PersistentContext = false };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.PersistentContext).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.RuntimeUnavailable);
        }
    }

    // ── §36, §37. Per-mode classification ─────────────────────────────────────────────────────────────────────────

    // 13, 17. Everything works in a mode: that mode is available.
    [Fact]
    public async Task AModeThatKeepsControlOfTheTargetIsAvailable()
    {
        var report = await Service(new FakeFactory()).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.Available);
            mode.TargetApplicationIdentified.Should().BeTrue();
            foreach (var stage in Enum.GetValues<BrowserAutomationDiagnosticStage>())
                State(mode, stage).Should().Be(BrowserAutomationDiagnosticStageState.Passed, $"{mode.Mode}/{stage}");
        }
    }

    // 14, 18. The spike's signature, per mode: control pages fine, page closes at the target.
    [Fact]
    public async Task TargetClosedAfterAPassingControlPageIsATargetRestrictionForThatMode()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
            State(mode, BrowserAutomationDiagnosticStage.ControlPage).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            // Blocked, not Failed: the diagnostic worked; the target refused.
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
            mode.ObservedExceptionType.Should().Be("TargetClosedException");
        }
    }

    // 15, 19. A mode whose browser never started says nothing about the target.
    [Fact]
    public async Task ALaunchFailureIsARuntimeFailureAndNeverATargetConclusion()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnLaunch = _ => throw new PlaywrightException("Executable doesn't exist"),
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.RuntimeUnavailable);
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
        }
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.PlaywrightRuntimeUnavailable);
    }

    // 16, 20. A page that closes on about:blank is the runtime failing, never a target-specific restriction.
    [Fact]
    public async Task TargetClosedOnBlankPageIsAControlFailure_NotATargetRestriction()
    {
        var factory = new FakeFactory { Configure = b => b.OnIsControllable = (_, _) => throw TargetClosed() };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.ControlFailure);
            mode.Result.Should().NotBe(BrowserAutomationDiagnosticModeResult.TargetRestricted);
        }
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.ControlPageFailure);
    }

    // A timeout at the target is an absence of a response, not an observation about automation.
    [Fact]
    public async Task ATargetTimeoutIsNotATargetRestriction()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = (_, url) => url.Host == TargetHost
                ? throw new PlaywrightException("Timeout 25000ms exceeded.")
                : Task.CompletedTask,
        };

        var report = await Service(factory).RunAsync(Request());

        report.Modes.Should().OnlyContain(m => m.Result == BrowserAutomationDiagnosticModeResult.Failed);
        report.Result.Should().NotBe(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        report.Headed!.Stage(BrowserAutomationDiagnosticStage.TargetNavigation)!.Detail
            .Should().Contain("not evidence of an automation restriction");
    }

    // ── §38. The comparison ───────────────────────────────────────────────────────────────────────────────────────

    // 21. Both modes keep control.
    [Fact]
    public async Task BothModesAvailable_IsAutomationAvailable()
    {
        var report = await Service(new FakeFactory()).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.AutomationAvailable);
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
    }

    // 22. The case the spike suggests: blocked in both modes.
    [Fact]
    public async Task BothModesRestricted_IsTargetRestrictedInBothModes()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        report.ResultLabel.Should().Be("Target-specific automation restriction detected in both modes");
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // 23. The case that matters most for unattended CI, and the reason both modes are run.
    [Fact]
    public async Task HeadedWorksAndHeadlessBlocked_IsHeadlessOnlyRestricted()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = ClosesTargetIn(BrowserAutomationDiagnosticMode.Headless),
        };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted);
        report.Headed!.BrowserControlRetained.Should().BeTrue();
        report.Headless!.BrowserControlRetained.Should().BeFalse();
        // 28. Headed success alone never satisfies the headless prerequisite.
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
        report.Interpretation.Should().Contain("unattended CI");
    }

    // 24. The mirror case.
    [Fact]
    public async Task HeadlessWorksAndHeadedBlocked_IsHeadedOnlyRestricted()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = ClosesTargetIn(BrowserAutomationDiagnosticMode.Headed),
        };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted);
        // 21 (gating). Headless is what the authentication diagnostic depends on, and headless worked.
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
    }

    // 25. One mode concluded and the other did not get far enough to agree. That is not a two-mode finding.
    [Fact]
    public async Task OneModeConcludingAloneIsInconclusive_NotARestriction()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnLaunch = b.Mode == BrowserAutomationDiagnosticMode.Headless
                ? _ => throw new PlaywrightException("Executable doesn't exist")
                : null,
        };
        factory.Configure = b =>
        {
            if (b.Mode == BrowserAutomationDiagnosticMode.Headless)
                b.OnLaunch = _ => throw new PlaywrightException("Executable doesn't exist");
            else
                b.OnNavigate = ClosesTargetIn(BrowserAutomationDiagnosticMode.Headed);
        };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.MixedOrInconclusive);
        report.Result.Should().NotBe(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // ── §39. Wording ──────────────────────────────────────────────────────────────────────────────────────────────

    // The restricted results never claim a cause, and never say developer tooling is disabled — this very run used it.
    [Fact]
    public async Task NoResultClaimsAConfirmedCauseOrGloballyDisabledDevTools()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        var everything = report.Interpretation + " " + report.ResultLabel + " " +
                         string.Join(" ", report.Modes.SelectMany(m => m.Stages).Select(s => s.Detail));
        everything.Should().NotContainAny(
            "Defender", "MCAS", "DevTools is disabled", "DevTools disabled", "confirmed", "Conditional Access blocked");
        report.Interpretation.Should().Contain("does not identify which organisational control is responsible");
        report.Interpretation.Should().Contain("does not mean browser developer tooling is disabled generally");
    }

    // ── §41. Cleanup ──────────────────────────────────────────────────────────────────────────────────────────────

    // 29–32. Both modes' browsers close on every path.
    [Fact]
    public async Task EveryModesBrowserIsClosedOnEveryPath()
    {
        var success = new FakeFactory();
        await Service(success).RunAsync(Request());
        success.Browsers.Values.Should().OnlyContain(b => b.Disposed, "after a successful run");

        var closed = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };
        await Service(closed).RunAsync(Request());
        closed.Browsers.Values.Should().OnlyContain(b => b.Disposed, "after the target closed the page");

        var crashed = new FakeFactory { Configure = b => b.OnIsControllable = (_, _) => throw new InvalidOperationException("boom") };
        await Service(crashed).RunAsync(Request());
        crashed.Browsers.Values.Should().OnlyContain(b => b.Disposed, "after an unexpected failure");
    }

    // 33. Cancellation cleans up both modes and is not reported as a failure.
    [Fact]
    public async Task CancellationCleansUpAndIsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = (_, url) =>
            {
                if (url.Host == "example.com") cts.Cancel();
                return Task.CompletedTask;
            },
        };

        var report = await Service(factory).RunAsync(Request(), cts.Token);

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.Cancelled);
        report.Result.Should().NotBe(BrowserAutomationDiagnosticComparison.MixedOrInconclusive);
        factory.Browsers.Values.Should().OnlyContain(b => b.Disposed);
        factory.Browsers.Values.Should().OnlyContain(b => !b.Navigations.Contains(new Uri(TargetUrl)),
            "the target is not contacted after cancelling");
    }

    // 34. Only the dedicated diagnostic profiles are ever launched.
    [Fact]
    public async Task OnlyDedicatedDiagnosticProfilesAreLaunched()
    {
        var factory = new FakeFactory();

        await Service(factory).RunAsync(Request());

        foreach (var browser in factory.Browsers.Values)
        {
            var profile = browser.LaunchOptions!.ProfileDirectory;
            profile.Should().Contain("BrowserAutomationDiagnostic");
            ManagedEdgePreflightService.IsNormalEdgeProfile(profile).Should().BeFalse();
            profile.Should().NotContainAny("LocalHttpsProxyEdgeProfile", "ManagedEdgeProfile", "HeadlessAuthDiagnostic");
        }
    }

    // ── §42. Report content ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReportCarriesBothModesAndNothingSensitive()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        report.Modes.Should().HaveCount(2);
        report.TargetEnvironmentName.Should().Be("M2LB DEV");
        report.TargetUrl.Should().Be(TargetUrl);
        report.ControlUrl.Should().Be(ControlUrl);
        report.EdgeVersion.Should().NotBeNullOrWhiteSpace();
        report.PlaywrightVersion.Should().NotBeNullOrWhiteSpace();
        report.Modes.Should().OnlyContain(m => m.ProfileDescription.Contains("Never signed in"));

        // The profiles are described, never handed over as paths with somebody's username in them.
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        serialized.Should().NotContain(@"C:\Users\someone");
        serialized.Should().NotContainAny("password", "token", "cookie", "Bearer ");
    }

    // 6. Each run is identifiable in a log and in a report.
    [Fact]
    public async Task EachRunHasItsOwnDiagnosticId()
    {
        var first = await Service(new FakeFactory()).RunAsync(Request(id: "a"));
        var second = await Service(new FakeFactory()).RunAsync(Request(id: "b"));

        first.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        second.DiagnosticId.Should().NotBe(first.DiagnosticId);
    }

    // ══ Target PASS audit: what the page Playwright controls actually IS ══════════════════════════════════════════

    private const string EntraAuthorize =
        "https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3/oauth2/v2.0/authorize"
        + "?client_id=23be8783-a768-47c0-804e-2740c6216272&state=STATE-SECRET&nonce=NONCE-SECRET"
        + "&code_challenge=CHALLENGE-SECRET&login_hint=someone%40bufdir.no&session_state=SESSION-SECRET";

    private static Func<BrowserAutomationDiagnosticMode, Uri, string?> RedirectsTargetTo(string final) =>
        (_, url) => url.Host == TargetHost ? final : null;

    // §30. THE false positive. M2LB redirects a fresh profile to Entra; Playwright can still read that page. The
    // navigation passed and browser control passed — but the target application was NOT reached, and must not be PASS.
    [Fact]
    public async Task ARedirectToEntraIsNeverTargetApplicationPass()
    {
        var factory = new FakeFactory { Configure = b => b.RedirectTo = RedirectsTargetTo(EntraAuthorize) };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetStability).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.NotReached);
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().NotBe(BrowserAutomationDiagnosticStageState.Passed);

            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary);
            mode.BrowserControlRetained.Should().BeTrue();
            mode.TargetApplicationIdentified.Should().BeFalse();

            var target = mode.Target!;
            target.RequestedUrl.Should().Be(TargetUrl);
            target.FinalHost.Should().Be("login.microsoftonline.com");
            target.FinalOrigin.Should().Be("https://login.microsoftonline.com");
            target.FinalScheme.Should().Be("https");
            target.FinalLocation.Should().Be(BrowserAutomationFinalLocation.AuthenticationAuthority);
            target.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.No);
            target.AuthenticationRedirect.Should().Be(BrowserAutomationEvidenceAnswer.Yes);
            target.AuthenticationHost.Should().Be("login.microsoftonline.com");
            target.TargetApplicationIdentified.Should().Be(BrowserAutomationEvidenceAnswer.No);
            target.FailurePhase.Should().Be(BrowserAutomationFailurePhase.None);
        }

        // Automation is available through the handoff, and the gate the authentication diagnostic needs is open.
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.AutomationAvailable);
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
        report.Interpretation.Should().Contain("redirected to authentication (login.microsoftonline.com)")
            .And.Contain("does NOT prove that the authenticated target application is controllable")
            .And.Contain("not yet reached");
        report.Interpretation.Should().NotContain("retained control of the target application");
    }

    // §17, §34. The redirect trace is recorded, and nothing sensitive in it survives.
    [Fact]
    public async Task TheRedirectTraceIsRecordedAndSanitized()
    {
        var factory = new FakeFactory { Configure = b => b.RedirectTo = RedirectsTargetTo(EntraAuthorize) };

        var report = await Service(factory).RunAsync(Request());

        var trace = report.Headless!.Target!.NavigationTrace;
        trace.Select(s => s.Url).Should().Equal(
            "https://m2lbdev.example.test/",
            "https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]");
        trace.Select(s => s.Location).Should().Equal(
            BrowserAutomationFinalLocation.TargetOrigin, BrowserAutomationFinalLocation.AuthenticationAuthority);
        report.Headless.Target.FinalUrl.Should().Be("https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]");

        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        serialized.Should().NotContainAny(
            "STATE-SECRET", "NONCE-SECRET", "CHALLENGE-SECRET", "SESSION-SECRET", "someone", "bufdir.no",
            "state=", "nonce=", "login_hint", "session_state", "client_id", "code_challenge",
            "25609970-3b75-45b9-9899-036bb1693ff3", "23be8783");
    }

    // §31. The real PASS: same origin, the configured application marker found, stable.
    [Fact]
    public async Task SameOriginWithTheConfiguredMarkerAndStableControlIsTargetApplicationPass()
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                b.RedirectTo = RedirectsTargetTo("https://m2lbdev.example.test/oversikt?tab=1");
                b.MarkerFound = true;
            },
        };

        var report = await Service(factory, marker: "[data-app-shell]").RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.Available);
            mode.TargetApplicationIdentified.Should().BeTrue();
            mode.Target!.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.Yes);
            mode.Target.AuthenticationRedirect.Should().Be(BrowserAutomationEvidenceAnswer.No);
            mode.Target.ApplicationMarkerConfigured.Should().BeTrue();
            mode.Target.ApplicationMarkerFound.Should().BeTrue();
            mode.Target.IdentificationEvidence.Should().Contain("configured application marker");
            mode.Target.FinalUrl.Should().Be("https://m2lbdev.example.test/oversikt?[redacted]");
        }
        // The configured marker is what was asked for, at both probes, in both modes.
        factory.Browsers.Values.SelectMany(b => b.MarkerSelectors).Should().OnlyContain(m => m == "[data-app-shell]");
        report.Interpretation.Should().Contain("identified as the target application");
    }

    // A configured marker that is absent means "not identified", never PASS — the origin alone is not allowed to
    // override a contract someone deliberately configured.
    [Fact]
    public async Task AConfiguredMarkerThatIsMissingIsNotAPass()
    {
        var factory = new FakeFactory { Configure = b => b.MarkerFound = false };

        var report = await Service(factory, marker: "[data-app-shell]").RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.Unknown);
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed);
            mode.Target!.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.Yes);
            mode.Target.TargetApplicationIdentified.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
        }
        // Still the target's path, so the authentication diagnostic may run.
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
    }

    // Without a configured marker the origin is the only evidence — and the report says exactly that.
    [Fact]
    public async Task WithoutAMarkerTheOriginIsTheStatedEvidence()
    {
        var report = await Service(new FakeFactory()).RunAsync(Request());

        report.Headless!.Target!.IdentificationEvidence.Should().Contain("No application marker is configured");
        report.Headless.Target.ApplicationMarkerConfigured.Should().BeFalse();
        report.Headless.Target.ApplicationMarkerFound.Should().BeNull();
    }

    // An unrelated final origin: automation survived, but this is neither the target nor its handoff.
    [Fact]
    public async Task AnUnrelatedFinalOriginIsNotTheTargetAndDoesNotOpenTheGate()
    {
        var factory = new FakeFactory { Configure = b => b.RedirectTo = RedirectsTargetTo("https://m2lbdev.example.test.evil.test/") };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed);
            mode.Target!.FinalLocation.Should().Be(BrowserAutomationFinalLocation.OtherOrigin);
            mode.Target.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.No);
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.NotReached);
        }
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // The configured Target Environment authority is recognised as an authentication redirect too.
    [Fact]
    public async Task TheConfiguredAuthorityIsRecognisedAsAnAuthenticationRedirect()
    {
        var factory = new FakeFactory { Configure = b => b.RedirectTo = RedirectsTargetTo("https://idp.example.test/connect/authorize?state=x") };

        var report = await Service(factory).RunAsync(Request() with { Authority = "https://idp.example.test/" });

        report.Headless!.Target!.FinalLocation.Should().Be(BrowserAutomationFinalLocation.AuthenticationAuthority);
        report.Headless.Result.Should().Be(BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary);
    }

    // §32, §21. Navigation returns, the first probe works, then the page closes during the stability window.
    // Control is NOT retained, and nobody gets to call it PASS because the early probe happened to succeed.
    [Fact]
    public async Task APageThatClosesDuringTheStabilityWindowIsBlocked_NotPass()
    {
        var factory = new FakeFactory { Configure = b => b.ClosesDuringStability = _ => true };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Passed,
                "the first probe did succeed — which is exactly why one probe is not enough");
            State(mode, BrowserAutomationDiagnosticStage.TargetStability).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
            mode.BrowserControlRetained.Should().BeFalse();

            mode.Target!.FailurePhase.Should().Be(BrowserAutomationFailurePhase.DuringStabilityWindow);
            mode.Target.LifecycleEvents.Should().ContainSingle(e => e.Kind == BrowserAutomationLifecycleEventKind.PageClosed)
                .Which.Phase.Should().Be(BrowserAutomationFailurePhase.DuringStabilityWindow);
            // Page.Close was observed; no exception was thrown, so none is claimed.
            mode.ObservedExceptionType.Should().BeNull();
            mode.Stage(BrowserAutomationDiagnosticStage.TargetStability)!.Detail
                .Should().Contain("PageClosed").And.Contain("Control was lost late, after an initial success");
        }
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // §33, §16. TargetClosedException from the first safe operation after Goto: a target-CONTROL restriction, with
    // the exception kept as first-class evidence — not a navigation success.
    [Fact]
    public async Task TargetClosedOnTheFirstProbeAfterNavigationIsATargetControlRestriction()
    {
        var factory = new FakeFactory { Configure = b => b.OnProbe = (_, index) => index == 0 ? throw TargetClosed() : Task.CompletedTask };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
            mode.Stage(BrowserAutomationDiagnosticStage.TargetControl)!.ExceptionType.Should().Be("TargetClosedException");
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
            mode.ObservedExceptionType.Should().Be("TargetClosedException");
            mode.Target!.ExceptionType.Should().Be("TargetClosedException");
            mode.Target.FailurePhase.Should().Be(BrowserAutomationFailurePhase.AfterTargetNavigation);
            mode.Stage(BrowserAutomationDiagnosticStage.TargetControl)!.Detail.Should().Contain("Target-control restriction");
        }
        report.Result.Should().NotBe(BrowserAutomationDiagnosticComparison.AutomationAvailable);
    }

    // The same exception on the SECOND probe is a late loss, reported as one.
    [Fact]
    public async Task TargetClosedOnTheClosingProbeIsALateLoss()
    {
        var factory = new FakeFactory { Configure = b => b.OnProbe = (_, index) => index == 1 ? throw TargetClosed() : Task.CompletedTask };

        var report = await Service(factory).RunAsync(Request());

        report.Headless!.Target!.FailurePhase.Should().Be(BrowserAutomationFailurePhase.DuringStabilityWindow);
        State(report.Headless, BrowserAutomationDiagnosticStage.TargetStability).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
        report.Headless.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
    }

    // §16. During navigation, the same exception is a navigation restriction — and is recorded with that phase.
    [Fact]
    public async Task TargetClosedDuringNavigationIsRecordedAsANavigationRestriction()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        report.Headless!.Target!.FailurePhase.Should().Be(BrowserAutomationFailurePhase.DuringTargetNavigation);
        report.Headless.Target.ExceptionType.Should().Be("TargetClosedException");
        report.Headless.Stage(BrowserAutomationDiagnosticStage.TargetNavigation)!.Detail.Should().Contain("Target-navigation restriction");
    }

    // A close reported while waiting for navigation to settle is a control restriction, not a pass.
    [Fact]
    public async Task ATerminationWhileSettlingIsBlocked()
    {
        var factory = new FakeFactory { Configure = b => b.SettleOutcome = (_, index) => index == 0 ? DiagnosticSettleOutcome.Terminated : DiagnosticSettleOutcome.Settled };

        var report = await Service(factory).RunAsync(Request());

        State(report.Headless!, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
        report.Headless!.Target!.FailurePhase.Should().Be(BrowserAutomationFailurePhase.AfterTargetNavigation);
    }

    // §13, §14. Navigation that never goes quiet is recorded as such, and observation continues rather than failing.
    [Fact]
    public async Task NavigationThatDoesNotSettleIsRecordedAndStillObserved()
    {
        var factory = new FakeFactory { Configure = b => b.SettleOutcome = (_, _) => DiagnosticSettleOutcome.BoundReached };

        var report = await Service(factory).RunAsync(Request());

        report.Headless!.Target!.NavigationSettled.Should().BeFalse();
        report.Headless.Result.Should().Be(BrowserAutomationDiagnosticModeResult.Available);
        // And the stability window is the configured one, not a number invented at the call site.
        factory.Headless.StabilityWindows.Should().Equal(FastTiming.StabilityWindow);
        report.Headless.Target.StabilityWindowMs.Should().Be((long)FastTiming.StabilityWindow.TotalMilliseconds);
    }

    // §22. Headed and headless are held to identical rules, so the two results are directly comparable.
    [Fact]
    public async Task HeadedAndHeadlessApplyTheSameStrictRules()
    {
        var factory = new FakeFactory { Configure = b => b.RedirectTo = RedirectsTargetTo(EntraAuthorize) };

        var report = await Service(factory).RunAsync(Request());

        report.Headed!.Result.Should().Be(report.Headless!.Result);
        report.Headed.Stages.Select(s => (s.Stage, s.State)).Should().Equal(report.Headless.Stages.Select(s => (s.Stage, s.State)));
        report.Headed.Target!.FinalLocation.Should().Be(report.Headless.Target!.FinalLocation);
    }

    // §23, §24, §36. Three controls reported apart. A Playwright PASS never says DevTools is enabled, a restriction
    // never says DevTools is disabled, and nothing claims a security policy blocked the target.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaywrightResultsNeverClaimADevToolsOrPolicyState(bool restricted)
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                if (restricted) b.OnNavigate = ClosesTargetIn(b.Mode);
                else b.RedirectTo = RedirectsTargetTo(EntraAuthorize);
            },
        };

        var report = await Service(factory).RunAsync(Request());

        report.ControlDimensions.Select(d => d.Name).Should().Equal(
            "Visible DevTools policy (F12 / Inspect)", "Remote debugging policy (RemoteDebuggingAllowed)",
            "Playwright-owned browser automation", "CDP attach to an existing or protected Edge");
        // Policy rows come from the policy reader, never from the Playwright result — whichever way it went.
        report.ControlDimensions[0].State.Should().Be("Not configured in Edge policy");
        report.ControlDimensions[1].State.Should().Be("Not configured");
        report.ControlDimensions[3].State.Should().Be("Not tested");
        report.ControlDimensions[2].State.Should().Be(restricted ? "Blocked on this target" : "Available");

        var everything = string.Join(" ", new[] { report.Interpretation, report.ResultLabel }
            .Concat(report.ControlDimensions.SelectMany(d => new[] { d.State, d.Basis }))
            .Concat(report.Modes.SelectMany(m => m.Stages).Select(s => s.Detail ?? "")));
        everything.Should().NotContainAny(
            "DevTools enabled", "DevTools is enabled", "DevTools disabled", "DevTools is disabled",
            "security policy blocked", "policy blocked the target", "Security policy blocked target");
    }

    // The M2LB shape observed for real (2026-09-23): the main page stays on the app's own /authentication/login route
    // while MSAL opens a SECOND page that goes to Entra and then through a Defender for Cloud Apps session-control host.
    // The handoff must be reported even though it is not in the diagnostic's own page — and it must not move the
    // final location off the page Playwright actually controls.
    [Fact]
    public async Task AnAuthenticationHandoffInAPopupIsDetectedAndReportedAsSuch()
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                b.RedirectTo = RedirectsTargetTo("https://m2lbdev.example.test/authentication/login?returnUrl=x");
                b.PopupNavigates = (_, url) => url.Host == TargetHost
                    ? [EntraAuthorize, "https://m2lbdev-example-test.access.mcas.ms/aad_login"]
                    : null;
            },
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            var target = mode.Target!;
            target.FinalLocation.Should().Be(BrowserAutomationFinalLocation.TargetOrigin, "the main page never left the target origin");
            target.FinalUrl.Should().Be("https://m2lbdev.example.test/authentication/login?[redacted]");
            target.AuthenticationRedirect.Should().Be(BrowserAutomationEvidenceAnswer.Yes);
            target.AuthenticationHost.Should().Be("login.microsoftonline.com");
            target.AuthenticationInSecondaryPage.Should().BeTrue();
            target.SessionControlHost.Should().Be("m2lbdev-example-test.access.mcas.ms");
            target.NavigationTrace.Where(s => s.SecondaryPage).Select(s => s.Location).Should().Equal(
                BrowserAutomationFinalLocation.AuthenticationAuthority, BrowserAutomationFinalLocation.SessionControlProxy);
            target.LifecycleEvents.Should().Contain(e => e.Kind == BrowserAutomationLifecycleEventKind.PageOpened);
            mode.Stage(BrowserAutomationDiagnosticStage.TargetApplication)!.Detail
                .Should().Contain("in a second page the target opened").And.Contain("the authenticated application is not");
        }
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
        report.Interpretation.Should().Contain("in a second page it opened")
            .And.Contain("This does NOT prove that the authenticated target application is controllable");
        System.Text.Json.JsonSerializer.Serialize(report).Should().NotContainAny("STATE-SECRET", "NONCE-SECRET", "returnUrl");
    }

    // ══ P1: correctness and reliability ══════════════════════════════════════════════════════════════════════════

    // §14, §44. A probe that finds the page gone, with no exception, is reported as what it was — never dressed up as
    // a TargetClosedException nobody threw.
    [Fact]
    public async Task ControlUnavailableWithoutAnExceptionNeverReportsTargetClosedException()
    {
        var factory = new FakeFactory { Configure = b => b.ProbeFindsPageClosed = (_, index) => index == 0 };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.ObservedExceptionType.Should().BeNull();
            mode.Target!.ExceptionType.Should().BeNull();
            mode.Stage(BrowserAutomationDiagnosticStage.TargetControl)!.ExceptionType.Should().BeNull();
            mode.Stage(BrowserAutomationDiagnosticStage.TargetControl)!.Detail.Should().Contain("the page was no longer available")
                .And.NotContain("TargetClosedException");
        }
        System.Text.Json.JsonSerializer.Serialize(report).Should().NotContain("TargetClosedException");
    }

    // §42. A browser disconnect after the first successful probe invalidates it.
    [Fact]
    public async Task ABrowserDisconnectDuringTheStabilityWindowBlocksControl()
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                b.ClosesDuringStability = mode => mode == BrowserAutomationDiagnosticMode.Headless;
                b.StabilityTermination = BrowserAutomationLifecycleEventKind.BrowserDisconnected;
            },
        };

        var report = await Service(factory).RunAsync(Request());

        report.Headless!.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
        State(report.Headless, BrowserAutomationDiagnosticStage.TargetStability).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
        report.Headless.Target!.LifecycleEvents.Should().ContainSingle(e => e.Kind == BrowserAutomationLifecycleEventKind.BrowserDisconnected);
        report.Headless.Stage(BrowserAutomationDiagnosticStage.TargetStability)!.Detail.Should().Contain("BrowserDisconnected");
        report.Headed!.BrowserControlRetained.Should().BeTrue("the disconnect happened in the headless mode only");
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted);
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // §15, §48. Cleanup is reported as it happened — and a cleanup problem is a warning beside the finding, not a
    // replacement for it.
    [Fact]
    public async Task ACleanupFailureIsAWarningThatNeverOverwritesTheFinding()
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                b.OnNavigate = ClosesTargetIn(b.Mode);
                b.OnClose = () => "PlaywrightException";
            },
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.Cleanup).Should().Be(BrowserAutomationDiagnosticStageState.Warning);
            mode.Stage(BrowserAutomationDiagnosticStage.Cleanup)!.ExceptionType.Should().Be("PlaywrightException");
            mode.Stage(BrowserAutomationDiagnosticStage.Cleanup)!.Detail.Should().Contain("The result above is unaffected")
                .And.Contain("no other Edge process was touched");
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted, "the main cause is preserved");
            mode.ObservedExceptionType.Should().Be("TargetClosedException", "the cleanup exception does not replace the finding's");
        }
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        factory.Browsers.Values.Should().OnlyContain(b => b.Closed && b.Disposed);
    }

    [Fact]
    public async Task ACleanCloseIsReportedAsPassed()
    {
        var factory = new FakeFactory();

        var report = await Service(factory).RunAsync(Request());

        report.Modes.Should().OnlyContain(m => m.Stage(BrowserAutomationDiagnosticStage.Cleanup)!.State == BrowserAutomationDiagnosticStageState.Passed);
        factory.Browsers.Values.Should().OnlyContain(b => b.Closed);
    }

    // §16, §48. Profiles belong to one target and one mode. Two targets never share a user-data directory, and the
    // run lock is per profile set, so one target's run never blocks another's.
    [Fact]
    public async Task ProfilesArePerTargetAndPerMode()
    {
        var dev = Request(id: "dev");
        var qa = Request(id: "qa") with { TargetUrl = "https://m2lbqa.example.test/", EnvironmentType = "QA" };

        BrowserAutomationDiagnosticPolicy.ProfileDirectory(dev, BrowserAutomationDiagnosticMode.Headed)
            .Should().NotBe(BrowserAutomationDiagnosticPolicy.ProfileDirectory(qa, BrowserAutomationDiagnosticMode.Headed));
        BrowserAutomationDiagnosticPolicy.ProfileDirectory(dev, BrowserAutomationDiagnosticMode.Headed)
            .Should().NotBe(BrowserAutomationDiagnosticPolicy.ProfileDirectory(dev, BrowserAutomationDiagnosticMode.Headless));
        // Same target, different path under the same origin: same profile (it is the same application).
        BrowserAutomationDiagnosticPolicy.TargetProfileKey(dev with { TargetUrl = "https://m2lbdev.example.test/admin" })
            .Should().Be(BrowserAutomationDiagnosticPolicy.TargetProfileKey(dev));
        // The folder name leaks neither the host nor the id.
        var key = BrowserAutomationDiagnosticPolicy.TargetProfileKey(dev);
        key.Should().MatchRegex("^[0-9a-f]{16}$");
        BrowserAutomationDiagnosticPolicy.ProfileDirectory(dev, BrowserAutomationDiagnosticMode.Headed).Should().NotContain("m2lbdev");

        // Two different targets can run at the same time; neither is refused as "already running".
        var gate = new TaskCompletionSource();
        var slow = new FakeFactory { Configure = b => b.OnLaunch = _ => gate.Task };
        var service = Service(slow);
        var first = service.RunAsync(dev);
        await Task.Delay(50);
        var second = await Service(new FakeFactory()).RunAsync(qa);
        second.Result.Should().NotBe(BrowserAutomationDiagnosticComparison.Blocked);
        gate.SetResult();
        (await first).Result.Should().NotBe(BrowserAutomationDiagnosticComparison.Blocked);
    }

    // §17. An explicit non-production allow-list, and the hostname is checked as well as the type.
    [Theory]
    [InlineData("Development", "https://m2lbdev.example.test/", null)]
    [InlineData("QA", "https://m2lbqa.example.test/", null)]
    [InlineData("Test", "https://m2lbtest.example.test/", null)]
    [InlineData("Local", "http://localhost:5173/", null)]
    [InlineData("RC", "https://m2lbrc.example.test/", null)]
    [InlineData("Production", "https://m2lb.example.test/", BrowserAutomationDiagnosticPolicy.ProductionBlockedReason)]
    [InlineData("Custom", "https://m2lbdev.example.test/", BrowserAutomationDiagnosticPolicy.UnrecognisedEnvironmentBlockedReason)]
    [InlineData("", "https://m2lbdev.example.test/", BrowserAutomationDiagnosticPolicy.UnrecognisedEnvironmentBlockedReason)]
    [InlineData("Staging", "https://m2lbdev.example.test/", BrowserAutomationDiagnosticPolicy.UnrecognisedEnvironmentBlockedReason)]
    [InlineData("QA", "https://m2lb-prod.example.test/", BrowserAutomationDiagnosticPolicy.ProductionHostBlockedReason)]
    public void OnlyExplicitlyNonProductionTargetsAreEligible(string type, string url, string? expected)
    {
        var request = Request(type) with { TargetUrl = url };

        BrowserAutomationDiagnosticPolicy.BlockedReason(request,
                [Profile(request, BrowserAutomationDiagnosticMode.Headed), Profile(request, BrowserAutomationDiagnosticMode.Headless)], true)
            .Should().Be(expected);
    }

    // §4, §18. The configured URL is sanitized for display and reports, while the evidence store still binds to the
    // exact configured URL.
    [Fact]
    public async Task TheConfiguredTargetUrlIsSanitizedInTheReportButStillCorrelatesExactly()
    {
        var configured = "https://m2lbdev.example.test/?code=CODE-SECRET&state=STATE-SECRET&login_hint=someone%40bufdir.no";
        var request = Request() with { TargetUrl = configured };

        var report = await Service(new FakeFactory()).RunAsync(request);

        report.TargetUrl.Should().Be("https://m2lbdev.example.test/?[redacted]");
        report.Headless!.Target!.RequestedUrl.Should().Be("https://m2lbdev.example.test/?[redacted]");
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        json.Should().NotContainAny("CODE-SECRET", "STATE-SECRET", "someone", "code=", "login_hint");

        var store = new BirkNext.Api.Services.HeadlessAuthDiagnostic.BrowserAutomationEvidenceStore();
        store.Record(report);
        store.Check(new BirkNext.HeadlessAuthDiagnostic.HeadlessDiagnosticRequest
        {
            TargetEnvironmentId = "dev", TargetUrl = configured, EnvironmentType = "Development",
        }).Available.Should().BeTrue("the gate binds to the exact configured URL, not the sanitized display value");
    }

    // §32, §33. The visible DevTools policy and the remote debugging policy are READ, separately, and never inferred.
    [Fact]
    public async Task PolicyDimensionsComeFromThePolicyReaderAndNothingElse()
    {
        var reader = new FakePolicyReader(EdgeRemoteDebuggingPolicyStatus.Blocked, EdgeDeveloperToolsPolicyStatus.Disallowed);

        var report = await Service(new FakeFactory(), policies: reader).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.AutomationAvailable);
        report.ControlDimensions[0].State.Should().Be("Disallowed by Edge policy",
            "a working Playwright run does not make DevTools 'enabled'");
        report.ControlDimensions[1].State.Should().Be("Blocked");
        report.ControlDimensions[2].State.Should().Be("Available", "Playwright owning its own Edge is a separate path");
    }

    [Fact]
    public async Task AFailingPolicyReaderIsUnknown_NotAGuess()
    {
        var report = await Service(new FakeFactory(), policies: new ThrowingPolicyReader()).RunAsync(Request());

        report.ControlDimensions[0].State.Should().Be("Unknown");
        report.ControlDimensions[1].State.Should().Be("Unknown");
    }

    private sealed class ThrowingPolicyReader : IEdgePolicyReader
    {
        public EdgeRemoteDebuggingPolicyStatus ReadRemoteDebuggingPolicy() => throw new UnauthorizedAccessException();
        public EdgeDeveloperToolsPolicyStatus ReadDeveloperToolsPolicy() => throw new UnauthorizedAccessException();
    }

    [Theory]
    [InlineData(0, EdgeDeveloperToolsPolicyStatus.AllowedExceptForceInstalledExtensions)]
    [InlineData(1, EdgeDeveloperToolsPolicyStatus.Allowed)]
    [InlineData(2, EdgeDeveloperToolsPolicyStatus.Disallowed)]
    [InlineData(3, EdgeDeveloperToolsPolicyStatus.Unknown)]
    [InlineData(-1, EdgeDeveloperToolsPolicyStatus.Unknown)]
    public void DeveloperToolsAvailabilityValuesMapOnlyToTheDocumentedMeanings(int value, EdgeDeveloperToolsPolicyStatus expected) =>
        BrowserAutomationTargetLocationPolicy.DeveloperToolsPolicy(value).Should().Be(expected);

    // ── Target navigation failure evidence ────────────────────────────────────────────────────────────────────────

    /// <summary>Records each log line, rendered, and every structured value — so a test can prove what never reaches a log.</summary>
    private sealed class CapturingLogger<T>(List<string> lines) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
            lock (lines) lines.Add($"{formatter(state, exception)} || {values} || {exception?.Message}");
        }
    }

    /// <summary>The shape Playwright produces against Edge: the code, then the full URL (query included), then a call log.</summary>
    private const string SensitiveTarget = "https://m2lbdev.example.test/callback?code=0.AAAA-secret-code&state=s3cr3t-state&token=eyJhbGciOi";
    private static PlaywrightException NavigationError(string code) =>
        new($"page.goto: {code} at {SensitiveTarget}\nCall log:\n  - navigating to \"{SensitiveTarget}\", waiting until \"commit\"\n");

    private static Func<BrowserAutomationDiagnosticMode, Uri, Task> TargetThrows(Func<BrowserAutomationDiagnosticMode, Exception?> error) =>
        (mode, url) => url.Host == TargetHost && error(mode) is { } ex ? throw ex : Task.CompletedTask;

    // §2 / §27 / §40 The QA run's shape, both modes: the code is now surfaced, and every later-stage fact is unchanged.
    [Fact]
    public async Task ADnsFailureAtTheTarget_SurfacesTheBrowserCode_AndKeepsEveryOtherSemantic()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = TargetThrows(_ => NavigationError("net::ERR_NAME_NOT_RESOLVED")) };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.Failed);
            mode.ObservedExceptionType.Should().Be("PlaywrightException");
            State(mode, BrowserAutomationDiagnosticStage.ControlPage).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
            State(mode, BrowserAutomationDiagnosticStage.TargetStability).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
            State(mode, BrowserAutomationDiagnosticStage.TargetApplication).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
            State(mode, BrowserAutomationDiagnosticStage.Cleanup).Should().Be(BrowserAutomationDiagnosticStageState.Passed);

            var target = mode.Target!;
            target.FinalUrl.Should().BeNull();
            target.FinalHost.Should().BeNull();
            target.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
            target.AuthenticationRedirect.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
            target.SessionControlHost.Should().BeNull();
            target.TargetApplicationIdentified.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
            target.ExceptionType.Should().Be("PlaywrightException");
            target.FailurePhase.Should().Be(BrowserAutomationFailurePhase.DuringTargetNavigation);

            var failure = target.NavigationFailure!;
            failure.ExceptionType.Should().Be("PlaywrightException");
            failure.BrowserErrorCode.Should().Be("net::ERR_NAME_NOT_RESOLVED");
            failure.Category.Should().Be(BrowserNavigationFailureCategory.Dns);
            failure.Interpretation.Should().Be("The browser could not resolve the target hostname.");
            failure.ObservedAtStage.Should().Be(BrowserAutomationDiagnosticStage.TargetNavigation);
        }

        // Same comparison as before; the interpretation now says what the browser saw, once, for both modes.
        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.MixedOrInconclusive);
        report.ResultLabel.Should().Be("Inconclusive");
        report.Interpretation.Should().Contain("In both modes, target navigation failed before browser control could be established: "
            + "the browser reported net::ERR_NAME_NOT_RESOLVED (DNS).")
            .And.Contain("MFA, Conditional Access and session control were not assessed");
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
    }

    // §34 / §4 / §17 Nothing from the message but the code reaches the report or the log.
    [Fact]
    public async Task ASensitiveNavigationMessage_ReachesNeitherTheReportNorTheLog()
    {
        var logs = new List<string>();
        var factory = new FakeFactory { Configure = b => b.OnNavigate = TargetThrows(_ => NavigationError("net::ERR_NAME_NOT_RESOLVED")) };

        var report = await Service(factory, logLines: logs).RunAsync(Request());

        var json = System.Text.Json.JsonSerializer.Serialize(report);
        json.Should().Contain("net::ERR_NAME_NOT_RESOLVED");
        foreach (var leak in new[] { "secret-code", "s3cr3t-state", "eyJhbGciOi", "callback", "Call log", "navigating to", "page.goto" })
        {
            json.Should().NotContain(leak);
            logs.Should().NotContain(l => l.Contains(leak, StringComparison.Ordinal));
        }

        logs.Should().Contain(l => l.StartsWith("HeadlessTargetNavigationFailed") && l.Contains("BrowserErrorCode=net::ERR_NAME_NOT_RESOLVED")
            && l.Contains("FailureCategory=Dns") && l.Contains("ExceptionType=PlaywrightException") && l.Contains("TargetEnvironmentId=dev"));
        logs.Should().Contain(l => l.StartsWith("HeadedTargetNavigationFailed"));
    }

    // §30 A Playwright navigation timeout is not a browser connection timeout.
    [Fact]
    public async Task APlaywrightTimeoutAtTheTarget_IsANavigationTimeout_NotAConnectionTimeout()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = TargetThrows(_ => new System.TimeoutException("Timeout 25000ms exceeded.")),
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
            mode.Target!.NavigationFailure!.Category.Should().Be(BrowserNavigationFailureCategory.NavigationTimeout);
            mode.Target.NavigationFailure.BrowserErrorCode.Should().BeNull();
        }
    }

    // §32 / §8 A close during the navigation stays the restriction finding — no network category, no fabricated code.
    [Fact]
    public async Task ATargetCloseDuringNavigation_StaysTargetRestricted_WithTargetClosedEvidence()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = ClosesTargetIn(BrowserAutomationDiagnosticMode.Headed, BrowserAutomationDiagnosticMode.Headless),
        };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        foreach (var mode in report.Modes)
        {
            mode.Result.Should().Be(BrowserAutomationDiagnosticModeResult.TargetRestricted);
            var failure = mode.Target!.NavigationFailure!;
            failure.Category.Should().Be(BrowserNavigationFailureCategory.TargetClosed);
            failure.ExceptionType.Should().Be("TargetClosedException");
            failure.BrowserErrorCode.Should().BeNull();
        }
        report.Interpretation.Should().NotContain("target navigation failed before browser control");
    }

    // §12 Headed and headless are classified by the same classifier, independently.
    [Fact]
    public async Task EachModeReportsItsOwnBrowserError()
    {
        var factory = new FakeFactory
        {
            Configure = b => b.OnNavigate = TargetThrows(mode => NavigationError(mode == BrowserAutomationDiagnosticMode.Headed
                ? "net::ERR_CERT_AUTHORITY_INVALID" : "net::ERR_PROXY_CONNECTION_FAILED")),
        };

        var report = await Service(factory).RunAsync(Request());

        report.Headed!.Target!.NavigationFailure!.Category.Should().Be(BrowserNavigationFailureCategory.TlsCertificate);
        report.Headless!.Target!.NavigationFailure!.Category.Should().Be(BrowserNavigationFailureCategory.Proxy);
        report.Interpretation.Should().Contain("In headed mode, target navigation failed").And.Contain("net::ERR_CERT_AUTHORITY_INVALID (TLS / certificate)")
            .And.Contain("In headless mode, target navigation failed").And.Contain("net::ERR_PROXY_CONNECTION_FAILED (Proxy)");
    }

    // The live QA run: headed Edge committed its error page before the navigation threw. That page must not become
    // the "final location", or a failed navigation reads as "reached another origin, no auth redirect".
    [Fact]
    public async Task TheBrowsersErrorPageAfterAFailedNavigation_IsNotAFinalLocation()
    {
        var factory = new FakeFactory
        {
            Configure = b =>
            {
                b.FailedNavigationCommitsErrorPage = true;
                b.OnNavigate = TargetThrows(_ => NavigationError("net::ERR_NAME_NOT_RESOLVED"));
            },
        };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            var target = mode.Target!;
            target.FinalUrl.Should().BeNull();
            target.FinalHost.Should().BeNull();
            target.FinalScheme.Should().BeNull();
            target.FinalLocation.Should().Be(BrowserAutomationFinalLocation.Unknown);
            target.ExpectedOriginReached.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
            target.AuthenticationRedirect.Should().Be(BrowserAutomationEvidenceAnswer.Unknown);
            target.NavigationTrace.Should().ContainSingle().Which.Should().Match<BrowserAutomationNavigationStep>(s =>
                s.Url == "[non-web URL]" && s.Location == BrowserAutomationFinalLocation.Unknown);
            target.NavigationFailure!.BrowserErrorCode.Should().Be("net::ERR_NAME_NOT_RESOLVED");
        }
    }

    // A navigation that succeeds carries no failure evidence at all.
    [Fact]
    public async Task ASuccessfulNavigation_HasNoNavigationFailure()
    {
        var report = await Service(new FakeFactory()).RunAsync(Request());
        report.Modes.Should().OnlyContain(m => m.Target != null && m.Target.NavigationFailure == null);
    }
}
