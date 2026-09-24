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
/// API Quality Review as a review workflow rather than a diagnostics dashboard. Before a run the page answers whether to
/// run: target, scope, access, readiness, run action, and what will be reviewed. After a run the result leads and the
/// configuration moves below it. These tests assert document order and disclosure state — the architecture, not pixels.
/// </summary>
public sealed class ApiQualityReviewWorkflowTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ApiHost = "api-dev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private static FrontendAnalysisContext Context(bool requiresAuth = true, string? swagger = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", SwaggerUrl = swagger };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", SwaggerUrl = swagger, RequiresAuthentication = requiresAuth,
            AuthenticationType = profile.Authentication.AuthenticationType,
            ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP"),
        };
    }

    private static ObservedNetworkEndpoint Ep(string path, bool auth, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, GraphQlOperationType op = GraphQlOperationType.None, string? name = null, string method = "GET") => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = ApiHost, Port = 443, Path = path, Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : method,
        AuthObserved = auth, LastStatus = 200, Count = 3, FirstObservedAt = T0, LastObservedAt = T0,
        Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/barn/1", OperationType = op, OperationName = name,
    };

    /// <summary>One authenticated REST service and one public GraphQL endpoint: a mixed scope, so the review can always run.</summary>
    private static ObservedNetworkEndpoint[] MixedEndpoints() =>
    [
        Ep("/api/autorisasjon/roller", auth: true),
        Ep("/api/autorisasjon/brukere", auth: true, method: "POST"),
        Ep("/api/public/graphql", auth: false, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "HentStatus"),
    ];

    private static ObservedNetworkEndpoint[] PublicEndpoints() => [Ep("/api/public/status", auth: false)];

    private readonly Mock<IApiReviewService> _review = new();
    private readonly EndpointDiscoveryService _discovery = new();
    private readonly ApiReviewHistoryService _history = new();

    private void Register(FrontendAnalysisContext context, bool authenticated, ObservedNetworkEndpoint[] endpoints)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        if (endpoints.Length > 0) _discovery.MergeObservedAsync(new Mock<IJSRuntime>().Object, "dev", endpoints).GetAwaiter().GetResult();
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedReviewCapabilities
            {
                Method = AuthenticatedTestingMethod.LocalHttpsProxy, PublicApi = true,
                AuthenticatedApi = authenticated, AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated,
                ContextStatus = authenticated ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic,
                Reason = authenticated ? "Authenticated via Local HTTPS Proxy" : "Start the local proxy",
            });
        _review.Setup(r => r.RunAsync(It.IsAny<ApiReviewRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ApiReviewRunRequest request, CancellationToken _) => Task.FromResult<(ApiReviewReport?, string?)>((Report(request, authenticated), null)));
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton<IApiReviewHistoryService>(_history);
        Services.AddSingleton(caps.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private static ApiReviewReport Report(ApiReviewRunRequest request, bool authenticated)
    {
        var targets = request.Targets.Select(t =>
        {
            var blocked = t.AuthRequired && !authenticated;
            return new ApiReviewTargetResult
            {
                Target = t,
                AccessMode = t.AuthRequired ? (authenticated ? ApiReviewAccessMode.AuthenticatedHttp : ApiReviewAccessMode.Unavailable) : ApiReviewAccessMode.PublicHttp,
                Status = blocked ? ApiReviewTargetStatus.Blocked : ApiReviewTargetStatus.Completed,
                AccessReason = blocked ? "Authenticated API context not available." : "Authenticated HTTP via the Local HTTPS proxy context.",
                FindingCount = blocked ? null : 1,
            };
        }).ToList();
        var assessed = targets.Where(t => t.Status == ApiReviewTargetStatus.Completed).ToList();
        return new ApiReviewReport
        {
            Environment = request.Environment, Policy = request.Policy, GeneratedAt = DateTimeOffset.UtcNow,
            Access = new AuthenticatedReviewCapabilities { AuthenticatedApi = authenticated },
            Targets = targets,
            Findings = assessed.Select(t => new ApiReviewFinding
            {
                Id = $"f-{t.Target.TargetId}", TargetId = t.Target.TargetId, Severity = ApiReviewSeverity.Low,
                Type = ApiReviewFindingType.Security, Endpoint = t.Target.Url, Check = "Security headers",
                Title = "Cache-Control not set to no-store", Description = "Authenticated JSON responses are cacheable.",
                Recommendation = "Send Cache-Control: no-store.",
            }).ToList(),
            Coverage = new ApiReviewCoverage
            {
                RestOperationsTotal = 1, RestOperationsReviewed = assessed.Count(t => t.Target.ApiType == ApiReviewTargetType.Rest),
                SecurityChecks = 17, TargetsCompleted = assessed.Count,
                TargetsBlocked = targets.Count(t => t.Status == ApiReviewTargetStatus.Blocked),
                UnsafeOperationsNotExecuted = 1,
            },
            ManualReviewItems = ["Write operations (POST/PUT/PATCH/DELETE, GraphQL mutations): behaviour, idempotency and side effects.", "Access control between roles/tenants.", "Business correctness of returned data."],
            Limitations = ["Automated API review is read-only: REST GET/HEAD/OPTIONS and GraphQL queries only."],
        };
    }

    private IRenderedComponent<ApiQualityReview> Landing(bool authenticated = false, ObservedNetworkEndpoint[]? endpoints = null, bool requiresAuth = true)
    {
        Register(Context(requiresAuth), authenticated, endpoints ?? MixedEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-decide]"));
        return page;
    }

    private async Task<IRenderedComponent<ApiQualityReview>> Result(bool authenticated = true)
    {
        var page = Landing(authenticated);
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse());
        await page.InvokeAsync(() => page.Find("[data-testid=aqr-run]").Click());
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-result-summary]"));
        return page;
    }

    /// <summary>The pre-run blocks in document order, which is the reading order and the focus order.</summary>
    private static IReadOnlyList<string> PreRunOrder(IRenderedComponent<ApiQualityReview> page) =>
        page.FindAll("[data-testid=aqr-environment], [data-testid=aqr-scope], [data-testid=aqr-access], [data-testid=aqr-readiness], [data-testid=aqr-run-row], [data-testid=aqr-domains], [data-testid=aqr-details]")
            .Select(e => e.GetAttribute("data-testid")!).ToList();

    /// <summary>Text the reader can actually see: a collapsed disclosure body carries <c>hidden</c> and does not count.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static IElement Toggle(IRenderedComponent<ApiQualityReview> page, string id) => page.Find($"[data-testid={id}-toggle]");
    private static IElement Body(IRenderedComponent<ApiQualityReview> page, string id) => page.Find($"[data-testid={id}-body]");
    private static bool Collapsed(IRenderedComponent<ApiQualityReview> page, string id) =>
        Toggle(page, id).GetAttribute("aria-expanded") == "false" && Body(page, id).HasAttribute("hidden");

    // ── §43. The pre-run decision area ────────────────────────────────────────────────────────────────────────────

    // 1, 2, 3, 4, 5.
    [Fact]
    public void PreRunReadsTargetThenScopeThenAccessThenReadinessThenRun()
    {
        var page = Landing();

        PreRunOrder(page).Should().Equal(
            "aqr-environment", "aqr-scope", "aqr-access", "aqr-readiness", "aqr-run-row", "aqr-domains", "aqr-details");

        var run = page.Find("[data-testid=aqr-run-row] [data-testid=aqr-run]");
        run.TextContent.Trim().Should().Be("Run API Quality Review");
        run.Closest(".disclosure-body").Should().BeNull("Run is reachable without expanding anything");
    }

    // 6. The review state is owned by Readiness and the access state by API access; Target repeats neither.
    [Fact]
    public void TargetCardCarriesNoReviewStatusAndNoAccessState()
    {
        var page = Landing();

        var target = page.Find("[data-testid=aqr-environment]");
        VisibleText(target).Should().NotContainAny("Review can run with limitations", "Ready to review", "Authenticated unavailable", "Authenticated available");
        page.FindAll("[data-testid=aqr-status-pill]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-readiness]").Should().ContainSingle();
    }

    // ── §44. Review scope and the API target disclosure ───────────────────────────────────────────────────────────

    // 7, 8, 9, 10.
    [Fact]
    public void ScopeSummaryCountsSelectedTargetsByProtocolAndAuthenticationNeed()
    {
        var page = Landing();

        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("2 API targets selected");
        page.Find("[data-testid=aqr-scope-protocols]").TextContent.Should().Be("1 REST · 1 GraphQL");
        page.Find("[data-testid=aqr-scope-auth]").TextContent.Should().Be("1 requires authenticated access");

        // Derived, not hard-coded: the counts follow the selected targets.
        var targets = page.FindAll("[data-testid=aqr-target]");
        targets.Count(t => t.GetAttribute("data-type") == "Rest").Should().Be(1);
        targets.Count(t => t.GetAttribute("data-type") == "GraphQl").Should().Be(1);
    }

    [Fact]
    public void AnAllPublicScopeSaysNothingAboutAuthentication()
    {
        var page = Landing(endpoints: PublicEndpoints(), requiresAuth: false);

        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("1 API target selected");
        page.FindAll("[data-testid=aqr-scope-auth]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-access-text]").Should().BeEmpty();
    }

    // 11, 12, 13.
    [Fact]
    public void ApiTargetsAreCollapsedByDefault_AndSelectionStillWorksWhenExpanded()
    {
        var page = Landing();

        Collapsed(page, "aqr-targets-disclosure").Should().BeTrue();
        Toggle(page, "aqr-targets-disclosure").TextContent.Should().Contain("API targets").And.Contain("2 selected").And.Contain("1 REST · 1 GraphQL");

        Toggle(page, "aqr-targets-disclosure").Click();

        var body = Body(page, "aqr-targets-disclosure");
        body.HasAttribute("hidden").Should().BeFalse();
        body.QuerySelectorAll("[data-testid=aqr-target]").Should().HaveCount(2);
        body.QuerySelectorAll("[data-testid=aqr-target-group]").Should().HaveCount(2);

        // 13. Deselecting still drives the scope summary above.
        page.FindAll("[data-testid=aqr-target-checkbox]")[1].Change(false);
        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("1 API target selected");
    }

    // ── §45. Access copy ──────────────────────────────────────────────────────────────────────────────────────────

    // 14, 15, 16.
    [Fact]
    public void AccessUsesOneVocabularyAndStatesUnavailabilityOnce()
    {
        var page = Landing(authenticated: false);

        var accessCard = page.Find("[data-testid=aqr-access]");
        accessCard.TextContent.Should().Contain("Public access").And.Contain("Authenticated API context");
        page.Find("[data-testid=aqr-public-access]").TextContent.Should().Contain("Available");
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Waiting for authenticated traffic");
        accessCard.TextContent.Should().NotContain("Not connected", "there is no connection concept behind this state");
        // The readiness card owns the one action toward Authentication; the access card does not repeat it.
        page.FindAll("[data-testid=aqr-access-action]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-access-text]").Should().BeEmpty("how many targets need authentication is Review scope's fact");

        // 15. The unavailability is stated once where a reader can act on it, not repeated across the decision area.
        var visible = VisibleText(page.Find("[data-testid=aqr-decide]"));
        Occurrences(visible, "authenticated access").Should().BeLessThanOrEqualTo(2, "the scope count and the frontend sign-in help");
        visible.Should().NotContain("Start the Local HTTPS Proxy", "the procedure belongs in the details");
        visible.Should().NotContain("backend gateway");
    }

    // 17, 18.
    [Fact]
    public void AuthenticationInstructionsAreCollapsed_AndUnchangedWhenExpanded()
    {
        var page = Landing(authenticated: false);

        Collapsed(page, "aqr-access-details").Should().BeTrue();

        Toggle(page, "aqr-access-details").Click();

        var body = Body(page, "aqr-access-details");
        body.HasAttribute("hidden").Should().BeFalse();
        var steps = body.QuerySelector("[data-testid=aqr-auth-missing]")!;
        steps.QuerySelectorAll("li").Select(li => li.TextContent).Should().Equal(
            "Open Target Environment → Authentication.",
            "Start the Local HTTPS Proxy.",
            "Open the dedicated Edge browser.",
            "Sign in and perform an authenticated action against the target.",
            "Return to API Quality Review once authenticated traffic is observed.");
        steps.QuerySelector("[data-testid=aqr-access-details-action]")!.GetAttribute("href").Should().Contain("tab=auth");
        body.TextContent.Should().Contain("backend gateway").And.Contain("never receives a token");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    // ── §11, §12. Readiness ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadinessIsStatedOnceWithItsLimitationsCollapsedWhileTheReviewCanStillRun()
    {
        var page = Landing(authenticated: false);
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review can run with limitations");
        Collapsed(page, "aqr-readiness-limitations").Should().BeTrue();
        page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse();

        Toggle(page, "aqr-readiness-limitations").Click();
        var body = Body(page, "aqr-readiness-limitations");
        body.TextContent.Should().Contain("Authenticated API context unavailable");
        // The action stays visible outside the collapsed list: collapsing the detail must not hide the way forward.
        body.QuerySelector("[data-testid=aqr-run-action]").Should().BeNull();
        page.Find("[data-testid=aqr-run-action]").TextContent.Should().Be("Open Authentication setup");
    }

    // ── §46. Contracts ────────────────────────────────────────────────────────────────────────────────────────────

    // 19, 20, 21, 22, 23.
    [Fact]
    public void ContractsLiveInReviewDetails_WithEachStateKeepingItsOwnMeaning()
    {
        var page = Landing();

        page.Find("[data-testid=aqr-details]").QuerySelector("[data-testid=aqr-contracts-disclosure]").Should().NotBeNull();
        Collapsed(page, "aqr-contracts-disclosure").Should().BeTrue();
        Toggle(page, "aqr-contracts-disclosure").TextContent.Should().Contain("Contracts").And.Contain("No previous baseline");

        Toggle(page, "aqr-contracts-disclosure").Click();

        var body = Body(page, "aqr-contracts-disclosure");
        var rest = body.QuerySelector("[data-testid=aqr-contract-row][data-protocol='REST']")!;
        rest.TextContent.Should().Contain("No contract configured").And.Contain("can still be reviewed structurally");
        rest.TextContent.Should().NotContainAny("failed", "error", "incompatible");
        // 23. Baseline history keeps its own row, and "not compared yet" is not "no drift".
        body.QuerySelector("[data-testid=aqr-baselines]")!.TextContent.Should().Contain("No previous baseline");
        body.QuerySelector("[data-testid=aqr-latest-comparison]")!.TextContent.Should().Be("Not compared yet");
    }

    // ── §47. Review domains ───────────────────────────────────────────────────────────────────────────────────────

    // 24, 28.
    [Fact]
    public void AllReviewDomainsRenderWithScopeVocabularyOnly()
    {
        var page = Landing();

        page.FindAll("[data-testid=aqr-domain]").Select(d => d.GetAttribute("data-domain"))
            .Should().Equal("security", "contracts", "errors", "performance", "rest", "graphql");
        page.Find("#aqr-domains-heading").TextContent.Should().Be("What will be reviewed");

        // 28. Access and configuration words are never a domain state.
        page.FindAll("[data-testid=aqr-domain-state]").Select(s => s.TextContent.Trim())
            .Should().OnlyContain(s => s == "Included" || s == "Limited" || s == "Partial evidence" || s == "Not included");
    }

    // 25. Partial authenticated coverage limits Security; it never removes it from the review.
    [Fact]
    public void SecurityStaysInTheReviewWhenAuthenticatedCoverageIsPartial()
    {
        var page = Landing(authenticated: false);

        var security = page.Find("[data-testid=aqr-domain][data-domain='security']");
        security.QuerySelector("[data-testid=aqr-domain-state]")!.TextContent.Trim().Should().Be("Limited");
        security.QuerySelector("[data-testid=aqr-domain-limitation]")!.TextContent.Should().Be("Authenticated checks cannot run until authenticated API access is available.");
        security.TextContent.Should().NotContain("selected target", "scope counts belong to Review scope, not to every card");
    }

    // 26. A missing REST contract limits Contracts; REST itself is still reviewed.
    [Fact]
    public void AMissingRestContractLimitsContractsWithoutRemovingRest()
    {
        var page = Landing();

        page.Find("[data-testid=aqr-domain][data-domain='contracts'] [data-testid=aqr-domain-state]").TextContent.Trim().Should().Be("Limited");
        var rest = page.Find("[data-testid=aqr-domain][data-domain='rest']");
        rest.QuerySelector("[data-testid=aqr-domain-state]")!.TextContent.Trim().Should().Be("Limited");
        rest.QuerySelector("[data-testid=aqr-domain-state]")!.TextContent.Should().NotContain("Not included");
        rest.QuerySelector("[data-testid=aqr-domain-limitation]")!.TextContent.Should().Be("Structural review is available. Published contract comparison is unavailable.");
    }

    // 27. GraphQL stays in the review; only its schema evidence is limited.
    [Fact]
    public void GraphQlStaysInTheReviewWhateverIntrospectionDoes()
    {
        var page = Landing();

        var graphql = page.Find("[data-testid=aqr-domain][data-domain='graphql']");
        graphql.QuerySelector("[data-testid=aqr-domain-state]")!.TextContent.Trim().Should().NotBe("Not included");
        graphql.TextContent.Should().NotContain("GraphQL unavailable");
    }

    // §35. Robustness is described conservatively for a read-only review.
    [Fact]
    public void ErrorHandlingIsDescribedAsSafeRequestsOnly()
    {
        var page = Landing();

        var errors = page.Find("[data-testid=aqr-domain][data-domain='errors']");
        errors.QuerySelector(".aqr-domain-purpose")!.TextContent.Should().Be("Safe, read-only error behaviour is reviewed.");
        errors.QuerySelector("[data-testid=aqr-domain-limitation]")!.TextContent
            .Should().Be("Write or destructive behaviour is never executed.");
    }

    // ── §48. Post-run order ───────────────────────────────────────────────────────────────────────────────────────

    // 29, 30, 31, 32, 33, 34.
    [Fact]
    public async Task AfterARunTheResultLeadsAndTheConfigurationFollows()
    {
        var page = await Result();

        page.FindAll("[data-testid=aqr-result-summary], [data-testid=aqr-key-findings], [data-testid=aqr-tabpanel], [data-testid=aqr-result-details]")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("aqr-result-summary", "aqr-key-findings", "aqr-tabpanel", "aqr-result-details");

        // 32, 33, 34. Coverage, the read-only explanation and the configuration are all collapsed detail now.
        foreach (var id in new[] { "aqr-coverage-disclosure", "aqr-limitations", "aqr-readonly", "aqr-targets-disclosure", "aqr-access-details" })
        {
            Collapsed(page, id).Should().BeTrue($"{id} is supporting detail after a run");
            Body(page, id).Closest("[data-testid=aqr-result-details]").Should().NotBeNull();
        }

        // 31. The service overview is result content and sits above the diagnostics.
        page.Find("[data-testid=aqr-services-table]").Closest("[data-testid=aqr-result-details]").Should().BeNull();
        // The pre-run decision wall is gone entirely.
        page.FindAll("[data-testid=aqr-decide]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-domains]").Should().BeEmpty();
    }

    // 37 (§37). Re-running is one click from the result.
    [Fact]
    public async Task RunAgainIsVisibleOnTheResult()
    {
        var page = await Result();

        var run = page.Find("[data-testid=aqr-result-summary] [data-testid=aqr-run]");
        run.TextContent.Trim().Should().Be("Run API Quality Review again");
        run.Closest(".disclosure-body").Should().BeNull();
    }

    // ── §49. Manual review stays distinct from findings ───────────────────────────────────────────────────────────

    // 35, 36, 37, 38.
    [Fact]
    public async Task ManualReviewObligationsAreNeverCountedAsFindings()
    {
        var page = await Result();

        var manual = page.Find("[data-testid=aqr-manual-review]");
        manual.TextContent.Should().Contain("review obligations, not findings");
        manual.TextContent.Should().Contain("Write operations").And.Contain("Access control between roles/tenants");
        page.Find("[data-testid=aqr-manual-count]").TextContent.Should().StartWith("3 areas remain");

        // 38. The finding total counts findings only; the three obligations are not added to it.
        page.Find("[data-testid=aqr-all-findings]").TextContent.Should().Be("All source findings (2)");
        var severities = page.FindAll("[data-testid=aqr-sev]").Select(s => s.TextContent.Trim()).ToList();
        severities.Should().Contain("Low 2");
        severities.Sum(s => int.Parse(s.Split(' ')[^1])).Should().Be(2, "severity counts are finding counts, nothing else");
    }

    // ── §50. Result semantics ─────────────────────────────────────────────────────────────────────────────────────

    // 39.
    [Fact]
    public async Task NoUnsupportedPassOrComplianceWordingAppearsInTheResult()
    {
        var page = await Result();

        var asserted = page.Find("[data-testid=aqr-result-summary]").TextContent;
        foreach (var claim in new[] { "Passed", "Secure", "Compliant", "Healthy" })
            asserted.Should().NotContain(claim);
    }

    // 40. Partial authenticated coverage coexists with a completed review.
    [Fact]
    public async Task PartialCoverageIsReportedAsPartialCoverage_NotAsFailure()
    {
        var page = await Result(authenticated: false);

        // Execution state and manual-review obligation are separate badges: the execution state alone must still be exact.
        page.Find("[data-testid=aqr-result-state]").QuerySelectorAll("span")[0].TextContent.Trim().Should().Be("Partial coverage");
        page.Find("[data-testid=aqr-result-manual]").TextContent.Should().Contain("Manual review required");
        page.Find("[data-testid=aqr-result-summary-text]").TextContent
            .Should().Contain("could not be reached").And.Contain("never as a pass");
        // 42. Blocked targets are reported as blocked, never as zero issues.
        page.Find("[data-testid=aqr-services-table]").TextContent.Should().Contain("Authentication required").And.Contain("Not tested");
    }

    // 41, 43. A review where nothing could be reached is an execution report, not a quality one.
    [Fact]
    public void AResultWhereNothingExecutedIsNotAQualityStatement()
    {
        var report = new ApiReviewReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Targets =
            [
                new ApiReviewTargetResult
                {
                    Target = new ApiReviewTarget { TargetId = "t1", ServiceName = "svc", Host = "x.test", BasePath = "/api", ApiType = ApiReviewTargetType.Rest, AuthRequired = true },
                    AccessMode = ApiReviewAccessMode.Unavailable, Status = ApiReviewTargetStatus.Blocked,
                },
            ],
        };

        var result = ApiReviewPresentation.Result(report);

        result.State.Should().Be(ApiReviewResultState.FailedToRun);
        ApiReviewResultStates.IsCompleted(result.State).Should().BeFalse();
        result.FindingCount.Should().Be(0);
        result.Summary.Should().Be("No selected API target could be reviewed, so nothing can be concluded about them.");
        result.Summary.Should().NotContain("0 findings");
    }

    // ── §42. Accessibility of the restructured page ───────────────────────────────────────────────────────────────

    [Fact]
    public void HeadingsAreHierarchicalAndDisclosuresExposeTheirState()
    {
        var page = Landing();

        page.FindAll("h1").Should().ContainSingle();
        var h2 = page.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        h2.Should().Contain(["Target", "Review scope", "API access", "What will be reviewed", "Review details"]);
        h2.Should().OnlyHaveUniqueItems();

        foreach (var toggle in page.FindAll(".disclosure-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("type").Should().Be("button");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            page.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty();
        }
        page.FindAll(".aqr-pill").Should().OnlyContain(p => p.TextContent.Trim().Length > 0, "state chips carry text, never colour alone");
    }

    // ── Returning to the setup view ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The result page offered no way back to the setup view: only Export HTML at the top and "Run API Quality Review
    /// again" inside the result, which is not navigation — it starts work. Frontend Quality Review already had the
    /// pattern, so this is the same one.
    /// </summary>
    [Fact]
    public async Task TheResultOffersBackToTheSetupViewBesideExport()
    {
        var page = await Result();

        var actions = page.Find(".page-header").QuerySelectorAll("button")
            .Select(b => b.TextContent.Trim()).ToList();

        // 1, 7. Navigate first, then export — the same order the Frontend Quality Review result uses.
        actions.Should().Equal("Back to API Quality Review", "Export HTML");
        // 8. Reachable by its accessible name, and never an icon alone.
        page.Find("[data-testid=aqr-back]").TextContent.Trim().Should().Be("Back to API Quality Review");
    }

    // 3. Back navigates. It does not start a review, and the run is never re-issued.
    [Fact]
    public async Task BackReturnsToSetupWithoutRunningAnything()
    {
        var page = await Result();
        _review.Invocations.Clear();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-back]").Click());

        page.WaitForAssertion(() => page.Find("[data-testid=aqr-decide]"));
        page.FindAll("[data-testid=aqr-result-summary]").Should().BeEmpty();
        _review.Verify(r => r.RunAsync(It.IsAny<ApiReviewRunRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "Back is navigation; Run again is the action that starts work");
    }

    // 4, 6, 9. The completed run survives Back: it stays in history and comes back when the page is re-entered.
    [Fact]
    public async Task BackPreservesTheCompletedRun()
    {
        var page = await Result();
        var before = _history.For("dev").LastReport;
        before.Should().NotBeNull();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-back]").Click());
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-decide]"));

        // Nothing was deleted or reset: the history entry is the same object the run recorded.
        _history.For("dev").LastReport.Should().BeSameAs(before);
        // And re-entering the page shows that result again, exactly as it does after navigating away and returning.
        var reopened = Render<ApiQualityReview>();
        reopened.WaitForAssertion(() => reopened.Find("[data-testid=aqr-result-summary]"));
    }

    // 2, 4. Back leaves the target selection alone. Rebuilding it from the saved configuration — which the page's own
    // context refresh does — would silently discard whatever the user had ticked before running.
    [Fact]
    public async Task BackKeepsTheTargetSelectionTheRunUsed()
    {
        var page = await Result();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-back]").Click());
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-decide]"));

        var selected = page.FindAll("[data-testid=aqr-target]").Count(t => t.ClassList.Contains("aqr-target-selected"));
        selected.Should().BePositive("the scope the review ran with is still selected");
    }

    // 5. The rerun action is unchanged and still runs a review.
    [Fact]
    public async Task RunAgainStillStartsAReview()
    {
        var page = await Result();
        _review.Invocations.Clear();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-run]").Click());

        page.WaitForAssertion(() =>
            _review.Verify(r => r.RunAsync(It.IsAny<ApiReviewRunRequest>(), It.IsAny<CancellationToken>()), Times.Once));
        page.Find("[data-testid=aqr-result-summary]").Should().NotBeNull();
    }

    // 6. Export is unchanged and still exports the completed result.
    [Fact]
    public async Task ExportStillExportsTheResult()
    {
        var page = await Result();
        var export = Services.GetRequiredService<IReportExportService>();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-export]").Click());

        Mock.Get(export).Verify(e => e.ExportApiReview(It.IsAny<ApiReviewReport>(), It.IsAny<string>()), Times.Once);
    }
}
