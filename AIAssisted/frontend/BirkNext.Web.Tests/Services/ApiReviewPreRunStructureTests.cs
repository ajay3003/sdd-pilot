using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The AQR pre-run in separate answers: execution readiness (can the read-only review run), target coverage, known limitations (one list),
/// optional active checks (never blocking, each with a reason and an action) and technical setup. REST and GraphQL are judged on their own
/// evidence paths: a REST target without OpenAPI is "Runtime ready · Contract limited", a GraphQL runtime schema is not a validated contract.
/// </summary>
public sealed class ApiReviewPreRunStructureTests : BunitContext
{
    private static ApiReviewTarget Rest(bool contract = false, bool auth = true) => new()
    {
        TargetId = "rest", ApiType = ApiReviewTargetType.Rest, Host = "api.example.test", BasePath = "/api", ServiceName = "Autorisasjon API",
        AuthRequired = auth, ContractSource = contract ? "openapi.json" : null,
        Operations = [new ApiReviewOperation { Method = "GET", Path = "/api/roller" }, new ApiReviewOperation { Method = "GET", Path = "/api/roller/1" }],
    };

    private static ApiReviewTarget GraphQl() => new()
    {
        TargetId = "gql", ApiType = ApiReviewTargetType.GraphQl, Host = "api.example.test", BasePath = "/graphql", ServiceName = "Autorisasjon GraphQL",
        AuthRequired = true,
    };

    private static AuthorizationCheckItem Check(string key, AuthorizationCheckReadiness readiness, string reason) =>
        new(key, "Autorisasjon API", "rest", "GET /api/roller", "role:reader", AuthorizationExpectation.Deny, AuthorizationCheckOrigin.Manual,
            AuthorizationCheckConfirmation.Confirmed, readiness, reason, true, new AuthorizationScenario(), null);

    [Fact]
    public void RestWithoutContract_IsRuntimeReady_ContractLimited_AndRunnable()
    {
        var review = ApiReviewPreRunPresentation.Review(Rest(), ApiReviewAccessAvailability.Available, hasFallback: false, schemaUnavailablePreviously: false);
        review.AccessReady.Should().BeTrue("a missing contract never makes the runtime review unavailable");
        review.StatusLine.Should().Be("Runtime review available · Not available");
        review.State.Should().Be(ApiReviewTargetReviewState.Ready, "a missing contract is a coverage note, not a limitation");
        review.Binding!.CompatibilityLabel.Should().Be("Not assessed");
        review.WillRun.Should().Contain(["Security", "Error handling", "Response timing"]);
    }

    [Fact]
    public void GraphQlRuntimeSchema_IsLabelledRuntime_AndAMissingSchemaIsExplicit()
    {
        ApiReviewPreRunPresentation.Review(GraphQl(), ApiReviewAccessAvailability.Available, false, false).StatusLine
            .Should().Be("Runtime review available · Published schema not available · runtime schema via introspection", "never a schema claim before it is observed");
        var unavailableBefore = ApiReviewPreRunPresentation.Review(GraphQl(), ApiReviewAccessAvailability.Available, false, schemaUnavailablePreviously: true);
        unavailableBefore.ContractValue.Should().Be("Unavailable previously — introspection attempted again during review");
        unavailableBefore.State.Should().Be(ApiReviewTargetReviewState.Ready);
        ApiReviewPreRunPresentation.KnownLimitations([unavailableBefore], new AuthorizationCheckSummary([]))
            .Should().ContainSingle(l => l.Area.EndsWith("GraphQL schema") && l.Text.Contains("Published schema: Not available") && l.Text.Contains("not a published contract"));
    }

    [Fact]
    public void TargetCoverage_StatesActualCounts()
    {
        var reviews = new[] { Rest(), GraphQl() }.Select(t => ApiReviewPreRunPresentation.Review(t, ApiReviewAccessAvailability.Available, false, false)).ToList();
        ApiReviewPreRunPresentation.TargetCoverage(reviews).Should().Be("2 selected · 1 REST · 1 GraphQL · 2 authenticated");
    }

