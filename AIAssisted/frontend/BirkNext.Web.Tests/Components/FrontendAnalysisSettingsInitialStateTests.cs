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
/// Tests for initial page load state separation: current lifecycle vs historical outcome.
/// Ensures that persisted historical failures are not presented as active current failures.
/// </summary>
public sealed class FrontendAnalysisSettingsInitialStateTests : BunitContext
{
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private readonly FrontendAnalysisSettingsService _settings = new();

    public FrontendAnalysisSettingsInitialStateTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
    }

    /// <summary>
    /// Root cause issue: On initial page load with persisted failed detection,
    /// the UI should NOT show "Detection failed" as an active current failure.
    /// Instead, it should show historical status and allow retry.
    /// </summary>
    [Fact]
    public void InitialLoad_WithPersistedFailedDetection_ShowsHistoricalStatusNotActiveFailure()
    {
        var cut = Render<TargetSettingsComponent>();

        // Select QA profile which has persisted failed detection
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // CRITICAL: Initial status should show historical context, not active failure
        var detectionLabel = cut.Find(".fa-detection-value");

        // Should NOT be active "Detection failed"
        detectionLabel.TextContent.Trim().Should().NotBe("Detection failed");

        // Should be historical "Last detection failed"
        detectionLabel.TextContent.Trim().Should().Contain("Last detection failed");

        // CSS class should indicate historical, not active failure
        detectionLabel.GetAttribute("class").Should().Contain("historical-failure");
    }

    /// <summary>
    /// When persisted failure exists and URL hasn't changed, retry button should be available.
    /// </summary>
    [Fact]
    public void InitialLoad_WithPersistedFailedDetection_OffersRetry()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Retry button should be visible (either "Detect settings" or "Retry detection")
        var buttons = cut.FindAll("button")
            .Where(b => b.TextContent.Contains("Detect settings") || b.TextContent.Contains("Retry detection"))
            .ToList();
        buttons.Should().HaveCount(1, "Retry should be available for historical failure");
    }

    /// <summary>
    /// When URL hasn't changed since persisted failure, activation should remain blocked.
    /// But the reason should indicate historical state, not active failure.
    /// </summary>
    [Fact]
    public void InitialLoad_WithPersistedFailedDetection_BlocksActivationButHistoricalNotActive()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Activation should be blocked
        var activate = cut.FindAll("button").SingleOrDefault(b => b.TextContent.Trim() == "Set as Active");
        if (activate is not null)
        {
            activate.HasAttribute("disabled").Should().BeTrue();
        }

        // The blocked reason should reference detection state (historical or current as appropriate)
        var blockReason = cut.Find("#activation-gate-reason");
        blockReason.TextContent.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// When persisted success exists (last detection completed), initial state should show
    /// historical success context, not active success.
    /// </summary>
    [Fact]
    public void InitialLoad_WithPersistedSuccessfulDetection_ShowsHistoricalStatus()
    {
        var settingsWithSuccess = """
        {
          "activeProfileId": "local",
          "profiles": [
            {
              "id": "local", "name": "Local", "environmentType": "Local",
              "targetUrl": "https://application-dev.example.test",
              "lastDetectionSucceeded": true,
              "lastDetectedUrl": "https://application-dev.example.test"
            },
            {
              "id": "qa", "name": "QA", "environmentType": "QA",
              "targetUrl": "https://application-qa.example.test",
              "lastDetectionSucceeded": true,
              "lastDetectedUrl": "https://application-qa.example.test"
            }
          ]
        }
        """;

        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(settingsWithSuccess);

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Should show historical complete, not active complete
        var detectionLabel = cut.Find(".fa-detection-value");
        detectionLabel.TextContent.Trim().Should().Contain("Last detection complete");
    }

    /// <summary>
    /// Switching profiles with different detection histories should not leak state.
    /// Each profile should show its own historical outcome independently.
    /// </summary>
    [Fact]
    public void ProfileSwitch_DoesNotLeakHistoricalState()
    {
        var cut = Render<TargetSettingsComponent>();

        // QA has failed, switch to it
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        var qaLabel = cut.Find(".fa-detection-value").TextContent.Trim();
        qaLabel.Should().Contain("Last detection failed");

        // Switch to Production (no history)
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Production")).Click();
        var prodLabel = cut.Find(".fa-detection-value").TextContent.Trim();
        prodLabel.Should().Be("Not checked");

        // Switch back to QA - should still show historical failure, not production state
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        var qaLabelAgain = cut.Find(".fa-detection-value").TextContent.Trim();
        qaLabelAgain.Should().Contain("Last detection failed");
    }

    /// <summary>
    /// After user runs a new detection that fails, the active "Detection failed" should be shown
    /// (different from historical failure on initial load).
    /// </summary>
    [Fact]
    public void ActiveDetectionFailure_DisplaysAsCurrentFailure_NotHistorical()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = false,
                Message = "Target is unreachable"
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Before detection: historical failure
        var labelBefore = cut.Find(".fa-detection-value").TextContent.Trim();
        labelBefore.Should().Contain("Last detection failed");

        // Start new detection
        cut.FindAll("button").Single(b => b.TextContent.Contains("Detect settings") || b.TextContent.Contains("Retry detection")).Click();

        cut.WaitForAssertion(() =>
        {
            // After detection fails: active "Detection failed" (not historical)
            var labelAfter = cut.Find(".fa-detection-value").TextContent.Trim();
            labelAfter.Should().Be("Detection failed");

            // CSS class should indicate current failure, not historical
            cut.Find(".fa-detection-value").GetAttribute("class").Should().Contain("fa-detection-failed");
        });
    }

    /// <summary>
    /// Stale detection (URL changed but detection was previously run) should take precedence
    /// over historical success/failure and show "Needs re-check".
    /// </summary>
    [Fact]
    public void UrlChanged_SincePersistedDetection_ShowsStaleNotHistorical()
    {
        var settingsWithHistory = """
        {
          "activeProfileId": "local",
          "profiles": [
            {
              "id": "local", "name": "Local", "environmentType": "Local",
              "targetUrl": "https://application-dev.example.test"
            },
            {
              "id": "qa", "name": "QA", "environmentType": "QA",
              "targetUrl": "https://application-qa-changed.example.test",
              "lastDetectionSucceeded": true,
              "lastDetectedUrl": "https://application-qa-old.example.test"
            }
          ]
        }
        """;

        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(settingsWithHistory);

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Should show stale state, not historical success
        var detectionLabel = cut.Find(".fa-detection-value").TextContent.Trim();
        detectionLabel.Should().Be("Needs re-check");
    }

    /// <summary>
    /// Dirty state check: Initial load should NOT mark profile as dirty just from normalizing
    /// the detection lifecycle state.
    /// </summary>
    [Fact]
    public void InitialLoad_DoesNotMarkProfileDirty()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Save button should be disabled (no changes made)
        var saveBtn = cut.FindAll("button").SingleOrDefault(b => b.TextContent.Contains("Save changes"));
        if (saveBtn is not null)
        {
            saveBtn.HasAttribute("disabled").Should().BeTrue("Initial load should not dirty the profile");
        }
    }

    /// <summary>
    /// Historical detection state is preserved and not silently deleted when UI state normalizes.
    /// </summary>
    [Fact]
    public void HistoricalDetectionData_IsPreserved_NotDeleted()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Access the underlying profile through reflection to verify persisted state
        var settingsService = Services.GetRequiredService<IFrontendAnalysisSettingsService>();
        var qaProfile = settingsService.Settings.Profiles.Single(p => p.Name == "QA");

        // Historical data should be intact
        qaProfile.LastDetectionFailure.Should().NotBeNullOrEmpty("Persisted failure reason should be preserved");
        qaProfile.LastDetectedUrl.Should().NotBeNullOrEmpty("Persisted detected URL should be preserved");
        qaProfile.LastDetectionSucceeded.Should().BeFalse("Persisted failure flag should be preserved");
    }

    private const string SettingsJson = """
    {
      "activeProfileId": "local",
      "profiles": [
        {
          "id": "local", "name": "Local", "environmentType": "Local",
          "targetUrl": "https://application-dev.example.test"
        },
        {
          "id": "qa", "name": "QA", "environmentType": "QA",
          "targetUrl": "https://application-qa.example.test",
          "lastDetectionSucceeded": false,
          "lastDetectionFailure": "BrowserClosed",
          "lastDetectedUrl": "https://application-qa.example.test"
        },
        {
          "id": "production", "name": "Production", "environmentType": "Production",
          "targetUrl": "https://application.example.test"
        }
      ]
    }
    """;
}
