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
            "Both selected APIs require authenticated access, but no authenticated API context is available.");
        page.FindAll("[data-testid=aqr-readiness-limitations]").Should().BeEmpty("a blocker is not presented as a limitation");

        Run(page).HasAttribute("disabled").Should().BeTrue();
        Run(page).GetAttribute("aria-describedby").Should().Be("aqr-run-unavailable");
        page.Find("#aqr-run-unavailable").TextContent.Should().Be("Run unavailable — authenticated API context is required for the selected APIs.");
        // The procedure (proxy, dedicated Edge, sign in) is Authentication details' content, not the blocker's.
        page.Find("[data-testid=aqr-readiness]").TextContent.Should().NotContainAny("Local HTTPS Proxy", "dedicated Edge");
        page.FindAll("[data-testid=aqr-readiness-help]").Should().BeEmpty();

        var actions = page.FindAll("[data-testid=aqr-decide] a").Where(a => a.GetAttribute("href")!.Contains("tab=auth")).ToList();
        actions.Should().ContainSingle("the blocker is the one action surface").Which.TextContent.Should().Be("Open Authentication setup");
        actions[0].GetAttribute("href").Should().Be(AuthHref);
        actions[0].HasAttribute("aria-describedby").Should().BeFalse();
        page.Find("[data-testid=aqr-auth-missing]").TextContent.Should().Contain("Local HTTPS Proxy").And.Contain("authenticated action");

        // Not a network state, not a failure.
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Waiting for authenticated traffic");
        // Per-target readiness, never a constant "Public access: Available" standing in for an authenticated target.
        page.FindAll("[data-testid=aqr-public-access]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-target-access-row]").Should().OnlyContain(r => r.GetAttribute("data-ready") == "false" && r.TextContent.Contains("Authenticated context unavailable"));
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
        message.Should().Be("2 targets ready. REST contract validation is unavailable.");
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
    [InlineData(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, "Waiting for authenticated traffic", "no authenticated API context is available")]
    [InlineData(AuthenticatedApiContextStatus.Stale, "Waiting for authenticated traffic", "no authenticated API context is available")]
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
        signIn.TextContent.Should().Be("Not required for frontend entry point", "a fact about the frontend, never read as the APIs' requirement");
        signIn.GetAttribute("aria-describedby").Should().Be("aqr-frontend-signin-help");
        page.Find("#aqr-frontend-signin-help").TextContent.Should().Be("Frontend access does not require sign-in; selected APIs may still require authenticated access.");
        page.Find("[data-testid=aqr-scope-auth]").TextContent.Should().Be("Access requirements: 2 authenticated · 0 public");
        page.FindAll("[data-testid=aqr-target-access]").Should().OnlyContain(a => a.TextContent == "Auth required");
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
    public void ApiTargetsStartOpen_OtherReviewDetailsStartCollapsed(AuthenticatedApiContextStatus status)
    {
        var page = Render(status);

        // API targets is the working section: open, with the same disclosure semantics as the rest.
        Toggle(page, "aqr-targets-disclosure").GetAttribute("aria-expanded").Should().Be("true");
        Toggle(page, "aqr-targets-disclosure").GetAttribute("aria-controls").Should().Be(Body(page, "aqr-targets-disclosure").Id);
        Body(page, "aqr-targets-disclosure").HasAttribute("hidden").Should().BeFalse();

        foreach (var id in new[] { "aqr-contracts-disclosure", "aqr-access-details", "aqr-readonly-disclosure" })
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

        // One table row per target: API | Type | Access | Operations | Contract/schema.
        page.FindAll("[data-testid=aqr-targets-table] > thead th").Select(th => th.TextContent).Should().Equal("API", "Type", "Access", "Operations", "Contract / schema");
        var rest = page.Find("[data-testid=aqr-target][data-type=Rest]");
        rest.QuerySelectorAll("td").Select(td => td.GetAttribute("data-label")).Should().Equal("Type", "Access", "Operations", "Contract / schema");
        rest.QuerySelector("[data-testid=aqr-target-type]")!.TextContent.Should().Be("REST");
        rest.QuerySelector("[data-testid=aqr-target-access]")!.TextContent.Should().Be("Auth required");
        rest.QuerySelector("[data-testid=aqr-target-operations]")!.TextContent.Should().Be("2 · 1 write (not executed)");
        rest.QuerySelector("[data-testid=aqr-target-contract]")!.TextContent.Should().Be("No contract");

        var ops = Toggle(page, $"aqr-ops-{rest.GetAttribute("data-target-id")}");
        ops.GetAttribute("aria-expanded").Should().Be("false");
        ops.TextContent.Should().Contain("Discovered traffic");
        ops.Click();
        var rows = Body(page, $"aqr-ops-{rest.GetAttribute("data-target-id")}").QuerySelectorAll("[data-testid=aqr-op-row]");
        rows.Select(r => r.QuerySelectorAll("td").Select(td => td.TextContent).ToArray()).Should().ContainEquivalentOf(
            new[] { "POST", "/api/children", "Authentication required", "Discovered traffic · 2× observed", "Not executed · manual review" });
    }

    // 55. GraphQL schema copy: one label, attempt-based value.
    [Theory]
    [InlineData(false, "Schema retrieval pending")]
    [InlineData(true, "Schema unavailable previously")]
    public void GraphQlSchemaIsLabelledOnceAndNeverPromised(bool rejectedPreviously, string expected)
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, rejectedPreviously);

        var gql = page.Find("[data-testid=aqr-target][data-type=GraphQl]");
        gql.QuerySelector("[data-testid=aqr-target-contract]")!.TextContent.Should().Be(expected);
        gql.TextContent.Should().NotContain("will be retrieved").And.NotContain("Schema available").And.NotContainAny("failed", "Failed");
    }

    // 56. Contract summary: availability and history, with drift kept apart.
    [Fact]
    public void CollapsedContractSummaryStatesEachContractFactAndTheBaselineCount()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, introspectionRejectedPreviously: true);

        Toggle(page, "aqr-contracts-disclosure").TextContent.Should()
            .Contain("REST: No published contract")
            .And.Contain("GraphQL: schema retry pending")
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
        fields["Execution"].Should().Contain("backend gateway").And.Contain("does not receive a token");
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
        var methods = page.Find("[data-testid=aqr-readonly-summary]").QuerySelectorAll("dl > div, div")
            .Where(d => d.QuerySelector("dt") is not null)
            .ToDictionary(d => d.QuerySelector("dt")!.TextContent, d => d.QuerySelector("dd")!.TextContent);
        methods["REST"].Should().Be("GET, HEAD, OPTIONS");
        methods["GraphQL"].Should().Be("Queries only");
        methods["Never executed"].Should().Contain("Mutations").And.Contain("write operations").And.Contain("destructive operations");
        page.Find("[data-testid=aqr-timing-scope]").TextContent.Should().Contain("backend gateway → API").And.Contain("not browser or end-user latency");
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
        (State("contracts"), Limit("contracts")).Should().Be(("Limited", "REST: No published OpenAPI contract. GraphQL: Runtime schema retrieval will be retried during the review."));
        (State("rest"), Limit("rest")).Should().Be(("Limited", "Structural review available. Published contract comparison unavailable."));
        (State("graphql"), Limit("graphql")).Should().Be(("Limited", "Observed operations can be reviewed. Schema-dependent checks remain limited until runtime schema retrieval succeeds."));
        (State("errors"), Limit("errors")).Should().Be(("Included", "Write and destructive operations are never executed."));
        (State("performance"), Limit("performance")).Should().Be(("Included", null));
        page.Find("[data-testid=aqr-domain][data-domain='performance'] .aqr-domain-purpose").TextContent.Should().Be("Measures response timing of the review's own read-only API requests.");

        page.FindAll("[data-testid=aqr-domain]").Should().OnlyContain(d => !d.TextContent.Contains("selected target"), "scope counts live in Review scope");
        page.FindAll("[data-testid=aqr-domain-limitation]").Should().OnlyContain(l => l.TextContent.Length <= 130);
    }

    // ── Final polish ──────────────────────────────────────────────────────────────────────────────────────────────

    // The procedure is in Authentication details, in order, with one secondary link to the same owner.
    [Fact]
    public void AuthenticationProcedureLivesInDetailsNotInTheBlocker()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        Toggle(page, "aqr-access-details").TextContent.Should().Contain("Waiting for authenticated traffic");
        Toggle(page, "aqr-access-details").Click();

        page.Find("[data-testid=aqr-auth-missing]").QuerySelectorAll("li").Select(li => li.TextContent).Should().Equal(
            "Open Target Environment → Authentication.",
            "Start the Local HTTPS Proxy.",
            "Open the dedicated Edge browser.",
            "Sign in and perform an authenticated action against the target.",
            "Return to API Quality Review once authenticated traffic is observed.");
        page.Find("[data-testid=aqr-access-details-action]").GetAttribute("href").Should().Be(AuthHref);
        page.FindAll("[data-testid=aqr-decide] .aqr-cta").Should().ContainSingle("one primary action");
        page.Find("[data-testid=aqr-readiness]").TextContent.Should().NotContainAny("Local HTTPS Proxy", "dedicated Edge", "Sign in");
    }

    // Open/closed state is the reader's: selection changes and rerenders never reset it.
    [Fact]
    public void ExpandedDetailsSurviveSelectionChangesAndRerender()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);
        Toggle(page, "aqr-targets-disclosure").GetAttribute("aria-expanded").Should().Be("true", "API targets starts open");
        foreach (var id in new[] { "aqr-contracts-disclosure", "aqr-access-details", "aqr-readonly-disclosure" })
            Toggle(page, id).GetAttribute("aria-expanded").Should().Be("false", $"{id} starts collapsed");

        Toggle(page, "aqr-access-details").Click();
        // A reader who collapses the open default keeps it collapsed: the default is not re-applied on rerender.
        Toggle(page, "aqr-targets-disclosure").Click();
        page.FindAll("[data-testid=aqr-target-checkbox]")[1].Change(false);
        page.Render();
        page.FindAll("[data-testid=aqr-target-checkbox]")[1].Change(true);

        Toggle(page, "aqr-access-details").GetAttribute("aria-expanded").Should().Be("true");
        Toggle(page, "aqr-targets-disclosure").GetAttribute("aria-expanded").Should().Be("false");
        Toggle(page, "aqr-contracts-disclosure").GetAttribute("aria-expanded").Should().Be("false");
    }

    // With authenticated context the blocker is gone and Run carries no unavailability reason.
    [Fact]
    public void AvailableContextRemovesTheBlockerAndEnablesRun()
    {
        var page = Render(AuthenticatedApiContextStatus.Available);

        page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().NotBe("Blocked");
        page.Find("#aqr-readiness-heading").TextContent.Should().NotContain("cannot start");
        Run(page).HasAttribute("disabled").Should().BeFalse();
        Run(page).HasAttribute("aria-describedby").Should().BeFalse();
        page.FindAll("#aqr-run-unavailable").Should().BeEmpty();
        page.Find("[data-testid=aqr-domain][data-domain='security'] [data-testid=aqr-domain-state]").TextContent.Should().Be("Included");
    }

    // Missing contract and refused introspection limit checks; neither is a failure, and no-drift is not compatibility.
    [Fact]
    public void ContractSummaryIsCompactAndNeverClaimsFailureOrCompatibility()
    {
        var page = Render(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, introspectionRejectedPreviously: true);
        Toggle(page, "aqr-contracts-disclosure").Click();

        var rows = page.FindAll("[data-testid=aqr-contract-row]").ToDictionary(r => r.QuerySelector("dt")!.TextContent, r => r.QuerySelector("dd")!.TextContent.Trim());
        rows["REST contract"].Should().EndWith("No published OpenAPI contract");
        rows["GraphQL schema"].Should().EndWith("Unavailable on previous attempt · retry during review");
        page.Find("[data-testid=aqr-contract-history] dt").TextContent.Should().Be("History");
        page.Find("[data-testid=aqr-contract-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-contract-details-body]").TextContent.Should()
            .Contain("policy observation, not as a failure").And.Contain("paths and types, never values").And.Contain("Baselines contain no values");

        var preRun = page.Find("[data-testid=aqr-decide]").TextContent + page.Find("[data-testid=aqr-domains]").TextContent + page.Find("[data-testid=aqr-details]").TextContent;
        preRun.Should().NotContainAny("Failed", "failed:", "compatible", "Compatible");
        page.Find("[data-testid=aqr-domain][data-domain='graphql'] [data-testid=aqr-domain-state]").TextContent.Should().Be("Limited");
    }

    // ── Density cleanup ───────────────────────────────────────────────────────────────────────────────────────────

    // The limitation card says each limitation once, in one short sentence; the reasons are one disclosure away.
    [Fact]
    public void LimitationCardIsACompactSummaryWithDetailsOnDemand()
    {
        var page = Render(AuthenticatedApiContextStatus.Available, introspectionRejectedPreviously: true);

        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Be(
            "2 targets ready. REST contract validation is unavailable. GraphQL schema retrieval will be retried during the review.");

        var toggle = Toggle(page, "aqr-readiness-limitations");
        toggle.TextContent.Should().Contain("View limitation details");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        var items = Body(page, "aqr-readiness-limitations").QuerySelectorAll("li").Select(li => li.TextContent.Trim()).ToList();
        items.Should().Contain(i => i.Contains("1 REST target selected"));
        items.Should().Contain(i => i.Contains("1 GraphQL target selected"));
        items.Should().Contain(i => i.Contains("Read-only review available"));
        items.Should().Contain(i => i.Contains("Authenticated requests available for 2 targets"));
        items.Should().Contain(i => i.Contains("REST contract validation unavailable — responses reviewed structurally"));
        items.Should().Contain(i => i.Contains("GraphQL schema unavailable on the previous attempt") && i.Contains("retried during this review"));
    }

    // A previous refusal is history, not this review's result; and manual review is only for unmatched operations.
    [Fact]
    public void GraphQlSchemaIsDescribedAsAPreviousAttemptWithARetry_AndManualReviewStaysNarrow()
    {
        var page = Render(AuthenticatedApiContextStatus.Available, introspectionRejectedPreviously: true);
        Toggle(page, "aqr-readiness-limitations").Click();
        Toggle(page, "aqr-contracts-disclosure").Click();
        Toggle(page, "aqr-contract-details").Click();

        var text = page.Find("[data-testid=aqr-decide]").TextContent + page.Find("[data-testid=aqr-domains]").TextContent + page.Find("[data-testid=aqr-details]").TextContent;
        Toggle(page, "aqr-contracts-disclosure").TextContent.Should().Contain("GraphQL: schema retry pending");
        page.Find("[data-testid=aqr-contract-row][data-protocol='GraphQL'] [data-testid=aqr-contract-state]").TextContent
            .Should().Contain("Unavailable on previous attempt").And.Contain("retry during review");

        // Never a present-tense failure of this review.
        text.Should().NotContainAny("GraphQL schema is unavailable", "Schema unavailable.", "Introspection failed", "schema retrieval failed");
        // Manual review is tied to unmatched operations only — never to GraphQL operations as a whole.
        text.Should().NotContainAny("all GraphQL operations require manual review", "observed operations are then marked for manual review");
        text.Should().Contain("unmatched operations need manual review");
        page.Find("[data-testid=aqr-contract-details-body]").TextContent.Should().Contain("cannot be matched to it are marked for manual review");
    }

    // Available access: Manage authentication stays, quietly; Run stays the one primary action.
    [Fact]
    public void ManageAuthenticationIsQuietWhenAccessIsAvailable_AndRunStaysPrimary()
    {
        var page = Render(AuthenticatedApiContextStatus.Available);

        var manage = page.Find("[data-testid=aqr-access-action]");
        manage.TextContent.Should().Be("Manage authentication");
        manage.GetAttribute("href").Should().Be(AuthHref);
        manage.ClassList.Should().Contain("aqr-link-quiet");
        manage.ClassList.Should().NotContain("aqr-cta");

        Run(page).ClassList.Should().Contain("btn-primary");
        Run(page).TextContent.Trim().Should().Be("Run API Quality Review");
        page.FindAll("[data-testid=aqr-decide] .btn-primary, [data-testid=aqr-decide] .aqr-cta").Should().HaveCount(1, "Run is the only strong action while nothing blocks it");
    }

    // Collapsed is not removed: every detail is still there once expanded.
    [Fact]
    public void EveryPreRunDetailIsStillAvailableWhenExpanded()
    {
        var page = Render(AuthenticatedApiContextStatus.Available, introspectionRejectedPreviously: true);
        // Collapsed headers still state each section's status.
        Toggle(page, "aqr-access-details").TextContent.Should().Contain("Authentication details").And.Contain("Available");
        Toggle(page, "aqr-readonly-disclosure").TextContent.Should().Contain("Read-only review").And.Contain("Safe requests only");
        foreach (var id in new[] { "aqr-contracts-disclosure", "aqr-access-details", "aqr-readonly-disclosure" }) Toggle(page, id).Click();

        var contracts = Body(page, "aqr-contracts-disclosure").TextContent;
        contracts.Should().Contain("REST contract").And.Contain("No published OpenAPI contract")
            .And.Contain("GraphQL schema").And.Contain("History").And.Contain("previous baseline");

        var access = Body(page, "aqr-access-details").TextContent;
        access.Should().Contain("Authenticated testing method").And.Contain("Local HTTPS proxy")
            .And.Contain("Available (memory only)").And.Contain("Authenticated REST traffic observed")
            .And.Contain("Authenticated GraphQL query traffic observed").And.Contain("does not receive a token")
            .And.Contain("Memory only means");

        var readOnly = Body(page, "aqr-readonly-disclosure").TextContent;
        readOnly.Should().Contain("GET, HEAD, OPTIONS").And.Contain("Queries only")
            .And.Contain("Mutations, write operations and destructive operations").And.Contain("not browser or end-user latency");

    }

    // Frontend sign-in and API authentication stay two facts.
    [Fact]
    public void FrontendSignInAndApiAuthenticationStayDistinct()
    {
        var page = Render(AuthenticatedApiContextStatus.Available);
        page.Find("[data-testid=aqr-frontend-signin-help]").TextContent.Should().Contain("selected APIs may still require authenticated access");
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Available");
        page.Find("[data-testid=aqr-target-access-summary]").TextContent.Should().Be("2 of 2 ready");
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
