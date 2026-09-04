using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TargetSettingsComponent = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

public sealed class FrontendAnalysisSettingsSemanticsTests : BunitContext
{
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private readonly FrontendAnalysisSettingsService _settings = new();

    public FrontendAnalysisSettingsSemanticsTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void ActiveTargetAndCustomProfiles_RenderWithUnambiguousTerminology()
    {
        var cut = Render<TargetSettingsComponent>();

        // New UI: Active card shows TYPE BADGE + NAME, no redundant context/badge
        cut.Markup.Should().Contain("fa-active-card");
        cut.Markup.Should().Contain("Local"); // Active target name
        cut.Markup.Should().Contain("Local with auth");
        cut.Markup.Should().Contain("Review Detect Settings");
        cut.Markup.Should().NotContain("Active Environment");
        cut.Markup.Should().NotContain("Active target environment"); // Removed redundant label
    }

    [Fact]
    public void MisclassifiedLocal_RendersWarningWithoutChangingStoredProfile()
    {
        var cut = Render<TargetSettingsComponent>();

        // New warning format: "Stored type: X · Detected type: Y"
        cut.Markup.Should().Contain("Stored type:");
        cut.Markup.Should().Contain("Local");
        cut.Markup.Should().Contain("Detected type:");
        cut.Markup.Should().Contain("Development");
        cut.Markup.Should().Contain("Review detected settings");
        _settings.ActiveProfile!.EnvironmentType.Should().Be(FrontendEnvironmentType.Local);
    }

    [Fact]
    public void SectionTabs_RenderExplicitSelectedStateTokens()
    {
        var cut = Render<TargetSettingsComponent>();
        var tabs = cut.FindAll("[role=tab]");

        tabs.Should().ContainSingle(tab => tab.TextContent.Trim() == "General" && tab.GetAttribute("aria-selected") == "true");
        tabs.Where(tab => tab.TextContent.Trim() != "General")
            .Should().OnlyContain(tab => tab.GetAttribute("aria-selected") == "false");
    }

    [Fact]
    public void DetectSettings_InvokesExistingDetectorOnceAndKeepsProposalDraftOnly()
    {
        _detection
            .Setup(x => x.DetectFromUrlAsync("https://application-dev.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                OriginalUrl = "https://application-dev.example.test",
                NormalizedTargetUrl = "https://application-dev.example.test",
                Reachability = TargetReachability.Reachable,
                SuggestedEnvironmentType = FrontendEnvironmentType.Development,
                SuggestedProfileName = "APPLICATION DEV",
                Confidence = DetectionConfidence.High
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Apply detected type"));
        _detection.Verify(x => x.DetectFromUrlAsync("https://application-dev.example.test", It.IsAny<CancellationToken>()), Times.Once);
        _settings.ActiveProfile!.EnvironmentType.Should().Be(FrontendEnvironmentType.Local);
        _settings.ActiveProfile.Name.Should().Be("Local");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        _settings.ActiveProfile.EnvironmentType.Should().Be(FrontendEnvironmentType.Local);
        _settings.ActiveProfile.Name.Should().Be("Local");
    }

    [Fact]
    public void ChangingDraftUrl_InvalidatesDetectionProvenance()
    {
        _detection
            .Setup(x => x.DetectFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                SuggestedEnvironmentType = FrontendEnvironmentType.Development
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Trim() == "QA").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        cut.Find("input[type=url]").Change("https://application-qa-b.example.test");

        cut.Markup.Should().NotContain("Detected from target");
        cut.Markup.Should().Contain("Stale");
        cut.Markup.Should().Contain("Frontend URL changed. Run Detect settings again before activating.");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").HasAttribute("disabled").Should().BeTrue();
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void ActiveAndSelectedProfiles_AreDistinctAndSelectionDoesNotActivate()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        cut.Find(".fa-active-card-name").TextContent.Trim().Should().Be("Local");
        cut.Find(".fa-detail-name").TextContent.Trim().Should().Be("QA");
        cut.Find(".fa-summary-url").TextContent.Trim().Should().Be("https://application-qa.example.test");
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Trim().StartsWith("LocalActive", StringComparison.Ordinal)).TextContent.Should().Contain("Active");
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).TextContent.Should().Contain("Selected");
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void UndetectedSelectedProfile_BlocksActivationWithAccessibleReason()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Not checked");
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();
        activate.GetAttribute("aria-describedby").Should().Be("activation-gate-reason");
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Run Detect settings");
    }

    [Fact]
    public void SuccessfulDetectionForCurrentUrl_EnablesButDoesNotPerformActivation()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult { Success = true, Reachability = TargetReachability.Reachable });
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").HasAttribute("disabled").Should().BeFalse();
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void FailedDetection_PreservesConfigurationAndBlocksActivation()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult { Success = false, Message = "Target could not be reached safely." });
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Failed"));
        cut.Markup.Should().Contain("Target could not be reached safely.");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save Environment").Should().NotBeNull();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").HasAttribute("disabled").Should().BeTrue();
        _settings.Settings.Profiles.Single(p => p.Id == "qa").TargetUrl.Should().Be("https://application-qa.example.test");
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void LegacyActiveWithoutDetectionMetadata_RemainsActiveAndProductionIsRepresented()
    {
        var cut = Render<TargetSettingsComponent>();

        _settings.Settings.ActiveProfileId.Should().Be("local");
        _settings.ActiveProfile.Should().NotBeNull();
        cut.Find(".fa-active-card-name").TextContent.Trim().Should().Be("Local");
        cut.FindAll(".fa-profile-chip").Should().Contain(b => b.TextContent.Contains("Production"));
    }

    private const string SettingsJson = """
    {
      "activeProfileId": "local",
      "profiles": [
        {
          "id": "local", "name": "Local", "environmentType": "Local",
          "description": "Misclassified legacy target", "targetUrl": "https://application-dev.example.test",
          "authentication": { "requiresAuthentication": false, "authenticationType": "None" },
          "performance": { "mode": "Default" }
        },
        {
          "id": "qa", "name": "QA", "environmentType": "QA",
          "targetUrl": "https://application-qa.example.test"
        },
        {
          "id": "custom", "name": "Local with auth", "environmentType": "Custom",
          "targetUrl": "https://custom.example.test",
          "authentication": { "requiresAuthentication": true, "authenticationType": "MicrosoftEntraId" }
        },
        {
          "id": "production", "name": "Production", "environmentType": "Production",
          "targetUrl": "https://application.example.test"
        }
      ]
    }
    """;
}
