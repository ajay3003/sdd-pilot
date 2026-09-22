using AngleSharp.Dom;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The diagnostic card: what a developer or test lead sees, and what they can take to IT.
///
/// The card's job is to make one contrast legible — automation works on a neutral page, and does not on this target —
/// without turning an observation into an accusation. These tests hold the wording as firmly as the behaviour, because
/// a report that overstates its evidence is worse than no report in a meeting about security controls.
/// </summary>
public sealed class BrowserAutomationDiagnosticCardTests : BunitContext
{
    private readonly Mock<IBrowserAutomationDiagnosticApiService> _api = new();

    private static FrontendAnalysisProfile Profile(
        string id, string name, FrontendEnvironmentType type, string? url = "https://m2lbdev.example.test/") =>
        new() { Id = id, Name = name, EnvironmentType = type, TargetUrl = url };

    private static readonly FrontendAnalysisProfile Dev = Profile("dev", "M2LB DEV", FrontendEnvironmentType.Development);
    private static readonly FrontendAnalysisProfile Qa = Profile("qa", "M2LB QA", FrontendEnvironmentType.QA, "https://m2lbqa.example.test/");
    private static readonly FrontendAnalysisProfile Prod = Profile("prod", "M2LB PROD", FrontendEnvironmentType.Production, "https://m2lb.example.test/");

    private static BrowserAutomationDiagnosticStageResult Stage(
        BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
        string? detail = null, string? url = null, string? exceptionType = null) =>
        new(stage, state, detail, url, exceptionType);

