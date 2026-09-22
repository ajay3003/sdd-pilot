using System.Reflection;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.ManagedEdge;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// The stage flow, the classification and the cleanup guarantee.
///
/// These are exercised against a fake browser on purpose. The failure the diagnostic exists to detect — a target that
/// closes the page underneath the automation — cannot be summoned from a real browser on demand, and the ordering rule
/// that makes the result meaningful ("a target conclusion requires a passing control page") is precisely the thing a
/// manual run can never prove. The one class that touches Playwright is separated out so everything else is testable.
/// </summary>
public sealed class BrowserAutomationDiagnosticRunTests
{
    // ── A fake browser that fails exactly where a test wants it to ────────────────────────────────────────────────

    private sealed class FakeBrowser : IDiagnosticBrowser
    {
        public Func<string, Task>? OnLaunch;
        /// <summary>Called for each navigation, with the URL, so a test can fail only at the target.</summary>
        public Func<Uri, Task>? OnNavigate;
        /// <summary>Called for each controllability check, with how many have happened before it.</summary>
        public Func<int, Task<bool>>? OnIsControllable;

        public int ControlChecks { get; private set; }
        public List<Uri> Navigations { get; } = [];
        public bool Disposed { get; private set; }
        public string? LaunchedProfile { get; private set; }
        public string? EdgeVersion => "153.0.0.0";

        public async Task LaunchAsync(string profileDirectory, TimeSpan timeout, CancellationToken ct)
        {
            LaunchedProfile = profileDirectory;
            if (OnLaunch is not null) await OnLaunch(profileDirectory);
        }

        public async Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Navigations.Add(url);
            if (OnNavigate is not null) await OnNavigate(url);
        }

        public async Task<bool> IsControllableAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = ControlChecks++;
            return OnIsControllable is null || await OnIsControllable(index);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory(FakeBrowser browser) : IDiagnosticBrowserFactory
    {
        public IDiagnosticBrowser Create() => browser;
    }

    private sealed class FakeEdgeLocator(EdgeInstallation? installation) : IEdgeInstallationLocator
    {
        public EdgeInstallation? Locate() => installation;
    }

    private const string Profile = @"C:\Users\someone\AppData\Local\BirkNext\BrowserAutomationDiagnosticEdgeProfile";
    private const string ControlUrl = "https://example.com/";
    private const string TargetUrl = "https://m2lbdev.example.test/";

    private static BrowserAutomationDiagnosticRequest Request(string environmentType = "Development") => new()
    {
        TargetEnvironmentId = "dev", TargetEnvironmentName = "M2LB DEV",
        EnvironmentType = environmentType, TargetUrl = TargetUrl,
    };

    /// <summary>The service is internal; the tests drive it through its public interface via reflection-free construction.</summary>
    private static IBrowserAutomationDiagnosticService Service(
        FakeBrowser browser, EdgeInstallation? edge = null, bool isLocalWorkstation = true, bool edgeFound = true)
    {
        var type = typeof(BrowserAutomationDiagnosticPolicy).Assembly
            .GetType("BirkNext.Api.Services.BrowserAutomationDiagnostic.BrowserAutomationDiagnosticService")!;
        var logger = typeof(NullLogger<>).MakeGenericType(type).GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        return (IBrowserAutomationDiagnosticService)Activator.CreateInstance(type,
            new FakeFactory(browser),
            new FakeEdgeLocator(edgeFound ? edge ?? new EdgeInstallation(@"C:\Program Files\Edge\msedge.exe", "153.0.0.0") : null),
            logger,
            (Func<bool>)(() => isLocalWorkstation),
            (Func<string>)(() => Profile),
            ControlUrl)!;
    }

    /// <summary>Playwright's .NET binding does not expose TargetClosedException, so this is what a caller actually sees.</summary>
    private static PlaywrightException TargetClosed() =>
        new("Target page, context or browser has been closed");

