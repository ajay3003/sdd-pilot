using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Post-fix verification tests for browser continuation wiring.
/// Critical path: Controller endpoint → Service → Strategy → DTO → Response.
/// </summary>
public sealed class TargetEnvironmentDetectionControllerBrowserContinuationTests
{
    private readonly Mock<ITargetEnvironmentDetectionService> _mockDetectionService;
    private readonly Mock<ILogger<TargetEnvironmentDetectionController>> _mockLogger;
    private readonly TargetEnvironmentDetectionController _controller;

    public TargetEnvironmentDetectionControllerBrowserContinuationTests()
    {
        _mockDetectionService = new Mock<ITargetEnvironmentDetectionService>();
        _mockLogger = new Mock<ILogger<TargetEnvironmentDetectionController>>();
        _controller = new TargetEnvironmentDetectionController(_mockDetectionService.Object, _mockLogger.Object);
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IAuthenticatedBrowserSessionManager>())
            .AddSingleton<ILogger<InteractiveBrowserDetectionStrategy>>(NullLogger<InteractiveBrowserDetectionStrategy>.Instance)
            .BuildServiceProvider();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_ValidRequest_CallsDetectionServiceWithStrategy()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "session-123",
            ProfileId = "profile-qa"
        };

        var expectedOutcome = new TargetDetectionOutcome
        {
            State = TargetDetectionState.Complete,
            IsActivationReady = true,
            DetectionResponse = new TargetEnvironmentDetectionResponse
            {
                OriginalUrl = "https://m2lbdev.bufetat.no/",
                Success = true
            }
        };

        _mockDetectionService
            .Setup(x => x.DetectWithStrategyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ITargetDetectionAuthenticationStrategy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedOutcome);

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var outcome = Assert.IsType<TargetDetectionOutcome>(okResult.Value);
        Assert.Equal(TargetDetectionState.Complete, outcome.State);
        Assert.True(outcome.IsActivationReady);

        // Verify service was called exactly once with InteractiveBrowserDetectionStrategy
        _mockDetectionService.Verify(
            x => x.DetectWithStrategyAsync(
                "https://m2lbdev.bufetat.no/",
                "session-123",
                "profile-qa",
                It.IsAny<InteractiveBrowserDetectionStrategy>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_MissingTargetUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "",
            ReviewSessionId = "session-123",
            ProfileId = "profile-qa"
        };

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockDetectionService.Verify(x => x.DetectWithStrategyAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<ITargetDetectionAuthenticationStrategy>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_MissingSessionId_ReturnsBadRequest()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "",
            ProfileId = "profile-qa"
        };

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockDetectionService.Verify(x => x.DetectWithStrategyAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<ITargetDetectionAuthenticationStrategy>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_MissingProfileId_ReturnsBadRequest()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "session-123",
            ProfileId = ""
        };

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockDetectionService.Verify(x => x.DetectWithStrategyAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<ITargetDetectionAuthenticationStrategy>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_NullRequest_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ContinueDetectionInBrowser(null!, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_ServiceThrowsException_Returns500()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "session-123",
            ProfileId = "profile-qa"
        };

        _mockDetectionService
            .Setup(x => x.DetectWithStrategyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ITargetDetectionAuthenticationStrategy>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Strategy failed"));

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusResult.StatusCode);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_ServiceThrowsOperationCanceledException_Returns408()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "session-123",
            ProfileId = "profile-qa"
        };

        _mockDetectionService
            .Setup(x => x.DetectWithStrategyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ITargetDetectionAuthenticationStrategy>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Timeout"));

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(408, statusResult.StatusCode);
    }

    [Fact]
    public async Task ContinueDetectionInBrowser_PartialDetectionResult_ReturnsOutcome()
    {
        // Arrange
        var request = new BrowserDetectionRequest
        {
            TargetUrl = "https://m2lbdev.bufetat.no/",
            ReviewSessionId = "session-123",
            ProfileId = "profile-qa"
        };

        var expectedOutcome = new TargetDetectionOutcome
        {
            State = TargetDetectionState.Partial,
            IsActivationReady = false,
            StrategySuggestion = "MFA required but not completed"
        };

        _mockDetectionService
            .Setup(x => x.DetectWithStrategyAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ITargetDetectionAuthenticationStrategy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedOutcome);

        // Act
        var result = await _controller.ContinueDetectionInBrowser(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var outcome = Assert.IsType<TargetDetectionOutcome>(okResult.Value);
        Assert.Equal(TargetDetectionState.Partial, outcome.State);
        Assert.False(outcome.IsActivationReady);
    }
}