    /// <summary>Blocked optional authorization checks are a limitation with their exact reason — never a blocker of the read-only review.</summary>
    [Fact]
    public void BlockedAuthorizationChecks_AreAKnownLimitation_WithTheirSharedReason()
    {
        var authorization = new AuthorizationCheckSummary(
        [
            Check("a", AuthorizationCheckReadiness.NeedsIdentity, "No approved test identity is mapped for role reader."),
            Check("b", AuthorizationCheckReadiness.BlockedByExecutionPermission, "Active authorization checks are not permitted for this Target Environment."),
        ]);
        var reviews = new[] { ApiReviewPreRunPresentation.Review(Rest(), ApiReviewAccessAvailability.Available, false, false) };

        var summary = ApiReviewPreRunPresentation.AuthorizationBlockerSummary(authorization);
        summary.Should().Contain("1 ").And.Contain("No approved test identity is mapped for role reader").And.Contain("not permitted for this Target Environment");
        var limitations = ApiReviewPreRunPresentation.KnownLimitations(reviews, authorization);
        limitations.Should().Contain(l => l.Area == "Authorization checks" && l.Text == "Optional · not ready — non-blocking. " + summary);
        ApiReviewPreRunPresentation.ReadinessSummary(reviews, limitations)
            .Should().Be("1 API target is ready for read-only review. Contract coverage: partial (1 coverage note). Optional active checks unavailable — non-blocking.");
    }

    /// <summary>A blank reason from the shared catalog is never rendered as "— ."; unknown permission says it could not be confirmed.</summary>
    [Fact]
    public void BlankOrUnknownExecutionPermission_StillHasAReason()
    {
        ApiReviewPreRunPresentation.AuthorizationBlockerSummary(new AuthorizationCheckSummary([Check("a", AuthorizationCheckReadiness.BlockedByExecutionPermission, "")]))
            .Should().Be("1 blocked by execution permission.");
        AuthorizationCheckCatalog.ReadinessLabel(AuthorizationCheckReadiness.BlockedByExecutionPermission).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReadyAuthorizationChecks_AddNoLimitation()
    {
        var authorization = new AuthorizationCheckSummary([Check("a", AuthorizationCheckReadiness.Ready, "Ready.")]);
        var reviews = new[] { ApiReviewPreRunPresentation.Review(Rest(contract: true), ApiReviewAccessAvailability.Available, false, false) };
        ApiReviewPreRunPresentation.KnownLimitations(reviews, authorization).Should().BeEmpty();
        ApiReviewPreRunPresentation.ReadinessSummary(reviews, []).Should().Be("1 API target is ready for read-only review.");
    }

    private IRenderedComponent<ApiSafeFuzzingPanel> Panel(string name, FrontendEnvironmentType type)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(Mock.Of<IApiReviewService>());
        var history = new Mock<IApiReviewHistoryService>();
        history.Setup(h => h.For(It.IsAny<string>())).Returns(new ApiReviewHistory());
        Services.AddSingleton(history.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = name, EnvironmentType = type, TargetUrl = "https://app.example.test/" };
        return Render<ApiSafeFuzzingPanel>(p => p.Add(c => c.Context, new FrontendAnalysisContext { ActiveProfile = profile }));
    }

    [Fact]
    public void FuzzingEnvironment_IsShownOnce_WhenTheTypeWouldRepeatTheName()
    {
        var cut = Panel("Development", FrontendEnvironmentType.Development);
        cut.Find("[data-testid=aqr-fuzzing-environment]").TextContent.Trim().Should().Be("Development");
        cut.FindAll("[data-testid=aqr-fuzzing-environment-type]").Should().BeEmpty();
    }

    [Fact]
    public void FuzzingEnvironment_KeepsTheTypeBadge_WhenItAddsMeaning()
    {
        var cut = Panel("M2LB DEV", FrontendEnvironmentType.Development);
        cut.Find("[data-testid=aqr-fuzzing-environment-type]").TextContent.Should().Be("Development");
    }

    /// <summary>Off by default; the panel's level choice is the one setting, and Off never sends a request.</summary>
    [Fact]
    public void Fuzzing_IsOffByDefault()
    {
        var cut = Panel("M2LB DEV", FrontendEnvironmentType.Development);
        cut.Find("input[type=radio]:checked").GetAttribute("data-testid").Should().Contain("off", Exactly.Once());
    }
}
