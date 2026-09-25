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
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
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

    // ── 1. The long phrase is stated once ────────────────────────────────────

    [Fact]
    public void TheLongVerificationPhraseAppearsOnlyOnceOnScreen()
    {
        var cut = Detect();

        Occurrences(cut.Markup, LongPhrase).Should().Be(1,
            "the Detection result card owns the full statement; other surfaces use compact wording");

        // The compact badge beside the Frontend URL no longer repeats the sentence.
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Needs verification");
        // The Verification card leads with a short status.
        cut.Find("[data-testid=manual-verification-status]").TextContent.Trim().Should().Be("Manual verification required");
    }

    // ── 2. Detection result remains the primary status surface ──────────────

    [Fact]
    public void DetectionResultRemainsPrimaryAndKeepsItsDetectedFacts()
    {
        var cut = Detect();

        var result = cut.Find("section.fa-result");
        result.QuerySelector("#detection-result-heading")!.TextContent.Trim().Should().Be(LongPhrase);

        var grid = cut.Find(".fa-result-grid").TextContent;
        grid.Should().Contain("Reachability").And.Contain("Reachable");
        grid.Should().Contain("Framework").And.Contain("Blazor WebAssembly");
        grid.Should().Contain("Environment").And.Contain("Development");
        grid.Should().Contain("Detected authentication").And.Contain("Configured authentication");
        grid.Should().Contain("Profile name").And.Contain("M2LB DEV");

        // Confidence stays readable text on the card.
        result.TextContent.Should().Contain("Very High");

        // The duplicated value row is gone; the heading already says it.
        grid.Should().NotContain("Manual authentication verification");
    }

    // ── 3–5. The Verification card keeps its workflow ───────────────────────

    [Fact]
    public void VerificationCardKeepsItsActionAndHelperText()
    {
        var cut = Detect();

        var card = cut.Find("section.fa-verification");
        card.QuerySelector("#manual-verification-heading")!.TextContent.Trim().Should().Be("Verification");
        card.QuerySelector("[data-testid=manual-verification-status]")!.TextContent.Trim().Should().Be("Manual verification required");

        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Open verification instructions");
        card.TextContent.Should().Contain("If you changed environment settings, save them before recording verification.");
        card.TextContent.Should().Contain("Activation requires current target detection and manual verification Passed.");
    }

    // ── 8. The workflow itself is unchanged ─────────────────────────────────

    [Fact]
    public void VerificationWorkflowStillRecordsAPassedResult()
    {
        var cut = Detect();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Open verification instructions").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Mark verification passed").Click();

        cut.WaitForAssertion(() => _settings.Settings.Profiles.Single(p => p.Id == "dev")
            .ManualVerification!.Result.Should().Be(ManualAuthenticationVerificationStatus.Passed));
        cut.Find("[data-testid=manual-verification-status]").TextContent.Trim().Should().Be("Manual verification passed");
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
        note.TextContent.Should().Contain("Configured integrations used by Integration Quality Review.");
        note.TextContent.Should().Contain("never added here automatically", "the page must not imply Endpoint Discovery populates it");
        note.ClassList.Should().NotContain(c => c.Contains("warn") || c.Contains("error"));
        note.GetAttribute("style").Should().BeNullOrEmpty("styling moved out of inline attributes");
    }
}
