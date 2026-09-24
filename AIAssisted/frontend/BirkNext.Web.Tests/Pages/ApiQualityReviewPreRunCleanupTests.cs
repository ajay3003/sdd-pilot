using AngleSharp.Dom;
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
/// The pre-run page as a decision: target + scope + access → the exact blocker or limitation → one action → Run when
/// ready. The fixture is the reported one: a frontend that needs no sign-in, and two selected APIs (one REST, one
/// GraphQL) that both require authenticated access.
/// </summary>
public sealed class ApiQualityReviewPreRunCleanupTests : BunitContext
{
    private const string Origin = "https://app.example.test";
    private const string ApiHost = "api.example.test";
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private const string AuthHref = "/admin/system-settings?section=target-environments&tab=auth&profile=dev";

    private readonly EndpointDiscoveryService _discovery = new();
    private readonly ApiReviewHistoryService _history = new();

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        profile.Authentication.RequiresAuthentication = false;
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.None;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", RequiresAuthentication = false, AuthenticationType = FrontendAuthenticationType.None,
            ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP"),
        };
    }

    private static ObservedNetworkEndpoint Ep(string path, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, GraphQlOperationType op = GraphQlOperationType.None, string? name = null, string method = "GET") => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = ApiHost, Port = 443, Path = path,
        Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : method, AuthObserved = true, LastStatus = 200, Count = 2,
        FirstObservedAt = T0, LastObservedAt = T0, Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/",
        OperationType = op, OperationName = name,
    };

    private static ObservedNetworkEndpoint[] Endpoints() =>
    [
        Ep("/api/children"),
        Ep("/api/children", method: "POST"),
        Ep("/api/graphql", ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "GetChildren"),
    ];

    /// <param name="status">What the backend gateway reports; <c>null</c> = the capability request failed (client fallback).</param>
    private IRenderedComponent<ApiQualityReview> Render(AuthenticatedApiContextStatus? status, bool introspectionRejectedPreviously = false)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var context = Context();
        _discovery.MergeObservedAsync(new Mock<IJSRuntime>().Object, "dev", Endpoints()).GetAwaiter().GetResult();
        if (introspectionRejectedPreviously)
        {
            var targets = ApiReviewTargetResolver.Resolve(context, _discovery.GetSnapshot("dev"));
            var report = new ApiReviewReport
            {
                Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "Dev" }, GeneratedAt = T0,
                Targets = targets.Select(t => new ApiReviewTargetResult
                {
                    Target = t, Status = ApiReviewTargetStatus.Completed, AccessMode = ApiReviewAccessMode.AuthenticatedHttp,
                    Contract = t.ApiType == ApiReviewTargetType.GraphQl ? new ApiReviewContractSummary { Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false } : null,
                }).ToList(),
            };
            _history.RecordAsync(new Mock<IJSRuntime>().Object, "dev", report).GetAwaiter().GetResult();
        }

        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        var authenticated = status == AuthenticatedApiContextStatus.Available;
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedReviewCapabilities
            {
                Method = AuthenticatedTestingMethod.LocalHttpsProxy, PublicApi = true, ContextStatus = status ?? AuthenticatedApiContextStatus.NotApplicable,
                AuthenticatedApi = authenticated, AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated,
                Reason = status is null ? "Authenticated capability status is unavailable; the backend could not be reached."
                    : authenticated ? "Authenticated via Local HTTPS Proxy (memory only)."
                    : "Start the local proxy, sign in to the target application in the proxy-configured browser, and perform an authenticated action.",
            });
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton<IApiReviewHistoryService>(_history);
        Services.AddSingleton(caps.Object);
        Services.AddSingleton(Mock.Of<IApiReviewService>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        var page = Render<ApiQualityReview>();
        if (introspectionRejectedPreviously)
        {
            // A previous report re-opens as the result; the pre-run view is one "Back" away, as it is for a user.
            page.WaitForAssertion(() => page.Find("[data-testid=aqr-back]"));
            page.Find("[data-testid=aqr-back]").Click();
        }
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target]").Should().HaveCount(2));
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().NotBe("Loading"));
        return page;
    }

    private static IElement Run(IRenderedComponent<ApiQualityReview> page) => page.Find("[data-testid=aqr-run-row] button");
    private static IElement Toggle(IRenderedComponent<ApiQualityReview> page, string id) => page.Find($"[data-testid='{id}-toggle']");
    private static IElement Body(IRenderedComponent<ApiQualityReview> page, string id) => page.Find($"[data-testid='{id}-body']");

    // 48. Blocked auth.
    [Fact]
    public void MissingAuthenticatedContext_ReviewCannotStart_WithOneActionTowardAuthentication()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);

        var readiness = page.Find("[data-testid=aqr-readiness]");
        readiness.GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review cannot start").And.NotContain("limitations");
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Be(
            "Authenticated API access is required for both selected targets, but no authenticated API context is currently available.");
        page.FindAll("[data-testid=aqr-readiness-limitations]").Should().BeEmpty("a blocker is not presented as a limitation");

        Run(page).HasAttribute("disabled").Should().BeTrue();
        Run(page).GetAttribute("aria-describedby").Should().Be("aqr-run-reason");

        var actions = page.FindAll("[data-testid=aqr-decide] a").Where(a => a.GetAttribute("href")!.Contains("tab=auth")).ToList();
        actions.Should().ContainSingle("the blocker is the one action surface").Which.TextContent.Should().Be("Open Authentication setup");
        actions[0].GetAttribute("href").Should().Be(AuthHref);
        actions[0].GetAttribute("aria-describedby").Should().Be("aqr-readiness-help");
        page.Find("#aqr-readiness-help").TextContent.Should().Contain("Local HTTPS Proxy").And.Contain("authenticated action");

        // Not a network state, not a failure.
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Waiting for authenticated traffic");
        page.Find("[data-testid=aqr-public-access]").TextContent.Should().Contain("Available", "public access and authenticated context stay separate");
        page.Markup.Should().NotContain("Not connected").And.NotContain("authentication failed").And.NotContain("unreachable");
    }

    // 49. Auth restored.
    [Fact]
    public void AuthenticatedContextAvailable_RunEnabled_OnlyContractLimitationsRemain()
    {
        var page = Render(AuthenticatedApiContextStatus.Available);

        page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited");
        page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review can run with limitations");
        Run(page).HasAttribute("disabled").Should().BeFalse();
        Run(page).HasAttribute("aria-describedby").Should().BeFalse();

        var message = page.Find("[data-testid=aqr-run-reason]").TextContent;
        message.Should().Be("2 targets ready. No published REST contract.");
        message.Should().NotContain("Authenticated API access is required", "no stale blocker once the context exists");
        page.FindAll("[data-testid=aqr-readiness-help]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-run-action]").Should().BeEmpty();

        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Available");
        var manage = page.Find("[data-testid=aqr-access-action]");
        manage.TextContent.Should().Be("Manage authentication");
        manage.GetAttribute("href").Should().Be(AuthHref);
        page.Find("[data-testid=aqr-domain][data-domain='security'] [data-testid=aqr-domain-state]").TextContent.Should().Be("Included");
    }

    // 50. Status wording follows the gateway's state; each stays distinct.
    [Theory]
    [InlineData(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, "Waiting for authenticated traffic", "no authenticated API context is currently available")]
    [InlineData(AuthenticatedApiContextStatus.Stale, "Waiting for authenticated traffic", "no authenticated API context is currently available")]
    [InlineData(AuthenticatedApiContextStatus.Expired, "Session expired", "the authenticated API session has expired")]
    [InlineData(null, "Status unavailable", "the authenticated API status could not be resolved")]
    public void AccessWordingMatchesTheReportedState(AuthenticatedApiContextStatus? status, string label, string reason)
    {
        var page = Render(status);

        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be(label);
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Contain(reason);
        Toggle(page, "aqr-access-details").TextContent.Should().Contain(label);
        page.Markup.Should().NotContain("Not connected");
    }

    // 51. Frontend sign-in and API authentication are separate facts, both visible.
    [Fact]
    public void FrontendSignInAndApiAuthenticationRenderIndependently()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);

        var signIn = page.Find("[data-testid=aqr-env-auth]");
        signIn.TextContent.Should().Be("Not required", "the value is the frontend's own configuration, unchanged");
        signIn.GetAttribute("aria-describedby").Should().Be("aqr-frontend-signin-help");
        page.Find("#aqr-frontend-signin-help").TextContent.Should().Be("Applies to the frontend only. Selected APIs may still require authenticated access independently.");
        page.Find("[data-testid=aqr-scope-auth]").TextContent.Should().Be("2 require authenticated access");
        page.FindAll("[data-testid=aqr-target-access]").Should().OnlyContain(a => a.TextContent == "Authentication required");
    }

    // 52. The link only navigates.
    [Fact]
    public void AuthenticationLinkIsPlainNavigationToTheSelectedEnvironment()
    {
        ApiReviewRunEligibility.AuthenticationHref("dev").Should().Be(AuthHref);
        ApiReviewRunEligibility.AuthenticationHref("qa env").Should().EndWith("&profile=qa%20env");
        ApiReviewRunEligibility.AuthenticationHref(null).Should().Be("/admin/system-settings?section=target-environments&tab=auth");

        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        var selectedBefore = page.FindAll("[data-testid=aqr-target-checkbox]").Select(c => c.HasAttribute("checked")).ToList();
        var link = page.Find("[data-testid=aqr-run-action]");
        link.TagName.Should().Be("A", "a link, not a button with a side effect");
        link.HasAttribute("onclick").Should().BeFalse();
        link.HasAttribute("blazor:onclick").Should().BeFalse();
        page.FindAll("[data-testid=aqr-target-checkbox]").Select(c => c.HasAttribute("checked")).Should().Equal(selectedBefore);
        page.FindAll("[data-testid=aqr-results]").Should().BeEmpty("nothing ran");
    }

    // 53. Review details start collapsed — including while blocked.
    [Theory]
    [InlineData(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic)]
    [InlineData(AuthenticatedApiContextStatus.Available)]
    public void ReviewDetailsAreCollapsedOnFirstRender(AuthenticatedApiContextStatus status)
    {
        var page = Render(status);

        foreach (var id in new[] { "aqr-targets-disclosure", "aqr-contracts-disclosure", "aqr-access-details", "aqr-readonly-disclosure" })
        {
            Toggle(page, id).GetAttribute("aria-expanded").Should().Be("false", id);
            Toggle(page, id).GetAttribute("aria-controls").Should().Be(Body(page, id).Id, id);
            Body(page, id).HasAttribute("hidden").Should().BeTrue(id);
        }
    }

    // 54. Targets: scannable metadata; operations stay collapsed.
    [Fact]
    public void ExpandedRestTargetShowsLabelledMetadata_OperationsStillCollapsed()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        Toggle(page, "aqr-targets-disclosure").Click();

        var rest = page.Find("[data-testid=aqr-target][data-type=Rest]");
        var meta = rest.QuerySelectorAll("dl.aqr-target-meta > div").ToDictionary(d => d.QuerySelector("dt")!.TextContent, d => d.QuerySelector("dd")!.TextContent.Trim());
        meta["Source"].Should().Be("Discovered traffic");
        meta["Access"].Should().Be("Authentication required");
        meta["Operations"].Should().Be("2 · 1 write (not executed)");

        var ops = rest.QuerySelector("[data-testid$='-toggle']")!;
        ops.GetAttribute("aria-expanded").Should().Be("false");
        ops.Click();
        var rows = page.Find("[data-testid=aqr-target][data-type=Rest]").QuerySelectorAll("[data-testid=aqr-op-row]");
        rows.Select(r => r.QuerySelectorAll("td").Select(td => td.TextContent).ToArray()).Should().ContainEquivalentOf(
            new[] { "POST", "/api/children", "Authentication required", "Discovered traffic · 2× observed", "Not executed · manual review" });
    }

    // 55. GraphQL schema copy: one label, attempt-based value.
    [Theory]
    [InlineData(false, "Retrieval will be attempted during review")]
    [InlineData(true, "Unavailable previously; retrieval will be attempted again")]
    public void GraphQlSchemaIsLabelledOnceAndNeverPromised(bool rejectedPreviously, string expected)
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, rejectedPreviously);

        var gql = page.Find("[data-testid=aqr-target][data-type=GraphQl]");
        gql.QuerySelector("[data-testid=aqr-target-schema]")!.TextContent.Should().Be(expected);
        gql.QuerySelectorAll("dt").Count(dt => dt.TextContent == "Schema").Should().Be(1);
        gql.TextContent.Should().NotContain("Schema Schema").And.NotContain("will be retrieved").And.NotContain("Schema available");
    }

    // 56. Contract summary: availability and history, with drift kept apart.
    [Fact]
    public void CollapsedContractSummaryStatesEachContractFactAndTheBaselineCount()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, introspectionRejectedPreviously: true);

        Toggle(page, "aqr-contracts-disclosure").TextContent.Should()
            .Contain("REST: No contract configured")
            .And.Contain("GraphQL: Introspection unavailable previously")
            .And.Contain("No previous baseline")
            .And.NotContain("drift");
        page.Find("[data-testid=aqr-latest-comparison]").TextContent.Should().NotContain("No drift detected", "no drift check was recorded");
        page.Find("[data-testid=aqr-contract-row][data-protocol=GraphQL]").TextContent.Should().NotContain("failed");
    }

    // 57. Authentication details: everything a person acting on access needs, no secret.
    [Fact]
    public void ExpandedAuthenticationDetailsExplainMethodStateTrafficAndExecution()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        Toggle(page, "aqr-access-details").Click();

        var body = Body(page, "aqr-access-details");
        var fields = body.QuerySelectorAll("dl > div").ToDictionary(d => d.QuerySelector("dt")!.TextContent, d => d.QuerySelector("dd")!.TextContent);
        fields["Authenticated testing method"].Should().Be(AuthenticatedTestingMethodLabels.ProxyOption);
        fields["Authenticated API context"].Should().Be("Waiting for authenticated traffic");
        fields["REST"].Should().Be("No authenticated REST traffic observed");
        fields["GraphQL"].Should().Be("No authenticated GraphQL traffic observed");
        fields["Resolution"].Should().Contain("perform an authenticated action");
        fields["Execution"].Should().Contain("backend gateway").And.Contain("never receives a token");
        body.QuerySelector("[data-testid=aqr-memory-only-help]")!.TextContent.Should().Contain("held only in the backend runtime");
        body.QuerySelector("[data-testid=aqr-access-details-action]")!.GetAttribute("href").Should().Be(AuthHref);
        body.TextContent.Should().NotContainAny("Bearer ", "eyJ", "token value", "authentication failed");
        page.FindAll("input[type=password], textarea").Should().BeEmpty("no credential entry on this page");
    }

    // 58. Read-only scope stays explicit; no write action exists.
    [Fact]
    public void ReadOnlyReviewListsSafeRequestsAndNoWriteIsOffered()
    {
        var page = Render(AuthenticatedApiContextStatus.Available);

        Toggle(page, "aqr-readonly-disclosure").TextContent.Should().Contain("Safe requests only");
        page.Find("[data-testid=aqr-readonly-summary]").TextContent.Should()
            .Contain("REST GET, HEAD and OPTIONS").And.Contain("GraphQL queries").And.Contain("never called");
        page.FindAll("button").Select(b => b.TextContent).Should().NotContain(t => t.Contains("POST") || t.Contains("mutation", StringComparison.OrdinalIgnoreCase));
    }

    // 59. Domain cards: each says its own limitation once, briefly.
    [Fact]
    public void DomainCardsCarryShortOwnLimitationsOnly()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, introspectionRejectedPreviously: true);
        string State(string key) => page.Find($"[data-testid=aqr-domain][data-domain='{key}'] [data-testid=aqr-domain-state]").TextContent;
        string? Limit(string key) => page.Find($"[data-testid=aqr-domain][data-domain='{key}']").QuerySelector("[data-testid=aqr-domain-limitation]")?.TextContent;

        (State("security"), Limit("security")).Should().Be(("Limited", "Authenticated checks cannot run until authenticated API access is available."));
        (State("contracts"), Limit("contracts")).Should().Be(("Limited", "REST: no published contract. GraphQL: introspection was unavailable previously; schema retrieval will be attempted again."));
        (State("rest"), Limit("rest")).Should().Be(("Limited", "Structural review is available. Published contract comparison is unavailable."));
        (State("graphql"), Limit("graphql")).Should().Be(("Limited", "Observed operations can be reviewed. Schema-dependent checks are limited until runtime schema retrieval succeeds."));
        (State("errors"), Limit("errors")).Should().Be(("Included", "Write or destructive behaviour is never executed."));
        (State("performance"), Limit("performance")).Should().Be(("Included", "Timing is measured from the backend gateway to the API, not from an end user."));

        page.FindAll("[data-testid=aqr-domain]").Should().OnlyContain(d => !d.TextContent.Contains("selected target"), "scope counts live in Review scope");
        page.FindAll("[data-testid=aqr-domain-limitation]").Should().OnlyContain(l => l.TextContent.Length <= 130);
    }
}

/// <summary>Following "Open Authentication setup" lands on the environment's Authentication tab without changing anything.</summary>
public sealed class ApiQualityReviewAuthenticationDeepLinkTests : BunitContext
{
    [Fact]
    public void TheLinkOpensAuthenticationForTheEnvironmentWithoutActivatingOrSavingIt()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[
              {"id":"dev","name":"Dev target","environmentType":"Development","targetUrl":"https://dev.example.test"},
              {"id":"qa","name":"QA target","environmentType":"QA","targetUrl":"https://qa.example.test"}]}
            """);

        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "auth").Add(c => c.InitialProfileId, "qa"));

        cut.Find("#target-tab-auth").GetAttribute("aria-selected").Should().Be("true");
        cut.Markup.Should().Contain("QA target");
        settings.Settings.ActiveProfileId.Should().Be("dev", "navigating selects the environment for viewing; it does not activate it");
        JSInterop.Invocations.Where(i => i.Identifier.Contains("setItem", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("navigation alone saves nothing");
    }
}
