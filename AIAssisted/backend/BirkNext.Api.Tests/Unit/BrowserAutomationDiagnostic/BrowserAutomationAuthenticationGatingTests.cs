using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.HeadlessAuthDiagnostic;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// The one prerequisite the Headless Authentication &amp; Session Control Diagnostic is built on.
///
/// Its meaning, precisely: HEADLESS Playwright stayed in stable control of the browser through the navigation to the
/// target, and the browser ended on the target origin or at the target's authentication handoff. It deliberately does
/// NOT require the target application to have been identified — a fresh profile is sent to Microsoft Entra before it
/// can see the application, and that redirect is exactly what the authentication diagnostic exists to inspect.
///
/// These tests drive the real evidence store with real browser diagnostic reports. There is exactly one source of
/// truth for the fact (<see cref="BrowserAutomationDiagnosticReport.HeadlessAutomationControlAfterTargetNavigation"/>)
/// and exactly one consumer, and the dependency runs one way: browser capability is established, then consumed.
/// </summary>
public sealed class BrowserAutomationAuthenticationGatingTests
{
    private const string TargetUrl = "https://m2lbdev.example.test/";

    /// <summary>How one mode's target observation ended.</summary>
    public enum Outcome { AuthRedirectControlled, TargetIdentified, ClosedBeforeRedirect, ClosedLate, UnrelatedOrigin }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static BrowserAutomationDiagnosticModeReport Mode(BrowserAutomationDiagnosticMode mode, Outcome outcome)
    {
        var (result, control, stability, location, application) = outcome switch
        {
            Outcome.AuthRedirectControlled => (BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary,
                BrowserAutomationDiagnosticStageState.Passed, BrowserAutomationDiagnosticStageState.Passed,
                BrowserAutomationFinalLocation.AuthenticationAuthority, BrowserAutomationDiagnosticStageState.NotReached),
            Outcome.TargetIdentified => (BrowserAutomationDiagnosticModeResult.Available,
                BrowserAutomationDiagnosticStageState.Passed, BrowserAutomationDiagnosticStageState.Passed,
                BrowserAutomationFinalLocation.TargetOrigin, BrowserAutomationDiagnosticStageState.Passed),
            Outcome.ClosedBeforeRedirect => (BrowserAutomationDiagnosticModeResult.TargetRestricted,
                BrowserAutomationDiagnosticStageState.Blocked, BrowserAutomationDiagnosticStageState.NotRun,
                BrowserAutomationFinalLocation.TargetOrigin, BrowserAutomationDiagnosticStageState.NotRun),
            Outcome.ClosedLate => (BrowserAutomationDiagnosticModeResult.TargetRestricted,
                BrowserAutomationDiagnosticStageState.Passed, BrowserAutomationDiagnosticStageState.Blocked,
                BrowserAutomationFinalLocation.AuthenticationAuthority, BrowserAutomationDiagnosticStageState.NotRun),
            _ => (BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed,
                BrowserAutomationDiagnosticStageState.Passed, BrowserAutomationDiagnosticStageState.Passed,
                BrowserAutomationFinalLocation.OtherOrigin, BrowserAutomationDiagnosticStageState.NotReached),
        };

        return new()
        {
            Mode = mode,
            Headless = mode == BrowserAutomationDiagnosticMode.Headless,
            Result = result,
            Stages =
            [
                new(BrowserAutomationDiagnosticStage.ControlPage, BrowserAutomationDiagnosticStageState.Passed),
                new(BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStageState.Passed),
                new(BrowserAutomationDiagnosticStage.TargetControl, control),
                new(BrowserAutomationDiagnosticStage.TargetStability, stability),
                new(BrowserAutomationDiagnosticStage.TargetApplication, application),
            ],
            Target = new()
            {
                FinalLocation = location,
                FinalHost = location switch
                {
                    BrowserAutomationFinalLocation.AuthenticationAuthority => "login.microsoftonline.com",
                    BrowserAutomationFinalLocation.OtherOrigin => "elsewhere.example.test",
                    _ => "m2lbdev.example.test",
                },
                AuthenticationHost = location == BrowserAutomationFinalLocation.AuthenticationAuthority ? "login.microsoftonline.com" : null,
                TargetApplicationIdentified = outcome == Outcome.TargetIdentified
                    ? BrowserAutomationEvidenceAnswer.Yes : BrowserAutomationEvidenceAnswer.No,
            },
        };
    }

