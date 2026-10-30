using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// API Quality Review capability-level readiness: an expired or missing authenticated session only skips the active authenticated checks; the
/// static contract review, public checks and stored runtime evidence stay available, and nothing unexecuted is presented as run or verified.
/// </summary>
public sealed class ApiReviewCapabilitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static FrontendAnalysisContext Context(string id = "dev")
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = id, EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.test/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = "https://app.example.test/", ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, id, "FP") };
    }

    private static ApiReviewTarget Protected(string id = "a", string? contract = "https://api.example.test/swagger/v1/swagger.json", DateTimeOffset? observedAt = null) => new()
    {
        TargetId = id, ApiType = ApiReviewTargetType.Rest, Host = "api.example.test", BasePath = "/api", ServiceName = id, AuthRequired = true, Selected = true, ContractSource = contract,
        Operations = observedAt is { } at ? [new ApiReviewOperation { Method = "GET", Path = "/api/versions", LastStatus = 200, AuthObserved = true, ObservedCount = 3, LastObservedAt = at, Source = ApiReviewTargetSource.DiscoveredTraffic }] : [],
    };

    private static AuthenticatedReviewCapabilities Live(AuthenticatedApiContextStatus status, bool authenticated = false) =>
        new() { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = status, PublicApi = true, AuthenticatedApi = authenticated, AuthenticatedRest = authenticated };

    private static ApiReviewCapability Cap(IReadOnlyList<ApiReviewCapability> all, string key) => all.Single(c => c.Key == key);

    private static IReadOnlyList<ApiReviewCapability> Evaluate(ApiReviewTarget target, AuthenticatedReviewCapabilities live, RuntimeAuthenticationResolution? auth = null, bool? registered = null, string? reason = null) =>
        ApiReviewCapabilities.Evaluate([target], live, auth, "/auth", registered, reason, Now);

    [Fact]
    public void ExpiredSession_WithOpenApi_RunsTheStaticReview_AndOffersReauthentication()
    {
        var target = Protected();
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), [target], ["a"], Live(AuthenticatedApiContextStatus.Expired));
        eligibility.Enabled.Should().BeTrue("the contract review needs no authentication");
        eligibility.Reason.Should().StartWith(ApiReviewRunEligibility.StaticOnlyPrefix).And.Contain("authentication expired");
        eligibility.ActionText.Should().Be("Re-authenticate");

        var caps = Evaluate(target, Live(AuthenticatedApiContextStatus.Expired));
        Cap(caps, ApiReviewCapabilities.Static).State.Should().Be(ApiReviewCapabilityState.Ready);
        Cap(caps, ApiReviewCapabilities.Active).State.Should().Be(ApiReviewCapabilityState.AuthenticationExpired);
        Cap(caps, ApiReviewCapabilities.Active).ActionText.Should().Be("Re-authenticate");
        Cap(caps, ApiReviewCapabilities.Active).Runs.Should().BeFalse();
    }

    [Fact]
    public void ExpiredSession_WithHistoricalObservations_KeepsThemAvailable_MarkedStale_NotAsCurrentAccess()
    {
        var target = Protected(contract: null, observedAt: Now.AddDays(-10));
        var caps = Evaluate(target, Live(AuthenticatedApiContextStatus.Expired));
        var runtime = Cap(caps, ApiReviewCapabilities.Runtime);
        runtime.State.Should().Be(ApiReviewCapabilityState.Available);
        runtime.Detail.Should().Contain("(stale)").And.Contain("not verified").And.Contain("not current access");
        Cap(caps, ApiReviewCapabilities.Active).Runs.Should().BeFalse();
    }

    [Fact]
    public void MissingAuthentication_AndNoStaticEvidence_DisablesRun_WithTheAction()
    {
        var target = Protected(contract: null);
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), [target], ["a"], Live(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic));
        eligibility.Enabled.Should().BeFalse();
        eligibility.Reason.Should().Be(ApiReviewRunEligibility.NoAuthContextReason);
        var caps = Evaluate(target, Live(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic));
        caps.Where(c => c.Runs).Should().BeEmpty();
        Cap(caps, ApiReviewCapabilities.Active).State.Should().Be(ApiReviewCapabilityState.AuthenticationRequired);
    }

    [Fact]
    public void ValidAttendedSession_RunsActiveChecks()
    {
        var caps = Evaluate(Protected(), Live(AuthenticatedApiContextStatus.Available, authenticated: true));
        Cap(caps, ApiReviewCapabilities.Active).State.Should().Be(ApiReviewCapabilityState.Ready);
        ApiReviewRunEligibility.Evaluate(Context(), [Protected()], ["a"], Live(AuthenticatedApiContextStatus.Available, true)).Reason.Should().Be("1 target ready.");
    }

    [Fact]
    public void SelectedUnattendedOrNone_NeverRunsActiveChecks_EvenWithALiveContext()
    {
        var live = Live(AuthenticatedApiContextStatus.Available, authenticated: true);
        var unattended = new RuntimeAuthenticationResolution { Effective = RuntimeAuthenticationMode.Unattended, State = RuntimeAuthenticationState.Unsupported };
        Cap(Evaluate(Protected(), live, unattended), ApiReviewCapabilities.Active).State.Should().Be(ApiReviewCapabilityState.NotAvailable);
        var none = new RuntimeAuthenticationResolution { Effective = RuntimeAuthenticationMode.None, State = RuntimeAuthenticationState.NotRequired };
        var skipped = Cap(Evaluate(Protected(), live, none), ApiReviewCapabilities.Active);
        skipped.State.Should().Be(ApiReviewCapabilityState.NotSelected);
        skipped.Detail.Should().Contain("skipped, not passed");
    }

    [Fact]
    public void AuthorizationChecks_BlockedByTrustedTargetPolicy_ArePermissionRequired_AndOptional()
    {
        var caps = Evaluate(Protected(), Live(AuthenticatedApiContextStatus.Available, true), registered: false, reason: "Not registered: SecurityTesting:TrustedTargets:dev.");
        var authz = Cap(caps, ApiReviewCapabilities.Authorization);
        authz.State.Should().Be(ApiReviewCapabilityState.PermissionRequired);
        authz.Label.Should().Contain("optional");
        authz.Detail.Should().Contain("TrustedTargets");
        Cap(caps, ApiReviewCapabilities.Static).Runs.Should().BeTrue("a missing server registration never blocks the read-only review");
    }

    [Fact]
    public void RunRequest_StaysWithTheActiveEnvironment_AndCarriesTheSelectedMode()
    {
        var context = Context("qa");
        var request = ApiQualityReview.BuildRequest(context, context.ReviewIdentity!, [Protected()], ["a"], null) with { AuthenticationMode = RuntimeAuthenticationMode.None };
        request.Environment.EnvironmentId.Should().Be("qa");
        request.Identity.ProfileId.Should().Be("qa");
        request.AuthenticationMode.Should().Be(RuntimeAuthenticationMode.None);
    }
}
