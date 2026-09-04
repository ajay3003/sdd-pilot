using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Tests for TargetEnvironmentDetectionService with authentication strategy.
/// Tests integration between preflight detection and strategy-based continuation.
/// </summary>
public sealed class TargetEnvironmentDetectionServiceStrategyTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;
    private readonly FakeDnsResolver _resolver = new();

    public TargetEnvironmentDetectionServiceStrategyTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
        _resolver.Add("app.example.com", "203.0.113.1");
        _resolver.Add("spa.corp.invalid", "203.0.113.2");
        _resolver.Add("login.microsoftonline.com", "203.0.113.10");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_PrefailureBeforeStrategy_ReturnsFailedOutcome()
    {
        // Arrange - preflight fails, strategy should not be called
        var handler = DetectionFixtures.TimeoutTarget(); // Fails preflight
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
        // Strategy should not be called
        mockStrategy.Verify(s => s.ContinueDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectWithStrategyAsync_PrefailureNoAuthRequired_SkipsStrategy()
    {
        // Arrange - preflight succeeds without auth requirement, strategy not called
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Complete);
        outcome.IsActivationReady.Should().BeTrue();
        // Strategy should not be called since auth is not required
        mockStrategy.Verify(s => s.ContinueDetectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectWithStrategyAsync_ReachableBlazorSpaNeedingBrowserInspection_InvokesStrategy()
    {
        var handler = DetectionFixtures.BlazorWasmTarget();
        var service = new TargetEnvironmentDetectionService(
            _validator,
            new HttpClient(handler),
            _resolver,
            new ClientFrameworkDetector(),
            _logger);
        var strategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        strategy.Setup(s => s.ContinueDetectionAsync(
                "https://spa.corp.invalid",
                "review-123",
                "profile-456",
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = true,
                IsFullCompletion = true,
                ResultingState = TargetDetectionState.Complete
            });

        var outcome = await service.DetectWithStrategyAsync(
            "https://spa.corp.invalid",
            "review-123",
            "profile-456",
            strategy.Object);

        outcome.State.Should().Be(TargetDetectionState.Complete);
        strategy.Verify(s => s.ContinueDetectionAsync(
            "https://spa.corp.invalid",
            "review-123",
            "profile-456",
            It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategySucceeds_ReturnsCompleteOutcome()
    {
        // Arrange - preflight identifies auth required, strategy succeeds
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = true,
                IsFullCompletion = true,
                ResultingState = TargetDetectionState.Complete,
                DeliveryContext = AuthenticatedDeliveryContext.DirectApplication,
                Duration = TimeSpan.FromSeconds(5)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Complete);
        outcome.IsActivationReady.Should().BeTrue();
        outcome.DetectionResponse.Success.Should().BeTrue();
        outcome.Message.Should().Contain("Authentication succeeded");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategyPartial_ReturnsPartialOutcome()
    {
        // Arrange - preflight identifies auth required, strategy succeeds partially (MCAS interstitial)
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = true,
                AwaitingUserContinuation = true,
                IsFullCompletion = false,
                ResultingState = TargetDetectionState.Partial,
                DeliveryContext = AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession,
                Duration = TimeSpan.FromSeconds(3)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Partial);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.Message.Should().ContainAny(new[] { "awaiting", "Awaiting" });
        outcome.StrategySuggestion.Should().Be("browser-automation-required");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategyCancelled_ReturnsCancelledOutcome()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                UserCancelled = true,
                Duration = TimeSpan.FromSeconds(2)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Partial);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.Message.Should().Contain("cancelled");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategyExpired_ReturnsExpiredOutcome()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                SessionExpired = true,
                Duration = TimeSpan.FromMinutes(15)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategyUnexpectedOrigin_ReturnsFailedOutcome()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                UnexpectedOriginEncountered = true,
                Duration = TimeSpan.FromSeconds(2)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.Message.Should().Contain("unexpected origin");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_AuthRequiredStrategyFails_ReturnsFailedOutcome()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = false,
                AuthenticationFailureReason = AuthenticationFailureReason.InvalidCredentials,
                Duration = TimeSpan.FromSeconds(2)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.Message.Should().Contain("Authentication failed");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_StrategyThrowsException_FailsClosed()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Browser crashed"));

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
        outcome.DetectionResponse.Success.Should().BeFalse();
    }

    [Fact]
    public async Task DetectWithStrategyAsync_PreflightDetectsEntraMetadata_StrategyReceivesCorrectUrl()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var capturedTargetUrl = "";
        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback((string url, string _, string _, TimeSpan? _, CancellationToken _) => capturedTargetUrl = url)
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = true,
                IsFullCompletion = true,
                ResultingState = TargetDetectionState.Complete,
                Duration = TimeSpan.FromSeconds(5)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com/dashboard",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        capturedTargetUrl.Should().Be("https://app.example.com/dashboard");
        mockStrategy.Verify(s => s.ContinueDetectionAsync(
            "https://app.example.com/dashboard", "review-123", "profile-456", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectWithStrategyAsync_StrategyReceivesReviewSessionAndProfileId()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var capturedReviewSessionId = "";
        var capturedProfileId = "";
        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string reviewId, string profileId, TimeSpan? _, CancellationToken _) =>
            {
                capturedReviewSessionId = reviewId;
                capturedProfileId = profileId;
            })
            .ReturnsAsync(new DetectionContinuationResult { AuthenticationSucceeded = true, IsFullCompletion = true, ResultingState = TargetDetectionState.Complete, Duration = TimeSpan.FromSeconds(5) });

        // Act
        await service.DetectWithStrategyAsync(
            "https://app.example.com",
            "review-abc-123",
            "profile-xyz-789",
            mockStrategy.Object);

        // Assert
        capturedReviewSessionId.Should().Be("review-abc-123");
        capturedProfileId.Should().Be("profile-xyz-789");
    }

    [Fact]
    public async Task DetectWithStrategyAsync_OutcomeUrlsAreCorrect()
    {
        // Arrange
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var mockStrategy = new Mock<ITargetDetectionAuthenticationStrategy>();
        mockStrategy.Setup(s => s.ContinueDetectionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectionContinuationResult
            {
                AuthenticationSucceeded = true,
                IsFullCompletion = true,
                ResultingState = TargetDetectionState.Complete,
                Duration = TimeSpan.FromSeconds(5)
            });

        // Act
        var outcome = await service.DetectWithStrategyAsync(
            "https://app.example.com/dashboard",
            "review-123",
            "profile-456",
            mockStrategy.Object);

        // Assert
        outcome.DetectedUrl.Should().Be("https://app.example.com/dashboard");
        outcome.IsUrlCurrent.Should().BeTrue();
    }
}


