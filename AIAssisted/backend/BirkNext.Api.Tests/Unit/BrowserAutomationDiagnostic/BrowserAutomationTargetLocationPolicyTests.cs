using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// Where a URL is relative to the target, and what of it may be written down. Pure rules, tested exhaustively,
/// because "same origin" decided by string prefix and a query parameter that slips past a filter are exactly the
/// kind of mistake nobody sees in a green report.
/// </summary>
public sealed class BrowserAutomationTargetLocationPolicyTests
{
    private static readonly Uri Target = new("https://m2lbdev.bufetat.no");

    // §8. Canonical origin: scheme, host, EFFECTIVE port. Paths do not matter; prefixes do not count.
    [Theory]
    [InlineData("https://m2lbdev.bufetat.no", true)]
    [InlineData("https://m2lbdev.bufetat.no/admin", true)]
    [InlineData("https://M2LBDEV.bufetat.no:443/admin?x=1", true)]
    [InlineData("http://m2lbdev.bufetat.no/", false)]
    [InlineData("https://m2lbdev.bufetat.no:8443/", false)]
    [InlineData("https://m2lbdev.bufetat.no.evil.test/", false)]
    [InlineData("https://evil.test/m2lbdev.bufetat.no", false)]
    [InlineData("https://login.microsoftonline.com/", false)]
    public void TheExpectedOriginIsComparedByOriginNotByString(string candidate, bool expected) =>
        BrowserAutomationTargetLocationPolicy.IsExpectedOrigin(new Uri(candidate), Target).Should().Be(expected);

    [Fact]
    public void TheCanonicalOriginOmitsTheDefaultPortOnly()
    {
        BrowserAutomationTargetLocationPolicy.CanonicalOrigin("https://M2LBDEV.bufetat.no:443/x").Should().Be("https://m2lbdev.bufetat.no");
        BrowserAutomationTargetLocationPolicy.CanonicalOrigin("https://m2lbdev.bufetat.no:8443/x").Should().Be("https://m2lbdev.bufetat.no:8443");
        BrowserAutomationTargetLocationPolicy.CanonicalOrigin("about:blank").Should().BeNull();
    }

    // §9. Recognised authentication authorities: the Entra sign-in hosts, and the configured authority.
    [Theory]
    [InlineData("https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize", null, BrowserAutomationFinalLocation.AuthenticationAuthority)]
    [InlineData("https://login.microsoft.com/common/", null, BrowserAutomationFinalLocation.AuthenticationAuthority)]
    [InlineData("https://idp.example.test/connect/authorize", "https://idp.example.test/", BrowserAutomationFinalLocation.AuthenticationAuthority)]
    [InlineData("https://idp.example.test/connect/authorize", null, BrowserAutomationFinalLocation.OtherOrigin)]
    [InlineData("http://login.microsoftonline.com/", null, BrowserAutomationFinalLocation.OtherOrigin)]
    [InlineData("https://login.microsoftonline.com.evil.test/", null, BrowserAutomationFinalLocation.OtherOrigin)]
    [InlineData("https://m2lbdev.bufetat.no.mcas.ms/", null, BrowserAutomationFinalLocation.SessionControlProxy)]
    [InlineData("https://m2lbdev.bufetat.no/signin-oidc", null, BrowserAutomationFinalLocation.TargetOrigin)]
    [InlineData("about:blank", null, BrowserAutomationFinalLocation.OtherOrigin)]
    [InlineData("not a url", null, BrowserAutomationFinalLocation.Unknown)]
    public void LocationsAreClassified(string url, string? authority, BrowserAutomationFinalLocation expected) =>
        BrowserAutomationTargetLocationPolicy.Classify(url, Target, authority).Should().Be(expected);

    // §17, §34. Every sensitive parameter the task names is gone — because the whole query is, not because a filter
    // happened to know its name.
    [Theory]
    [InlineData("code")] [InlineData("state")] [InlineData("nonce")] [InlineData("access_token")] [InlineData("id_token")]
    [InlineData("refresh_token")] [InlineData("session_state")] [InlineData("client_assertion")] [InlineData("login_hint")]
    [InlineData("SAMLResponse")] [InlineData("RelayState")]
    public void NoSensitiveQueryParameterSurvivesSanitization(string parameter)
    {
        var raw = $"https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?{parameter}=SECRET-VALUE&other=1#{parameter}=FRAGMENT-SECRET";

        var sanitized = BrowserAutomationTargetLocationPolicy.Sanitize(raw, authority: true);

        sanitized.Should().NotContain(parameter).And.NotContain("SECRET").And.NotContain("other=");
        sanitized.Should().EndWith("?[redacted]#[redacted]");
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3/oauth2/v2.0/authorize?state=x", true,
        "https://login.microsoftonline.com/[tenant]/oauth2/v2.0/authorize?[redacted]")]
    [InlineData("https://m2lbdev.bufetat.no/", false, "https://m2lbdev.bufetat.no/")]
    [InlineData("https://m2lbdev.bufetat.no/personer/12345678901", false, "https://m2lbdev.bufetat.no/personer/[redacted]")]
    [InlineData("https://m2lbdev.bufetat.no/sak/3f2504e0-4f89-11d3-9a0c-0305e82c3301/detaljer", false, "https://m2lbdev.bufetat.no/sak/[id]/detaljer")]
    [InlineData("https://m2lbdev.bufetat.no/u/someone@bufdir.no", false, "https://m2lbdev.bufetat.no/u/[redacted]")]
    [InlineData("https://user:pass@m2lbdev.bufetat.no/", false, "https://m2lbdev.bufetat.no/")]
    [InlineData("https://m2lbdev.bufetat.no/#id_token=eyJ", false, "https://m2lbdev.bufetat.no/#[redacted]")]
    [InlineData("https://m2lbdev.bufetat.no/cb/eyJhbGciOiJSUzI1NiJ9abc123", false, "https://m2lbdev.bufetat.no/cb/[redacted]")]
    [InlineData("about:blank", false, "about:blank")]
    [InlineData("chrome-error://chromewebdata/", false, "[non-web URL]")]
    public void UrlsAreReducedToWhatIsSafeToShow(string raw, bool authority, string expected) =>
        BrowserAutomationTargetLocationPolicy.Sanitize(raw, authority).Should().Be(expected);

    // §14. The observation timing is a documented policy, not a magic number at a call site.
    [Fact]
    public void TheDefaultObservationTimingIsBounded()
    {
        var timing = BrowserAutomationTargetObservationTiming.Default;

        timing.QuietPeriod.Should().BePositive();
        timing.SettleBound.Should().BeGreaterThan(timing.QuietPeriod, "the bound has to leave room for the quiet period");
        timing.StabilityWindow.Should().BePositive();
        (timing.SettleBound * 2 + timing.StabilityWindow).Should().BeLessThan(TimeSpan.FromMinutes(1),
            "the whole target observation per mode is bounded well under a minute");
    }
}
