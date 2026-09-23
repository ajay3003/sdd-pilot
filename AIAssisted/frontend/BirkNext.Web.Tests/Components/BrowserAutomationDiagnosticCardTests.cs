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
    private static readonly FrontendAnalysisProfile Qa = Profile("qa", "M2LB QA", FrontendEnvironmentType.QA, "https://m2lbqa.example.test/");
    private static readonly FrontendAnalysisProfile Prod = Profile("prod", "M2LB PROD", FrontendEnvironmentType.Production, "https://m2lb.example.test/");

    private const string ProfileNote =
        "Dedicated BirkNext diagnostic Edge profile (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnostic\\{0}). "
        + "Never signed in, never the normal Edge profile.";

    private static BrowserAutomationDiagnosticStageResult Stage(
        BrowserAutomationDiagnosticStage stage, BrowserAutomationDiagnosticStageState state,
        string? detail = null, string? url = null, string? exceptionType = null) =>
        new(stage, state, detail, url, exceptionType);

    /// <summary>A mode that kept control of the target all the way through.</summary>
    private static BrowserAutomationDiagnosticModeReport AvailableMode(BrowserAutomationDiagnosticMode mode) => new()
    {
        Mode = mode,
        Headless = mode == BrowserAutomationDiagnosticMode.Headless,
        Result = BrowserAutomationDiagnosticModeResult.Available,
        EdgeVersion = "153.0.3456.78",
        ProfileDescription = string.Format(ProfileNote, mode),
        Stages =
        [
            Stage(BrowserAutomationDiagnosticStage.Runtime, BrowserAutomationDiagnosticStageState.Passed, "Microsoft Edge 153.0.3456.78 found."),
            Stage(BrowserAutomationDiagnosticStage.EdgeLaunch, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.PersistentContext, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.BlankPage, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.ControlPage, BrowserAutomationDiagnosticStageState.Passed, url: "https://example.com/"),
            Stage(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Passed, url: "https://m2lbdev.example.test/"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed),
            Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Passed, "Diagnostic browser closed."),
        ],
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
                "The Playwright-controlled page or context was closed while navigating to the target application.",
                "https://m2lbdev.example.test/", "TargetClosedException"),
            Stage(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.NotRun),
            Stage(BrowserAutomationDiagnosticStage.Cleanup, BrowserAutomationDiagnosticStageState.Passed, "Diagnostic browser closed."),
        ],
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

    private static BrowserAutomationDiagnosticReport AutomationAvailable() => Report(
        BrowserAutomationDiagnosticComparison.AutomationAvailable,
        "Browser automation available",
        // Verbatim from BrowserAutomationDiagnosticPolicy.Interpretation.
        "Playwright retained control of the target in both headed and headless Microsoft Edge. Browser automation "
        + "is not the blocker. This does not establish that authentication, MFA or Conditional Access work under "
        + "automation — that is assessed separately by the Headless Authentication & Session Control Diagnostic. "
        + "No sign-in was attempted.",
        AvailableMode(BrowserAutomationDiagnosticMode.Headed),
        AvailableMode(BrowserAutomationDiagnosticMode.Headless));

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
        headed.QuerySelector("[data-testid=bad-mode-result]")!.TextContent.Trim().Should().Be("Automation available");
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

    // 29, 30. Every stage is shown per mode, with its state as a WORD, and the control/target contrast intact.
    [Fact]
    public void EveryStageIsShownWithinItsOwnMode()
    {
        var card = Ran(HeadlessOnlyRestricted());

        foreach (var mode in new[] { "Headed", "Headless" })
        {
            var stages = ModePanel(card, mode).QuerySelectorAll("[data-testid=bad-stage]");
            stages.Select(s => s.GetAttribute("data-stage")).Should().Equal(
                "Runtime", "EdgeLaunch", "PersistentContext", "BlankPage", "ControlPage",
                "TargetNavigation", "TargetControl", "Cleanup");
        }

        // The contrast a reader needs, inside the mode where it happened: the control page passed, the target did not.
        var headless = ModePanel(card, "Headless");
        State(headless, "ControlPage").Should().Be("PASS");
        State(headless, "TargetNavigation").Should().Be("BLOCKED");
        State(headless, "TargetControl").Should().Be("Not run");

        // And the headed mode, which is the one that worked, is not contaminated by the headless result.
        State(ModePanel(card, "Headed"), "TargetControl").Should().Be("PASS");

        static string State(IElement panel, string stage) => panel
            .QuerySelectorAll("[data-testid=bad-stage]").Single(s => s.GetAttribute("data-stage") == stage)
            .QuerySelector("[data-testid=bad-stage-state]")!.TextContent.Trim();
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

        text.Should().Contain("Browser Automation Diagnostic");
        text.Should().Contain("M2LB DEV").And.Contain("https://m2lbdev.example.test/");
        text.Should().Contain("Control URL: https://example.com/");
        text.Should().Contain("Edge: 153.0.3456.78").And.Contain("Playwright: 1.49.0.0");
        // The stage lines, which are the whole point of handing this over.
        text.Should().Contain("Control page: PASS").And.Contain("Target navigation: BLOCKED");
        text.Should().Contain("Observed exception: TargetClosedException");
        // Each mode states its own verdict, so neither can be read off the other.
        text.Should().Contain("Result: Automation available").And.Contain("Result: Restricted for this target");
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
            .Should().Contain("Headless mode kept control of this target");
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
                r.EnvironmentType == "QA"),
            It.IsAny<CancellationToken>()), Times.Once));
    }
}
