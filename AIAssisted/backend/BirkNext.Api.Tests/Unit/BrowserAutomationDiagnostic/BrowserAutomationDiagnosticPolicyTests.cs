using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// What the diagnostic is allowed to do, decided before a browser exists.
///
/// The diagnostic drives a real browser at a real application, so every one of these refusals is a case where BirkNext
/// declines to start rather than one where it stops partway.
/// </summary>
public sealed class BrowserAutomationDiagnosticPolicyTests
{
    private const string Profile = @"C:\Users\someone\AppData\Local\BirkNext\BrowserAutomationDiagnosticEdgeProfile";

    private static BrowserAutomationDiagnosticRequest Request(
        string environmentType = "Development", string url = "https://m2lbdev.example.test/") => new()
        {
            TargetEnvironmentId = "dev", TargetEnvironmentName = "M2LB DEV",
            EnvironmentType = environmentType, TargetUrl = url,
        };

    // 1, 5. A non-production target with a real URL and a dedicated profile is the case the diagnostic exists for.
    [Theory]
    [InlineData("Development")]
    [InlineData("QA")]
    [InlineData("Local")]
    public void AnEligibleNonProductionTargetIsAccepted(string environmentType)
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(environmentType), [Profile], isLocalWorkstation: true)
            .Should().BeNull();
    }

    // 2. Production is refused. Driving a browser at production is not a diagnostic decision to make implicitly.
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData(" Production ")]
    public void ProductionIsRejected(string environmentType)
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(environmentType), [Profile], isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.ProductionBlockedReason);
        BrowserAutomationDiagnosticPolicy.IsEligibleEnvironmentType(environmentType).Should().BeFalse();
    }

    // 3. No target, or one that is not navigable, is refused with the reason that fits.
    [Fact]
    public void AMissingOrUnusableTargetIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: ""), [Profile], true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NoTargetBlockedReason);
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: "not-a-url"), [Profile], true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.InvalidTargetBlockedReason);
        // A scheme the diagnostic cannot navigate is not silently accepted either.
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: "file:///C:/app/index.html"), [Profile], true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.InvalidTargetBlockedReason);
    }

    // 4. The hard invariant: the diagnostic never runs in the employee's own Edge profile.
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge\User Data")]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge\User Data\Default")]
    [InlineData(@"C:/Users/someone/AppData/Local/Microsoft/Edge Beta/User Data")]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge Dev\User Data")]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Edge SxS\User Data")]
    public void ANormalEdgeProfileIsRejected(string profileDirectory)
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), [profileDirectory], isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NormalProfileBlockedReason);
    }

    [Fact]
    public void ARelativeProfilePathIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), [@"profiles\diagnostic"], isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NormalProfileBlockedReason);
    }

    // Each mode gets BirkNext's own profile, and neither is one of the profiles above.
    [Fact]
    public void EachModeHasItsOwnDedicatedBirkNextProfile()
    {
        var headed = BrowserAutomationDiagnosticPolicy.ProfileDirectory(BrowserAutomationDiagnosticMode.Headed);
        var headless = BrowserAutomationDiagnosticPolicy.ProfileDirectory(BrowserAutomationDiagnosticMode.Headless);

        headed.Should().Contain("BirkNext").And.Contain("BrowserAutomationDiagnostic").And.EndWith("Headed");
        headless.Should().EndWith("Headless");
        // Separate directories: two sequential Chromium launches sharing one user-data directory report a profile
        // lock rather than the target's behaviour.
        headed.Should().NotBe(headless);
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), [headed, headless], isLocalWorkstation: true).Should().BeNull();
        // Not the proxy's, the managed browser's or the auth diagnostic's profile either: a diagnostic that reused a
        // signed-in profile would be testing something else entirely.
        foreach (var path in new[] { headed, headless })
            path.Should().NotContainAny("LocalHttpsProxyEdgeProfile", "ManagedEdgeProfile", "HeadlessAuthDiagnostic");
    }

    [Fact]
    public void ARemoteRuntimeIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), [Profile], isLocalWorkstation: false)
            .Should().Be(BrowserAutomationDiagnosticPolicy.RemoteRuntimeBlockedReason);
    }

    // 19, 14. The tool reports behaviour. It never names an organisational control as the cause.
    [Fact]
    public void NoInterpretationClaimsAConfirmedCause()
    {
        foreach (var result in Enum.GetValues<BrowserAutomationDiagnosticComparison>())
        {
            var text = BrowserAutomationDiagnosticPolicy.Interpretation(result);

            text.Should().NotContainAny(
                "Defender", "security policy confirmed", "policy confirmed", "DevTools disabled",
                "blocked by Defender", "confirmed");
        }

        // The results that are about a restriction stop at what was observed, and explicitly refuse the one claim this
        // very run disproves: developer tooling was demonstrably usable on the control page.
        foreach (var restricted in new[]
        {
            BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes),
            BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted),
        })
        {
            restricted.Should().ContainEquivalentOf("consistent with");
            restricted.Should().Contain("No sign-in was attempted");
            restricted.Should().NotContainAny("DevTools", "Defender", "MCAS");
        }
        BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes)
            .Should().Contain("does not identify which organisational control is responsible");
    }

    // 15. The success wording does not let anyone read it as "E2E is now configured".
    [Fact]
    public void SuccessDoesNotImplyAuthenticationOrE2eIsSolved()
    {
        var passed = BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticComparison.AutomationAvailable);

        passed.Should().Contain("does not establish").And.Contain("MFA").And.Contain("Conditional Access");
        passed.Should().Contain("Headless Authentication & Session Control Diagnostic",
            "success here is a prerequisite for that diagnostic, not a substitute for it");
    }
}
