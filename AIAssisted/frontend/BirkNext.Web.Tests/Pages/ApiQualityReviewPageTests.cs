using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The API Quality Review page binds to the ACTIVE Target Environment, lists API targets from Endpoint Discovery + configuration,
/// enables Run only when a runnable target is selected (with an explicit reason and action otherwise), fails fast when only
/// authenticated targets exist without a proxy context, keeps the review snapshot immutable, and offers "Analyze in API Review" from
/// Endpoint Discovery.
/// </summary>
public sealed class ApiQualityReviewPageTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private static FrontendAnalysisContext Context(string id = "dev", string? rest = null, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy, bool requiresAuth = false)
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = id == "dev" ? "M2LB DEV" : id.ToUpperInvariant(), EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", RestBaseUrl = rest };
        profile.Authentication.AuthenticatedTestingMethod = method;
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = Origin + "/", RestBaseUrl = rest, RequiresAuthentication = requiresAuth, ReviewIdentity = new AuthenticatedReviewIdentity(method, id, "FP") };
    }

    private static ObservedNetworkEndpoint Ep(string path, bool auth, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, GraphQlOperationType op = GraphQlOperationType.None, string? name = null) => new()
    {
        Category = cat, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443, Path = path, Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : "GET", AuthObserved = auth, LastStatus = 200, Count = 4,
        FirstObservedAt = T0, LastObservedAt = T0, Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/barn/1", OperationType = op, OperationName = name,
    };

    private Mock<IApiReviewService> Register(FrontendAnalysisContext context, bool authenticated, params ObservedNetworkEndpoint[] endpoints)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        var discovery = new EndpointDiscoveryService();
        if (endpoints.Length > 0) discovery.MergeObservedAsync(new Mock<IJSRuntime>().Object, context.ActiveProfile.Id, endpoints).GetAwaiter().GetResult();
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedReviewCapabilities { Method = context.ReviewIdentity!.Method, PublicApi = true, AuthenticatedApi = authenticated, AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated, Reason = authenticated ? "Authenticated via Local HTTPS Proxy" : "Start the local proxy" });
        var review = new Mock<IApiReviewService>();
        review.Setup(r => r.RunAsync(It.IsAny<ApiReviewRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ApiReviewRunRequest request, CancellationToken _) => Task.FromResult<(ApiReviewReport?, string?)>((new ApiReviewReport
            {
                Environment = request.Environment, Policy = request.Policy, GeneratedAt = DateTimeOffset.UtcNow, Access = new AuthenticatedReviewCapabilities { AuthenticatedApi = authenticated },
                Targets = request.Targets.Select(t => new ApiReviewTargetResult { Target = t, AccessMode = t.AuthRequired ? (authenticated ? ApiReviewAccessMode.AuthenticatedHttp : ApiReviewAccessMode.Unavailable) : ApiReviewAccessMode.PublicHttp,
                    Status = t.AuthRequired && !authenticated ? ApiReviewTargetStatus.Blocked : ApiReviewTargetStatus.Completed, FindingCount = t.AuthRequired && !authenticated ? null : 0, AccessReason = "r", RequiredAction = t.AuthRequired && !authenticated ? ApiReviewRunEligibility.NoAuthContextAction : null }).ToList(),
            }, null)));
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService>(discovery);
        Services.AddSingleton<IApiReviewHistoryService, ApiReviewHistoryService>();
        Services.AddSingleton(caps.Object);
        Services.AddSingleton(review.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        return review;
    }

    private static IElement RunButton(IRenderedComponent<ApiQualityReview> page) => page.Find("[data-testid=aqr-run-row] button");

    [Fact]
    public void NoTargets_RunDisabled_WithReasonAndDiscoveryAction()
    {
        Register(Context(), authenticated: false);
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-no-targets]").TextContent.Should().Contain(ApiReviewRunEligibility.NoTargetsReason));
        RunButton(page).HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Be(ApiReviewRunEligibility.NoTargetsReason);
        page.Find("[data-testid=aqr-run-action]").GetAttribute("href").Should().Be(ApiReviewRunEligibility.TargetEnvironmentsHref);
        page.Find("[data-testid=aqr-env-name]").TextContent.Should().Be("M2LB DEV");
    }

    [Fact]
    public void RootCause_DiscoveredRestTargetWithoutConfiguredUrls_EnablesRun()
    {
        // Regression for the disabled Run button: no RestBaseUrl/GraphQlEndpoint/Swagger/Health configured, but Endpoint Discovery has a verified public REST endpoint.
        var context = Context(method: AuthenticatedTestingMethod.ManagedEdgeCdp);
        Register(context, authenticated: false, Ep("/api/public/status", auth: false));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target]").Should().ContainSingle());
        context.HasRestBaseUrl.Should().BeFalse();
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Contain("1 target(s) ready");
        page.Find("[data-testid=aqr-target-checkbox]").HasAttribute("checked").Should().BeTrue("verified targets are selected by default");
    }

    [Fact]
    public void AuthOnlyTargets_WithoutProxyContext_RunDisabledFastWithAction()
    {
        Register(Context(), authenticated: false, Ep("/api/children", auth: true), Ep("/api/graphql-v2", auth: true, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "GetChildren"));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Be(ApiReviewRunEligibility.NoAuthContextReason));
        RunButton(page).HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-run-action]").TextContent.Should().Be(ApiReviewRunEligibility.NoAuthContextAction);
        page.Find("[data-testid=aqr-auth-missing]").TextContent.Should().Contain("Local HTTPS Proxy");
        page.FindAll("[data-testid=aqr-target]").Should().HaveCount(2).And.OnlyContain(t => t.GetAttribute("data-auth") == "true");
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Public HTTP only");
    }

    [Fact]
    public async Task AuthTargets_WithProxyContext_RunEnabled_ReviewRunsWithSnapshot_ResultsShown()
    {
        var context = Context(requiresAuth: true);
        var review = Register(context, authenticated: true, Ep("/api/children", auth: true), Ep("/api/graphql-v2", auth: true, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "GetChildren"));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Authenticated HTTP via Local HTTPS Proxy");

        await page.InvokeAsync(() => RunButton(page).Click());

        page.WaitForAssertion(() => page.Find("[data-testid=aqr-results]"));
        review.Verify(r => r.RunAsync(It.Is<ApiReviewRunRequest>(q => q.Environment.EnvironmentId == "dev" && q.Environment.Name == "M2LB DEV" && q.Targets.Count == 2 && q.Identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy && q.Policy.ReadOnly), It.IsAny<CancellationToken>()), Times.Once);
        page.Find("[data-testid=aqr-result-env]").TextContent.Should().Be("M2LB DEV");
        page.FindAll("[data-testid=aqr-service-row]").Should().HaveCount(2);
        page.FindAll("[data-testid=aqr-service-status]").Should().OnlyContain(e => e.TextContent == "Completed");
        page.FindAll("[data-testid=aqr-service-findings]").Should().OnlyContain(e => e.TextContent == "0");
        page.Find("[data-testid=aqr-result-access]").TextContent.Should().Contain("Authenticated HTTP via Local HTTPS Proxy");
        page.Find("[data-testid=aqr-tab-findings]").Click();
        page.Find("[data-testid=aqr-no-findings]").TextContent.Should().Contain("never as passes");
    }

    [Fact]
    public async Task MixedTargets_WithoutContext_PublicRuns_AuthBlockedNotTested()
    {
        Register(Context(), authenticated: false, Ep("/api/children", auth: true), Ep("/api/public/status", auth: false));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Contain("1 of 2");
        await page.InvokeAsync(() => RunButton(page).Click());
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-service-status]").Should().HaveCount(2));
        page.FindAll("[data-testid=aqr-service-status]").Select(e => e.TextContent).Should().BeEquivalentTo(["Completed", "Blocked"]);
        page.FindAll("[data-testid=aqr-service-findings]").Select(e => e.TextContent).Should().BeEquivalentTo(["0", "Not tested"]);
    }

    [Fact]
    public void QueryParameter_PreselectsTheAnalyzedTarget_EvenWhenNotSelectedByDefault()
    {
        var candidate = Ep("/internal/config", auth: false) with { Confidence = ObservedEndpointConfidence.Candidate };
        Register(Context(method: AuthenticatedTestingMethod.ManagedEdgeCdp), authenticated: false, candidate);
        var id = ApiReviewTargetResolver.Id(ApiReviewTargetType.Rest, "https://api-dev.bufetat.no", "/internal/config");
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/api-quality-review?target={id}");
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-target-checkbox]").HasAttribute("checked").Should().BeTrue());
        RunButton(page).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void EndpointDiscovery_OffersAnalyzeInApiReview_WithSafeTargetReference()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", TargetUrl = Origin + "/" };
        var endpoint = Ep("/api/children", auth: true) with { Path = "/api/children" };
        var status = new LocalHttpsProxyStatus { SessionId = "s", State = LocalHttpsProxyState.Ready, ObservedNetworkEndpoints = [endpoint] };
        var cut = Render<EndpointDiscoveryTab>(p => p.Add(x => x.Profile, profile).Add(x => x.ProxyStatus, status));
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        cut.Find(".ed-expand").Click();
        var link = cut.Find("[data-testid=discovery-analyze-api]");
        link.GetAttribute("href").Should().Be($"/api-quality-review?target={ApiReviewTargetResolver.Id(ApiReviewTargetType.Rest, "https://api-dev.bufetat.no", "/api/children")}");
        link.GetAttribute("href").Should().NotContainAny("?token", "Bearer", "api-dev.bufetat.no", "children");
    }
}