    /// <summary>The spike's outcome: control pages fine, target closed.</summary>
    private static BrowserAutomationDiagnosticReport RestrictedReport() => new()
    {
        DiagnosticId = "abc123def456",
        StartedAt = DateTimeOffset.UtcNow,
        Result = BrowserAutomationDiagnosticResult.TargetRestricted,
        ResultLabel = "Target-specific automation restriction detected",
        Interpretation = "Browser automation works in general: Playwright launched Microsoft Edge and kept control of a "
            + "neutral control page. It could not retain control of the target application. A possible cause is an "
            + "organisational or browser security policy, or another target-specific browser protection — BirkNext "
            + "observes the behaviour and cannot determine which control is responsible. No sign-in was attempted.",
        TargetEnvironmentId = "dev", TargetEnvironmentName = "M2LB DEV", TargetEnvironmentType = "Development",
        TargetUrl = "https://m2lbdev.example.test/", ControlUrl = "https://example.com/",
        EdgeVersion = "153.0.3456.78", PlaywrightVersion = "1.49.0.0", OperatingSystem = "Microsoft Windows 10.0.26200",
        ProfileDescription = "Dedicated BirkNext diagnostic Edge profile (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnosticEdgeProfile). Never signed in, never the normal Edge profile.",
        ObservedExceptionType = "TargetClosedException",
        Stages =
        [
            Stage(BrowserAutomationDiagnosticStage.Runtime, BrowserAutomationDiagnosticStageState.Passed, "Microsoft Edge 153.0.3456.78 found."),
            Stage(BrowserAutomationDiagnosticStage.EdgeLaunch, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.BlankPage, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.ControlPage, BrowserAutomationDiagnosticStageState.Passed, url: "https://example.com/"),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Blocked,
                "The Playwright-controlled page or context was closed while navigating to the target application.",
                "https://m2lbdev.example.test/", "TargetClosedException"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Passed, "Diagnostic browser closed."),
        ],
    };

    private IRenderedComponent<BrowserAutomationDiagnosticCard> Card(params FrontendAnalysisProfile[] targets)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        return Render<BrowserAutomationDiagnosticCard>(p => p.Add(c => c.Targets, targets.ToList()));
    }

    private void Returns(BrowserAutomationDiagnosticReport report) =>
        _api.Setup(a => a.RunAsync(It.IsAny<BrowserAutomationDiagnosticRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

    // ── §41. The card ─────────────────────────────────────────────────────────────────────────────────────────────

    // 26, 27. The run action and the target selector, with the environment it will drive named beside them.
    [Fact]
    public void TheRunActionAndTargetSelectorArePresent()
    {
        var card = Card(Dev, Qa);

        card.Find("[data-testid=bad-run]").TextContent.Trim().Should().Be("Run diagnostic");
        card.Find("[data-testid=bad-target-select]").QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim()).Should().Equal("M2LB DEV (Development)", "M2LB QA (QA)");
        card.Find("[data-testid=bad-target-url]").TextContent.Should().Contain("https://m2lbdev.example.test/");
    }

    // 33. A Production target is never offered. The backend refuses it too; the card simply does not propose it.
    [Fact]
    public void ProductionTargetsAreNotOffered()
    {
        var card = Card(Dev, Prod);

        var options = card.Find("[data-testid=bad-target-select]").QuerySelectorAll("option")
            .Select(o => o.TextContent).ToList();
        options.Should().NotContain(o => o.Contains("PROD"));
        options.Should().HaveCount(1);
    }

    [Fact]
    public void WithNoEligibleTargetThereIsNothingToRun()
    {
        var card = Card(Prod);

        card.FindAll("[data-testid=bad-run]").Should().BeEmpty();
        card.Find("[data-testid=bad-no-targets]").TextContent.Should().Contain("Development and QA");
    }

    // 28. While it runs, the card says what is about to happen on screen — an Edge window opening by itself is
    // alarming if nobody said it would.
    [Fact]
    public void TheRunningStateExplainsTheBrowserWindow()
    {
        var gate = new TaskCompletionSource<BrowserAutomationDiagnosticReport?>();
        _api.Setup(a => a.RunAsync(It.IsAny<BrowserAutomationDiagnosticRequest>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var card = Card(Dev);

        card.Find("[data-testid=bad-run]").Click();

        card.WaitForAssertion(() => card.Find("[data-testid=bad-progress]").TextContent
            .Should().Contain("will open and close on its own").And.Contain("Do not sign in"));
        card.Find("[data-testid=bad-cancel]").Should().NotBeNull();
        gate.SetResult(RestrictedReport());
    }

    // 29, 30. Every stage is shown, with its state as a WORD, and the restricted target reads as a diagnosis rather
    // than a crash.
    [Fact]
    public void EveryStageIsShownAndTheRestrictedTargetIsNotStyledAsAFailure()
    {
        Returns(RestrictedReport());
        var card = Card(Dev);

        card.Find("[data-testid=bad-run]").Click();
        card.WaitForAssertion(() => card.Find("[data-testid=bad-result]"));

        var stages = card.FindAll("[data-testid=bad-stage]");
        stages.Should().HaveCount(7, "every stage appears, including the ones that did not run");
        stages.Select(s => s.GetAttribute("data-stage")).Should().Equal(
            "Runtime", "EdgeLaunch", "BlankPage", "ControlPage", "TargetNavigation", "TargetControl", "Cleanup");

        // The contrast a reader needs: the control page passed, the target did not.
        var control = stages.Single(s => s.GetAttribute("data-stage") == "ControlPage");
        control.QuerySelector("[data-testid=bad-stage-state]")!.TextContent.Trim().Should().Be("PASS");
        var target = stages.Single(s => s.GetAttribute("data-stage") == "TargetNavigation");
        target.QuerySelector("[data-testid=bad-stage-state]")!.TextContent.Trim().Should().Be("BLOCKED");

        // Blocked carries the warning tone, not the one used for something that crashed.
        card.Find("[data-testid=bad-result-label]").ClassList.Should().Contain("fqr-pill-warning");
        card.Find("[data-testid=bad-result]").GetAttribute("data-result").Should().Be("TargetRestricted");
        card.Find("[data-testid=bad-exception]").TextContent.Should().Contain("TargetClosedException");
    }

    // 34. The result never claims a cause it cannot see.
    [Fact]
    public void TheResultNeverClaimsASecurityPolicyIsConfirmed()
    {
        Returns(RestrictedReport());
        var card = Card(Dev);

        card.Find("[data-testid=bad-run]").Click();
        card.WaitForAssertion(() => card.Find("[data-testid=bad-result]"));

        var text = card.Find("[data-testid=browser-automation-diagnostic]").TextContent;
        text.Should().NotContainAny("Defender", "confirmed", "DevTools disabled", "policy blocked Playwright");
        card.Find("[data-testid=bad-interpretation]").TextContent
            .Should().Contain("possible cause").And.Contain("cannot determine which control is responsible");
    }

    // The card states up front that it changes nothing and signs in to nothing.
    [Fact]
    public void TheCardStatesThatItSignsInToNothingAndChangesNothing()
    {
        var intro = Card(Dev).Find("[data-testid=bad-intro]").TextContent;

        intro.Should().Contain("No sign-in is attempted");
        intro.Should().Contain("nothing about your browser or its security settings is changed");
        intro.Should().Contain("dedicated BirkNext profile");
    }

    // ── §42. The report ───────────────────────────────────────────────────────────────────────────────────────────

    // 31, 42. The copied report carries what IT needs and nothing else.
    [Fact]
    public void TheCopiedReportCarriesTheEvidenceAndNoSecrets()
    {
        var text = BrowserAutomationDiagnosticReportText.Build(RestrictedReport());

        text.Should().Contain("Browser Automation Diagnostic");
        text.Should().Contain("M2LB DEV").And.Contain("https://m2lbdev.example.test/");
        text.Should().Contain("Control URL: https://example.com/");
        text.Should().Contain("Edge: 153.0.3456.78").And.Contain("Playwright: 1.49.0.0");
        // The stage lines, which are the whole point of handing this over.
        text.Should().Contain("Control page: PASS").And.Contain("Target navigation: BLOCKED");
        text.Should().Contain("Observed exception: TargetClosedException");
        text.Should().Contain("Interpretation:");
        // 7. The question everyone asks first, answered without being asked.
        text.Should().Contain("No login, MFA, credential or token was attempted or captured");

        // 32, 42. Nothing sensitive, and no path with somebody's user name in it.
        text.Should().NotContainAny("password", "Bearer ", "Cookie:", "token=");
        text.Should().NotContain(@"C:\Users\");
        text.Should().Contain("%LOCALAPPDATA%", "the profile is described, not located");
    }

    // 31. The copy action exists and reports that it copied.
    [Fact]
    public void TheReportCanBeCopied()
    {
        Returns(RestrictedReport());
        var card = Card(Dev);
        card.Find("[data-testid=bad-run]").Click();
        card.WaitForAssertion(() => card.Find("[data-testid=bad-copy]"));

        card.Find("[data-testid=bad-copy]").TextContent.Trim().Should().Be("Copy diagnostic report");
        card.Find("[data-testid=bad-copy]").Click();

        card.WaitForAssertion(() => card.Find("[data-testid=bad-copy]").TextContent.Trim().Should().Be("Report copied"));
    }

    // A blocked run explains itself rather than looking like a crash.
    [Fact]
    public void ABlockedRunShowsItsReason()
    {
        Returns(RestrictedReport() with
        {
            Result = BrowserAutomationDiagnosticResult.Blocked,
            ResultLabel = "Diagnostic not run",
            BlockedReason = "The browser automation diagnostic does not run against Production.",
            Stages = [Stage(BrowserAutomationDiagnosticStage.Runtime, BrowserAutomationDiagnosticStageState.Blocked)],
        });
        var card = Card(Dev);

        card.Find("[data-testid=bad-run]").Click();
        card.WaitForAssertion(() => card.Find("[data-testid=bad-blocked-reason]").TextContent
            .Should().Contain("does not run against Production"));
    }

    // The request the card sends is the selected environment's canonical values — never a hard-coded host.
    [Fact]
    public void TheRequestCarriesTheSelectedTargetEnvironment()
    {
        Returns(RestrictedReport());
        var card = Card(Dev, Qa);

        card.Find("[data-testid=bad-target-select]").Change("qa");
        card.Find("[data-testid=bad-run]").Click();

        card.WaitForAssertion(() => _api.Verify(a => a.RunAsync(
            It.Is<BrowserAutomationDiagnosticRequest>(r =>
                r.TargetEnvironmentId == "qa" &&
                r.TargetEnvironmentName == "M2LB QA" &&
                r.TargetUrl == "https://m2lbqa.example.test/" &&
                r.EnvironmentType == "QA"),
            It.IsAny<CancellationToken>()), Times.Once));
    }
}
