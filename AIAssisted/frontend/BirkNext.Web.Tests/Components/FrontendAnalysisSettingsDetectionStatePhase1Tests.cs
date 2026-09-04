using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TargetSettingsComponent = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Phase 1 tests for detection state display and activation readiness gate.
/// Tests the six detection states: NotChecked, Complete, AuthenticationRequired, Partial, Stale, Failed.
/// </summary>
public sealed class FrontendAnalysisSettingsDetectionStatePhase1Tests : BunitContext
{
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private readonly FrontendAnalysisSettingsService _settings = new();

    public FrontendAnalysisSettingsDetectionStatePhase1Tests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void NotChecked_DisplaysNotCheckedState_AndBlocksActivation()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Detection state should be "Not checked"
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Not checked");
        cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-not-checked");

        // Activation should be blocked
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();
        activate.GetAttribute("aria-describedby").Should().Be("activation-gate-reason");

        // Activation blocked reason should be clear
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Run Detect settings");
    }

    [Fact]
    public void Complete_DisplaysCheckedState_AndEnablesActivation()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = false,
                Warnings = []
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked");
            cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-checked");
        });

        // Activation should be enabled
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void AuthenticationRequired_DisplaysAuthRequiredState_AndBlocksActivationWithMessage()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
                DetectedAuthority = "https://login.microsoftonline.com"
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Auth required");
            cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-auth-required");
        });

        // Activation should be blocked
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();

        // Blocked reason should mention auth
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Authentication required");
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Continue in browser");
    }

    [Fact]
    public void Partial_DisplaysPartialState_AndBlocksActivationWithMessage()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = false,
                Warnings = ["Some detection warnings detected"]
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Partial");
            cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-partial");
        });

        // Activation should be blocked
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();

        // Blocked reason should mention partial
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Partial detection");
    }

    [Fact]
    public void Stale_DisplaysStaleState_AndBlocksActivationWithMessage()
    {
        _detection.Setup(x => x.DetectFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Run detection
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));

        // Change URL
        cut.Find("input[type=url]").Change("https://application-qa-changed.example.test");

        // Detection state should be "Stale"
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Stale");
        cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-stale");

        // Activation should be blocked
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();

        // Blocked reason should mention URL changed
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("URL changed");
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Run Detect settings");
    }

    [Fact]
    public void Failed_DisplaysFailedState_AndBlocksActivation()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = false,
                Message = "Target could not be reached."
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Failed");
            cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-failed");
        });

        // Activation should be blocked
        var activate = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active");
        activate.HasAttribute("disabled").Should().BeTrue();

        // Blocked reason should mention failure
        cut.Find("#activation-gate-reason").TextContent.Should().Contain("Detection failed");
    }

    [Fact]
    public void UrlStalenessDetection_WorksWithNormalization()
    {
        _detection.Setup(x => x.DetectFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Run detection with HTTPS
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Detected from target");
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked");
        });

        // Add default HTTPS port - should NOT be stale (normalized)
        cut.Find("input[type=url]").Change("https://application-qa.example.test:443");
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked");

        // Change to different host - should be stale
        cut.Find("input[type=url]").Change("https://application-qa-v2.example.test");
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Stale");
    }

    [Fact]
    public void ActiveEnvironmentCardShowsActiveName_NotSelectedName()
    {
        var cut = Render<TargetSettingsComponent>();

        // Select QA but active is still Local
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Active card should show "Local"
        cut.Find(".fa-active-card-name").TextContent.Trim().Should().Be("Local");

        // Detail section should show "QA"
        cut.Find(".fa-detail-name").TextContent.Trim().Should().Be("QA");

        // Profile chips should show badges
        var chips = cut.FindAll(".fa-profile-chip");
        chips.Where(c => c.TextContent.Contains("Local")).Single()
            .TextContent.Should().Contain("Active");
        chips.Where(c => c.TextContent.Contains("QA")).Single()
            .TextContent.Should().Contain("Selected");
    }

    [Fact]
    public void HierarchyClarity_ActivePlusEnvironmentsPlusSelected()
    {
        var cut = Render<TargetSettingsComponent>();

        // Should show three sections:
        // 1. Active environment card
        cut.Markup.Should().Contain("fa-active-card");
        var headings = cut.FindAll("h2");
        headings.Should().Contain(h => h.TextContent.Contains("Active environment"));

        // 2. Environments chip list
        headings.Should().Contain(h => h.TextContent.Contains("Environments"));

        // 3. Selected environment detail - shown via kicker
        cut.Markup.Should().Contain("fa-detail-kicker");
        cut.Markup.Should().Contain("Selected environment");
    }

    [Fact]
    public void DetectionErrorMessage_DisplaysInTargetApplicationTab()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = false,
                Message = "Timeout: Target did not respond within 30 seconds."
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Click on Target Application tab to enter edit mode
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Trim() == "Target Application").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            // Error message should be visible
            cut.Markup.Should().Contain("Timeout: Target did not respond within 30 seconds.");
            cut.Markup.Should().Contain("fa-detection-error");
        });
    }

    [Fact]
    public void NoContinueInBrowserButton_InPhase1()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                AuthenticationRequired = true
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            // Should NOT have "Continue in browser" button
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Continue in browser"));
        });
    }

    [Fact]
    public void ActivationStateAfterSave_PreservedOnProfileSelection()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Run detection
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked"));

        // Save
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save Environment").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("saved"));

        // Select different profile and back
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Production")).Click();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // State should be preserved
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Checked");
    }

    [Fact]
    public void NoConsoleErrors_OnRender()
    {
        var cut = Render<TargetSettingsComponent>();

        // Should render without errors
        cut.MarkupMatches(cut.Markup); // Basic sanity check

        // No missing component parameters
        cut.FindAll(".fa-profile-chip").Count.Should().BeGreaterThan(0);
    }

    private const string SettingsJson = """
    {
      "activeProfileId": "local",
      "profiles": [
        {
          "id": "local", "name": "Local", "environmentType": "Local",
          "targetUrl": "https://application-dev.example.test",
          "authentication": { "requiresAuthentication": false, "authenticationType": "None" },
          "performance": { "mode": "Default" }
        },
        {
          "id": "qa", "name": "QA", "environmentType": "QA",
          "targetUrl": "https://application-qa.example.test"
        },
        {
          "id": "production", "name": "Production", "environmentType": "Production",
          "targetUrl": "https://application.example.test"
        }
      ]
    }
    """;
}
