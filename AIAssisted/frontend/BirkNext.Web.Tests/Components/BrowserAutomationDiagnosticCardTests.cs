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
/// The card's job is to make two contrasts legible — automation works on a neutral page and does not on this target,
/// and it behaves one way with a window and another without one — while never turning an observation into an
/// accusation. These tests hold the wording as firmly as the behaviour, because a report that overstates its evidence
/// is worse than no report in a meeting about security controls.
/// </summary>
public sealed class BrowserAutomationDiagnosticCardTests : BunitContext
{
    private readonly Mock<IBrowserAutomationDiagnosticApiService> _api = new();

    private static FrontendAnalysisProfile Profile(
        string id, string name, FrontendEnvironmentType type, string? url = "https://m2lbdev.example.test/") =>
        new() { Id = id, Name = name, EnvironmentType = type, TargetUrl = url };

    private static readonly FrontendAnalysisProfile Dev = Profile("dev", "M2LB DEV", FrontendEnvironmentType.Development);
    private static readonly FrontendAnalysisProfile Qa = WithAuthority(
        Profile("qa", "M2LB QA", FrontendEnvironmentType.QA, "https://m2lbqa.example.test/"),
        "https://login.microsoftonline.com/tenant-qa/v2.0");

    private static FrontendAnalysisProfile WithAuthority(FrontendAnalysisProfile profile, string authority)
    {
        profile.Authentication.ExpectedAuthority = authority;
        return profile;
    }
    private static readonly FrontendAnalysisProfile Prod = Profile("prod", "M2LB PROD", FrontendEnvironmentType.Production, "https://m2lb.example.test/");

    private const string ProfileNote =
        "Dedicated BirkNext diagnostic Edge profile (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnostic\\{0}). "
        + "Never signed in, never the normal Edge profile.";

    private static BrowserAutomationDiagnosticStageResult Stage(
        BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
        string? detail = null, string? url = null, string? exceptionType = null) =>
        new(stage, state, detail, url, exceptionType);

    private static List<BrowserAutomationDiagnosticStageResult> SetupStagesPassed() =>
    [
        Stage(BrowserAutomationDiagnosticStage.Runtime, BrowserAutomationDiagnosticStageState.Passed, "Microsoft Edge 153.0.3456.78 found."),
        Stage(BrowserAutomationDiagnosticStage.EdgeLaunch, BrowserAutomationDiagnosticStageState.Passed),
        Stage(BrowserAutomationDiagnosticStage.PersistentContext, BrowserAutomationDiagnosticStageState.Passed),
        Stage(BrowserAutomationDiagnosticStage.BlankPage, BrowserAutomationDiagnosticStageState.Passed),
        Stage(BrowserAutomationDiagnosticStage.ControlPage, BrowserAutomationDiagnosticStageState.Passed, url: "https://example.com/"),
    ];

