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
/// Redesigned API Quality Review page: target → access → API targets → contracts → readiness/run, then results. Authenticated
/// availability is never confused with "API requires authentication", missing contracts are not failures, technical detail is
/// behind collapsed disclosures, severity counts carry text, and manual-review obligations stay distinct from findings.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ApiHost = "api-dev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private static FrontendAnalysisContext Context(AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy, bool requiresAuth = true, string? swagger = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", SwaggerUrl = swagger };
        profile.Authentication.AuthenticatedTestingMethod = method;
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", SwaggerUrl = swagger, RequiresAuthentication = requiresAuth,
            AuthenticationType = profile.Authentication.AuthenticationType, ReviewIdentity = new AuthenticatedReviewIdentity(method, "dev", "FP"),
        };
    }

    private static ObservedNetworkEndpoint Ep(string path, bool auth, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, GraphQlOperationType op = GraphQlOperationType.None, string? name = null, string method = "GET") => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = ApiHost, Port = 443, Path = path, Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : method, AuthObserved = auth, LastStatus = 200, Count = 3,
        FirstObservedAt = T0, LastObservedAt = T0, Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/barn/1", OperationType = op, OperationName = name,
    };

    /// <summary>The M2LB-like scenario: one authenticated REST service (2 GETs + 1 POST) and one GraphQL endpoint (query + mutation).</summary>
    private static ObservedNetworkEndpoint[] AutorisasjonEndpoints() =>
    [
        Ep("/api/autorisasjon/roller", auth: true), Ep("/api/autorisasjon/brukere", auth: true), Ep("/api/autorisasjon/brukere", auth: true, method: "POST"),
        Ep("/api/autorisasjon/graphql", auth: true, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "HentRoller"),
        Ep("/api/autorisasjon/graphql", auth: true, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Mutation, "OppdaterRolle"),
    ];

    private static string RestId => ApiReviewTargetResolver.Id(ApiReviewTargetType.Rest, $"https://{ApiHost}", "/api/autorisasjon");
    private static string GqlId => ApiReviewTargetResolver.Id(ApiReviewTargetType.GraphQl, $"https://{ApiHost}", "/api/autorisasjon/graphql");

    private Mock<IApiReviewService> _review = new();
    private Mock<IReportExportService> _export = new();
    private EndpointDiscoveryService _discovery = new();
    private ApiReviewHistoryService _history = new();

    private void Register(FrontendAnalysisContext context, bool authenticated, ObservedNetworkEndpoint[] endpoints, Func<ApiReviewRunRequest, ApiReviewReport>? report = null, AuthenticatedApiContextStatus? contextStatus = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        if (endpoints.Length > 0) _discovery.MergeObservedAsync(new Mock<IJSRuntime>().Object, "dev", endpoints).GetAwaiter().GetResult();
        var caps = new Mock<IAuthenticatedReviewCapabilitiesService>();
        caps.Setup(c => c.ResolveAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedReviewCapabilities
            {
                Method = context.ReviewIdentity!.Method, PublicApi = true, AuthenticatedApi = authenticated, AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated,
                ContextStatus = contextStatus ?? (authenticated ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic),
                Reason = authenticated ? "Authenticated via Local HTTPS Proxy" : "Start the local proxy",
            });
        _review.Setup(r => r.RunAsync(It.IsAny<ApiReviewRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ApiReviewRunRequest request, CancellationToken _) => Task.FromResult<(ApiReviewReport?, string?)>(((report ?? (q => StubReport(q, authenticated)))(request), null)));
        _export.Setup(e => e.ExportApiReview(It.IsAny<ApiReviewReport>(), It.IsAny<string>())).Returns("<html></html>");
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton<IApiReviewHistoryService>(_history);
        Services.AddSingleton(caps.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(_export.Object);
    }

    /// <summary>Report shaped like the engine's: per-target access mode/status from the auth situation, realistic coverage, findings and manual items.</summary>
    private static ApiReviewReport StubReport(ApiReviewRunRequest request, bool authenticated)
    {
        var targets = request.Targets.Select(t =>
        {
            var blocked = t.AuthRequired && !authenticated;
            return new ApiReviewTargetResult
            {
                Target = t,
                AccessMode = t.AuthRequired ? (authenticated ? ApiReviewAccessMode.AuthenticatedHttp : ApiReviewAccessMode.Unavailable) : ApiReviewAccessMode.PublicHttp,
                Status = blocked ? ApiReviewTargetStatus.Blocked : ApiReviewTargetStatus.Completed,
                AccessReason = blocked ? "Authenticated API context not available." : "Authenticated HTTP via the Local HTTPS proxy context (read-only, executed by the backend gateway).",
                RequiredAction = blocked ? ApiReviewRunEligibility.NoAuthContextAction : null,
                FindingCount = blocked ? null : (t.ApiType == ApiReviewTargetType.Rest ? 2 : 1),
                Contract = t.ApiType == ApiReviewTargetType.GraphQl
                    ? new ApiReviewContractSummary { Kind = "GraphQL schema", Available = !blocked, Status = blocked ? ApiReviewCheckResult.Blocked : ApiReviewCheckResult.Pass, IntrospectionEnabled = blocked ? null : true, OperationCount = 12, TypeCount = 30, MutationCount = 4, Note = blocked ? "" : "Schema retrieved by introspection." }
                    : null,
                Operations = blocked ? [] : t.Operations.Where(o => o.IsSafe).Select(o => new ApiReviewOperationResult { Display = o.Display, Method = o.Method, Path = o.Path, AccessMode = ApiReviewAccessMode.AuthenticatedHttp, Executed = true, StatusCode = 200, ContentType = "application/json", ElapsedMs = 120, ContentLength = 2048, Result = ApiReviewCheckResult.Pass }).ToList(),
                Baseline = blocked ? null : new ApiReviewBaseline { TargetId = t.TargetId, RecordedAt = DateTimeOffset.UtcNow },
            };
        }).ToList();
        var assessed = targets.Where(t => t.Status == ApiReviewTargetStatus.Completed).ToList();
        var findings = new List<ApiReviewFinding>();
        foreach (var t in assessed)
        {
            findings.Add(new ApiReviewFinding { Id = $"f-{t.Target.TargetId}-1", TargetId = t.Target.TargetId, Severity = ApiReviewSeverity.Low, Type = ApiReviewFindingType.Security, Endpoint = t.Target.Url, Check = "Security headers", Title = "Cache-Control not set to no-store", Description = "Authenticated JSON responses are cacheable.", Evidence = ["cache-control: absent"], Recommendation = "Send Cache-Control: no-store." });
            if (t.Target.ApiType == ApiReviewTargetType.Rest)
                findings.Add(new ApiReviewFinding { Id = $"f-{t.Target.TargetId}-2", TargetId = t.Target.TargetId, Severity = ApiReviewSeverity.Info, Type = ApiReviewFindingType.Performance, Endpoint = t.Target.Url, Check = "Response time", Title = "Response time above warning threshold", Description = "620 ms observed.", Recommendation = "Investigate server-side latency." });
        }
        var auth = targets.Where(t => t.AccessMode is ApiReviewAccessMode.AuthenticatedHttp or ApiReviewAccessMode.Unavailable).ToList();
        var pub = targets.Where(t => t.AccessMode == ApiReviewAccessMode.PublicHttp).ToList();
        return new ApiReviewReport
        {
            Environment = request.Environment, Policy = request.Policy, GeneratedAt = DateTimeOffset.UtcNow, Access = new AuthenticatedReviewCapabilities { AuthenticatedApi = authenticated },
            Targets = targets, Findings = findings,
            Coverage = new ApiReviewCoverage
            {
                RestOperationsTotal = 2, RestOperationsReviewed = assessed.Any(t => t.Target.ApiType == ApiReviewTargetType.Rest) ? 2 : 0,
                GraphQlOperationsObserved = 2, GraphQlOperationsMatched = assessed.Any(t => t.Target.ApiType == ApiReviewTargetType.GraphQl) ? 1 : 0,
                ContractChecks = 3, SecurityChecks = 17,
                AuthenticatedPlanned = auth.Count, AuthenticatedExecuted = auth.Count(t => t.Status == ApiReviewTargetStatus.Completed),
                PublicPlanned = pub.Count, PublicExecuted = pub.Count(t => t.Status == ApiReviewTargetStatus.Completed),
                TargetsBlocked = targets.Count(t => t.Status == ApiReviewTargetStatus.Blocked), TargetsCompleted = assessed.Count, UnsafeOperationsNotExecuted = 2,
            },
            ManualReviewItems = ["Write operations (POST/PUT/PATCH/DELETE, GraphQL mutations): behaviour, idempotency and side effects.", "Access control between roles/tenants.", "Business correctness of returned data."],
            Limitations = ["Automated API review is read-only: REST GET/HEAD/OPTIONS and GraphQL queries only.", "Response bodies are parsed transiently; only JSON paths and types are recorded, never values."],
        };
    }

    private static IElement Run(IRenderedComponent<ApiQualityReview> page) => page.Find("[data-testid=aqr-run]");

    private async Task<IRenderedComponent<ApiQualityReview>> RenderAndRun()
    {
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => Run(page).HasAttribute("disabled").Should().BeFalse());
        await page.InvokeAsync(() => Run(page).Click());
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-results]"));
        return page;
    }

    // 1. Target summary renders the selected environment.
    [Fact]
    public void TargetSummary_RendersEnvironmentCompactly_WithTechnicalDetailsCollapsed()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-access-mode]").GetAttribute("data-availability").Should().Be("Available"));

        page.Find("[data-testid=aqr-env-name]").TextContent.Should().Be("M2LB DEV");
        page.Find("[data-testid=aqr-environment]").TextContent.Should().Contain("Dev");
        page.Find("[data-testid=aqr-env-url]").TextContent.Should().Be(Origin + "/");
        // The Target card states the environment's own sign-in policy. It is labelled as such, because "Authentication"
        // next to an API access card reporting authenticated access available read as a contradiction.
        page.Find("[data-testid=aqr-env-auth]").TextContent.Should().Be("Microsoft Entra ID");
        page.Find("[data-testid=aqr-environment]").TextContent.Should().Contain("Frontend sign-in");
        // API access owns the access state and Readiness owns the review state; the Target card repeats neither.
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Contain("Available");
        page.Find("[data-testid=aqr-environment]").TextContent.Should().NotContain("Authenticated available");
        page.FindAll("[data-testid=aqr-status-pill]").Should().BeEmpty();
        // Discovered REST traffic carries no published contract, so the review runs without contract validation.
        page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review can run with limitations");
        page.Find("h1").TextContent.Should().Be("API Quality Review");
        page.Find(".page-lead").TextContent.Should().StartWith("Review discovered REST and GraphQL APIs");
        var toggle = page.Find("[data-testid=aqr-target-details-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-target-details-body]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=aqr-target-details-body]").TextContent.Should().Contain("Local HTTPS proxy").And.Contain("never receives a token");
        page.Find("[data-testid=aqr-environment]").TextContent.Should().NotContain("ACTIVE TARGET").And.NotContain("Production policy");
    }

    // 2. Public-only access state renders correctly (no API requires authentication).
    /// <summary>
    /// A missing authenticated context is not a limitation when nothing selected needs authentication. The review is
    /// still limited here — by the absent REST contract — and the point is that the limitation is not about access.
    /// </summary>
    [Fact]
    public void PublicOnlyTargets_WithoutAuthContext_AreNotLimitedByAuthentication()
    {
        Register(Context(requiresAuth: false), authenticated: false, [Ep("/api/public/status", auth: false)]);
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));
        page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review can run with limitations");
        page.Find("[data-testid=aqr-readiness]").TextContent
            .Should().Contain("REST contract validation is unavailable")
            .And.NotContain("Authenticated requests cannot be sent", "nothing selected needs authentication");

        page.FindAll("[data-testid=aqr-scope-auth]").Should().BeEmpty("no selected API needs authentication, so the scope says nothing about it");
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Waiting for authenticated traffic");
        page.FindAll("[data-testid=aqr-frontend-signin-help]").Should().BeEmpty("no selected API needs authentication, so there is nothing to distinguish");
        page.FindAll("[data-testid=aqr-auth-missing]").Should().BeEmpty("no selected API needs authentication, so no warning block");
        page.Find("[data-testid=aqr-readiness-items]").TextContent.Should().Contain("No selected API requires authentication").And.Contain("Read-only review available");
        page.Find("[data-testid=aqr-target-access]").TextContent.Should().Be("Public");
        Run(page).HasAttribute("disabled").Should().BeFalse();
    }

    // 3. Authenticated-unavailable is not confused with an auth-required API.
    [Fact]
    public void AuthRequiredTargets_WithoutContext_ShowRequirementOnTargets_AndUnavailabilityOnAccess()
    {
        Register(Context(), authenticated: false, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked"));

        page.FindAll("[data-testid=aqr-target-access]").Should().OnlyContain(e => e.TextContent == "Auth required");
        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Be("Waiting for authenticated traffic");
        page.Find("[data-testid=aqr-environment]").TextContent.Should().NotContain("Authenticated unavailable", "the access card states this once");
        var steps = page.Find("[data-testid=aqr-auth-missing]");
        steps.QuerySelectorAll("li").Should().HaveCount(5);
        steps.TextContent.Should().Contain("Start the Local HTTPS Proxy").And.Contain("Sign in and perform an authenticated action");
        page.FindAll("[data-testid=aqr-access-action]").Should().BeEmpty("the blocker owns the one action");
        page.Find("[data-testid=aqr-run-action]").TextContent.Should().Be("Open Authentication setup");
        page.Find("[data-testid=aqr-readiness]").GetAttribute("role").Should().Be("alert");
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().StartWith("Both selected APIs require authenticated access");
        // TextContent includes hidden descendants, so the collapsed details must be subtracted to
        // assert on what is actually visible before the disclosure is expanded.
        var accessText = page.Find("[data-testid=aqr-access]").TextContent;
        var collapsedText = page.Find("[data-testid=aqr-access-details-body]").TextContent;
        accessText.Replace(collapsedText, "").Should()
            .NotContain("backend gateway", "gateway mechanics live in the collapsed details only");
        page.Find("[data-testid=aqr-access-details-body]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=aqr-access-details-body]").TextContent.Should().Contain("backend gateway");
    }

    [Fact]
    public void MixedTargets_WithoutContext_IsLimited_WithWarningItem_AndRunEnabled()
    {
        Register(Context(), authenticated: false, [Ep("/api/autorisasjon/roller", auth: true), Ep("/api/public/status", auth: false)]);
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        page.Find("[data-testid=aqr-readiness]").GetAttribute("role").Should().Be("status");
        page.Find("[data-testid=aqr-readiness]").QuerySelector("h2")!.TextContent.Should().Contain("Review can run with limitations");
        page.Find("[data-testid=aqr-readiness-items]").TextContent.Should().Contain("Authenticated API context unavailable").And.Contain("1 target will be reported as authentication required");
        Run(page).HasAttribute("disabled").Should().BeFalse();
        page.FindAll("[data-testid=aqr-run-row] p, [data-testid=aqr-run-row] .aqr-warn").Should().BeEmpty("no sentence wall next to the Run button");
    }

    // 4. Authenticated available state.
    [Fact]
    public void AuthenticatedAvailable_RendersAvailableAndReady()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        // Limited, not Ready: these targets come from discovered traffic and have no published REST contract.
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        page.Find("[data-testid=aqr-access-mode]").TextContent.Should().Contain("Available");
        page.Find("[data-testid=aqr-access]").TextContent.Should().Contain("Available");
        page.FindAll("[data-testid=aqr-auth-missing]").Should().BeEmpty("no selected API needs authentication");
        page.Find("[data-testid=aqr-readiness-items]").TextContent.Should().Contain("Authenticated requests available for 2 targets");
        // The panel legitimately explains that no token is received, so the bare words cannot be
        // the assertion. What must never appear is an actual credential value.
        page.Find("[data-testid=aqr-access]").TextContent.Should()
            .NotContainAny("Bearer ", "eyJ", "Authorization:", "Set-Cookie", "Cookie:");
    }

    // 5/6/7/8. Grouping, collapsed operations, selected count.
    [Fact]
    public void Targets_GroupedByProtocol_OperationsCollapsed_SelectedCountTracksCheckboxes()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-target]").Should().HaveCount(2));

        // One compact table, REST first; the Type column replaces the per-protocol groups.
        var rows = page.FindAll("[data-testid=aqr-targets-table] tr[data-testid=aqr-target]");
        rows.Select(g => g.GetAttribute("data-type")).Should().Equal("Rest", "GraphQl");
        rows.Select(r => r.QuerySelector("[data-testid=aqr-target-type]")!.TextContent).Should().Equal("REST", "GraphQL");
        rows[0].QuerySelector("[data-testid=aqr-target-name]")!.TextContent.Should().Be("Autorisasjon API");
        rows[0].QuerySelector("[data-testid=aqr-target-path]")!.TextContent.Should().Be("/api/autorisasjon");
        rows[0].QuerySelector("[data-testid=aqr-target-path]")!.GetAttribute("title").Should().Be($"https://{ApiHost}/api/autorisasjon");
        rows[0].QuerySelector("[data-testid=aqr-target-operations]")!.TextContent.Should().Be("3 · 1 write (not executed)");
        rows[1].QuerySelector("[data-testid=aqr-target-name]")!.TextContent.Should().Be("Autorisasjon GraphQL");
        // Pre-run this is a plan. The schema has not been fetched yet, so the row does not say it is available.
        rows[1].QuerySelector("[data-testid=aqr-target-contract]")!.TextContent.Should().Be("Schema retrieval pending");

        var opsToggle = page.Find($"[data-testid='aqr-ops-{RestId}-toggle']");
        opsToggle.GetAttribute("aria-expanded").Should().Be("false");
        var body = page.Find($"[data-testid='aqr-ops-{RestId}-body']");
        body.HasAttribute("hidden").Should().BeTrue();
        body.QuerySelectorAll("[data-testid=aqr-op-row]").Should().HaveCount(3);
        body.TextContent.Should().Contain("Not executed · manual review").And.Contain($"https://{ApiHost}/api/autorisasjon");
        opsToggle.Click();
        page.Find($"[data-testid='aqr-ops-{RestId}-body']").HasAttribute("hidden").Should().BeFalse();
        var gqlBody = page.Find($"[data-testid='aqr-ops-{GqlId}-body']");
        gqlBody.QuerySelectorAll("[data-testid=aqr-op-row]").Select(r => r.QuerySelector("td")!.TextContent).Should().BeEquivalentTo(["Query", "Mutation"]);

        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("2 API targets selected");
        page.FindAll("[data-testid=aqr-target-checkbox]").Should().OnlyContain(c => c.GetAttribute("aria-label")!.StartsWith("Include "));
        page.FindAll("[data-testid=aqr-target-checkbox]")[1].Change(false);
        page.Find("[data-testid=aqr-scope-headline]").TextContent.Should().Be("1 API target selected");
    }

    // 9/10/11. Contracts: missing OpenAPI is not a failure, runtime schema labelled, baseline count.
    [Fact]
    public void Contracts_MissingOpenApiIsUnavailableNotFailed_RuntimeSchemaLabelled_NoBaselineYet()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-contract-row]").Should().HaveCount(2));

        var rest = page.Find("[data-testid=aqr-contract-row][data-protocol='REST']");
        rest.GetAttribute("data-state").Should().Be("NotConfigured");
        rest.QuerySelector("[data-testid=aqr-contract-state]")!.TextContent.Should().Contain("No published OpenAPI contract");
        rest.TextContent.Should().NotContainAny("Fail", "fail");
        page.Find("[data-testid=aqr-contract-details-body]").TextContent.Should().Contain("records the observed JSON structure");
        var gql = page.Find("[data-testid=aqr-contract-row][data-protocol='GraphQL']");
        gql.GetAttribute("data-state").Should().Be("RuntimeSchema");
        gql.QuerySelector("[data-testid=aqr-contract-state]")!.TextContent.Should().Contain("Retrieved during review");
        page.Find("[data-testid=aqr-contracts]").TextContent.Should().NotContain("Not applicable");
        page.Find("[data-testid=aqr-baselines]").TextContent.Should().Contain("No previous baseline");
        page.Find("[data-testid=aqr-latest-comparison]").TextContent.Should().Be("Not compared yet");
        page.Find("[data-testid=aqr-contract-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-contract-details-body]").TextContent.Should().Contain("policy observation, not as a failure");
        page.Find("[data-testid=aqr-contracts] .aqr-contract-rows").TextContent.Should().NotContain("policy observation", "long explanations stay behind the disclosure");
    }

    [Fact]
    public async Task Contracts_AfterARun_BaselineCountAndComparisonReflectHistory()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = await RenderAndRun();

        page.Find("[data-testid=aqr-back]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-baselines]").TextContent.Should().Contain("2 previous baselines"));
        page.Find("[data-testid=aqr-latest-comparison]").TextContent.Should().Contain("first review records the baseline");
        page.Find("[data-testid=aqr-contract-details-body]").TextContent.Should().Contain("Schema available");

        await page.InvokeAsync(() => Run(page).Click());
        page.Find("[data-testid=aqr-back]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-latest-comparison]").TextContent.Should().Be("Not compared yet (no drift checks recorded)"));
    }

    [Fact]
    public void Contracts_ConfiguredOpenApi_IsAvailable()
    {
        Register(Context(swagger: $"https://{ApiHost}/swagger/v1/swagger.json"), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-contract-row][data-protocol='REST']").GetAttribute("data-state").Should().Be("Available"));
        page.Find("[data-testid=aqr-target][data-type='Rest'] [data-testid=aqr-target-contract]").TextContent.Should().Be("OpenAPI contract");
    }

    // 12/14/15. Read-only visible, blocking reason, results hidden before a run.
    [Fact]
    public void BeforeRun_ReadOnlyVisible_NoResults_NoSuccessWording()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        // Limited, not Ready: these targets come from discovered traffic and have no published REST contract.
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        page.Find("[data-testid=aqr-readiness-items]").TextContent.Should().Contain("Read-only review available");
        page.FindAll("[data-testid=aqr-results]").Should().BeEmpty();
        page.FindAll("[data-testid=aqr-export]").Should().BeEmpty("export needs a report");
        page.FindAll("[data-testid=aqr-finding-counts]").Should().BeEmpty();
        // "secure" alone matches the access panel's "existing secure gateway session", which
        // describes the transport rather than claiming the API is secure. Assert the verdict
        // wording that would actually constitute an unearned pass.
        page.Markup.Should().NotContain("Review results")
            .And.NotContainAny("Passed", "is secure", "security approved", "compliant", "penetration");
    }

    [Fact]
    public void NoActiveTarget_BlockingReasonShown()
    {
        var ctx = new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment", ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } };
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(ctx);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IApiReviewHistoryService, ApiReviewHistoryService>();
        Services.AddSingleton(Mock.Of<IAuthenticatedReviewCapabilitiesService>());
        Services.AddSingleton(Mock.Of<IApiReviewService>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-active-target-error]").TextContent.Should().Be("No active Target Environment"));
        page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=aqr-run-reason]").TextContent.Should().Be("No active Target Environment");
        page.Find("[data-testid=aqr-run-action]").TextContent.Should().Be("Open Target Environments");
        Run(page).HasAttribute("disabled").Should().BeTrue();
    }

    // 13/16/17/18/19/20/21/22. Run wiring and results.
    [Fact]
    public async Task Run_InvokesOrchestration_ThenSummaryCoverageSeverityAndOverviewRender()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = await RenderAndRun();

        _review.Verify(r => r.RunAsync(It.Is<ApiReviewRunRequest>(q => q.Targets.Count == 2 && q.Policy.ReadOnly), It.IsAny<CancellationToken>()), Times.Once);
        page.Find("#aqr-result-heading").TextContent.Should().Be("Review result");
        page.Find("[data-testid=aqr-result-env]").TextContent.Should().Be("M2LB DEV");
        page.Find("[data-testid=aqr-result-services]").TextContent.Should().Be("REST 1 · GraphQL 1");
        page.Find("[data-testid=aqr-result-access]").TextContent.Should().Be("Authenticated");
        page.Find("[data-testid=aqr-coverage]").TextContent.Should().Contain("2 of 2 authentication-required targets reviewed with authenticated requests");

        var severities = page.FindAll("[data-testid=aqr-sev]");
        severities.Should().HaveCount(5);
        // StubReport emits one Low finding per completed target plus one Info performance finding
        // for REST targets: REST = Low + Info, GraphQL = Low.
        severities.Select(sv => sv.TextContent.Trim()).Should().Equal("Critical 0", "High 0", "Medium 0", "Low 2", "Info 1");
        severities.Should().OnlyContain(s => s.QuerySelector(".aqr-sev-label") != null && s.QuerySelector(".aqr-sev-count") != null);

        var coverage = page.FindAll("[data-testid=aqr-coverage-row]");
        coverage.Select(r => r.GetAttribute("data-coverage")).Should().Contain(["REST", "GraphQL", "Contracts", "Security", "Access", "Write operations"]);
        page.Find("[data-testid=aqr-coverage-row][data-coverage='REST']").TextContent.Should().Contain("2 / 2 operations reviewed");
        page.Find("[data-testid=aqr-coverage-row][data-coverage='GraphQL']").TextContent.Should().Contain("1 / 2 matched to the runtime schema");
        page.Find("[data-testid=aqr-coverage-row][data-coverage='Security']").TextContent.Should().Contain("17 passive, read-only checks executed");
        page.Find("[data-testid=aqr-coverage-row][data-coverage='Contracts']").TextContent.Should().Contain("No REST contract configured").And.Contain("GraphQL runtime schema available");

        // Simplified overview table with expandable service details.
        page.FindAll("[data-testid=aqr-services-table] thead th").Select(h => h.TextContent).Should().Equal("Service", "Type", "Access", "Review status", "Contract", "Source findings");
        var rows = page.FindAll("[data-testid=aqr-service-row]");
        rows.Should().HaveCount(2);
        // "Reviewed": the service took part in the review. Whether every domain was covered is the contract column's job.
        rows[0].QuerySelector("[data-testid=aqr-service-status]")!.TextContent.Should().Be("Reviewed");
        rows[0].QuerySelector("[data-testid=aqr-service-access]")!.TextContent.Should().Be("Authenticated");
        rows[0].QuerySelector("[data-testid=aqr-service-contract]")!.TextContent.Should().Be("No contract configured");
        rows[1].QuerySelector("[data-testid=aqr-service-contract]")!.TextContent.Should().Be("Runtime schema");
        // The backend's raw "Completed" status never reaches the service table; it is translated to a review status.
        page.Find("[data-testid=aqr-services-table]").TextContent.Should().NotContain("Completed");
        var toggle = rows[0].QuerySelector("[data-testid=aqr-service-toggle]")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        var details = page.Find($"[data-testid=aqr-service-details][data-target-id='{RestId}']");
        details.HasAttribute("hidden").Should().BeTrue();
        details.TextContent.Should().Contain($"https://{ApiHost}/api/autorisasjon").And.Contain("Discovered traffic");
        toggle.Click();
        page.Find($"[data-testid=aqr-service-details][data-target-id='{RestId}']").HasAttribute("hidden").Should().BeFalse();
        page.Find($"[data-testid=aqr-service-row][data-target-id='{RestId}'] [data-testid=aqr-service-toggle]").GetAttribute("aria-expanded").Should().Be("true");

        // Manual review and limitations are distinct from findings.
        var manual = page.Find("[data-testid=aqr-manual-review]");
        manual.TextContent.Should().Contain("Manual review still required").And.Contain("Access control between roles/tenants").And.Contain("not findings");
        manual.QuerySelectorAll(".aqr-sev").Should().BeEmpty();
        var limitations = page.Find("[data-testid=aqr-limitations]");
        limitations.TextContent.Should().Contain("intentionally read-only");
        page.Find("[data-testid=aqr-limitations-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-limitations-list]").TextContent.Should().Contain("only JSON paths and types are recorded, never values");
        page.Find("[data-testid=aqr-readonly]").TextContent.Should().Contain("does not execute write operations").And.Contain("says nothing about whether they are safe");
        page.Find("[data-testid=aqr-key-findings] .aqr-key-findings").QuerySelectorAll("li").Should().HaveCount(3, "KeyFindings caps at max=5 and does not filter by severity, so all 3 stub findings show");
        page.Markup.Should().NotContainAny("Passed", "penetration", "security approved");
    }

    [Fact]
    public async Task Run_WithoutAuthContext_BlockedTargetIsAuthenticationRequired_NotTested_NeverAPass()
    {
        Register(Context(), authenticated: false, [Ep("/api/autorisasjon/roller", auth: true), Ep("/api/public/status", auth: false)]);
        var page = await RenderAndRun();

        var statuses = page.FindAll("[data-testid=aqr-service-status]").Select(e => e.TextContent).ToList();
        statuses.Should().BeEquivalentTo(["Authentication required", "Reviewed"]);
        // The blocked target is not tested; the public REST target carries the stub's two findings.
        page.FindAll("[data-testid=aqr-service-findings]").Select(e => e.TextContent).Should().BeEquivalentTo(["Not tested", "2"]);
        page.Find("[data-testid=aqr-result-access]").TextContent.Should().Be("Public only");
        page.Find("[data-testid=aqr-coverage]").TextContent.Should().Contain("0 of 1 authentication-required target reviewed").And.Contain("1 of 1 public target reviewed");
        page.Find("[data-testid=aqr-result-services]").TextContent.Should().Contain("1 not executed");
        page.FindAll("[data-testid=aqr-service-access]").Select(e => e.TextContent).Should().BeEquivalentTo(["Authentication required · not executed", "Public"]);
    }

    [Fact]
    public async Task Findings_CompactTableWithExpandableDetails_AndTabsNavigate()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = await RenderAndRun();

        var tabs = page.FindAll("[role=tab]");
        tabs.Select(t => t.TextContent).Should().Equal("Overview", "REST", "GraphQL", "Contracts", "Security", "Errors", "Performance", "Findings");
        tabs.Should().OnlyContain(t => t.GetAttribute("aria-controls") == "aqr-tabpanel");
        page.Find("[data-testid=aqr-tab-overview]").GetAttribute("aria-selected").Should().Be("true");
        page.Find("[data-testid=aqr-tab-overview]").GetAttribute("tabindex").Should().Be("0");
        page.Find("[data-testid=aqr-tab-findings]").GetAttribute("tabindex").Should().Be("-1");

        page.Find("[data-testid=aqr-tab-findings]").Click();
        page.Find("[data-testid=aqr-tabpanel]").GetAttribute("data-tab").Should().Be("Findings");
        page.Find("[data-testid=aqr-tab-findings]").GetAttribute("aria-selected").Should().Be("true");
        page.FindAll("[data-testid=aqr-findings-table] thead th").Select(h => h.TextContent.Trim()).Should().Equal("Severity", "Service", "Type", "Endpoint / operation", "Finding", "Details");
        var rows = page.FindAll("[data-testid=aqr-finding-row]");
        rows.Should().HaveCount(3, "stub report generates 2 for REST + 1 for GraphQL");
        rows[0].TextContent.Should().Contain("Autorisasjon");
        var toggle = rows[0].QuerySelector("[data-testid=aqr-finding-toggle]")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll("[data-testid=aqr-finding-details]").Should().OnlyContain(d => d.HasAttribute("hidden"));
        toggle.Click();
        var details = page.FindAll("[data-testid=aqr-finding-details]").First(d => !d.HasAttribute("hidden"));
        details.TextContent.Should().Contain("Recommendation").And.Contain("Evidence").And.Contain("cache-control: absent");

        page.Find("[data-testid=aqr-tab-security]").Click();
        page.Find("[data-testid=aqr-security-note]").TextContent.Should().Contain("does not establish that the API is secure");
        page.Find("[data-testid=aqr-tab-performance]").Click();
        page.Find("[data-testid=aqr-performance-note]").TextContent.Should().Contain("Not end-user or production performance");
        page.Find("[data-testid=aqr-tab-rest]").Click();
        page.FindAll("[data-testid=aqr-target-result]").Should().ContainSingle();
        page.Find("[data-testid=aqr-tab-contracts]").Click();
        page.Find("[data-testid=aqr-contracts-table]").TextContent.Should().Contain("GraphQL schema").And.Contain("Enabled");
        page.Find("[data-testid=aqr-tab-errors]").Click();
        page.Find("[data-testid=aqr-no-findings]").TextContent.Should().Contain("never as passes");
    }

    [Fact]
    public async Task ExportHtml_StillUsesReportExportService()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = await RenderAndRun();

        await page.InvokeAsync(() => page.Find("[data-testid=aqr-export]").Click());

        _export.Verify(e => e.ExportApiReview(It.Is<ApiReviewReport>(r => r.Targets.Count == 2), It.IsAny<string>()), Times.Once);
        JSInterop.VerifyInvoke("downloadHtmlFile");
    }

    // 24. Accessible semantics.
    [Fact]
    public async Task Semantics_DisclosuresTabsCheckboxesAndTablesAreAccessible()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = await RenderAndRun();

        foreach (var toggle in page.FindAll(".disclosure-toggle, .aqr-row-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("type").Should().Be("button");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            page.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty();
        }
        page.FindAll("h1").Should().ContainSingle();
        page.FindAll("h2").Select(h => h.TextContent.Trim()).Should().Contain(["Review result", "Key issues", "Review details"]);
        page.FindAll("[data-testid=aqr-target-checkbox]").Should().OnlyContain(c => c.HasAttribute("aria-label") && c.HasAttribute("id"));
        page.FindAll("table thead th").Should().OnlyContain(th => th.GetAttribute("scope") == "col");
        page.FindAll(".aqr-pill, .aqr-sev").Should().OnlyContain(p => p.TextContent.Trim().Length > 0, "state chips carry text");
        page.Find("[role=tablist]").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        page.Find("[role=tabpanel]").GetAttribute("aria-labelledby").Should().Be("aqr-tab-btn-overview");
    }
}
