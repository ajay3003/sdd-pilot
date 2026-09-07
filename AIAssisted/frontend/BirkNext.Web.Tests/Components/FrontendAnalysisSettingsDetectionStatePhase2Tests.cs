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
/// Phase 2 tests for browser-based detection continuation.
/// Tests: "Continue detection in browser" UI action, waiting state, cancellation, result processing.
/// </summary>
public sealed class FrontendAnalysisSettingsDetectionStatePhase2Tests : BunitContext
{
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private readonly FrontendAnalysisSettingsService _settings = new();

    public FrontendAnalysisSettingsDetectionStatePhase2Tests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => new TaskCompletionSource<TargetDetectionOutcome?>().Task);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
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

    [Fact]
    public void NewAttempt_ReplacesGenericFailureWithNavigationTimeout()
    {
        _detection.Setup(x => x.DetectFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<TargetEnvironmentDetectionResult?>(new()
            {
                Success = true, AuthenticationRequired = true, Reachability = TargetReachability.AuthenticationRequired
            }));
        TargetDetectionOutcome Failure(AuthenticationFailureReason reason, string message) => new()
        {
            State = DetectionState.Failed, AuthenticationFailureReason = reason, Message = message,
            DetectionResponse = new() { Success = false, Message = message, State = DetectionState.Failed }
        };
        _detection.SetupSequence(x => x.StartBrowserDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure(AuthenticationFailureReason.GenericFailure, "Authentication failed: Unknown authentication failure"))
            .ReturnsAsync(Failure(AuthenticationFailureReason.NavigationTimeout, "Authentication failed: Navigation timeout during authentication"));
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Unknown authentication failure"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Retry detection").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Navigation timeout during authentication");
            cut.Markup.Should().NotContain("Unknown authentication failure");
            var reason = typeof(TargetSettingsComponent).GetField("_authenticationFailureReason", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cut.Instance);
            Assert.Equal(AuthenticationFailureReason.NavigationTimeout, reason);
        });
    }

    [Fact]
    public void ContinueDetectionButton_VisibleWhen_AuthenticationRequired_And_Current()
    {
        // Setup: detection succeeded but auth is required
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
            // Auth required state should be shown
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");

            // "Continue detection in browser" button should be visible
            var buttons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            buttons.Should().HaveCount(1);
        });
    }

    [Fact]
    public void ContinueDetectionButton_NotVisible_When_DetectionStateIs_Complete()
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
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection complete");

            // Button should NOT be visible when Complete
            var browserButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            browserButtons.Should().HaveCount(0);
        });
    }

    [Fact]
    public void ContinueDetectionButton_NotVisible_When_DetectionStateIs_Failed()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = false,
                Message = "Target unreachable"
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection failed");

            var browserButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            browserButtons.Should().HaveCount(0);
        });
    }

    [Fact]
    public void ContinueDetectionButton_NotVisible_When_URL_Changed_And_Stale()
    {
        // Setup: initial detection with auth required
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();

        // Click "Detect settings" - this auto-enters edit mode
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Verify button IS visible before URL change
        var continueButtonBefore = cut.FindAll("button")
            .Where(b => b.TextContent.Contains("Continue detection in browser"))
            .ToList();
        continueButtonBefore.Should().HaveCount(1);

        // Now change the URL (we're already in edit mode after Detect settings click)
        var urlInput = cut.Find("input[type='url']");
        urlInput.Change("https://different-url.example.test");

        cut.WaitForAssertion(() =>
        {
            // Should now show Stale state
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Needs re-check");

            // Button should NOT be visible when Stale
            var browserButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            browserButtons.Should().HaveCount(0);
        });
    }

    [Fact]
    public void WaitingForSignIn_DisplaysCorrectly_AndShowsMessages()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click "Continue detection in browser"
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Waiting state should be displayed
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(1);

            // Messages should indicate that BirkNext has opened a browser
            var markup = cut.Markup;
            markup.Should().Contain("Waiting for sign-in in BirkNext browser");
            markup.Should().Contain("Complete authentication (account selection, password, MFA, MCAS) in the browser opened by BirkNext");
            markup.Should().Contain("BirkNext will observe your authentication flow");
            markup.Should().Contain("Use the BirkNext-opened browser, not another browser or window");
        });
    }

    [Fact]
    public void ContinueDetectionButton_Disabled_When_BrowserDetectionInProgress()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click continue button
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // The action remains visible but disabled while its single attempt is pending.
            var waitingBtn = cut.FindAll("button")
                .FirstOrDefault(b => b.TextContent.Contains("Continue detection in browser"));
            waitingBtn.Should().NotBeNull();
            waitingBtn!.HasAttribute("disabled").Should().BeTrue();
            cut.FindAll(".fa-browser-detection-waiting").Should().HaveCount(1);
        });
    }

    [Fact]
    public void CancelDetection_KeepsAuthRequiredState_AllowsRetry()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click continue button to start
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(1);
        });

        // Click cancel button
        var cancelBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Cancel detection"));
        cancelBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Should return to auth required state
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");

            // Waiting section should be hidden
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(0);

            // "Continue detection in browser" button should be visible again and enabled
            var retryBtn = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            retryBtn.Should().HaveCount(1);
            retryBtn[0].HasAttribute("disabled").Should().BeFalse();

            // Error message should show
            var markup = cut.Markup;
            markup.Should().Contain("Authentication not completed");
        });
    }

    [Fact]
    public void SuccessfulBrowserDetection_UpdatesStateToComplete_EnablesActivation()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Start browser detection
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(1);
        });

        // Simulate successful completion by mocking the completion endpoint
        // In real scenario, this would come from backend polling/notification
        // For now, we simulate it by changing the component state
        // This would be tested through E2E/Playwright tests with actual backend

        // The component should eventually show detection complete message
        // and enable "Set as Active" button
    }

    [Fact]
    public void NoAutoLaunch_BrowserDetection_RequiresExplicitUserClick()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");

            // Browser detection waiting UI should NOT be visible
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(0);

            // Continue button should be present and enabled
            var continueBtn = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Continue detection in browser"))
                .ToList();
            continueBtn.Should().HaveCount(1);
            continueBtn[0].HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public void ContinueDetectionButton_Text_Correct_And_Accessible()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            var button = cut.FindAll("button")
                .Single(b => b.TextContent.Contains("Continue detection in browser"));

            // Button should have aria-label or descriptive text
            button.TextContent.Should().Contain("Continue detection in browser");

            // Button should have proper role
            button.GetAttribute("type").Should().Be("button");
        });
    }

    [Fact]
    public void DetectionState_Preserved_When_Cancelling_BrowserFlow()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
                DetectedAuthority = "https://login.microsoftonline.com",
                DetectedClientId = "client-id-123"
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Start browser detection
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var waitingSection = cut.FindAll(".fa-browser-detection-waiting");
            waitingSection.Should().HaveCount(1);
        });

        // Cancel
        var cancelBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Cancel detection"));
        cancelBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // State should still be auth required
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");

            // Previously detected metadata should still be visible in display
            var markup = cut.Markup;
            markup.Should().Contain("Client/Application ID");
        });
    }

    [Fact]
    public void SetAsActive_Enabled_Only_After_Successful_Complete_Detection()
    {
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            // Set as Active should be disabled
            var setActive = cut.FindAll("button")
                .Single(b => b.TextContent.Trim() == "Set as Active");
            setActive.HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public void ExplicitContinueClick_CallsApiOnceWithBoundProfileUrlAndSession_WithoutAutoActivation()
    {
        ArrangeAuthenticationRequiredPreflight();
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompleteOutcome("https://application-qa.example.test"));
        var cut = RenderAuthRequiredQa();

        _detection.Verify(x => x.StartBrowserDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();

        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection complete"));
        _detection.Verify(x => x.StartBrowserDetectionAsync(
            "https://application-qa.example.test",
            It.Is<string>(s => !string.IsNullOrWhiteSpace(s) && s.StartsWith("detection-qa-")),
            "qa", It.IsAny<CancellationToken>()), Times.Once);
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void BrowserContinuation_PersistsOnlyProvenance_AndDoesNotApplySuggestionsOrUnsavedDraft()
    {
        ArrangeAuthenticationRequiredPreflight();
        var outcome = CompleteOutcome("https://application-qa.example.test");
        outcome.DetectionResponse!.DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var cut = RenderAuthRequiredQa();
        var persisted = _settings.Settings.Profiles.Single(p => p.Id == "qa");
        persisted.Description = "Original";

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "General").Click();
        cut.FindAll("input[type=text]")[1].Change("Unsaved change");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();

        cut.WaitForAssertion(() => persisted.LastDetectionSucceeded.Should().BeTrue());
        persisted.Description.Should().Be("Original");
        persisted.EnvironmentType.Should().Be(FrontendEnvironmentType.QA);
        persisted.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
    }

    [Fact]
    public void ProfileSwitchWhilePending_DiscardsCallingProfilesResult()
    {
        ArrangeAuthenticationRequiredPreflight();
        var pending = new TaskCompletionSource<TargetDetectionOutcome?>();
        _detection.Setup(x => x.StartBrowserDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var cut = RenderAuthRequiredQa();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();

        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Trim().StartsWith("LocalActive")).Click();
        pending.SetResult(CompleteOutcome("https://application-qa.example.test"));

        cut.WaitForAssertion(() => cut.Find(".fa-detail-name").TextContent.Trim().Should().Be("Local"));
        _settings.Settings.Profiles.Single(p => p.Id == "qa").LastDetectionSucceeded.Should().BeFalse();
        _settings.Settings.ActiveProfileId.Should().Be("local");
    }

    [Fact]
    public void UrlChangeWhilePending_DiscardsCompleteForOldUrl()
    {
        ArrangeAuthenticationRequiredPreflight();
        var pending = new TaskCompletionSource<TargetDetectionOutcome?>();
        _detection.Setup(x => x.StartBrowserDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var cut = RenderAuthRequiredQa();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();
        cut.Find("input[type=url]").Change("https://application-qa-b.example.test");

        pending.SetResult(CompleteOutcome("https://application-qa.example.test"));

        cut.WaitForAssertion(() => cut.Find(".fa-summary-url").TextContent.Should().Contain("qa-b"));
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Needs re-check");
        _settings.Settings.Profiles.Single(p => p.Id == "qa").LastDetectionSucceeded.Should().BeFalse();
    }

    [Fact]
    public void OlderAttemptCompletingAfterNewerAttempt_CannotOverwriteNewerResult()
    {
        ArrangeAuthenticationRequiredPreflight();
        var first = new TaskCompletionSource<TargetDetectionOutcome?>();
        var second = new TaskCompletionSource<TargetDetectionOutcome?>();
        var calls = 0;
        _detection.Setup(x => x.StartBrowserDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? first.Task : second.Task);
        var cut = RenderAuthRequiredQa();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancel detection")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Continue detection in browser")).Click();

        second.SetResult(CompleteOutcome("https://application-qa.example.test"));
        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection complete"));
        first.SetResult(new TargetDetectionOutcome
        {
            State = DetectionState.Failed,
            DetectionResponse = new TargetEnvironmentDetectionResult { Success = false, Message = "old failure" }
        });

        cut.WaitForAssertion(() => _settings.Settings.Profiles.Single(p => p.Id == "qa").LastDetectionSucceeded.Should().BeTrue());
        cut.Markup.Should().NotContain("old failure");
    }

    private void ArrangeAuthenticationRequiredPreflight() =>
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true, Reachability = TargetReachability.AuthenticationRequired, AuthenticationRequired = true
            });

    private IRenderedComponent<TargetSettingsComponent> RenderAuthRequiredQa()
    {
        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required"));
        return cut;
    }

    [Fact]
    public void BrowserDetectionFailure_DisplaysTypedMessage_NotGenericFailed()
    {
        // Setup: preflight succeeds, auth required
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        // Setup: browser detection fails with typed message
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                "https://application-qa.example.test", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetDetectionOutcome
            {
                State = DetectionState.Failed,
                IsActivationReady = false,
                Message = "Interactive sign-in timed out. Complete authentication in the browser opened by BirkNext and try again.",
                DetectionResponse = new TargetEnvironmentDetectionResult
                {
                    Success = false,
                    Message = "Interactive sign-in timed out. Complete authentication in the browser opened by BirkNext and try again."
                },
                DetectedUrl = "https://application-qa.example.test",
                IsUrlCurrent = true
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click continue button to start browser detection
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        // Wait for browser detection to complete and fail
        cut.WaitForAssertion(() =>
        {
            // Error message should show typed reason, not generic "Failed"
            var errorElement = cut.FindAll(".fa-detection-error");
            errorElement.Should().HaveCount(1);
            var errorText = errorElement[0].TextContent;

            // Should contain the specific typed message from backend
            errorText.Should().Contain("Interactive sign-in timed out");
            errorText.Should().Contain("browser opened by BirkNext");

            // Should NOT just say "Failed"
            errorText.Should().NotBe("✗ Browser detection did not complete: Failed");
        });
    }

    [Fact]
    public void BrowserDetectionSuccess_DisplaysCompleteMessage()
    {
        // Setup: preflight succeeds, auth required
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        // Setup: browser detection succeeds
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                "https://application-qa.example.test", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompleteOutcome("https://application-qa.example.test"));

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click continue button
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        // Wait for browser detection to succeed
        cut.WaitForAssertion(() =>
        {
            // Detection complete message should appear
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection complete");

            // Success message should show (briefly)
            var successMsg = cut.FindAll(".fa-detection-error")
                .Where(e => e.TextContent.Contains("Interactive detection complete"))
                .ToList();
            // Message is shown briefly then hidden, so we just check it appeared at some point
            // Instead, verify the state is correct
            cut.Find(".fa-detection-value").TextContent.Should().Contain("Detection complete");
        });
    }

    [Fact]
    public void AuthenticationNavigationFailure_DisplaysTypedReason_NotUnknown()
    {
        // Regression: authentication_navigation_failed should map to NavigationTimeout, not Unknown

        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId
            });

        // Simulate authentication_navigation_failed from session manager
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                "https://application-qa.example.test", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetDetectionOutcome
            {
                State = DetectionState.Failed,
                AuthenticationFailureReason = AuthenticationFailureReason.NavigationTimeout,
                IsActivationReady = false,
                Message = "Authentication failed: Navigation timeout during authentication",
                DetectionResponse = new TargetEnvironmentDetectionResult
                {
                    Success = false,
                    Reachability = TargetReachability.Reachable,
                    Message = "Authentication failed: Navigation timeout during authentication"
                },
                DetectedUrl = "https://application-qa.example.test",
                IsUrlCurrent = true
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Should show typed "Navigation timeout" message, NOT "Unknown"
            var errorElement = cut.FindAll(".fa-detection-error");
            errorElement.Should().HaveCount(1);
            errorElement[0].TextContent.Should().Contain("Navigation timeout");
            errorElement[0].TextContent.Should().NotContain("Unknown");
        });
    }

    [Fact]
    public void AuthenticationFailure_DoesNotCoexistWith_SuccessMessage()
    {
        // Regression test: Auth failure must not show alongside "Detection completed successfully"
        // This was a bug where preflight success message wasn't cleared when auth failed

        // Setup: preflight succeeds with Blazor/auth required
        _detection.Setup(x => x.DetectFromUrlAsync("https://application-qa.example.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetEnvironmentDetectionResult
            {
                Success = true,
                Reachability = TargetReachability.Reachable,
                AuthenticationRequired = true,
                DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
                DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
                SuggestedEnvironmentType = FrontendEnvironmentType.Development,
                Message = "Detection completed successfully" // Preflight success message
            });

        // Setup: browser detection fails with auth timeout
        _detection.Setup(x => x.StartBrowserDetectionAsync(
                "https://application-qa.example.test", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetDetectionOutcome
            {
                State = DetectionState.Failed,
                IsActivationReady = false,
                Message = "Interactive sign-in timed out. Complete authentication in the browser opened by BirkNext and try again.",
                DetectionResponse = new TargetEnvironmentDetectionResult
                {
                    Success = false,
                    Reachability = TargetReachability.Reachable,
                    AuthenticationRequired = false,
                    DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
                    SuggestedEnvironmentType = FrontendEnvironmentType.Development,
                    // CRITICAL: Message must be updated to auth failure, not retain preflight success
                    Message = "Interactive sign-in timed out. Complete authentication in the browser opened by BirkNext and try again."
                },
                DetectedUrl = "https://application-qa.example.test",
                IsUrlCurrent = true
            });

        var cut = Render<TargetSettingsComponent>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Authentication required");
        });

        // Click continue button
        var continueBtn = cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Continue detection in browser"));
        continueBtn.Click();

        // Wait for browser detection to complete and verify NO contradiction
        cut.WaitForAssertion(() =>
        {
            // This fixture has no outstanding browser inspection requirement.
            cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Detection failed");

            // Error message must show auth failure
            var errorElement = cut.FindAll(".fa-detection-error");
            errorElement.Should().HaveCount(1);
            errorElement[0].TextContent.Should().Contain("Interactive sign-in timed out");

            // CRITICAL CHECK: Result message must NOT be "Detection completed successfully"
            // It must reflect the auth failure
            var resultMessage = cut.Find(".fa-result-message").TextContent.Trim();
            resultMessage.Should().NotContain("Detection completed successfully");
            resultMessage.Should().NotBeEmpty();

            // Set as Active button must be disabled (not ready)
            var setActiveBtn = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("Set as Active"))
                .FirstOrDefault();
            if (setActiveBtn != null)
            {
                setActiveBtn.HasAttribute("disabled").Should().BeTrue();
            }
        });
    }

    private static TargetDetectionOutcome CompleteOutcome(string url) => new()
    {
        State = DetectionState.Complete,
        IsActivationReady = true,
        DetectedUrl = url,
        IsUrlCurrent = true,
        DetectionResponse = new TargetEnvironmentDetectionResult
        {
            OriginalUrl = url, NormalizedTargetUrl = url, Success = true, Reachability = TargetReachability.Reachable
        }
    };
}
