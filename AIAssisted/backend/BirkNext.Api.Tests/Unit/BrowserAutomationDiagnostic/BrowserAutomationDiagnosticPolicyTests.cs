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
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(environmentType), Profile, isLocalWorkstation: true)
            .Should().BeNull();
    }

    // 2. Production is refused. Driving a browser at production is not a diagnostic decision to make implicitly.
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData(" Production ")]
    public void ProductionIsRejected(string environmentType)
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(environmentType), Profile, isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.ProductionBlockedReason);
        BrowserAutomationDiagnosticPolicy.IsEligibleEnvironmentType(environmentType).Should().BeFalse();
    }

    // 3. No target, or one that is not navigable, is refused with the reason that fits.
    [Fact]
    public void AMissingOrUnusableTargetIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: ""), Profile, true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NoTargetBlockedReason);
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: "not-a-url"), Profile, true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.InvalidTargetBlockedReason);
        // A scheme the diagnostic cannot navigate is not silently accepted either.
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(url: "file:///C:/app/index.html"), Profile, true)
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
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), profileDirectory, isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NormalProfileBlockedReason);
    }

    [Fact]
    public void ARelativeProfilePathIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), @"profiles\diagnostic", isLocalWorkstation: true)
            .Should().Be(BrowserAutomationDiagnosticPolicy.NormalProfileBlockedReason);
    }

    // The default profile is BirkNext's own, and is never one of the profiles above.
    [Fact]
    public void TheDefaultProfileIsADedicatedBirkNextDirectory()
    {
        var path = BrowserAutomationDiagnosticPolicy.DefaultProfileDirectory();

        path.Should().Contain("BirkNext").And.Contain("BrowserAutomationDiagnosticEdgeProfile");
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), path, isLocalWorkstation: true).Should().BeNull();
        // Not the proxy's or the managed browser's profile either: a diagnostic that reused a signed-in profile would
        // be testing something else entirely.
        path.Should().NotContain("LocalHttpsProxyEdgeProfile").And.NotContain("ManagedEdgeProfile");
    }

    [Fact]
    public void ARemoteRuntimeIsRejected()
    {
        BrowserAutomationDiagnosticPolicy.BlockedReason(Request(), Profile, isLocalWorkstation: false)
            .Should().Be(BrowserAutomationDiagnosticPolicy.RemoteRuntimeBlockedReason);
    }

    // 19, 14. The tool reports behaviour. It never names an organisational control as the cause.
    [Fact]
    public void NoInterpretationClaimsAConfirmedCause()
    {
        foreach (var result in Enum.GetValues<BrowserAutomationDiagnosticResult>())
        {
            var text = BrowserAutomationDiagnosticPolicy.Interpretation(result);

            text.Should().NotContainAny(
                "Defender", "security policy confirmed", "policy confirmed", "DevTools disabled",
                "blocked by Defender", "confirmed");
        }

        // The one result that is about a restriction offers possibilities and says the tool cannot tell which.
        var restricted = BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticResult.TargetRestricted);
        restricted.Should().ContainEquivalentOf("possible cause");
        restricted.Should().Contain("cannot determine which control is responsible");
        restricted.Should().Contain("No sign-in was attempted");
    }

    // 15. The success wording does not let anyone read it as "E2E is now configured".
    [Fact]
    public void SuccessDoesNotImplyAuthenticationOrE2eIsSolved()
    {
        var passed = BrowserAutomationDiagnosticPolicy.Interpretation(BrowserAutomationDiagnosticResult.Passed);

        passed.Should().Contain("does not mean").And.Contain("MFA").And.Contain("Critical E2E");
    }
}
