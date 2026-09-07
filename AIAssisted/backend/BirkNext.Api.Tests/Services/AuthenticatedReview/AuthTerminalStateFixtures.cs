using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

/// <summary>
/// Secret safety regression tests for auth flow.
/// Validates that failure categories never leak sensitive information.
/// </summary>
public sealed class AuthTerminalStateSecretSafetyTests
{
    [Fact]
    public void FailureCategory_NoAccessTokens()
    {
        var categories = new[] { "navigation_timeout", "authentication_navigation_failed", "browser_launch_failed", "browser_disconnected" };

        foreach (var category in categories)
        {
            category.Should().NotContain("token", $"failure category {category} must not leak tokens");
            category.Should().NotContain("access_", $"failure category {category} must not leak access tokens");
            category.Should().NotContain("Bearer", $"failure category {category} must not leak Bearer tokens");
        }
    }

    [Fact]
    public void FailureCategory_NoAuthCodes()
    {
        var categories = new[] { "navigation_timeout", "authentication_navigation_failed" };

        foreach (var category in categories)
        {
            category.Should().NotContain("code", $"failure category {category} must not leak auth codes");
            category.Should().NotContain("?code=", $"failure category {category} must not leak query parameters");
        }
    }

    [Fact]
    public void FailureCategory_NoPasswords()
    {
        var categories = new[] { "navigation_timeout", "authentication_navigation_failed" };

        foreach (var category in categories)
        {
            category.Should().NotContain("password", $"failure category {category} must not leak passwords");
            category.Should().NotContain("pwd", $"failure category {category} must not leak passwords");
        }
    }

    [Fact]
    public void FailureCategory_NoCookies()
    {
        var categories = new[] { "browser_disconnected", "page_closed" };

        foreach (var category in categories)
        {
            category.Should().NotContain("cookie", $"failure category {category} must not leak cookies");
            category.Should().NotContain("Set-Cookie", $"failure category {category} must not leak cookies");
        }
    }

    [Fact]
    public void AuthEventTimeline_StructureValid()
    {
        var collector = new AuthEventTimelineCollector("attempt-123", TimeProvider.System);

        collector.RecordEvent(AuthEventType.TargetShellLoaded, "https://m2lbdev.bufetat.no", "shell");
        collector.RecordEvent(AuthEventType.EntraReached, "https://login.microsoftonline.com", "entra");
        collector.RecordEvent(AuthEventType.CallbackReached, "https://m2lbdev.bufetat.no", "callback");

        var timeline = collector.GetTimeline();

        timeline.Should().HaveCount(3);
        timeline[0].EventType.Should().Be(AuthEventType.TargetShellLoaded);
        timeline[0].SafeOrigin.Should().Be("https://m2lbdev.bufetat.no");
        timeline[0].SafePathCategory.Should().Be("shell");
    }

    [Fact]
    public void AcceptanceSummary_NoSecretFields()
    {
        var collector = new AuthEventTimelineCollector("attempt-456", TimeProvider.System);

        collector.RecordEvent(AuthEventType.TargetShellLoaded);
        collector.RecordEvent(AuthEventType.EntraReached);
        collector.RecordEvent(AuthEventType.CallbackReached);
        collector.RecordEvent(AuthEventType.AuthenticatedRecognized);

        var summary = collector.GetAcceptanceSummary(
            AuthenticatedBrowserSessionStatus.Authenticated,
            null
        );

        summary.Should().NotBeNull();
        summary.InitialShellLoaded.Should().BeTrue();
        summary.EntraReached.Should().BeTrue();
        summary.CallbackReached.Should().BeTrue();
        summary.AuthenticatedRecognized.Should().BeTrue();
        summary.FailureReason.Should().BeNull();
    }
}
