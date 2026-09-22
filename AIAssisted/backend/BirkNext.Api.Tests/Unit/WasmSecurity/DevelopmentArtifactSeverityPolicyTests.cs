using BirkNext.Api.Services.WasmSecurity;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmSecurity;

/// <summary>
/// Severity for development artefacts follows the RISK MODEL, not the environment label.
///
/// The question this pass asked was whether "localhost in DEV" should automatically be High. The answer, after reading
/// what each rule actually asserts, is that the environment is the wrong axis — and that two rules which look alike are
/// not alike at all:
///
/// <list type="bullet">
/// <item><b>MSAL-LOCALHOST-REDIRECT stays High.</b> A localhost redirect URI on a deployed application lets an
/// authorization response be redirected to whatever is listening on the visitor's own machine. That is a token
/// redirection path, and the rule is already conditioned on the target NOT being local, so it never fires for a
/// genuinely local run.</item>
/// <item><b>ENDPOINT-LOCALHOST is Medium.</b> A localhost URL baked into a client asset resolves to the visitor's own
/// machine, so the call fails or reaches something unintended. It is a build-hygiene defect: no data is exposed and no
/// access is granted. It used to be High, which put it beside the redirect-URI case as though it carried the same
/// consequence.</item>
/// </list>
///
/// Neither severity is a discount for Development targets: both are the same on every environment, and both are what
/// the evidence supports. Nothing is lowered because a target happens to be called DEV.
/// </summary>
public sealed class DevelopmentArtifactSeverityPolicyTests
{
    private static WasmScanRequest Request(string url) => new() { TargetUrl = url };

    private static List<WasmSecurityFinding> EndpointFindings(string targetUrl, params string[] localhostUrls) =>
        BlazorWasmSecurityReviewService.CheckBackendEndpoints(
            localhostUrls.Select(url => new DiscoveredEndpoint
            {
                Url = url, Classification = "Localhost", FoundIn = "app.js",
            }).ToList(),
            Request(targetUrl)).ToList();

    // 23, 24, 74. The artefact rule is Medium, and it is Medium on every environment.
    [Theory]
    [InlineData("https://m2lbdev.example.test/")]
    [InlineData("https://m2lb.example.test/")]
    public void LocalhostInClientAssetsIsADevelopmentArtefact_NotAHighSecurityRisk(string targetUrl)
    {
        var findings = EndpointFindings(targetUrl, "http://localhost:5000/api/values");

        var localhost = findings.Should().ContainSingle(f => f.Id == "ENDPOINT-LOCALHOST").Subject;
        localhost.Severity.Should().Be(WasmSecuritySeverity.Medium,
            "a localhost URL in a deployed build breaks a call; it exposes nothing and grants no access");
        localhost.Category.Should().Be(WasmSecurityCategory.DevelopmentArtifact);
        localhost.Description.Should().Contain("resolve to the visitor's own machine");
        localhost.Description.Should().Contain("No data is exposed");
    }

    // 74. The policy is explicit rather than implicit: the same evidence produces the same severity either way.
    [Fact]
    public void TheSeverityDoesNotDependOnTheEnvironmentTheTargetIsCalled()
    {
        var dev = EndpointFindings("https://m2lbdev.example.test/", "http://localhost:5000/api/values");
        var prod = EndpointFindings("https://m2lb.example.test/", "http://localhost:5000/api/values");

        dev.Single(f => f.Id == "ENDPOINT-LOCALHOST").Severity
            .Should().Be(prod.Single(f => f.Id == "ENDPOINT-LOCALHOST").Severity);
    }

    // 24. The rule whose evidence DOES support High keeps it — this is not a blanket downgrade.
    [Fact]
    public void ARealTokenRedirectionPathKeepsItsHighSeverity()
    {
        var findings = BlazorWasmSecurityReviewService.CheckMsalConfig(
            """{"AzureAd":{"RedirectUri":"http://localhost:5001/authentication/login-callback"}}""",
            "appsettings.json", Request("https://m2lbdev.example.test/")).ToList();

        var redirect = findings.Should().ContainSingle(f => f.Id == "MSAL-LOCALHOST-REDIRECT").Subject;
        redirect.Severity.Should().Be(WasmSecuritySeverity.High,
            "a localhost redirect URI on a deployed app is a token redirection path");
        redirect.Category.Should().Be(WasmSecurityCategory.AuthenticationConfiguration);
    }

    // The redirect rule is conditioned on the deployment, so a genuinely local target never triggers it.
    [Fact]
    public void ALocalhostRedirectOnALocalTargetIsNotAFinding()
    {
        var findings = BlazorWasmSecurityReviewService.CheckMsalConfig(
            """{"AzureAd":{"RedirectUri":"http://localhost:5001/authentication/login-callback"}}""",
            "appsettings.json", Request("http://localhost:5001/")).ToList();

        findings.Should().NotContain(f => f.Id == "MSAL-LOCALHOST-REDIRECT",
            "the redirect matches where the application actually runs");
    }
}
