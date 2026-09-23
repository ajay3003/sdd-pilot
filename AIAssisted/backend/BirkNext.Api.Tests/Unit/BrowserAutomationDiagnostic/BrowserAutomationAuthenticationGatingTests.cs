using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.HeadlessAuthDiagnostic;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// The one prerequisite the Headless Authentication &amp; Session Control Diagnostic is built on.
///
/// The rule that matters, and the reason both browser modes are run at all: authentication may only be assessed once
/// HEADLESS target control has been demonstrated. A target that can be automated with a window open on somebody's
/// desk, but not without one, cannot be authenticated against by unattended CI — so headed success must never open
/// that gate.
///
/// These tests drive the real evidence store with real browser diagnostic reports. There is exactly one source of
/// truth for the fact (<see cref="BrowserAutomationDiagnosticReport.HeadlessTargetControlAvailable"/>) and exactly one
/// consumer, and the dependency runs one way: browser capability is established, then consumed.
/// </summary>
public sealed class BrowserAutomationAuthenticationGatingTests
{
    private const string TargetUrl = "https://m2lbdev.example.test/";

    private static BrowserAutomationDiagnosticModeReport Mode(
        BrowserAutomationDiagnosticMode mode, bool targetControlled, bool controlPagePassed = true) => new()
        {
            Mode = mode,
            Headless = mode == BrowserAutomationDiagnosticMode.Headless,
            Result = targetControlled
                ? BrowserAutomationDiagnosticModeResult.Available
                : BrowserAutomationDiagnosticModeResult.TargetRestricted,
            Stages =
            [
                new(BrowserAutomationDiagnosticStage.ControlPage,
                    controlPagePassed ? BrowserAutomationDiagnosticStageState.Passed : BrowserAutomationDiagnosticStageState.Failed),
                new(BrowserAutomationDiagnosticStage.TargetControl,
                    targetControlled ? BrowserAutomationDiagnosticStageState.Passed : BrowserAutomationDiagnosticStageState.Blocked),
            ],
        };

    private static BrowserAutomationDiagnosticReport Report(
        bool headedControlled, bool headlessControlled,
        string environmentId = "dev", string url = TargetUrl, string environmentType = "Development") => new()
        {
            DiagnosticId = Guid.NewGuid().ToString("N")[..12],
            TargetEnvironmentId = environmentId,
            TargetUrl = url,
            TargetEnvironmentType = environmentType,
            Modes =
            [
                Mode(BrowserAutomationDiagnosticMode.Headed, headedControlled),
                Mode(BrowserAutomationDiagnosticMode.Headless, headlessControlled),
            ],
        };

    private static HeadlessDiagnosticRequest AuthRequest(
        string environmentId = "dev", string url = TargetUrl, string environmentType = "Development") => new()
        {
            TargetEnvironmentId = environmentId, TargetUrl = url, EnvironmentType = environmentType,
            TargetEnvironmentName = "M2LB DEV",
        };

    // 18. Headless target control demonstrated: the authentication diagnostic may run.
    [Fact]
    public void HeadlessTargetControlAvailable_SatisfiesThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(headedControlled: true, headlessControlled: true);

        report.HeadlessTargetControlAvailable.Should().BeTrue();
        store.Record(report);

        var prerequisite = store.Check(AuthRequest());
        prerequisite.Available.Should().BeTrue();
        prerequisite.DiagnosticId.Should().Be(report.DiagnosticId, "the prerequisite names the run that established it");
    }

    // 19. Headless target control blocked: the authentication diagnostic cannot assess MFA or Conditional Access.
    [Fact]
    public void HeadlessTargetControlBlocked_BlocksThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(headedControlled: false, headlessControlled: false);

        report.HeadlessTargetControlAvailable.Should().BeFalse();
        store.Record(report);

        store.Check(AuthRequest()).Available.Should().BeFalse();
    }

    // 20, 28. THE case both modes exist for. Headed works, headless does not — and the gate stays shut, because
    // unattended CI has no visible window and "it worked when I watched it" is not an answer for CI.
    [Fact]
    public void HeadedAvailableButHeadlessBlocked_StillBlocksThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(headedControlled: true, headlessControlled: false);

        report.Headed!.TargetControlAvailable.Should().BeTrue();
        report.Headless!.TargetControlAvailable.Should().BeFalse();
        report.HeadlessTargetControlAvailable.Should().BeFalse("headed success says nothing about unattended execution");
        store.Record(report);

        var prerequisite = store.Check(AuthRequest());
        prerequisite.Available.Should().BeFalse();
        prerequisite.DiagnosticId.Should().BeNull("an unmet prerequisite cites no qualifying run");

        // And the sentence the reader sees names the trap, because a headed pass they just watched succeed otherwise
        // reads as a contradiction of "not available".
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(report)
            .Should().Contain("headed-only success is not sufficient");
    }

    // 21. The mirror: headless works even though headed does not. Headless is the only thing the gate asks about.
    [Fact]
    public void HeadlessAvailableButHeadedBlocked_SatisfiesThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(headedControlled: false, headlessControlled: true);

        store.Record(report);

        store.Check(AuthRequest()).Available.Should().BeTrue(
            "the authentication diagnostic runs headless, so headless is the mode that qualifies it");
    }

    // 22. A result for a different target, URL or environment type never qualifies another one.
    [Theory]
    [InlineData("other", TargetUrl, "Development")]
    [InlineData("dev", "https://m2lbqa.example.test/", "Development")]
    [InlineData("dev", TargetUrl, "QA")]
    public void AResultFromADifferentTargetIsNotAccepted(string environmentId, string url, string environmentType)
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(headedControlled: true, headlessControlled: true));

        store.Check(AuthRequest(environmentId, url, environmentType)).Available.Should().BeFalse();
    }

    // 15 (stale results). The target URL changing is exactly the case a cached pass must not survive: the evidence
    // was about a different application.
    [Fact]
    public void ChangingTheTargetUrlInvalidatesAnEarlierPass()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(headedControlled: true, headlessControlled: true));
        store.Check(AuthRequest()).Available.Should().BeTrue();

        store.Check(AuthRequest(url: "https://m2lbdev-new.example.test/")).Available.Should().BeFalse();
    }

    // A later blocked run replaces an earlier passing one; the gate reflects the most recent evidence.
    [Fact]
    public void ALaterBlockedRunClosesAGateAnEarlierRunOpened()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(headedControlled: true, headlessControlled: true));
        store.Check(AuthRequest()).Available.Should().BeTrue();

        store.Record(Report(headedControlled: true, headlessControlled: false));

        store.Check(AuthRequest()).Available.Should().BeFalse();
    }

    // Never run at all: the gate is shut, and the reason says what to do rather than what went wrong.
    [Fact]
    public void WithNoBrowserDiagnosticAtAllThePrerequisiteIsUnmet()
    {
        var prerequisite = new BrowserAutomationEvidenceStore().Check(AuthRequest());

        prerequisite.Available.Should().BeFalse();
        prerequisite.Reason.Should().Contain("Run Browser Automation Diagnostic");
        prerequisite.DiagnosticId.Should().BeNull();
    }

    // The prerequisite sentence the UI shows comes from the same single fact, so the two can never disagree.
    [Fact]
    public void ThePrerequisiteSentenceFollowsTheHeadlessFact()
    {
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(Report(true, true))
            .Should().Contain("can run");
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(Report(true, false))
            .Should().Contain("cannot yet assess MFA, Conditional Access or session control");
    }
}
