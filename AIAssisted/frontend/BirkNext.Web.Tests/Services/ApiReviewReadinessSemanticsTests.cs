using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// API Quality Review readiness describes what the review can execute, never what it will find.
///
/// "Ready to review" is a claim of complete selected scope. It was made whenever the authenticated context was fine,
/// even with no REST contract to validate against — so a review that could not run contract validation at all still
/// announced itself as ready. A limitation is now anything that removes a check the selected scope would otherwise run.
/// </summary>
public sealed class ApiReviewReadinessSemanticsTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/",
            ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP"),
        };
    }

    private static ApiReviewTarget Target(ApiReviewTargetType type, string basePath, bool authRequired = true, string? contract = null) => new()
    {
        TargetId = $"{type}:{basePath}", EnvironmentId = "dev", ApiType = type, Scheme = "https", Host = "m2lbdev.bufetat.no", Port = 443,
        BasePath = basePath, ServiceName = basePath, Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = authRequired,
        DiscoveredAt = T0, Confidence = ObservedEndpointConfidence.Verified, ContractSource = contract, Selected = true,
        Operations = [new ApiReviewOperation { Method = "GET", Path = basePath, Source = ApiReviewTargetSource.DiscoveredTraffic, Confidence = ObservedEndpointConfidence.Verified, AuthObserved = authRequired }],
    };

    private static AuthenticatedReviewCapabilities Capabilities(bool authenticated) => new()
    {
        AuthenticatedApi = authenticated,
        ContextStatus = authenticated ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic,
        AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated,
    };

    private static readonly ApiReviewRunEligibility Runnable = new(true, "The review runs read-only against the selected targets.", null, null);

    private static ApiReviewReadinessModel Readiness(IReadOnlyList<ApiReviewTarget> targets, bool authenticated, ApiReviewHistory? history = null, ApiReviewReport? lastReport = null)
    {
        var selected = targets.Select(t => t.TargetId).ToList();
        var contracts = ApiReviewPresentation.Contracts(targets, selected, history, lastReport);
        return ApiReviewPresentation.Readiness(Runnable, Context(), targets, selected, Capabilities(authenticated), contracts);
    }

    // 19.
    [Fact]
    public void EverythingTheSelectedScopeNeedsIsPresent_ReadyToReview()
    {
        // A REST target with a published contract, a GraphQL target, authenticated access, and a baseline to compare with.
        var targets = new List<ApiReviewTarget>
        {
            Target(ApiReviewTargetType.Rest, "/api/autorisasjon", contract: "https://m2lbdev.bufetat.no/swagger/v1/swagger.json"),
            Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql"),
        };
        var history = new ApiReviewHistory { Baselines = targets.ToDictionary(t => t.TargetId, _ => new ApiReviewBaseline()) };

        var readiness = Readiness(targets, authenticated: true, history);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Ready);
        readiness.Title.Should().Be("Ready to review");
        readiness.SelectedCount.Should().Be(2);
        readiness.CanRun.Should().BeTrue();
    }

    // 20, 23.
    [Fact]
    public void ARestTargetWithoutAPublishedContract_RunsWithLimitations_ButRestIsNotUnavailable()
    {
        var targets = new List<ApiReviewTarget>
        {
            Target(ApiReviewTargetType.Rest, "/api/autorisasjon"),
            Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql"),
        };

        var readiness = Readiness(targets, authenticated: true);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Limited);
        readiness.Title.Should().Be("Review can run with limitations");
        readiness.Message.Should().Contain("No published REST contract");
        readiness.CanRun.Should().BeTrue("a missing contract limits the review, it does not block it");
        readiness.Items.Should().Contain(i => i.Label.Contains("responses are reviewed structurally") && i.State == ApiReviewReadinessItemState.Warning);

        // REST is Limited, never Unavailable: routes, statuses, headers and structure are still reviewable.
        var contracts = ApiReviewPresentation.Contracts(targets, targets.Select(t => t.TargetId).ToList(), null, null);
        contracts.Rows.Single(r => r.Label == "REST").State.Should().Be(ApiReviewContractState.NotConfigured);
        contracts.Rows.Single(r => r.Label == "REST").Detail.Should().Contain("can still be reviewed structurally");
    }

    /// <summary>
    /// The documented baseline/optional rule: a first review has nothing to compare against, so drift comparison is not
    /// reduced, it is not yet applicable. It is stated as an item so the reader knows why no drift result appears, but it
    /// never on its own makes a review "limited" — otherwise every environment would start out limited forever.
    /// </summary>
    // 21.
    [Fact]
    public void NoPreviousBaselineAloneIsStatedButDoesNotLimitTheReview()
    {
        var targets = new List<ApiReviewTarget>
        {
            Target(ApiReviewTargetType.Rest, "/api/autorisasjon", contract: "https://m2lbdev.bufetat.no/swagger/v1/swagger.json"),
        };

        var readiness = Readiness(targets, authenticated: true, history: null);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Ready);
        readiness.Items.Should().Contain(i => i.Label.Contains("No previous baseline") && i.State == ApiReviewReadinessItemState.Missing);
        readiness.Message.Should().NotContain("baseline", "an absent first baseline is not a limitation of this review");
    }

    // 22.
    [Fact]
    public void AnAuthenticatedTargetWithoutAuthenticatedAccess_RunsWithLimitationsAndOffersTheFix()
    {
        var targets = new List<ApiReviewTarget> { Target(ApiReviewTargetType.Rest, "/api/autorisasjon", contract: "https://x/swagger.json") };

        var readiness = Readiness(targets, authenticated: false);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Limited);
        readiness.Title.Should().Be("Review can run with limitations");
        readiness.Message.Should().Contain("Authenticated requests cannot be sent");
        readiness.ActionText.Should().Be("Open Authentication setup");
        readiness.ActionHref.Should().Be("/admin/system-settings?section=target-environments&tab=auth&profile=dev");
        readiness.CanRun.Should().BeTrue();
    }

    /// <summary>Several missing things are several limitations, and none of them blocks the run.</summary>
    [Fact]
    public void LimitationsAccumulateWithoutBlocking()
    {
        var targets = new List<ApiReviewTarget>
        {
            Target(ApiReviewTargetType.Rest, "/api/autorisasjon"),
            Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql"),
        };

        var readiness = Readiness(targets, authenticated: false);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Limited);
        readiness.Message.Should().Contain("Authenticated requests cannot be sent").And.Contain("No published REST contract");
        readiness.CanRun.Should().BeTrue();
    }

    /// <summary>Blocked stays reserved for a review that genuinely cannot start.</summary>
    [Fact]
    public void OnlyAnUnrunnableReviewIsBlocked()
    {
        var blocked = new ApiReviewRunEligibility(false, "Select at least one API target.", "Select targets", null);

        var readiness = ApiReviewPresentation.Readiness(blocked, Context(), [], [], Capabilities(true), null);

        readiness.Level.Should().Be(ApiReviewReadinessLevel.Blocked);
        readiness.CanRun.Should().BeFalse();
    }

    // 44. Pre-run describes capability and scope, never an outcome.
    [Fact]
    public void ReadinessNeverClaimsAnOutcome()
    {
        var targets = new List<ApiReviewTarget>
        {
            Target(ApiReviewTargetType.Rest, "/api/autorisasjon"),
            Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql"),
        };

        var readiness = Readiness(targets, authenticated: true);
        var text = readiness.Title + " " + readiness.Message + " " + string.Join(" ", readiness.Items.Select(i => i.Label));

        foreach (var verdict in new[] { "Passed", "Failed", "Compliant", "Secure", "Fully covered", "All APIs available", "All contracts available" })
            text.Should().NotContain(verdict);
    }
}