    private static BrowserAutomationDiagnosticStageResult Cleanup() =>
        Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Passed, "Diagnostic browser closed.");

    /// <summary>A mode that kept control and whose page was identified as the target application.</summary>
    private static BrowserAutomationDiagnosticModeReport AvailableMode(BrowserAutomationDiagnosticMode mode) => new()
    {
        Mode = mode,
        Headless = mode == BrowserAutomationDiagnosticMode.Headless,
        Result = BrowserAutomationDiagnosticModeResult.Available,
        EdgeVersion = "153.0.3456.78",
        ProfileDescription = string.Format(ProfileNote, mode),
        Stages =
        [
            .. SetupStagesPassed(),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Passed, url: "https://m2lbdev.example.test/"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetApplication, BrowserAutomationDiagnosticStageState.Passed,
                "Identified by: Expected target origin and the configured application marker."),
            Cleanup(),
        ],
        Target = new()
        {
            RequestedUrl = "https://m2lbdev.example.test/", ExpectedOrigin = "https://m2lbdev.example.test",
            FinalUrl = "https://m2lbdev.example.test/oversikt", FinalScheme = "https", FinalHost = "m2lbdev.example.test",
            FinalOrigin = "https://m2lbdev.example.test", FinalLocation = BrowserAutomationFinalLocation.TargetOrigin,
            ExpectedOriginReached = BrowserAutomationEvidenceAnswer.Yes, AuthenticationRedirect = BrowserAutomationEvidenceAnswer.No,
            TargetApplicationIdentified = BrowserAutomationEvidenceAnswer.Yes,
            IdentificationEvidence = "Expected target origin and the configured application marker.",
            ApplicationMarkerConfigured = true, ApplicationMarkerFound = true, NavigationSettled = true, StabilityWindowMs = 5000,
            NavigationTrace = [new(1, 180, "https://m2lbdev.example.test/", BrowserAutomationFinalLocation.TargetOrigin)],
        },
    };

    /// <summary>
    /// What M2LB really does for a fresh profile: the navigation is accepted, the SPA redirects to Entra, and Playwright
    /// stays in control of THAT page. Automation available; target application not yet reached.
    /// </summary>
    private static BrowserAutomationDiagnosticModeReport AuthRedirectMode(BrowserAutomationDiagnosticMode mode) => new()
    {
        Mode = mode,
        Headless = mode == BrowserAutomationDiagnosticMode.Headless,
        Result = BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary,
        EdgeVersion = "153.0.3456.78",
        ProfileDescription = string.Format(ProfileNote, mode),
        Stages =
        [
            .. SetupStagesPassed(),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Passed, url: "https://m2lbdev.example.test/"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetApplication, BrowserAutomationDiagnosticStageState.NotReached,
                "Not yet reached — authentication required first. The browser is at login.microsoftonline.com."),
            Cleanup(),
        ],
        Target = new()
        {
            RequestedUrl = "https://m2lbdev.example.test/", ExpectedOrigin = "https://m2lbdev.example.test",
            FinalUrl = "https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]", FinalScheme = "https",
            FinalHost = "login.microsoftonline.com", FinalOrigin = "https://login.microsoftonline.com",
            FinalLocation = BrowserAutomationFinalLocation.AuthenticationAuthority,
            ExpectedOriginReached = BrowserAutomationEvidenceAnswer.No, AuthenticationRedirect = BrowserAutomationEvidenceAnswer.Yes,
            AuthenticationHost = "login.microsoftonline.com", TargetApplicationIdentified = BrowserAutomationEvidenceAnswer.No,
            IdentificationEvidence = "Not yet reached: the target redirected to authentication first.",
            NavigationSettled = true, StabilityWindowMs = 5000,
            NavigationTrace =
            [
                new(1, 180, "https://m2lbdev.example.test/", BrowserAutomationFinalLocation.TargetOrigin),
                new(2, 3900, "https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]", BrowserAutomationFinalLocation.AuthenticationAuthority),
            ],
        },
    };

    /// <summary>Navigation and the first probe succeeded; then the page closed during the stability window.</summary>
    private static BrowserAutomationDiagnosticModeReport LateCloseMode(BrowserAutomationDiagnosticMode mode) => AuthRedirectMode(mode) with
    {
        Result = BrowserAutomationDiagnosticModeResult.TargetRestricted,
        Stages =
        [
            .. SetupStagesPassed(),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStageState.Blocked,
                "The navigation was accepted and the first probe succeeded, then playwright reported PageClosed at 5200 ms during the stability window. Control was lost late, after an initial success."),
            Stage(BrowserAutomationDiagnosticStage.TargetApplication, BrowserAutomationDiagnosticStageState.NotRun),
            Cleanup(),
        ],
        Target = AuthRedirectMode(mode).Target! with
        {
            TargetApplicationIdentified = BrowserAutomationEvidenceAnswer.Unknown,
            FailurePhase = BrowserAutomationFailurePhase.DuringStabilityWindow,
            LifecycleEvents = [new(BrowserAutomationLifecycleEventKind.PageClosed, 5200, BrowserAutomationFailurePhase.DuringStabilityWindow)],
        },
    };

    /// <summary>The spike's outcome for one mode: control pages fine, target closed.</summary>
    private static BrowserAutomationDiagnosticModeReport RestrictedMode(BrowserAutomationDiagnosticMode mode) => new()
    {
        Mode = mode,
        Headless = mode == BrowserAutomationDiagnosticMode.Headless,
        Result = BrowserAutomationDiagnosticModeResult.TargetRestricted,
        EdgeVersion = "153.0.3456.78",
        ProfileDescription = string.Format(ProfileNote, mode),
        ObservedExceptionType = "TargetClosedException",
        Stages =
        [
            Stage(BrowserAutomationDiagnosticStage.Runtime, BrowserAutomationDiagnosticStageState.Passed, "Microsoft Edge 153.0.3456.78 found."),
            Stage(BrowserAutomationDiagnosticStage.EdgeLaunch, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.PersistentContext, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.BlankPage, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.ControlPage, BrowserAutomationDiagnosticStageState.Passed, url: "https://example.com/"),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Blocked,
                "Playwright reported TargetClosedException while navigating to the target application: the Playwright-controlled page or context was closed. Target-navigation restriction.",
                "https://m2lbdev.example.test/", "TargetClosedException"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.TargetApplication, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Passed, "Diagnostic browser closed."),
        ],
        Target = new()
        {
            RequestedUrl = "https://m2lbdev.example.test/", ExpectedOrigin = "https://m2lbdev.example.test",
            ExceptionType = "TargetClosedException", FailurePhase = BrowserAutomationFailurePhase.DuringTargetNavigation,
        },
    };

    private static BrowserAutomationDiagnosticReport Report(
        BrowserAutomationDiagnosticComparison result, string label, string interpretation,
        params BrowserAutomationDiagnosticModeReport[] modes) => new()
        {
            DiagnosticId = "abc123def456",
            StartedAt = DateTimeOffset.UtcNow,
            Result = result,
            ResultLabel = label,
            Interpretation = interpretation,
            TargetEnvironmentId = "dev", TargetEnvironmentName = "M2LB DEV", TargetEnvironmentType = "Development",
            TargetUrl = "https://m2lbdev.example.test/", ControlUrl = "https://example.com/",
            EdgeVersion = "153.0.3456.78", PlaywrightVersion = "1.49.0.0",
            OperatingSystem = "Microsoft Windows 10.0.26200",
            Modes = [.. modes],
        };

    /// <summary>Both modes lost the target. The whole-workstation conclusion is unavailable in either direction.</summary>
    private static BrowserAutomationDiagnosticReport RestrictedInBothModes() => Report(
        BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes,
        "Target-specific automation restriction detected in both modes",
        // Verbatim from BrowserAutomationDiagnosticPolicy.Interpretation, so the wording rules below are checked
        // against what the backend actually sends rather than against invented fixture text.
        "Playwright browser automation works on neutral pages in both headed and headless Microsoft Edge, and "
        + "automation control cannot be retained on the configured target in either mode. This is consistent with "
        + "a target or security-context-specific browser automation restriction. It does not identify which "
        + "organisational control is responsible, and it does not mean browser developer tooling is disabled "
        + "generally — the same automation worked on the control page. No sign-in was attempted.",
        RestrictedMode(BrowserAutomationDiagnosticMode.Headed),
        RestrictedMode(BrowserAutomationDiagnosticMode.Headless));

    /// <summary>The case the two modes exist for: it works while you watch it, and not when you do not.</summary>
    private static BrowserAutomationDiagnosticReport HeadlessOnlyRestricted() => Report(
        BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted,
        "Headless automation restricted for this target",
        // Verbatim from BrowserAutomationDiagnosticPolicy.Interpretation.
        "Playwright retained control of the target in headed Microsoft Edge but not in headless. Automation works "
        + "on neutral pages in both modes, so this is consistent with a restriction that distinguishes headless "
        + "automation for this target or security context. This matters for unattended CI, which has no visible "
        + "browser. BirkNext cannot determine which control is responsible. No sign-in was attempted.",
        AvailableMode(BrowserAutomationDiagnosticMode.Headed),
        RestrictedMode(BrowserAutomationDiagnosticMode.Headless));

    private const string AvailableInterpretation =
        // Verbatim from BrowserAutomationDiagnosticPolicy.Interpretation.
        "Playwright remained in control of the browser through the tested path in both headed and headless "
        + "Microsoft Edge. That is a statement about browser automation, not about which page was reached. It does "
        + "not establish that authentication, MFA or Conditional Access work under automation — that is assessed "
        + "separately by the Headless Authentication & Session Control Diagnostic. No sign-in was attempted.";

    private static readonly List<BrowserAutomationControlDimension> Dimensions =
    [
        new("Corporate Edge DevTools UI (F12 / Inspect)", "Unknown", "Not observed by this diagnostic."),
        new("Playwright-owned browser automation", "Available", "Observed in this run: headed available, headless available."),
        new("CDP attach to an existing or protected Edge", "Not tested", "This diagnostic never attaches to an Edge it did not start."),
    ];

    private static BrowserAutomationDiagnosticReport AutomationAvailable() => Report(
        BrowserAutomationDiagnosticComparison.AutomationAvailable,
        "Browser automation available",
        AvailableInterpretation + " In both modes, the page Playwright controlled was identified as the target application.",
        AvailableMode(BrowserAutomationDiagnosticMode.Headed),
        AvailableMode(BrowserAutomationDiagnosticMode.Headless)) with { ControlDimensions = Dimensions };

    /// <summary>The M2LB reality for a fresh profile: all automation works, and both modes end on Entra.</summary>
    private static BrowserAutomationDiagnosticReport AvailableThroughAuthRedirect() => Report(
        BrowserAutomationDiagnosticComparison.AutomationAvailable,
        "Browser automation available",
        AvailableInterpretation + " In both modes, the target redirected to authentication (login.microsoftonline.com) and "
        + "Playwright remained in control of the browser through that handoff. This proves browser automation is available "
        + "through the authentication handoff. It does NOT prove that the authenticated target application is controllable: "
        + "the target application was not yet reached. Corporate Edge visible DevTools policy is a separate control and is "
        + "not inferred from this result.",
        AuthRedirectMode(BrowserAutomationDiagnosticMode.Headed),
        AuthRedirectMode(BrowserAutomationDiagnosticMode.Headless)) with { ControlDimensions = Dimensions };

    private IRenderedComponent<BrowserAutomationDiagnosticCard> Card(params FrontendAnalysisProfile[] targets)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        return Render<BrowserAutomationDiagnosticCard>(p => p.Add(c => c.Targets, targets.ToList()));
    }

    private void Returns(BrowserAutomationDiagnosticReport report) =>
        _api.Setup(a => a.RunAsync(It.IsAny<BrowserAutomationDiagnosticRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

    private IRenderedComponent<BrowserAutomationDiagnosticCard> Ran(BrowserAutomationDiagnosticReport report)
    {
        Returns(report);
        var card = Card(Dev);
        card.Find("[data-testid=bad-run]").Click();
        card.WaitForAssertion(() => card.Find("[data-testid=bad-result]"));
        return card;
    }

    private static IElement ModePanel(IRenderedComponent<BrowserAutomationDiagnosticCard> card, string mode) =>
        card.FindAll("[data-testid=bad-mode]").Single(m => m.GetAttribute("data-mode") == mode);

    /// <summary>Markup wraps sentences across lines; a reader sees one sentence, so the assertions do too.</summary>
    private static string Normalise(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── The card ──────────────────────────────────────────────────────────────────────────────────────────────────

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

    // 28. While it runs, the card says what is about to happen on screen — twice over, because a window that opens
    // by itself is alarming, and a second run with no window at all looks like nothing happening.
    [Fact]
    public void TheRunningStateExplainsBothBrowserModes()
    {
        var gate = new TaskCompletionSource<BrowserAutomationDiagnosticReport?>();
        _api.Setup(a => a.RunAsync(It.IsAny<BrowserAutomationDiagnosticRequest>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var card = Card(Dev);

        card.Find("[data-testid=bad-run]").Click();

        card.WaitForAssertion(() =>
        {
            var progress = card.Find("[data-testid=bad-progress]").TextContent;
            progress.Should().Contain("will open and close on its own").And.Contain("Do not sign in");
            progress.Should().Contain("Headless mode").And.Contain("no window at all");
        });
        card.Find("[data-testid=bad-cancel]").Should().NotBeNull();
        gate.SetResult(RestrictedInBothModes());
    }

    // ── §12. The two modes are peers ─────────────────────────────────────────────────────────────────────────────

    // Both modes are shown at the same level, in the order they ran. Neither is nested inside the other.
    [Fact]
    public void BothModesAreShownAsPeersInTheOrderTheyRan()
    {
        var card = Ran(RestrictedInBothModes());

        var modes = card.FindAll("[data-testid=bad-mode]");
        modes.Should().HaveCount(2);
        modes.Select(m => m.GetAttribute("data-mode")).Should().Equal("Headed", "Headless");
        modes.Select(m => m.QuerySelector("[data-testid=bad-mode-name]")!.TextContent.Trim())
            .Should().Equal("Headed mode", "Headless mode");
    }

    // The point of §12: headless must not be hidden behind a disclosure. It is visible without the reader opening
    // anything, and it is not a descendant of any collapsible section.
    [Fact]
    public void HeadlessIsNotHiddenUnderTechnicalDetails()
    {
        var card = Ran(RestrictedInBothModes());

        var headless = ModePanel(card, "Headless");
        headless.Closest("details").Should().BeNull("headless is a peer result, not a technical detail");

        // And it sits in the same container as headed, at the same depth.
        var headed = ModePanel(card, "Headed");
        headless.ParentElement.Should().BeSameAs(headed.ParentElement);
        card.Find("[data-testid=bad-modes]").Closest("details").Should().BeNull();
    }

    // Each mode carries its own verdict and says why it matters, so "headless" is not read as a variant of one test.
    [Fact]
    public void EachModeCarriesItsOwnVerdictAndItsReasonForExisting()
    {
        var card = Ran(HeadlessOnlyRestricted());

        var headed = ModePanel(card, "Headed");
        headed.GetAttribute("data-mode-result").Should().Be("Available");
        headed.QuerySelector("[data-testid=bad-mode-result]")!.TextContent.Trim().Should().Be("Target application controllable");
        headed.QuerySelector("[data-testid=bad-mode-result]")!.ClassList.Should().Contain("fqr-pill-ready");
        headed.QuerySelector("[data-testid=bad-mode-description]")!.TextContent.Should().Contain("visible Edge window");

        var headless = ModePanel(card, "Headless");
        headless.GetAttribute("data-mode-result").Should().Be("TargetRestricted");
        headless.QuerySelector("[data-testid=bad-mode-result]")!.TextContent.Trim()
            .Should().Be("Restricted for this target");
        // A detected restriction is a successful diagnosis, not a crash.
        headless.QuerySelector("[data-testid=bad-mode-result]")!.ClassList.Should().Contain("fqr-pill-warning");
        headless.QuerySelector("[data-testid=bad-mode-description]")!.TextContent
            .Should().Contain("unattended pipeline");
    }

    private static string Fact(IElement panel, string key) => panel
        .QuerySelectorAll("[data-testid=bad-fact]").Single(f => f.GetAttribute("data-fact") == key)
        .QuerySelector("[data-testid=bad-fact-value]")!.TextContent.Trim();

    // 29, 30. Setup stages are listed per mode with their state as a WORD; what happened AT the target is shown as
    // separate facts beside them, with the control/target contrast intact.
    [Fact]
    public void EveryStageIsShownWithinItsOwnMode()
    {
        var card = Ran(HeadlessOnlyRestricted());

        foreach (var mode in new[] { "Headed", "Headless" })
        {
            var stages = ModePanel(card, mode).QuerySelectorAll("[data-testid=bad-stages] [data-testid=bad-stage]");
            stages.Select(s => s.GetAttribute("data-stage")).Should().Equal(
                "Runtime", "EdgeLaunch", "PersistentContext", "BlankPage", "ControlPage", "Cleanup");
        }

        // The contrast a reader needs, inside the mode where it happened: the control page passed, the target did not.
        var headless = ModePanel(card, "Headless");
        State(headless, "ControlPage").Should().Be("PASS");
        Fact(headless, "navigation").Should().Be("BLOCKED");
        Fact(headless, "control").Should().Be("Not run");
        Fact(headless, "exception").Should().Contain("TargetClosedException").And.Contain("during target navigation");

        // And the headed mode, which is the one that worked, is not contaminated by the headless result.
        Fact(ModePanel(card, "Headed"), "control").Should().Be("PASS");
        Fact(ModePanel(card, "Headed"), "application").Should().Be("PASS");

        static string State(IElement panel, string stage) => panel
            .QuerySelectorAll("[data-testid=bad-stage]").Single(s => s.GetAttribute("data-stage") == stage)
            .QuerySelector("[data-testid=bad-stage-state]")!.TextContent.Trim();
    }

    // §37, §30 (frontend). The M2LB reality: the target redirected to Entra. The card shows where the browser actually
    // is, that control survived, and that the target application was NOT reached — never "Target application PASS".
    [Fact]
    public void AnEntraRedirectIsShownAsNotYetReached_NeverTargetApplicationPass()
    {
        var card = Ran(AvailableThroughAuthRedirect());

        foreach (var mode in new[] { "Headed", "Headless" })
        {
            var panel = ModePanel(card, mode);
            Fact(panel, "requested").Should().Be("https://m2lbdev.example.test/");
            Fact(panel, "navigation").Should().Be("PASS");
            Fact(panel, "final-host").Should().Be("login.microsoftonline.com");
            Fact(panel, "final-location").Should().StartWith("https://login.microsoftonline.com/");
            Fact(panel, "expected-origin").Should().Be("NO");
            Fact(panel, "auth-redirect").Should().Be("DETECTED");
            Fact(panel, "control").Should().Be("PASS");
            Fact(panel, "stability").Should().Be("PASS");
            Fact(panel, "application").Should().Be("NOT YET REACHED");
            Fact(panel, "application").Should().NotBe("PASS");

            panel.GetAttribute("data-mode-result").Should().Be("AvailableAtAuthenticationBoundary");
            panel.QuerySelector("[data-testid=bad-mode-result]")!.TextContent.Trim()
                .Should().Be("Available through authentication redirect");
        }

        // The whole card never pairs the words "Target application" with PASS for this run.
        var facts = card.FindAll("[data-testid=bad-fact]")
            .Select(f => Normalise(f.TextContent)).ToList();
        facts.Should().NotContain(f => f.StartsWith("Target application") && f.EndsWith("PASS"));
        card.Find("[data-testid=bad-interpretation]").TextContent.Should()
            .Contain("does NOT prove that the authenticated target application is controllable");
    }

    // §38. An authentication redirect is an expected boundary: informational, not red and not a failure.
    [Fact]
    public void TheAuthenticationRedirectIsInformational_NotAFailure()
    {
        var headless = ModePanel(Ran(AvailableThroughAuthRedirect()), "Headless");

        Tone(headless, "auth-redirect").Should().Be("fqr-pill-info");
        Tone(headless, "application").Should().Be("fqr-pill-info");
        Tone(headless, "control").Should().Be("fqr-pill-ready");
        headless.QuerySelector("[data-testid=bad-mode-result]")!.ClassList.Should().Contain("fqr-pill-info")
            .And.NotContain("fqr-pill-attention").And.NotContain("fqr-pill-warning");

        static string Tone(IElement panel, string key) => panel
            .QuerySelectorAll("[data-testid=bad-fact]").Single(f => f.GetAttribute("data-fact") == key)
            .QuerySelector("[data-testid=bad-fact-value]")!.ClassList.Single(c => c.StartsWith("fqr-pill-"));
    }

    // §37. The important evidence is visible without opening Technical details; only the raw trace is disclosed.
    [Fact]
    public void TheTargetEvidenceIsVisibleWithoutOpeningAnything()
    {
        var headless = ModePanel(Ran(AvailableThroughAuthRedirect()), "Headless");

        var evidence = headless.QuerySelector("[data-testid=bad-target-evidence]")!;
        evidence.Closest("details").Should().BeNull();
        headless.QuerySelector("[data-testid=bad-trace]")!.TagName.Should().Be("DETAILS");
        headless.QuerySelectorAll("[data-testid=bad-trace-step]").Select(s => Normalise(s.TextContent)).Should().HaveCount(2)
            .And.Contain(s => s.Contains("authorize?[redacted]") && s.Contains("authentication authority"));
    }

    // §32 (frontend). A late close is shown as a blocked stability check with the event that caused it.
    [Fact]
    public void ALateCloseIsShownAsABlockedStabilityCheck()
    {
        var card = Ran(Report(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes,
            "Target-specific automation restriction detected in both modes", "…",
            LateCloseMode(BrowserAutomationDiagnosticMode.Headed), LateCloseMode(BrowserAutomationDiagnosticMode.Headless)));

        var headless = ModePanel(card, "Headless");
        Fact(headless, "control").Should().Be("PASS");
        Fact(headless, "stability").Should().Be("BLOCKED");
        Fact(headless, "application").Should().Be("Not run");
        headless.QuerySelector("[data-testid=bad-target-blocked-detail]")!.TextContent.Should().Contain("Control was lost late");
        headless.QuerySelector("[data-testid=bad-lifecycle-event]")!.TextContent.Should().Contain("PageClosed");
        card.Find("[data-testid=bad-prerequisite]").GetAttribute("data-available").Should().Be("false");
    }

    // §23, §24, §36. Three independent controls, shown apart; a working Playwright never reads as "DevTools enabled".
    [Fact]
    public void IndependentControlsAreShownApartAndNothingClaimsDevToolsIsEnabled()
    {
        var card = Ran(AvailableThroughAuthRedirect());

        card.FindAll("[data-testid=bad-control-dimension]").Select(d => d.QuerySelector("dt")!.TextContent.Trim()).Should().Equal(
            "Corporate Edge DevTools UI (F12 / Inspect)", "Playwright-owned browser automation", "CDP attach to an existing or protected Edge");
        card.FindAll("[data-testid=bad-control-dimension-state]").Select(d => d.TextContent.Trim())
            .Should().Equal("Unknown", "Available", "Not tested");

        var text = Normalise(card.Find("[data-testid=browser-automation-diagnostic]").TextContent);
        text.Should().NotContainAny("DevTools enabled", "DevTools is enabled", "DevTools disabled", "DevTools is disabled",
            "Security policy blocked target", "security policy blocked");
    }

    // The exception type is reported inside the mode that observed it, never attributed to the run as a whole.
    [Fact]
    public void TheObservedExceptionStaysWithTheModeThatSawIt()
    {
        var card = Ran(HeadlessOnlyRestricted());

        ModePanel(card, "Headless").QuerySelector("[data-testid=bad-exception]")!.TextContent
            .Should().Contain("TargetClosedException");
        ModePanel(card, "Headed").QuerySelector("[data-testid=bad-exception]").Should().BeNull();
    }

    // The overall result reads as a diagnosis rather than a crash.
    [Fact]
    public void TheOverallResultIsTonedAsADiagnosis()
    {
        var card = Ran(RestrictedInBothModes());

        card.Find("[data-testid=bad-result]").GetAttribute("data-result").Should().Be("TargetRestrictedInBothModes");
        card.Find("[data-testid=bad-result-label]").ClassList.Should().Contain("fqr-pill-warning");
    }

    // ── §25. The prerequisite chain ───────────────────────────────────────────────────────────────────────────────

    // Headless works: the authentication diagnostic may run, and the card says so.
    [Fact]
    public void WhenHeadlessKeepsControlThePrerequisiteIsMet()
    {
        var card = Ran(AutomationAvailable());

        var prerequisite = card.Find("[data-testid=bad-prerequisite]");
        prerequisite.GetAttribute("data-available").Should().Be("true");
        prerequisite.ClassList.Should().Contain("fqr-pill-ready");
        card.Find("[data-testid=bad-prerequisite-headline]").TextContent.Trim()
            .Should().Be("Headless authentication diagnostic can run");
    }

    // The case that misleads: headed worked, so "cannot run" must say which mode was asked about, or the reader
    // reads it as a contradiction of what they just watched happen.
    [Fact]
    public void WhenOnlyHeadedWorksThePrerequisiteExplainsWhyThatIsNotEnough()
    {
        var card = Ran(HeadlessOnlyRestricted());

        var prerequisite = card.Find("[data-testid=bad-prerequisite]");
        prerequisite.GetAttribute("data-available").Should().Be("false");
        prerequisite.ClassList.Should().Contain("fqr-pill-warning");
        card.Find("[data-testid=bad-prerequisite-headline]").TextContent.Trim()
            .Should().Be("Headless authentication diagnostic cannot run yet");

        var explanation = card.Find("[data-testid=bad-prerequisite-explanation]").TextContent;
        explanation.Should().Contain("Headed mode kept control");
        explanation.Should().Contain("Headed-only success is not sufficient");
        explanation.Should().Contain("MFA, Conditional Access and session control cannot be assessed yet");
    }

    // Both modes blocked: the same unmet prerequisite, without the headed qualifier that would not be true.
    [Fact]
    public void WhenNeitherModeWorksThePrerequisiteIsUnmetWithoutTheHeadedQualifier()
    {
        var card = Ran(RestrictedInBothModes());

        card.Find("[data-testid=bad-prerequisite]").GetAttribute("data-available").Should().Be("false");
        var explanation = card.Find("[data-testid=bad-prerequisite-explanation]").TextContent;
        explanation.Should().Contain("Headless mode did not keep control");
        explanation.Should().NotContain("Headed mode kept control");
    }

    // ── What the card must never say ─────────────────────────────────────────────────────────────────────────────

    // 34. The result never claims a cause it cannot see, and never generalises a target restriction into a statement
    // about browser developer tooling.
    [Fact]
    public void TheResultNeverClaimsASecurityPolicyIsConfirmed()
    {
        var card = Ran(RestrictedInBothModes());

        var text = card.Find("[data-testid=browser-automation-diagnostic]").TextContent;
        text.Should().NotContainAny(
            "Defender", "MCAS", "Conditional Access caused", "confirmed",
            "DevTools is disabled", "DevTools disabled", "policy blocked Playwright");
        // It offers the possibility and leaves the question open, which is what makes it worth taking to IT.
        var interpretation = Normalise(card.Find("[data-testid=bad-interpretation]").TextContent);
        interpretation.Should().Contain("This is consistent with");
        interpretation.Should().Contain("does not identify which organisational control is responsible");
        // And it refuses the global claim its own evidence would contradict.
        interpretation.Should().Contain("does not mean browser developer tooling is disabled generally");
    }

    // The headless-only case is the one most likely to be over-read, so it is held to the same rule.
    [Fact]
    public void TheHeadlessOnlyCaseIsDescribedAsATargetRestrictionNotADisabledFeature()
    {
        var card = Ran(HeadlessOnlyRestricted());

        var text = card.Find("[data-testid=browser-automation-diagnostic]").TextContent;
        text.Should().NotContainAny("DevTools is disabled", "DevTools disabled", "headless is blocked by policy");

        var interpretation = Normalise(card.Find("[data-testid=bad-interpretation]").TextContent);
        interpretation.Should().Contain("consistent with a restriction that distinguishes headless automation");
        interpretation.Should().Contain("cannot determine which control is responsible");
        // The reason this mode is worth running at all.
        interpretation.Should().Contain("matters for unattended CI");
    }

    // The card states up front that it changes nothing and signs in to nothing, and that it runs twice.
    [Fact]
    public void TheCardStatesThatItSignsInToNothingAndChangesNothing()
    {
        // Normalised, because the sentence wraps across lines in the markup and the reader sees one sentence.
        var intro = Normalise(Card(Dev).Find("[data-testid=bad-intro]").TextContent);

        intro.Should().Contain("No sign-in is attempted");
        intro.Should().Contain("nothing about your browser or its security settings is changed");
        intro.Should().Contain("dedicated BirkNext profile");
        intro.Should().Contain("once with a visible window and once without");
    }

    // ── §13. The report handed to IT ─────────────────────────────────────────────────────────────────────────────

    // Separate sections per mode, then the conclusion that reads them together. An IT reader told only "automation
    // is restricted" will ask "in which mode?".
    [Fact]
    public void TheCopiedReportHasASectionPerModeAndThenTheOverallInterpretation()
    {
        var text = BrowserAutomationDiagnosticReportText.Build(HeadlessOnlyRestricted());

        text.Should().Contain("HEADED MODE");
        text.Should().Contain("HEADLESS MODE");
        text.Should().Contain("OVERALL INTERPRETATION");

        // Order matters: each mode's evidence before the conclusion drawn from both.
        text.IndexOf("HEADED MODE", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("HEADLESS MODE", StringComparison.Ordinal));
        text.IndexOf("HEADLESS MODE", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("OVERALL INTERPRETATION", StringComparison.Ordinal));
    }

    // 31, 42. The copied report carries what IT needs, per mode, and nothing else.
    [Fact]
    public void TheCopiedReportCarriesTheEvidenceAndNoSecrets()
    {
        var text = BrowserAutomationDiagnosticReportText.Build(HeadlessOnlyRestricted());

        text.Should().Contain("BROWSER AUTOMATION DIAGNOSTIC");
        text.Should().Contain("M2LB DEV").And.Contain("https://m2lbdev.example.test/");
        text.Should().Contain("Control URL: https://example.com/");
        text.Should().Contain("Edge: 153.0.3456.78").And.Contain("Playwright: 1.49.0.0");
        // The stage lines, which are the whole point of handing this over.
        text.Should().Contain("Control page: PASS").And.Contain("Initial navigation: BLOCKED");
        text.Should().Contain("Observed exception: TargetClosedException");
        // Each mode states its own verdict, so neither can be read off the other.
        text.Should().Contain("Result: Target application controllable").And.Contain("Result: Restricted for this target");
        // 7. The question everyone asks first, answered without being asked.
        text.Should().Contain("No login, MFA, credential or token was attempted or captured");

        // 32, 42. Nothing sensitive, and no path with somebody's user name in it.
        text.Should().NotContainAny("password", "Bearer ", "Cookie:", "token=");
        text.Should().NotContain(@"C:\Users\");
        text.Should().Contain("%LOCALAPPDATA%", "the profile is described, not located");
    }

    // The report carries the prerequisite too, because the person reading it is the one who has to decide what can
    // be tested next.
    [Fact]
    public void TheCopiedReportStatesWhatTheHeadlessResultMeansForAuthenticationTesting()
    {
        BrowserAutomationDiagnosticReportText.Build(HeadlessOnlyRestricted())
            .Should().Contain("HEADLESS AUTHENTICATION DIAGNOSTIC")
            .And.Contain("Headed-only success is not sufficient");

        BrowserAutomationDiagnosticReportText.Build(AutomationAvailable())
            .Should().Contain("Headless automation stayed in control through the navigation to this target");
    }

    // §40. The IT report is evidence-rich, and for an Entra redirect it says what was and was not proven.
    [Fact]
    public void TheCopiedReportSpellsOutTheAuthenticationHandoff()
    {
        var text = BrowserAutomationDiagnosticReportText.Build(AvailableThroughAuthRedirect());

        text.Should().Contain("Requested target: https://m2lbdev.example.test/");
        text.Should().Contain("Final location: https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]");
        text.Should().Contain("Final host: login.microsoftonline.com");
        text.Should().Contain("Expected target origin: NO");
        text.Should().Contain("Authentication redirect: DETECTED");
        text.Should().Contain("Browser control after navigation: PASS");
        text.Should().Contain("Stability check: PASS");
        text.Should().Contain("Target application: NOT YET REACHED");
        text.Should().Contain("Navigation trace (sanitized):");
        text.Should().Contain("does NOT prove that the authenticated target application is controllable");
        text.Should().Contain("Corporate Edge visible DevTools policy is a separate control and is not inferred from this result");
        text.Should().Contain("INDEPENDENT CONTROLS").And.Contain("CDP attach to an existing or protected Edge: Not tested");
        text.Should().Contain("redirect to login.microsoftonline.com");

        // §36. Never the false PASS, never a DevTools claim, never an unevidenced policy claim.
        text.Should().NotContain("Target application: PASS");
        text.Should().NotContainAny("DevTools enabled", "DevTools is enabled", "Security policy blocked target");
        text.Should().NotContainAny("state=", "nonce=", "login_hint", "code=");
    }

    // A mode that never ran says so, rather than being absent and read as a pass.
    [Fact]
    public void AModeThatDidNotRunSaysSoInBothTheCardAndTheReport()
    {
        var partial = Report(
            BrowserAutomationDiagnosticComparison.Cancelled, "Diagnostic cancelled", "The diagnostic was cancelled.",
            RestrictedMode(BrowserAutomationDiagnosticMode.Headed));

        var card = Ran(partial);
        var headless = ModePanel(card, "Headless");
        headless.GetAttribute("data-mode-result").Should().Be("NotRun");
        headless.QuerySelector("[data-testid=bad-mode-empty]")!.TextContent.Should().Contain("not run");

        BrowserAutomationDiagnosticReportText.Build(partial).Should().Contain("HEADLESS MODE")
            .And.Contain("Not run.");
    }

    // 31. The copy action exists and reports that it copied.
    [Fact]
    public void TheReportCanBeCopied()
    {
        var card = Ran(RestrictedInBothModes());

        card.Find("[data-testid=bad-copy]").TextContent.Trim().Should().Be("Copy diagnostic report");
        card.Find("[data-testid=bad-copy]").Click();

        card.WaitForAssertion(() => card.Find("[data-testid=bad-copy]").TextContent.Trim().Should().Be("Report copied"));
    }

    // A blocked run explains itself rather than looking like a crash.
    [Fact]
    public void ABlockedRunShowsItsReason()
    {
        var card = Ran(Report(
            BrowserAutomationDiagnosticComparison.Blocked, "Diagnostic not run",
            "The diagnostic did not run.") with
        {
            BlockedReason = "The browser automation diagnostic does not run against Production.",
        });

        card.Find("[data-testid=bad-blocked-reason]").TextContent
            .Should().Contain("does not run against Production");
    }

    // The request the card sends is the selected environment's canonical values — never a hard-coded host.
    [Fact]
    public void TheRequestCarriesTheSelectedTargetEnvironment()
    {
        Returns(RestrictedInBothModes());
        var card = Card(Dev, Qa);

        card.Find("[data-testid=bad-target-select]").Change("qa");
        card.Find("[data-testid=bad-run]").Click();

        card.WaitForAssertion(() => _api.Verify(a => a.RunAsync(
            It.Is<BrowserAutomationDiagnosticRequest>(r =>
                r.TargetEnvironmentId == "qa" &&
                r.TargetEnvironmentName == "M2LB QA" &&
                r.TargetUrl == "https://m2lbqa.example.test/" &&
                r.EnvironmentType == "QA" &&
                r.Authority == "https://login.microsoftonline.com/tenant-qa/v2.0"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    // The M2LB popup shape: the handoff happened in a second page, and a session-control hop was observed. Both are
    // shown as observations; neither is shown as a cause.
    [Fact]
    public void APopupHandoffAndASessionControlHopAreShownAsObservations()
    {
        var popup = AvailableMode(BrowserAutomationDiagnosticMode.Headless) with
        {
            Target = AvailableMode(BrowserAutomationDiagnosticMode.Headless).Target! with
            {
                FinalUrl = "https://m2lbdev.example.test/authentication/login?[redacted]",
                ApplicationMarkerConfigured = false, ApplicationMarkerFound = null,
                AuthenticationRedirect = BrowserAutomationEvidenceAnswer.Yes, AuthenticationHost = "login.microsoftonline.com",
                AuthenticationInSecondaryPage = true, SessionControlHost = "m2lbdev-example-test.access.mcas.ms",
                NavigationTrace =
                [
                    new(1, 200, "https://m2lbdev.example.test/", BrowserAutomationFinalLocation.TargetOrigin),
                    new(2, 1200, "https://m2lbdev.example.test/authentication/login?[redacted]", BrowserAutomationFinalLocation.TargetOrigin),
                    new(3, 1800, "https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]", BrowserAutomationFinalLocation.AuthenticationAuthority, SecondaryPage: true),
                ],
            },
        };
        var card = Ran(Report(BrowserAutomationDiagnosticComparison.AutomationAvailable, "Browser automation available",
            AvailableInterpretation, AvailableMode(BrowserAutomationDiagnosticMode.Headed), popup));

        var headless = ModePanel(card, "Headless");
        Fact(headless, "auth-redirect").Should().Be("DETECTED (second page: login.microsoftonline.com)");
        Fact(headless, "session-control").Should().Be("OBSERVED (m2lbdev-example-test.access.mcas.ms)");
        Fact(headless, "authenticated-application").Should().StartWith("NOT ASSESSED");
        headless.QuerySelectorAll("[data-testid=bad-trace-step]").Last().TextContent.Should().Contain("second page");

        var text = BrowserAutomationDiagnosticReportText.Build(Report(BrowserAutomationDiagnosticComparison.AutomationAvailable,
            "Browser automation available", AvailableInterpretation, AvailableMode(BrowserAutomationDiagnosticMode.Headed), popup));
        text.Should().Contain("Session-control proxy: OBSERVED (m2lbdev-example-test.access.mcas.ms)")
            .And.Contain("Authenticated application: NOT ASSESSED")
            .And.Contain("authentication authority, second page");
    }

    // §36, §52. When the prerequisite is met, the card offers a way to the authentication diagnostic — a link, not a
    // second run button. The diagnostic still runs from its own card only.
    [Fact]
    public void WhenThePrerequisiteIsMetTheCardLinksToTheAuthenticationDiagnostic_WithoutRunningIt()
    {
        var card = Ran(AvailableThroughAuthRedirect());

        var link = card.Find("[data-testid=bad-go-to-auth-diagnostic]");
        link.TagName.Should().Be("A");
        link.TextContent.Trim().Should().Be("Go to Headless Authentication Diagnostic");
        link.GetAttribute("href").Should().Be(
            "admin/system-settings?section=target-environments&tab=auth&open=headless-auth&profile=dev");
        card.FindAll("[data-testid=had-run]").Should().BeEmpty("the run button lives on the authentication card only");
        card.FindAll("button").Select(b => b.TextContent).Should().NotContain(t => t.Contains("headless authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenThePrerequisiteIsNotMetThereIsNoLink()
    {
        var card = Ran(RestrictedInBothModes());

        card.FindAll("[data-testid=bad-go-to-auth-diagnostic]").Should().BeEmpty();
    }

    // §38. Each target fact states where it comes from.
    [Fact]
    public void EveryTargetFactStatesItsProvenance()
    {
        var headless = ModePanel(Ran(AvailableThroughAuthRedirect()), "Headless");

        string Provenance(string key) => headless.QuerySelectorAll("[data-testid=bad-fact]")
            .Single(f => f.GetAttribute("data-fact") == key).QuerySelector("[data-testid=bad-fact-provenance]")!.TextContent.Trim();

        Provenance("requested").Should().Be("Configured");
        Provenance("final-location").Should().Be("Observed");
        Provenance("auth-redirect").Should().Be("Observed");
        Provenance("expected-origin").Should().Be("Derived");
        Provenance("application").Should().Be("Derived");
        Provenance("authenticated-application").Should().Be("Unknown");
        BrowserAutomationDiagnosticReportText.Build(AvailableThroughAuthRedirect())
            .Should().Contain("Final location: https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted] [Observed]");
    }

    // §15. A cleanup warning is shown as a warning, beside a finding it does not replace.
    [Fact]
    public void ACleanupWarningIsShownAsAWarning()
    {
        var mode = RestrictedMode(BrowserAutomationDiagnosticMode.Headless);
        mode = mode with
        {
            Stages = [.. mode.Stages.Select(s => s.Stage == BrowserAutomationDiagnosticStage.Cleanup
                ? Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Warning,
                    "Cleanup warning: closing the diagnostic browser reported an error. The result above is unaffected.", exceptionType: "PlaywrightException")
                : s)],
        };
        var card = Ran(Report(BrowserAutomationDiagnosticComparison.MixedOrInconclusive, "Inconclusive", "…",
            RestrictedMode(BrowserAutomationDiagnosticMode.Headed), mode));

        var cleanup = ModePanel(card, "Headless").QuerySelectorAll("[data-testid=bad-stage]").Single(s => s.GetAttribute("data-stage") == "Cleanup");
        cleanup.QuerySelector("[data-testid=bad-stage-state]")!.TextContent.Trim().Should().Be("WARNING");
        ModePanel(card, "Headless").GetAttribute("data-mode-result").Should().Be("TargetRestricted");
    }

    // ── Target navigation failure evidence ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The QA run's shape: control page fine, the target navigation itself failed. The failure evidence is what the
    /// backend classifier sends — type and code observed, category and interpretation derived — and nothing else.
    /// </summary>
    private static BrowserAutomationDiagnosticModeReport NavigationFailedMode(
        BrowserAutomationDiagnosticMode mode, string? code = "net::ERR_NAME_NOT_RESOLVED",
        BrowserNavigationFailureCategory category = BrowserNavigationFailureCategory.Dns, string label = "DNS",
        string interpretation = "The browser could not resolve the target hostname.", string exceptionType = "PlaywrightException") => new()
    {
        Mode = mode,
        Headless = mode == BrowserAutomationDiagnosticMode.Headless,
        Result = BrowserAutomationDiagnosticModeResult.Failed,
        EdgeVersion = "153.0.3456.78",
        ProfileDescription = string.Format(ProfileNote, mode),
        ObservedExceptionType = exceptionType,
        Stages =
        [
            .. SetupStagesPassed(),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Failed,
                "Navigation to the target application failed before automation control could be tested.",
                "https://m2lbdev.example.test/", exceptionType),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.TargetApplication, BrowserAutomationDiagnosticStageState.NotRun),
            Cleanup(),
        ],
        Target = new()
        {
            RequestedUrl = "https://m2lbdev.example.test/", ExpectedOrigin = "https://m2lbdev.example.test",
            ExceptionType = exceptionType, FailurePhase = BrowserAutomationFailurePhase.DuringTargetNavigation,
            NavigationFailure = new()
            {
                ExceptionType = exceptionType, BrowserErrorCode = code, Category = category, CategoryLabel = label,
                Interpretation = interpretation, NavigationTimeoutMs = 25000,
            },
        },
    };

    private static BrowserAutomationDiagnosticReport NavigationFailedInBothModes(
        Func<BrowserAutomationDiagnosticMode, BrowserAutomationDiagnosticModeReport>? mode = null)
    {
        mode ??= m => NavigationFailedMode(m);
        return Report(
            BrowserAutomationDiagnosticComparison.MixedOrInconclusive, "Inconclusive",
            "The two modes did not produce comparable evidence. In both modes, target navigation failed before browser "
            + "control could be established: the browser reported net::ERR_NAME_NOT_RESOLVED (DNS). Authentication was "
            + "not reached, so MFA, Conditional Access and session control were not assessed.",
            mode(BrowserAutomationDiagnosticMode.Headed), mode(BrowserAutomationDiagnosticMode.Headless));
    }

    private static string Provenance(IElement panel, string key) => panel
        .QuerySelectorAll("[data-testid=bad-fact]").Single(f => f.GetAttribute("data-fact") == key)
        .QuerySelector("[data-testid=bad-fact-provenance]")!.TextContent.Trim();

    private static bool HasFact(IElement panel, string key) =>
        panel.QuerySelectorAll("[data-testid=bad-fact]").Any(f => f.GetAttribute("data-fact") == key);

    // §14 / §13 / §38 Both mode cards show the browser error, its category and interpretation, with provenance.
    [Fact]
    public void ANavigationFailureShowsTheBrowserErrorCategoryAndInterpretation_InBothModes()
    {
        var card = Ran(NavigationFailedInBothModes());

        foreach (var mode in new[] { "Headed", "Headless" })
        {
            var panel = ModePanel(card, mode);
            Fact(panel, "navigation").Should().Be("FAIL");
            Fact(panel, "final-location").Should().Be("Not observed");
            Fact(panel, "final-host").Should().Be("Not observed");
            Fact(panel, "expected-origin").Should().Be("UNKNOWN");
            Fact(panel, "auth-redirect").Should().Be("UNKNOWN");
            Fact(panel, "session-control").Should().Be("NOT OBSERVED");
            Fact(panel, "control").Should().Be("Not run");
            Fact(panel, "stability").Should().Be("Not run");
            Fact(panel, "application").Should().Be("Not run");
            Fact(panel, "authenticated-application").Should().StartWith("NOT ASSESSED");

            Fact(panel, "exception").Should().Be("PlaywrightException (during target navigation)");
            Provenance(panel, "exception").Should().Be("Observed");
            Fact(panel, "browser-error").Should().Be("net::ERR_NAME_NOT_RESOLVED");
            Provenance(panel, "browser-error").Should().Be("Observed");
            Fact(panel, "failure-category").Should().Be("DNS");
            Provenance(panel, "failure-category").Should().Be("Derived");
            Fact(panel, "failure-interpretation").Should().Be("The browser could not resolve the target hostname.");
            Provenance(panel, "failure-interpretation").Should().Be("Derived");
            // §25 Authentication is said to be not reached — never shown as assessed, never a bare "UNKNOWN".
            Fact(panel, "authentication-assessment").Should().Be("NOT REACHED — MFA, Conditional Access and session control not assessed");
        }
    }

    // §38 The browser-error rows exist only when there is navigation-failure evidence.
    [Fact]
    public void NoNavigationFailure_NoBrowserErrorRows()
    {
        var card = Ran(AvailableThroughAuthRedirect());
        var keys = new[] { "browser-error", "failure-category", "failure-interpretation", "authentication-assessment" };
        foreach (var mode in new[] { "Headed", "Headless" })
            keys.Should().NotContain(k => HasFact(ModePanel(card, mode), k));

        foreach (var mode in RestrictedInBothModes().Modes)
            BrowserAutomationTargetFacts.For(mode).Select(f => f.Key).Should().NotIntersectWith(keys);
    }

    // §33 / §9 No code: "Not available", Unknown — never invented, never filled from a message.
    [Fact]
    public void ANavigationFailureWithoutACode_SaysNotAvailable()
    {
        var card = Ran(NavigationFailedInBothModes(m => NavigationFailedMode(m, code: null, category: BrowserNavigationFailureCategory.Unknown,
            label: "Unknown", interpretation: "Target navigation failed before browser control could be established. The browser did not expose a recognised network/navigation error code.")));

        var panel = ModePanel(card, "Headless");
        Fact(panel, "browser-error").Should().Be("Not available");
        Provenance(panel, "browser-error").Should().Be("Unknown");
        Fact(panel, "failure-category").Should().Be("Unknown");
        card.Find("[data-testid=bad-prerequisite-explanation]").TextContent.Should().Contain("Observed browser error: not available (Unknown).");
    }

    // §24 / §37 The prerequisite stays blocked and names the first blocker safely.
    [Fact]
    public void AHeadlessNavigationFailure_BlocksTheAuthDiagnostic_AndNamesTheBrowserError()
    {
        var card = Ran(NavigationFailedInBothModes());

        card.Find("[data-testid=bad-prerequisite-headline]").TextContent.Trim().Should().Be("Headless authentication diagnostic cannot run yet");
        Normalise(card.Find("[data-testid=bad-prerequisite-explanation]").TextContent).Should().Be(
            "Headless browser navigation failed before authentication could be observed. Observed browser error: "
            + "net::ERR_NAME_NOT_RESOLVED (DNS). MFA, Conditional Access and session control were not assessed.");
    }

    // A close during navigation keeps the restriction wording; it is not relabelled as a navigation error.
    [Fact]
    public void ATargetCloseDuringNavigation_KeepsTheRestrictionExplanation()
    {
        var closed = RestrictedMode(BrowserAutomationDiagnosticMode.Headless) with
        {
            Target = RestrictedMode(BrowserAutomationDiagnosticMode.Headless).Target! with
            {
                NavigationFailure = new()
                {
                    ExceptionType = "TargetClosedException", Category = BrowserNavigationFailureCategory.TargetClosed,
                    CategoryLabel = "Target closed", Interpretation = "The browser page or context closed while the target navigation was in progress.",
                },
            },
        };
        var report = RestrictedInBothModes() with { Modes = [RestrictedMode(BrowserAutomationDiagnosticMode.Headed), closed] };

        BrowserAutomationDiagnosticPrerequisite.Explanation(report).Should().NotContain("navigation failed before authentication")
            .And.Contain("did not keep control through the target navigation");
        BrowserAutomationTargetFacts.For(closed).Single(f => f.Key == "failure-category").Value.Should().Be("Target closed");
        BrowserAutomationTargetFacts.For(closed).Single(f => f.Key == "browser-error").Value.Should().Be("Not available");
    }

    // §16 / §39 The IT report carries the safe evidence, says authentication was not reached, and nothing raw.
    [Fact]
    public void TheCopiedReportCarriesTheNavigationFailureEvidence()
    {
        var text = BrowserAutomationDiagnosticReportText.Build(NavigationFailedInBothModes());

        var headless = text[text.IndexOf("HEADLESS MODE", StringComparison.Ordinal)..];
        headless.Should().Contain("Requested target: https://m2lbdev.example.test/ [Configured]")
            .And.Contain("Initial navigation: FAIL [Observed]")
            .And.Contain("Exception: PlaywrightException (during target navigation) [Observed]")
            .And.Contain("Browser error: net::ERR_NAME_NOT_RESOLVED [Observed]")
            .And.Contain("Failure category: DNS [Derived]")
            .And.Contain("Interpretation: The browser could not resolve the target hostname. [Derived]")
            .And.Contain("Final location: Not observed [Unknown]")
            .And.Contain("Browser control after navigation: Not run")
            .And.Contain("Authentication assessment: NOT REACHED — MFA, Conditional Access and session control not assessed [Derived]")
            .And.Contain("Technical details: stage Initial navigation; exception type PlaywrightException; browser error net::ERR_NAME_NOT_RESOLVED; navigation timeout 25000 ms");
        text.Should().Contain("HEADLESS AUTHENTICATION DIAGNOSTIC\n".Replace("\n", Environment.NewLine)
            + "Headless browser navigation failed before authentication could be observed. Observed browser error: net::ERR_NAME_NOT_RESOLVED (DNS).");

        text.Should().NotContainAny("page.goto", "Call log", "navigating to", "?code=", "state=", "access_token", "Cookie:", "   at ");
    }
}
