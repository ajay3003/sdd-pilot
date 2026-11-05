using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The Target Application tab's next action is derived from actual state — no fixed sequence that blocks legitimate alternatives.</summary>
public sealed class TargetApplicationNextActionTests
{
    private static TargetEnvironmentDetectionResult Detected(bool auth = true, TargetReachability reach = TargetReachability.Reachable, bool success = true) => new()
    {
        Success = success, Reachability = reach, AuthenticationRequired = auth, DetectedAuthenticationType = auth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None,
    };

    [Theory]
    [InlineData(null, "https://a.example/", "application")]
    [InlineData("M2LB", null, "general")]
    public void Missing_application_or_url_comes_first(string? app, string? url, string action) =>
        TargetApplicationNextActions.Next(new(app, url, Detected(), false, false, false)).Action.Should().Be(action);

    [Fact]
    public void Unchecked_stale_failed_or_unreachable_targets_ask_for_a_check()
    {
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", null, false, false, false)).Action.Should().Be("check");
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(), true, false, false)).Label.Should().Be("Check target again");
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(success: false), false, false, false)).Action.Should().Be("check");
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(reach: TargetReachability.Timeout), false, false, false)).Action.Should().Be("check");
    }

    [Fact]
    public void Sign_in_required_without_a_session_continues_to_authentication_and_a_detected_provider_is_not_verification()
    {
        var next = TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(reach: TargetReachability.AuthenticationRequired), false, false, true));
        next.Action.Should().Be("auth");
        next.Reason.Should().Contain("no authenticated session");
        TargetApplicationNextActions.Reachability(Detected(reach: TargetReachability.AuthenticationRequired)).Should().Be("Reachable · sign-in required");
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(), false, true, true)).Action.Should().Be("endpoint");
        TargetApplicationNextActions.Next(new("M2LB", "https://a.example/", Detected(auth: false), false, false, false)).Action.Should().Be("endpoint");
    }

    [Fact]
    public void Reachability_reads_through_the_shared_vocabulary()
    {
        TargetApplicationNextActions.ReachabilityStatus(null).Should().Be(SemanticStatus.NotAssessed);
        TargetApplicationNextActions.ReachabilityStatus(Detected()).Should().Be(SemanticStatus.Available);
        TargetApplicationNextActions.ReachabilityStatus(Detected(reach: TargetReachability.Unreachable)).Should().Be(SemanticStatus.Unavailable);
        TargetApplicationNextActions.ReachabilityStatus(Detected(success: false)).Should().Be(SemanticStatus.Failed);
    }
}
