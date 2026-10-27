using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using BirkNext.Web.Tests.Integration;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Two focused presentation guarantees: the manual-verification state is stated once and then the UI moves on to the
/// action, and the Integrations add flow makes the Known/Custom choice deliberate with an actionable no-template state.
/// Neither changes detection, verification or integration semantics.
/// </summary>
public sealed class TargetEnvironmentUiPolishTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";
    private const string LongPhrase = "Manual authentication verification required";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();

    public TargetEnvironmentUiPolishTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IIntegrationCatalogApiService>(new FakeIntegrationCatalogApi { Catalog = M2lbFixture.Catalog() });
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}"}
        ]}
        """);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
            Confidence = DetectionConfidence.VeryHigh,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
            SuggestedEnvironmentType = FrontendEnvironmentType.Development,
            SuggestedProfileName = "M2LB DEV",
            State = DetectionState.ManualAuthenticationVerificationRequired,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com/tenant",
        });
    }

    private IRenderedComponent<Component> Open()
    {
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        return cut;
    }

    private IRenderedComponent<Component> Detect()
    {
        var cut = Open();
        cut.Find("#target-tab-target").Click(); cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check target application").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        return cut;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ── 1. No global verification: the limitation is stated once, as an authentication fact ──

    [Fact]
    public void TheAuthenticationLimitationIsStatedOnce_AndNoGlobalVerificationRemains()
    {
        var cut = Detect();

        Occurrences(cut.Markup, LongPhrase).Should().Be(0, "a manual sign-in requirement is not a target-wide verification state");
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Target detection complete");
        cut.FindAll("[data-testid=detection-auth-limitation]").Should().BeEmpty("detection never states sign-in or runtime verification");
        cut.Find("section.fa-result").TextContent.Should().NotContain("Not verified").And.NotContain("work profile").And.NotContain("sign-in check");
        cut.FindAll("section.fa-verification").Should().BeEmpty();
        cut.Markup.Should().NotContain("Mark verification").And.NotContain("Activation requires");
    }

    // ── 2. Detection result remains the primary status surface ──────────────

    [Fact]
    public void DetectionResultRemainsPrimaryAndKeepsItsDetectedFacts()
    {
        var cut = Detect();

        var result = cut.Find("section.fa-result");
        result.QuerySelector("#detection-result-heading")!.TextContent.Trim().Should().Be("Target detection complete");

        // A successful check lists its facts once, in "Detected from target"; configured authentication is the Authentication tab's.
        var facts = cut.Find("[data-testid=target-detected-summary]").TextContent;
        facts.Should().Contain("Reachability").And.Contain("Reachable");
        facts.Should().Contain("Framework").And.Contain("Blazor WebAssembly");
        facts.Should().Contain("Environment type").And.Contain("Development");
        facts.Should().Contain("Profile name").And.Contain("M2LB DEV");
        result.TextContent.Should().NotContain("Configured authentication");

        // Confidence stays readable text on the card.
        result.TextContent.Should().Contain("Very High");
        facts.Should().NotContain("Manual authentication verification");
    }

    // ── 3. The optional manual sign-in check still records a result, on the Authentication tab ──

    [Fact]
    public void TheManualSignInCheckStillRecordsAResult_OnTheAuthenticationTab()
    {
        var cut = Detect();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        cut.FindAll("[role=tab][aria-selected=true]").Single().TextContent.Trim().Should().Be("Authentication");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Open manual sign-in check").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Record sign-in check completed").Click();

        cut.WaitForAssertion(() => _settings.Settings.Profiles.Single(p => p.Id == "dev")
            .ManualVerification!.Result.Should().Be(ManualAuthenticationVerificationStatus.Passed));
        cut.Find("[data-testid=authentication-verification-status]").TextContent.Trim().Should().Be("Completed (recorded manually)");
        cut.Find("[data-testid=authentication-runtime-state]").TextContent.Trim().Should().Be("Not verified", "a recorded check is not runtime evidence");
    }

    // ── Integrations ────────────────────────────────────────────────────────

    private IRenderedComponent<Component> OpenIntegrations()
    {
        var cut = Open();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Integrations").Click();
        return cut;
    }

    // ── 17. Informational strip stays, as neutral guidance ──────────────────

    [Fact]
    public void IntegrationQualityReviewGuidanceRemainsAndIsNotStyledAsAWarning()
    {
        var cut = OpenIntegrations();

        var note = cut.WaitForElement("[data-testid=ip-intro]");
        note.TextContent.Should().Contain("reviewed in Integration Quality Review");
        cut.Find("[data-testid=ip-how-evidence-toggle]").Click();
        cut.Find("[data-testid=ip-how-evidence-list]").TextContent.Should().Contain("never added here automatically", "the page must not imply Endpoint Discovery populates it");
        note.ClassList.Should().NotContain(c => c.Contains("warn") || c.Contains("error"));
        note.GetAttribute("style").Should().BeNullOrEmpty("styling moved out of inline attributes");
    }
}
