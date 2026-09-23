using System.Reflection;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.ManagedEdge;
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
        /// <summary>Called for each controllability check, with the mode and how many have happened before it.</summary>
        public Func<BrowserAutomationDiagnosticMode, int, Task<bool>>? OnIsControllable;
        public bool PersistentContext = true;

        public BrowserAutomationDiagnosticMode Mode { get; } = mode;
        public int ControlChecks { get; private set; }
        public List<Uri> Navigations { get; } = [];
        public bool Disposed { get; private set; }
        public BrowserAutomationDiagnosticLaunchOptions? LaunchOptions { get; private set; }
        public string? EdgeVersion => "153.0.0.0";
        public bool HasPersistentContext => LaunchOptions is not null && PersistentContext;

        public async Task LaunchAsync(BrowserAutomationDiagnosticLaunchOptions options, CancellationToken ct)
        {
            LaunchOptions = options;
            if (OnLaunch is not null) await OnLaunch(options);
        }

        public async Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Navigations.Add(url);
            if (OnNavigate is not null) await OnNavigate(Mode, url);
        }

        public async Task<bool> IsControllableAsync(TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = ControlChecks++;
            return OnIsControllable is null || await OnIsControllable(Mode, index);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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

    private static string Profile(BrowserAutomationDiagnosticMode mode) =>
        System.IO.Path.Combine(ProfileRoot, mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed");

    private static BrowserAutomationDiagnosticRequest Request(
        string environmentType = "Development", string id = "dev") => new()
        {
            TargetEnvironmentId = id, TargetEnvironmentName = "M2LB DEV",
            EnvironmentType = environmentType, TargetUrl = TargetUrl,
        };

    private static IBrowserAutomationDiagnosticService Service(
        FakeFactory factory, bool isLocalWorkstation = true, bool edgeFound = true,
        Func<BrowserAutomationDiagnosticMode, string>? profile = null)
    {
        var type = typeof(BrowserAutomationDiagnosticPolicy).Assembly
            .GetType("BirkNext.Api.Services.BrowserAutomationDiagnostic.BrowserAutomationDiagnosticService")!;
        var logger = typeof(NullLogger<>).MakeGenericType(type).GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        return (IBrowserAutomationDiagnosticService)Activator.CreateInstance(type,
            factory,
            new FakeEdgeLocator(edgeFound ? new EdgeInstallation(@"C:\Program Files\Edge\msedge.exe", "153.0.0.0") : null),
            logger,
            (Func<bool>)(() => isLocalWorkstation),
            profile ?? Profile,
            ControlUrl)!;
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
        var report = await Service(factory, profile: mode =>
            mode == BrowserAutomationDiagnosticMode.Headless ? normal : Profile(mode)).RunAsync(Request());

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
        // Checks per mode: 0 blank, 1 control, 2 target — only the target check fails.
        var factory = new FakeFactory { Configure = b => b.OnIsControllable = (_, index) => Task.FromResult(index < 2) };

        var report = await Service(factory).RunAsync(Request());

        foreach (var mode in report.Modes)
        {
            State(mode, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed,
                "navigation itself succeeded — which is exactly why navigation alone is not the check");
            State(mode, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
            mode.TargetControlAvailable.Should().BeFalse();
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
            mode.TargetControlAvailable.Should().BeTrue();
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
        report.HeadlessTargetControlAvailable.Should().BeTrue();
    }

    // 22. The case the spike suggests: blocked in both modes.
    [Fact]
    public async Task BothModesRestricted_IsTargetRestrictedInBothModes()
    {
        var factory = new FakeFactory { Configure = b => b.OnNavigate = ClosesTargetIn(b.Mode) };

        var report = await Service(factory).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes);
        report.ResultLabel.Should().Be("Target-specific automation restriction detected in both modes");
        report.HeadlessTargetControlAvailable.Should().BeFalse();
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
        report.Headed!.TargetControlAvailable.Should().BeTrue();
        report.Headless!.TargetControlAvailable.Should().BeFalse();
        // 28. Headed success alone never satisfies the headless prerequisite.
        report.HeadlessTargetControlAvailable.Should().BeFalse();
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
        report.HeadlessTargetControlAvailable.Should().BeTrue();
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
        report.HeadlessTargetControlAvailable.Should().BeFalse();
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
}
