using BirkNext.Api.Controllers;
using BirkNext.Api.Services.AuthenticatedReview;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Text.Json;

namespace BirkNext.Api.Tests.Controllers;

public sealed class AuthenticatedBrowserSessionControllerTests
{
    [Fact]
    public async Task Authenticate_StartsExistingOwnedSessionAndReturnsSafeStatus()
    {
        var manager = new Mock<IAuthenticatedBrowserSessionManager>();
        manager.Setup(x => x.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Descriptor(AuthenticatedBrowserSessionStatus.AuthenticationInProgress));
        var result = await new AuthenticatedBrowserSessionController(manager.Object).Authenticate(
            "opaque-session", new("review", "profile", "https://login.microsoftonline.com/tenant"), default);
        var response = Assert.IsType<AuthenticatedBrowserSessionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.Status.Should().Be(AuthenticatedBrowserSessionStatus.AuthenticationInProgress);
        response.TargetOrigin.Should().Be("https://app.example");
    }

    [Fact]
    public async Task Authenticate_WrongOwnerOrDisposedSession_ReturnsNotFound()
    {
        var manager = new Mock<IAuthenticatedBrowserSessionManager>();
        manager.Setup(x => x.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Collections.Generic.KeyNotFoundException());
        var result = await new AuthenticatedBrowserSessionController(manager.Object).Authenticate(
            "stale", new("wrong", "wrong", "https://login.microsoftonline.com"), default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Authenticate_UnsupportedDeployment_ReturnsConflict()
    {
        var manager = new Mock<IAuthenticatedBrowserSessionManager>();
        manager.Setup(x => x.BeginAuthenticationAsync(It.IsAny<BeginAuthenticationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticatedReviewUnavailableException("disabled"));
        var result = await new AuthenticatedBrowserSessionController(manager.Object).Authenticate(
            "opaque", new("review", "profile", "https://login.microsoftonline.com"), default);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Status_ProgressionPreservesSafeDeliveryMetadata()
    {
        var manager = new Mock<IAuthenticatedBrowserSessionManager>();
        manager.Setup(x => x.GetStatusAsync("opaque-session", "review", "profile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Descriptor(AuthenticatedBrowserSessionStatus.Authenticated, AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession, true));
        var result = await new AuthenticatedBrowserSessionController(manager.Object).Status("opaque-session", "review", "profile", default);
        var response = Assert.IsType<AuthenticatedBrowserSessionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.DeliveryContext.Should().Be(AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession);
        response.ApplicationValidationCurrent.Should().BeTrue();
    }

    [Fact]
    public void AuthApiDtos_DoNotSerializeIdentityOrSecretSentinels()
    {
        var response = new AuthenticatedBrowserSessionResponse("opaque", AuthenticatedBrowserSessionStatus.AwaitingUserContinuation, "https://app.example", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(45), null, AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession, false);
        var json = JsonSerializer.Serialize(response).ToLowerInvariant();
        foreach (var sentinel in new[] { "synthetic.user@example.test", "password-sentinel", "mfa-sentinel", "cookie-sentinel", "token-sentinel", "auth-code-sentinel", "state-sentinel", "nonce-sentinel" })
            json.Should().NotContain(sentinel);
        typeof(AuthenticatedBrowserSessionResponse).GetProperties().Select(p => p.Name).Should().NotContain(name =>
            new[] { "url", "email", "user", "cookie", "token", "storage", "authorization" }.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)) && name != "TargetOrigin");
    }

    private static AuthenticatedBrowserSessionDescriptor Descriptor(AuthenticatedBrowserSessionStatus status, AuthenticatedDeliveryContext delivery = AuthenticatedDeliveryContext.None, bool valid = false) =>
        new("opaque-session", "review", "profile", "https://app.example", status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(45), null, delivery, valid);
}
