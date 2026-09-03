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
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        cut.Find("input[type=url]").Change("https://application-qa.example.test");

        cut.Markup.Should().NotContain("Detected from target");
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
        }
      ]
    }
    """;
}
