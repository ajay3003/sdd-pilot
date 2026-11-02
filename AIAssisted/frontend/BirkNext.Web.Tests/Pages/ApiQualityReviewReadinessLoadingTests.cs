using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
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
/// API Quality Review loads its own runtime evidence: the readiness never depends on Endpoint Discovery having been opened first, loading and
/// unknown are never Blocked, a slow dependency shows staged progress, a retrieval failure is "could not be fully assessed" with Retry, persisted
/// history is used when this browser holds nothing, freshness is Recent / Historical / Stale (never "missing"), and a refresh keeps the last
/// assessed readiness. Generic fixtures; no network.
/// </summary>
public sealed class ApiQualityReviewReadinessLoadingTests : BunitContext
{
    private const string Origin = "https://app.example.test";
    /// <summary>Generous under a loaded test host; the waits end as soon as the state is reached.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string ApiHost = "app.example.test";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", AuthenticationType = FrontendAuthenticationType.None,
            ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP"),
        };
    }

    private static ObservedNetworkEndpoint Ep(string path, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, string? name = null,
        ObservedEndpointConfidence confidence = ObservedEndpointConfidence.Verified, DateTimeOffset? seen = null) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = ApiHost, Port = 443, Path = path,
        Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : "GET", AuthObserved = true, LastStatus = 200, Count = 3,
        FirstObservedAt = seen ?? Now, LastObservedAt = seen ?? Now, Confidence = confidence, PageOrigin = Origin, PagePath = "/",
        OperationType = cat == ObservedTrafficCategory.GraphQl ? GraphQlOperationType.Query : GraphQlOperationType.None, OperationName = name,
    };

    private static ObservedNetworkEndpoint[] Traffic(ObservedEndpointConfidence confidence = ObservedEndpointConfidence.Verified) =>
        [Ep("/api/children", confidence: confidence), Ep("/api/graphql", ObservedTrafficCategory.GraphQl, "GetChildren", confidence)];

    private static Mock<ILocalHttpsProxyApiService> Proxy(Task<LocalHttpsProxyStatus> runtime)
    {
        var proxy = new Mock<ILocalHttpsProxyApiService>();
        proxy.Setup(p => p.GetRuntimeAsync()).Returns(runtime);
        return proxy;
    }

    private static LocalHttpsProxyStatus Live(params ObservedNetworkEndpoint[] observed) =>
        new() { RuntimeId = "rt", ProfileId = "dev", ContextFingerprint = "FP", State = LocalHttpsProxyState.Ready, ObservedNetworkEndpoints = observed };

    private IRenderedComponent<ApiQualityReview> Render(IEndpointDiscoveryService discovery, ILocalHttpsProxyApiService? proxy = null,
        IEndpointObservationHistoryApiService? history = null, bool authenticated = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context());
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, PublicApi = true, AuthenticatedApi = authenticated, AuthenticatedRest = authenticated,
            AuthenticatedGraphQlQuery = authenticated, ContextStatus = authenticated ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.NotApplicable,
        });
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(discovery);
        Services.AddSingleton<IApiReviewHistoryService>(new ApiReviewHistoryService());
        Services.AddSingleton(caps.Object);
        Services.AddSingleton<IGraphQlSchemaArtifactApiService>(new BirkNext.Web.Tests.Integration.FakeGraphQlSchemaArtifactApi());
        Services.AddSingleton(Mock.Of<IApiReviewService>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        if (proxy is not null) Services.AddSingleton(proxy);
        if (history is not null) Services.AddSingleton(history);
        return Render<ApiQualityReview>();
    }

    private static string Decision(IRenderedComponent<ApiQualityReview> page) => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-decision")!;

    private static (string Decision, string Readiness, int Targets) Final(IRenderedComponent<ApiQualityReview> page)
    {
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-decide]").Should().NotBeEmpty(), Wait);
        return (Decision(page), page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness")!, page.FindAll("[data-testid=aqr-target]").Count);
    }

    [Fact]
    public void Opening_API_Quality_Review_directly_gives_the_same_readiness_as_after_visiting_Endpoint_Discovery()
    {
        // A: fresh browser store; only the live capture (backend) holds the traffic — nothing was hydrated by Endpoint Discovery.
        var direct = Final(Render(new EndpointDiscoveryService(), Proxy(Task.FromResult(Live(Traffic()))).Object));

        // B: Endpoint Discovery was opened first and merged the same capture into the browser store.
        using var other = new BunitContext();
        var visited = new EndpointDiscoveryService();
        visited.MergeObservedAsync(new Mock<IJSRuntime>().Object, "dev", Traffic()).GetAwaiter().GetResult();
        var after = Final(RenderIn(other, visited));

        direct.Should().Be(after, "readiness must not depend on navigation order");
        direct.Targets.Should().Be(2);
        direct.Decision.Should().NotBe(nameof(ReviewDecision.Blocked));
    }

    private static IRenderedComponent<ApiQualityReview> RenderIn(BunitContext ctx, IEndpointDiscoveryService discovery)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context());
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true,
            ContextStatus = AuthenticatedApiContextStatus.Available,
        });
        ctx.Services.AddSingleton(factory.Object);
        ctx.Services.AddSingleton(discovery);
        ctx.Services.AddSingleton<IApiReviewHistoryService>(new ApiReviewHistoryService());
        ctx.Services.AddSingleton(caps.Object);
        ctx.Services.AddSingleton<IGraphQlSchemaArtifactApiService>(new BirkNext.Web.Tests.Integration.FakeGraphQlSchemaArtifactApi());
        ctx.Services.AddSingleton(Mock.Of<IApiReviewService>());
        ctx.Services.AddSingleton<RuntimeReviewSessionService>();
        ctx.Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        ctx.Services.AddSingleton(Mock.Of<IReportExportService>());
        return ctx.Render<ApiQualityReview>();
    }

    [Fact]
    public void A_delayed_evidence_query_shows_staged_assessing_progress_never_Blocked_then_the_final_readiness()
    {
        var pending = new TaskCompletionSource<LocalHttpsProxyStatus>();
        var page = Render(new EndpointDiscoveryService(), Proxy(pending.Task).Object);

        Decision(page).Should().Be(nameof(ReviewDecision.Assessing));
        var section = page.Find("[data-testid=aqr-readiness]");
        section.GetAttribute("role").Should().Be("status");
        section.GetAttribute("aria-live").Should().Be("polite", "the loading state is announced");
        section.TextContent.Should().Contain("Assessing API review readiness").And.NotContain("Blocked").And.NotContain("Cannot run");
        page.FindAll("[data-testid=aqr-assessment-stage]").Single(s => s.GetAttribute("data-stage") == "evidence").GetAttribute("data-state").Should().Be("Running");
        page.FindAll("[data-testid=aqr-assessment-stage]").Single(s => s.GetAttribute("data-stage") == "target").GetAttribute("data-state").Should().Be("Done");
        page.FindAll("[data-testid=aqr-blockers]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-run]").Should().BeEmpty("nothing to run before readiness is evaluated");

        pending.SetResult(Live(Traffic()));
        page.WaitForAssertion(() => Decision(page).Should().NotBe(nameof(ReviewDecision.Assessing)), Wait);
        Decision(page).Should().NotBe(nameof(ReviewDecision.Blocked));
        page.Find("[data-testid=aqr-runtime-evidence]").GetAttribute("data-source").Should().Be(nameof(RuntimeEvidenceSource.LiveCapture));
    }

    [Fact]
    public void An_evidence_retrieval_failure_is_could_not_be_fully_assessed_with_Retry_never_Blocked()
    {
        var discovery = new Mock<IEndpointDiscoveryService>();
        discovery.Setup(d => d.LoadAsync(It.IsAny<IJSRuntime>())).ThrowsAsync(new JSException("storage unavailable"));
        var page = Render(discovery.Object);

        page.WaitForAssertion(() => Decision(page).Should().Be(nameof(ReviewDecision.FailedToAssess)), Wait);
        page.Find("[data-testid=aqr-readiness]").TextContent.Should().Contain("Readiness could not be fully assessed").And.NotContain("Blocked");
        page.Find("[data-testid=aqr-assessment-error]").TextContent.Should().Contain("Nothing was evaluated as blocked").And.NotContain("storage unavailable", "diagnostics stay safe");
        page.Find("[data-testid=aqr-assessment-retry]").Should().NotBeNull();
        page.FindAll("[data-testid=aqr-assessment-stage]").Single(s => s.GetAttribute("data-stage") == "evidence").GetAttribute("data-state").Should().Be("Failed");
    }

    [Fact]
    public void Persisted_history_is_the_evidence_when_this_browser_holds_none_with_honest_freshness()
    {
        var history = new Mock<IEndpointObservationHistoryApiService>();
        var seen = Now.AddDays(-3);
        history.Setup(h => h.HistoryAsync("dev", Origin + "/", It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointObservationHistoryView("dev", ApiHost, true,
            new EndpointDiscoveryRunSummary("run-1", "dev", ApiHost, seen, seen, EndpointDiscoveryRunSummary.Completed, 2, 2, 0, 0), null, [],
            [
                new ObservedOperationHistory { Identity = "GET /api/children", Protocol = "REST", Host = ApiHost, Method = "GET", Path = "/api/children", AuthObserved = true,
                    LastObservedAt = seen, FirstObservedAt = seen, ObservationCount = 4, LastStatus = 200, Confidence = ObservedEndpointConfidence.Verified,
                    Provenance = RequestProvenance.ApplicationTraffic, State = ObservedOperationState.New },
            ]));
        var page = Render(new EndpointDiscoveryService(), history: history.Object);

        var (decision, _, targets) = Final(page);
        targets.Should().Be(1);
        decision.Should().NotBe(nameof(ReviewDecision.Blocked));
        var evidence = page.Find("[data-testid=aqr-runtime-evidence]");
        evidence.GetAttribute("data-source").Should().Be(nameof(RuntimeEvidenceSource.PersistedHistory));
        evidence.GetAttribute("data-freshness").Should().Be(nameof(EvidenceFreshness.Historical), "three days old is historical, not missing");
        evidence.TextContent.Should().Contain("Available").And.Contain("Historical").And.Contain("persisted");
        page.Find("[data-testid=aqr-view-discovery]").GetAttribute("href").Should().Contain("tab=endpoint&profile=dev");
    }

    [Fact]
    public void Old_evidence_is_Stale_not_missing()
    {
        var discovery = new EndpointDiscoveryService();
        discovery.MergeObservedAsync(new Mock<IJSRuntime>().Object, "dev", [Ep("/api/children", seen: Now.AddDays(-60))]).GetAwaiter().GetResult();
        var page = Render(discovery);
        Final(page);
        page.Find("[data-testid=aqr-runtime-evidence]").GetAttribute("data-freshness").Should().Be(nameof(EvidenceFreshness.Stale));
        page.Find("[data-testid=aqr-runtime-evidence]").TextContent.Should().Contain("Available · Stale");
    }

    [Fact]
    public void Targets_without_a_selection_need_review_and_are_not_blocked()
    {
        var page = Render(new EndpointDiscoveryService(), Proxy(Task.FromResult(Live(Traffic(ObservedEndpointConfidence.Candidate)))).Object);
        var (decision, readiness, _) = Final(page);
        decision.Should().Be(nameof(ReviewDecision.NeedsReview));
        readiness.Should().Be(nameof(ApiReviewReadinessLevel.NeedsReview));
        page.FindAll("[data-testid=aqr-blockers]").Should().BeEmpty();
    }

    [Fact]
    public void Another_environments_capture_is_never_this_reviews_evidence()
    {
        var other = Live(Traffic()) with { ProfileId = "qa" };
        var page = Render(new EndpointDiscoveryService(), Proxy(Task.FromResult(other)).Object);
        var (decision, _, targets) = Final(page);
        targets.Should().Be(0);
        decision.Should().Be(nameof(ReviewDecision.Blocked), "no API target exists for this environment — the only genuine readiness blocker here");
        page.Find("[data-testid=aqr-runtime-evidence]").TextContent.Should().Contain("Not available");
    }

    [Fact]
    public void Refresh_keeps_the_last_assessed_readiness_while_evidence_reloads()
    {
        var runtime = new Queue<Task<LocalHttpsProxyStatus>>();
        var slow = new TaskCompletionSource<LocalHttpsProxyStatus>();
        runtime.Enqueue(Task.FromResult(Live(Traffic())));
        runtime.Enqueue(slow.Task);
        var proxy = new Mock<ILocalHttpsProxyApiService>();
        proxy.Setup(p => p.GetRuntimeAsync()).Returns(() => runtime.Count > 0 ? runtime.Dequeue() : slow.Task);
        var page = Render(new EndpointDiscoveryService(), proxy.Object);
        var before = Final(page);

        page.Find("[data-testid=aqr-refresh-readiness]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-refreshing]").TextContent.Should().Be("Refreshing evidence…"), Wait);
        Final(page).Should().Be(before, "the last assessed readiness stays while refreshing — no Assessing or Blocked flash");

        slow.SetResult(Live(Traffic()));
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-refreshing]").Should().BeEmpty(), Wait);
        Final(page).Should().Be(before);
    }

    [Fact]
    public void An_outdated_assessment_never_commits()
    {
        using var first = new ReviewAssessment(1, [("a", "A")]);
        using var second = new ReviewAssessment(2, [("a", "A")]);
        first.IsCurrent(latestGeneration: 2).Should().BeFalse("a newer generation started");
        second.IsCurrent(2).Should().BeTrue();
        second.Cancel();
        second.IsCurrent(2).Should().BeFalse("navigation/target change cancelled it");
    }

    [Theory]
    [InlineData(1, EvidenceFreshness.Recent)]
    [InlineData(24 * 5, EvidenceFreshness.Historical)]
    [InlineData(24 * 45, EvidenceFreshness.Stale)]
    public void Freshness_is_derived_from_the_last_observation(int hoursAgo, EvidenceFreshness expected) =>
        ReviewDecisions.FreshnessOf(Now.AddHours(-hoursAgo), Now).Should().Be(expected);

    [Fact]
    public void No_timestamp_is_Unknown_freshness() => ReviewDecisions.FreshnessOf(null, Now).Should().Be(EvidenceFreshness.Unknown);
}