    private static BrowserAutomationDiagnosticReport Report(
        Outcome headed, Outcome headless,
        string environmentId = "dev", string url = TargetUrl, string environmentType = "Development") => new()
        {
            DiagnosticId = Guid.NewGuid().ToString("N")[..12],
            TargetEnvironmentId = environmentId,
            TargetUrl = url,
            TargetEnvironmentType = environmentType,
            Modes =
            [
                Mode(BrowserAutomationDiagnosticMode.Headed, headed),
                Mode(BrowserAutomationDiagnosticMode.Headless, headless),
            ],
        };

    private static HeadlessDiagnosticRequest AuthRequest(
        string environmentId = "dev", string url = TargetUrl, string environmentType = "Development") => new()
        {
            TargetEnvironmentId = environmentId, TargetUrl = url, EnvironmentType = environmentType,
            TargetEnvironmentName = "M2LB DEV",
        };

    // §35 A. The case M2LB actually produces for a fresh profile: headless reaches Entra and stays controllable. The
    // target application was NOT identified — and the prerequisite is still satisfied, because inspecting that
    // redirect is the authentication diagnostic's job.
    [Fact]
    public void HeadlessReachingEntraWithControlRetained_SatisfiesThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(Outcome.AuthRedirectControlled, Outcome.AuthRedirectControlled);

        report.Headless!.TargetApplicationIdentified.Should().BeFalse("the browser is on Entra, not on the target application");
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeTrue();
        store.Record(report);

