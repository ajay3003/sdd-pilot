using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Tests for interactive browser detection strategy.
/// Uses controlled fixtures simulating different authentication flows without real credentials.
/// </summary>
public sealed class InteractiveBrowserDetectionStrategyTests
{
    private const string FakeReviewSessionId = "review-session-123";
    private const string FakeProfileId = "profile-456";
    private const string FakeTargetUrl = "https://app.example.com/dashboard";

    [Fact]
    public async Task ContinueDetectionAsync_SuccessfulAuthentication_ReturnsAuthenticatedState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        // Mock successful authentication flow
        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // Simulate successful authentication after a few polls
        var pollCount = 0;
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                pollCount++;
                if (pollCount >= 2)
                {
                    return Task.FromResult<AuthenticatedBrowserSessionDescriptor?>(
                        new(sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                            AuthenticatedBrowserSessionStatus.Authenticated,
                            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15),
                            DeliveryContext: AuthenticatedDeliveryContext.DirectApplication));
                }
                return Task.FromResult<AuthenticatedBrowserSessionDescriptor?>(
                    new(sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                        AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));
            });

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.AuthenticationSucceeded.Should().BeTrue();
        result.IsFullCompletion.Should().BeTrue();
        result.ResultingState.Should().Be(TargetDetectionState.Complete);
        result.UserCancelled.Should().BeFalse();
        result.SessionExpired.Should().BeFalse();
        result.AuthenticationFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ContinueDetectionAsync_UserCancellation_ReturnsCancelledState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // User cancelled
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.Cancelled,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.UserCancelled.Should().BeTrue();
        result.AuthenticationSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ContinueDetectionAsync_SessionExpiry_ReturnsExpiredState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // Session expired during authentication
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.Expired,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.SessionExpired.Should().BeTrue();
        result.AuthenticationSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ContinueDetectionAsync_AuthenticationFailure_ReturnsFailedState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // Authentication failed
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.Failed,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15),
                FailureCategory: "invalid_credentials"));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.AuthenticationSucceeded.Should().BeFalse();
        result.AuthenticationFailureReason.Should().Be(AuthenticationFailureReason.InvalidCredentials);
    }

    [Fact]
    public async Task ContinueDetectionAsync_UnexpectedOrigin_ReturnsFailedState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // Unexpected origin after login
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.UnexpectedOrigin,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.UnexpectedOriginEncountered.Should().BeTrue();
        result.AuthenticationSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ContinueDetectionAsync_McasAwaitingUserContinuation_ReturnsPartialState()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15)));

        // Session awaiting user continuation (MCAS interstitial)
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AwaitingUserContinuation,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15),
                DeliveryContext: AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.AwaitingUserContinuation.Should().BeTrue();
        result.AuthenticationSucceeded.Should().BeTrue(); // Partial success
        result.IsFullCompletion.Should().BeFalse();
        result.ResultingState.Should().Be(TargetDetectionState.Partial);
    }

    [Fact]
    public async Task ContinueDetectionAsync_InvalidTargetUrl_ReturnsFailure()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync("not-a-valid-url", FakeReviewSessionId, FakeProfileId);

        // Assert
        result.AuthenticationSucceeded.Should().BeFalse();
        result.AuthenticationFailureReason.Should().Be(AuthenticationFailureReason.GenericFailure);
    }

    [Fact]
    public async Task ContinueDetectionAsync_BrowserLaunchFailure_ReturnsFailure()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                "session-789", FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.Failed,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15),
                FailureCategory: "browser_launch_failed"));

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act
        var result = await strategy.ContinueDetectionAsync(FakeTargetUrl, FakeReviewSessionId, FakeProfileId);

        // Assert
        result.AuthenticationSucceeded.Should().BeFalse();
        result.AuthenticationFailureReason.Should().Be(AuthenticationFailureReason.BrowserResourceFailure);
    }

    [Fact]
    public async Task ContinueDetectionAsync_NavigationTimeout_ReturnsTimeout()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var sessionId = "session-789";

        sessionManager
            .Setup(m => m.StartAsync(It.IsAny<AuthenticatedBrowserSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.BrowserReady,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5)));

        sessionManager
            .Setup(m => m.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5)));

        // Simulate timeout - never returns authenticated
        sessionManager
            .Setup(m => m.GetStatusAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedBrowserSessionDescriptor(
                sessionId, FakeReviewSessionId, FakeProfileId, "https://app.example.com",
                AuthenticatedBrowserSessionStatus.AuthenticationInProgress,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5)));

        sessionManager
            .Setup(m => m.CancelAsync(sessionId, FakeReviewSessionId, FakeProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act - use a very short timeout
        var result = await strategy.ContinueDetectionAsync(
            FakeTargetUrl, FakeReviewSessionId, FakeProfileId, TimeSpan.FromMilliseconds(100));

        // Assert
        result.AuthenticationSucceeded.Should().BeFalse();
        result.AuthenticationFailureReason.Should().Be(AuthenticationFailureReason.NavigationTimeout);
    }

    [Fact]
    public async Task ContinueDetectionAsync_StrategyName_IsCorrect()
    {
        // Arrange
        var sessionManager = new Mock<IAuthenticatedBrowserSessionManager>();
        var strategy = new InteractiveBrowserDetectionStrategy(sessionManager.Object, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);

        // Act & Assert
        strategy.StrategyName.Should().Be("interactive-browser");
    }
}