    private static BrowserAutomationDiagnosticStageState State(
        BrowserAutomationDiagnosticReport report, BrowserAutomationDiagnosticStage stage) =>
        report.Stage(stage)!.State;

    // ── §38. Stage flow ───────────────────────────────────────────────────────────────────────────────────────────

    // 12. Everything works: the target is reached and stays under control.
    [Fact]
    public async Task EverythingControllable_IsPassed()
    {
        var browser = new FakeBrowser();

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.Passed);
        foreach (var stage in Enum.GetValues<BrowserAutomationDiagnosticStage>())
            State(report, stage).Should().Be(BrowserAutomationDiagnosticStageState.Passed, $"{stage} should have passed");
        browser.Navigations.Should().Equal(new Uri(ControlUrl), new Uri(TargetUrl));
    }

    // 8. A launch failure stops everything after it, and never reads as a statement about the target.
    [Fact]
    public async Task EdgeLaunchFailure_StopsLaterStages()
    {
        var browser = new FakeBrowser { OnLaunch = _ => throw new PlaywrightException("Executable doesn't exist") };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.RuntimeUnavailable);
        State(report, BrowserAutomationDiagnosticStage.EdgeLaunch).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
        foreach (var stage in new[]
        {
            BrowserAutomationDiagnosticStage.BlankPage, BrowserAutomationDiagnosticStage.ControlPage,
            BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStage.TargetControl,
        })
            State(report, stage).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
        browser.Navigations.Should().BeEmpty("the target is never contacted when the browser did not start");
    }

    // 9, 16. A page that closes on about:blank is the runtime failing. It is NOT a target-specific restriction: the
    // target has not been contacted, so there is nothing target-specific to conclude.
    [Fact]
    public async Task TargetClosedOnBlankPage_IsControlFailure_NotTargetRestricted()
    {
        var browser = new FakeBrowser { OnIsControllable = _ => throw TargetClosed() };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.ControlFailure);
        report.Result.Should().NotBe(BrowserAutomationDiagnosticResult.TargetRestricted);
        State(report, BrowserAutomationDiagnosticStage.BlankPage).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
        browser.Navigations.Should().BeEmpty();
    }

    // 10, 17. A control page that cannot be reached is a network problem, and the target is not attempted — a failure
    // there would say nothing without a working control page to contrast it with.
    [Fact]
    public async Task ControlPageNetworkFailure_PreventsAnyTargetConclusion()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "example.com"
                ? throw new PlaywrightException("net::ERR_NAME_NOT_RESOLVED")
                : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.ControlFailure);
        State(report, BrowserAutomationDiagnosticStage.ControlPage).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
        State(report, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.NotRun);
        browser.Navigations.Should().ContainSingle().Which.Host.Should().Be("example.com");
        // The report says so in words, so nobody reads a network problem as a Playwright one.
        report.Stage(BrowserAutomationDiagnosticStage.ControlPage)!.Detail
            .Should().Contain("not evidence about the target").And.Contain("network");
    }

    // 11, 20. The target is only attempted once the control page has passed.
    [Fact]
    public async Task TheTargetIsAttemptedOnlyAfterTheControlPagePasses()
    {
        var browser = new FakeBrowser();

        await Service(browser).RunAsync(Request());

        browser.Navigations[0].Should().Be(new Uri(ControlUrl));
        browser.Navigations[1].Should().Be(new Uri(TargetUrl));
    }

    // ── §39. Classification ───────────────────────────────────────────────────────────────────────────────────────

    // 13, 15. The spike's exact signature: control pages fine, page closes at the target.
    [Fact]
    public async Task TargetClosedAtTheTarget_AfterAPassingControlPage_IsTargetRestricted()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test" ? throw TargetClosed() : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.TargetRestricted);
        report.ResultLabel.Should().Be("Target-specific automation restriction detected");
        State(report, BrowserAutomationDiagnosticStage.ControlPage).Should().Be(BrowserAutomationDiagnosticStageState.Passed);
        // Blocked, not Failed: the diagnostic worked; the target refused.
        State(report, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
        report.ObservedExceptionType.Should().Be("TargetClosedException");
    }

    // The other half of the spike: navigation returns, and the page is gone when asked anything.
    [Fact]
    public async Task ATargetPageThatClosesAfterNavigating_IsAlsoTargetRestricted()
    {
        // Checks: 0 blank, 1 control, 2 target — only the target check fails.
        var browser = new FakeBrowser { OnIsControllable = index => Task.FromResult(index < 2) };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.TargetRestricted);
        State(report, BrowserAutomationDiagnosticStage.TargetNavigation).Should().Be(BrowserAutomationDiagnosticStageState.Passed,
            "navigation itself succeeded — which is exactly why navigation alone is not the check");
        State(report, BrowserAutomationDiagnosticStage.TargetControl).Should().Be(BrowserAutomationDiagnosticStageState.Blocked);
    }

    // 18. A target that never answers is a distinct outcome. A timeout is an absence of a response, not an
    // observation that automation was terminated, and calling it a restriction would overstate the run.
    [Fact]
    public async Task TargetTimeout_IsDistinctFromATargetRestriction()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test"
                ? throw new PlaywrightException("Timeout 25000ms exceeded.")
                : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().NotBe(BrowserAutomationDiagnosticResult.TargetRestricted);
        report.Result.Should().Be(BrowserAutomationDiagnosticResult.Failed);
        report.Stage(BrowserAutomationDiagnosticStage.TargetNavigation)!.Detail
            .Should().Contain("not evidence of an automation restriction");
    }

    // 14. An unexpected exception at the target is reported by type, never collapsed into "diagnostic failed".
    [Fact]
    public async Task AnUnexpectedTargetException_IsReportedByTypeAndIsNotARestriction()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test"
                ? throw new InvalidOperationException("something else")
                : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.Failed);
        report.ObservedExceptionType.Should().Be(nameof(InvalidOperationException));
    }

    // 19. Nothing the run produces claims a confirmed cause.
    [Fact]
    public async Task ARestrictedResultNeverClaimsSecurityPolicyIsConfirmed()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test" ? throw TargetClosed() : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        var everything = report.Interpretation + " " + report.ResultLabel + " " +
                         string.Join(" ", report.Stages.Select(s => s.Detail));
        everything.Should().NotContainAny("Defender", "confirmed", "DevTools disabled");
        report.Interpretation.Should().Contain("possible cause", Exactly.Once());
    }

    // ── Guards reaching the runner ────────────────────────────────────────────────────────────────────────────────

    // 2. Production never reaches a browser at all.
    [Fact]
    public async Task ProductionIsBlockedBeforeAnyBrowserIsCreated()
    {
        var browser = new FakeBrowser();

        var report = await Service(browser).RunAsync(Request("Production"));

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.Blocked);
        report.BlockedReason.Should().Be(BrowserAutomationDiagnosticPolicy.ProductionBlockedReason);
        browser.LaunchedProfile.Should().BeNull("no browser is started for a blocked diagnostic");
        browser.Navigations.Should().BeEmpty();
    }

    // 18 (runtime). No Edge means nothing was tested — and, crucially, nothing was learned about the target.
    [Fact]
    public async Task AMissingEdgeInstallationIsRuntimeUnavailable_AndNeverTouchesTheTarget()
    {
        var browser = new FakeBrowser();

        var report = await Service(browser, edgeFound: false).RunAsync(Request());

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.RuntimeUnavailable);
        State(report, BrowserAutomationDiagnosticStage.Runtime).Should().Be(BrowserAutomationDiagnosticStageState.Failed);
        browser.LaunchedProfile.Should().BeNull();
        browser.Navigations.Should().BeEmpty();
        report.Interpretation.Should().Contain("says nothing about the target application");
    }

    // 6. Every run is identifiable in a log and in a report.
    [Fact]
    public async Task EachRunHasItsOwnDiagnosticId()
    {
        var first = await Service(new FakeBrowser()).RunAsync(Request());
        var second = await Service(new FakeBrowser()).RunAsync(Request());

        first.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        second.DiagnosticId.Should().NotBe(first.DiagnosticId);
    }

    // ── §40. Cleanup ──────────────────────────────────────────────────────────────────────────────────────────────

    // 20, 21, 22. The browser closes on every path, including the one where the target killed the page.
    [Fact]
    public async Task TheDiagnosticBrowserIsClosedOnEveryPath()
    {
        var success = new FakeBrowser();
        await Service(success).RunAsync(Request());
        success.Disposed.Should().BeTrue("after a successful run");

        var closed = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test" ? throw TargetClosed() : Task.CompletedTask,
        };
        await Service(closed).RunAsync(Request());
        closed.Disposed.Should().BeTrue("after the target closed the page");

        var timedOut = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test"
                ? throw new PlaywrightException("Timeout 25000ms exceeded.")
                : Task.CompletedTask,
        };
        await Service(timedOut).RunAsync(Request());
        timedOut.Disposed.Should().BeTrue("after a timeout");

        var crashed = new FakeBrowser { OnIsControllable = _ => throw new InvalidOperationException("boom") };
        await Service(crashed).RunAsync(Request());
        crashed.Disposed.Should().BeTrue("after an unexpected failure");
    }

    // 7, 23. Cancellation stops the run, cleans up, and is not reported as a failure.
    [Fact]
    public async Task CancellationStopsTheRunCleanlyAndIsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        var browser = new FakeBrowser
        {
            OnNavigate = url =>
            {
                if (url.Host == "example.com") cts.Cancel();
                return Task.CompletedTask;
            },
        };

        var report = await Service(browser).RunAsync(Request(), cts.Token);

        report.Result.Should().Be(BrowserAutomationDiagnosticResult.Cancelled);
        report.Result.Should().NotBe(BrowserAutomationDiagnosticResult.Failed);
        browser.Disposed.Should().BeTrue();
        browser.Navigations.Should().NotContain(new Uri(TargetUrl), "the target is not contacted after cancelling");
    }

    // 24, 25. The run only ever touches the profile it was given, and never a normal Edge profile.
    [Fact]
    public async Task OnlyTheDedicatedDiagnosticProfileIsEverLaunched()
    {
        var browser = new FakeBrowser();

        await Service(browser).RunAsync(Request());

        browser.LaunchedProfile.Should().Be(Profile);
        ManagedEdgePreflightService.IsNormalEdgeProfile(browser.LaunchedProfile!).Should().BeFalse();
    }

    // ── §42. Report content ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReportCarriesTheMetadataItNeedsAndNothingSensitive()
    {
        var browser = new FakeBrowser
        {
            OnNavigate = url => url.Host == "m2lbdev.example.test" ? throw TargetClosed() : Task.CompletedTask,
        };

        var report = await Service(browser).RunAsync(Request());

        report.TargetEnvironmentName.Should().Be("M2LB DEV");
        report.TargetUrl.Should().Be(TargetUrl);
        report.ControlUrl.Should().Be(ControlUrl);
        report.EdgeVersion.Should().NotBeNullOrWhiteSpace();
        report.PlaywrightVersion.Should().NotBeNullOrWhiteSpace();
        report.OperatingSystem.Should().NotBeNullOrWhiteSpace();
        report.ProfileDescription.Should().Contain("Never signed in");

        // The profile is described, never handed over as a path with somebody's username in it.
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        serialized.Should().NotContain(@"C:\Users\someone");
        serialized.Should().NotContainAny("password", "token", "cookie", "Bearer ");
    }
}