        var prerequisite = store.Check(AuthRequest());
        prerequisite.Available.Should().BeTrue();
        prerequisite.DiagnosticId.Should().Be(report.DiagnosticId, "the prerequisite names the run that established it");
        prerequisite.Reason.Should().Contain("login.microsoftonline.com");
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(report)
            .Should().Contain("through the target's authentication redirect").And.Contain("can run");
    }

    // Target identified on its own origin also satisfies it.
    [Fact]
    public void HeadlessOnTheIdentifiedTarget_SatisfiesThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(Outcome.TargetIdentified, Outcome.TargetIdentified));

        store.Check(AuthRequest()).Available.Should().BeTrue();
    }

    // §35 B. The page closes before or at the redirect: nothing reached the authentication boundary under control.
    [Theory]
    [InlineData(Outcome.ClosedBeforeRedirect)]
    [InlineData(Outcome.ClosedLate)]
    public void HeadlessLosingThePage_BlocksThePrerequisite(Outcome headless)
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(Outcome.AuthRedirectControlled, headless);

        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
        store.Record(report);

        store.Check(AuthRequest()).Available.Should().BeFalse();
    }

    // §35 C. THE case both modes exist for. Headed works, headless does not — and the gate stays shut, because
    // unattended CI has no visible window and "it worked when I watched it" is not an answer for CI.
    [Fact]
    public void HeadedAvailableButHeadlessBlocked_StillBlocksThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(Outcome.AuthRedirectControlled, Outcome.ClosedBeforeRedirect);

        report.Headed!.ControlRetainedThroughTargetNavigation.Should().BeTrue();
        report.Headless!.ControlRetainedThroughTargetNavigation.Should().BeFalse();
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse("headed success says nothing about unattended execution");
        store.Record(report);

        var prerequisite = store.Check(AuthRequest());
        prerequisite.Available.Should().BeFalse();
        prerequisite.DiagnosticId.Should().BeNull("an unmet prerequisite cites no qualifying run");

        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(report)
            .Should().Contain("headed-only success is not sufficient");
    }

    // The mirror: headless works even though headed does not. Headless is the only thing the gate asks about.
    [Fact]
    public void HeadlessAvailableButHeadedBlocked_SatisfiesThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(Outcome.ClosedBeforeRedirect, Outcome.AuthRedirectControlled));

        store.Check(AuthRequest()).Available.Should().BeTrue(
            "the authentication diagnostic runs headless, so headless is the mode that qualifies it");
    }

    // Control survived, but along somebody else's path: the browser ended on an unrelated origin. That is not the
    // target's authentication handoff, so it cannot qualify the target's authentication diagnostic.
    [Fact]
    public void HeadlessControlOnAnUnrelatedOrigin_BlocksThePrerequisite()
    {
        var store = new BrowserAutomationEvidenceStore();
        var report = Report(Outcome.UnrelatedOrigin, Outcome.UnrelatedOrigin);

        report.Headless!.BrowserControlRetained.Should().BeTrue();
        report.HeadlessAutomationControlAfterTargetNavigation.Should().BeFalse();
        store.Record(report);

        store.Check(AuthRequest()).Available.Should().BeFalse();
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(report)
            .Should().Contain("elsewhere.example.test").And.Contain("neither the target origin nor a recognised authentication handoff");
    }

    // §35 D. A result for a different target, URL or environment type never qualifies another one.
    [Theory]
    [InlineData("other", TargetUrl, "Development")]
    [InlineData("dev", "https://m2lbqa.example.test/", "Development")]
    [InlineData("dev", TargetUrl, "QA")]
    public void AResultFromADifferentTargetIsNotAccepted(string environmentId, string url, string environmentType)
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(Outcome.AuthRedirectControlled, Outcome.AuthRedirectControlled));

        store.Check(AuthRequest(environmentId, url, environmentType)).Available.Should().BeFalse();
    }

    // §35 E. Stale evidence: a pass older than the validity window no longer opens the gate.
    [Fact]
    public void StaleEvidenceIsNotAccepted()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
        var store = new BrowserAutomationEvidenceStore(clock);
        store.Record(Report(Outcome.AuthRedirectControlled, Outcome.AuthRedirectControlled));
        store.Check(AuthRequest()).Available.Should().BeTrue();

        clock.Now += BrowserAutomationEvidenceStore.Validity + TimeSpan.FromSeconds(1);

        store.Check(AuthRequest()).Available.Should().BeFalse();
    }

    // The target URL changing is exactly the case a cached pass must not survive: the evidence was about a different
    // application.
    [Fact]
    public void ChangingTheTargetUrlInvalidatesAnEarlierPass()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(Outcome.AuthRedirectControlled, Outcome.AuthRedirectControlled));
        store.Check(AuthRequest()).Available.Should().BeTrue();

        store.Check(AuthRequest(url: "https://m2lbdev-new.example.test/")).Available.Should().BeFalse();
    }

    // A later blocked run replaces an earlier passing one; the gate reflects the most recent evidence.
    [Fact]
    public void ALaterBlockedRunClosesAGateAnEarlierRunOpened()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(Report(Outcome.AuthRedirectControlled, Outcome.AuthRedirectControlled));
        store.Check(AuthRequest()).Available.Should().BeTrue();

        store.Record(Report(Outcome.AuthRedirectControlled, Outcome.ClosedLate));

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
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(Report(Outcome.TargetIdentified, Outcome.TargetIdentified))
            .Should().Contain("remained controllable on the target origin").And.Contain("can run");
        BrowserAutomationDiagnosticPolicy.AuthenticationPrerequisite(Report(Outcome.TargetIdentified, Outcome.ClosedLate))
            .Should().Contain("cannot yet assess MFA, Conditional Access or session control");
    }
}
