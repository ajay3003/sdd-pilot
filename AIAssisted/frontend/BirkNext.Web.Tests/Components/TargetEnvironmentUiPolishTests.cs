using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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
    private readonly Mock<IIntegrationTemplateService> _templates = new();

    public TargetEnvironmentUiPolishTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_templates.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}"}
        ]}
        """);
        _templates.Setup(t => t.GetForEnvironmentAsync(It.IsAny<string?>())).ReturnsAsync([]);
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
        card.TextContent.Should().Contain("Status").And.Contain("Manual verification required");

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

    private IRenderedComponent<Component> OpenAddIntegration()
    {
        var cut = Open();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Integrations").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add Integration").Click();
        return cut;
    }

    // ── 9–11. Both options render as one accessible single choice ───────────

    [Fact]
    public void KnownAndCustomRenderAsOneAccessibleRadioGroup()
    {
        var cut = OpenAddIntegration();

        var known = cut.Find("[data-testid=fa-add-mode-known]");
        var custom = cut.Find("[data-testid=fa-add-mode-custom]");

        // Real radio semantics: same group name, native keyboard behaviour, exposed selection.
        foreach (var input in new[] { known, custom })
        {
            input.GetAttribute("type").Should().Be("radio");
            input.GetAttribute("name").Should().Be("fa-add-mode");
        }
        known.HasAttribute("checked").Should().BeTrue("Known is the default choice");
        custom.HasAttribute("checked").Should().BeFalse();

        // Each option explains itself.
        var cards = cut.FindAll(".fa-add-mode-card").Select(c => c.TextContent).ToList();
        cards.Should().ContainSingle(c => c.Contains("Known M2LB integration") && c.Contains("predefined integration template"));
        cards.Should().ContainSingle(c => c.Contains("Custom integration") && c.Contains("Configure an integration manually"));
        cut.Find(".fa-add-mode legend").TextContent.Trim().Should().Be("Add integration");
    }

    [Fact]
    public void CustomOptionListsTheSupportedIntegrationTypes()
    {
        var cut = OpenAddIntegration();

        var types = cut.Find(".fa-add-mode-types").TextContent;
        foreach (var type in new[] { "REST", "GraphQL", "Event Hub", "Service Bus", "Kafka", "RabbitMQ" })
            types.Should().Contain(type);
    }

    // ── 12–13, 15, 18. Empty template state is neutral and actionable ───────

    [Fact]
    public void NoKnownTemplateStateIsNeutralActionableAndFabricatesNothing()
    {
        var cut = OpenAddIntegration();

        var empty = cut.Find("[data-testid=fa-templates-empty]");
        empty.TextContent.Should().Contain("No known M2LB templates are available for this environment.");
        empty.TextContent.Should().Contain("You can configure a custom integration instead.");

        // Neutral, not an error or a warning.
        empty.ClassList.Should().NotContain(c => c.Contains("warn") || c.Contains("error") || c.Contains("danger"));
        empty.TextContent.Should().NotContainAny("Error", "Failed", "Warning", "Invalid");
        // Provenance stays truthful: nothing is called verified or discovered when no template exists.
        empty.TextContent.Should().NotContainAny("verified", "Verified", "Discovered");

        // No template is invented for an environment that has none.
        cut.FindAll("[data-testid=fa-template-select]").Should().BeEmpty();
        cut.FindAll("[data-testid=fa-templates-empty-custom]").Should().ContainSingle();
    }

    // ── 14, 16. The action switches the flow and the custom form still works ─

    [Fact]
    public void ConfigureCustomIntegrationSwitchesTheFlowAndKeepsTheCustomForm()
    {
        var cut = OpenAddIntegration();

        cut.Find("[data-testid=fa-templates-empty-custom]").Click();

        cut.Find("[data-testid=fa-add-mode-custom]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=fa-add-mode-known]").HasAttribute("checked").Should().BeFalse();
        cut.FindAll("[data-testid=fa-templates-empty]").Should().BeEmpty();

        // The existing custom confirm action is untouched and still adds an integration.
        cut.Find("[data-testid=fa-add-custom-confirm]").Click();
        cut.FindAll("[data-testid=fa-integration-row], .fa-integration-row").Should().NotBeEmpty();
    }

    // ── 17. Informational strip stays, as neutral guidance ──────────────────

    [Fact]
    public void IntegrationQualityReviewGuidanceRemainsAndIsNotStyledAsAWarning()
    {
        var cut = OpenAddIntegration();

        var note = cut.Find("[data-testid=fa-integrations-note]");
        note.TextContent.Should().Contain("Configure service integrations used by Integration Quality Review.");
        note.TextContent.Should().Contain("not added here automatically", "the page must not imply Endpoint Discovery populates it");
        note.ClassList.Should().NotContain(c => c.Contains("warn") || c.Contains("error"));
        note.GetAttribute("style").Should().BeNullOrEmpty("styling moved out of inline attributes");
    }
}
