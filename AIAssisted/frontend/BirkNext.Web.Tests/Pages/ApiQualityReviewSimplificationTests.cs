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
/// API Quality Review workflow simplification: the explicit run decision (can run / with limitations / cannot run), one
/// collapsed Diagnostics &amp; setup section, the unified contract/schema preparation, a short post-run Overview, Findings as
/// the one canonical occurrence list (logical issue ≠ occurrence), compact area summaries, Contracts as the contract owner,
/// grouped Security and the "not a load test" Performance statement.
/// </summary>
public sealed class ApiQualityReviewSimplificationTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string ApiHost = "api-dev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private readonly Mock<IApiReviewService> _review = new();
    private readonly EndpointDiscoveryService _discovery = new();
    private readonly ApiReviewHistoryService _history = new();

    private static FrontendAnalysisContext Context(bool requiresAuth = true)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", RequiresAuthentication = requiresAuth, AuthenticationType = profile.Authentication.AuthenticationType,
            ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP"),
        };
    }

    private static ObservedNetworkEndpoint Ep(string path, bool auth, ObservedTrafficCategory cat = ObservedTrafficCategory.Rest, GraphQlOperationType op = GraphQlOperationType.None, string? name = null) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = ApiHost, Port = 443, Path = path, Method = cat == ObservedTrafficCategory.GraphQl ? "POST" : "GET",
        AuthObserved = auth, LastStatus = 200, Count = 3, FirstObservedAt = T0, LastObservedAt = T0,
        Confidence = ObservedEndpointConfidence.Verified, PageOrigin = Origin, PagePath = "/barn/1", OperationType = op, OperationName = name,
    };

    private static ObservedNetworkEndpoint[] Mixed() =>
    [
        Ep("/api/autorisasjon/roller", auth: true),
        Ep("/api/public/graphql", auth: false, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "HentStatus"),
    ];

    private void Register(bool authenticated, ObservedNetworkEndpoint[] endpoints, bool requiresAuth = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context(requiresAuth));
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
            .Returns((ApiReviewRunRequest request, CancellationToken _) => Task.FromResult<(ApiReviewReport?, string?)>((Report(request), null)));
        Services.AddSingleton(factory.Object);
        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton<IApiReviewHistoryService>(_history);
        Services.AddSingleton(caps.Object);
        Services.AddSingleton<IGraphQlSchemaArtifactApiService>(new BirkNext.Web.Tests.Integration.FakeGraphQlSchemaArtifactApi());
        Services.AddSingleton(_review.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private static ApiReviewFinding Finding(string id, ApiReviewTargetResult t, ApiReviewSeverity severity, ApiReviewFindingType type, string rule, string title, string endpoint) => new()
    {
        Id = id, RuleId = rule, TargetId = t.Target.TargetId, Severity = severity, Type = type, Endpoint = endpoint, Check = title,
        Title = title, Description = "Synthetic test finding.", Recommendation = "Fix it.",
    };

    /// <summary>
    /// REST + GraphQL, both reviewed. Four source findings: the same HSTS rule on the shared API origin for both services (one
    /// logical issue, two occurrences), one Cache-Control finding on REST, one error-handling finding on GraphQL.
    /// </summary>
    private static ApiReviewReport Report(ApiReviewRunRequest request)
    {
        var targets = request.Targets.Select(t => new ApiReviewTargetResult
        {
            Target = t, AccessMode = t.AuthRequired ? ApiReviewAccessMode.AuthenticatedHttp : ApiReviewAccessMode.PublicHttp,
            Status = ApiReviewTargetStatus.Completed, AccessReason = "Reviewed.",
            Operations = t.ApiType == ApiReviewTargetType.Rest
                ? [new ApiReviewOperationResult { Display = "GET /api/autorisasjon/roller", Method = "GET", Executed = true, StatusCode = 200, ElapsedMs = 120, ContentType = "application/json", Note = "Synthetic note" }]
                : [],
            Contract = t.ApiType == ApiReviewTargetType.GraphQl
                ? new ApiReviewContractSummary { Kind = "GraphQL schema", Available = false, Status = ApiReviewCheckResult.NotTested, IntrospectionEnabled = false, Note = "HTTP 400; introspection disabled or rejected" }
                : null,
            AuthenticationEnforcement = t.AuthRequired ? new ApiAuthenticationEnforcementResult { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /api/autorisasjon/roller", StatusCode = 401, Reason = "Refused without a credential." } : null,
            SecurityHeaders = [new SecurityHeaderEvaluation("Strict-Transport-Security", true, false, null, SecurityHeaderOutcome.Missing, default, "")],
            Checks = [new ApiReviewCheck { CheckId = "perf-latency", Area = ApiReviewFindingType.Performance, Title = "Single request latency", Result = ApiReviewCheckResult.Pass, Detail = "120 ms", Evidence = ["120 ms"] },
                      new ApiReviewCheck { CheckId = "err-404", Area = ApiReviewFindingType.Errors, Title = "Unknown route", Result = ApiReviewCheckResult.NotTested, Detail = "Not tested in this environment." }],
            FindingCount = t.ApiType == ApiReviewTargetType.Rest ? 2 : 2,
        }).ToList();
        var rest = targets.Single(t => t.Target.ApiType == ApiReviewTargetType.Rest);
        var gql = targets.Single(t => t.Target.ApiType == ApiReviewTargetType.GraphQl);
        var origin = $"https://{ApiHost}";
        return new ApiReviewReport
        {
            Environment = request.Environment, Policy = request.Policy, GeneratedAt = T0,
            Access = new AuthenticatedReviewCapabilities { AuthenticatedApi = true },
            Targets = targets,
            Findings =
            [
                Finding("sec-no-hsts-1", rest, ApiReviewSeverity.Low, ApiReviewFindingType.Security, "sec-no-hsts", "HSTS header missing on API responses", origin),
                Finding("sec-no-hsts-2", gql, ApiReviewSeverity.Low, ApiReviewFindingType.Security, "sec-no-hsts", "HSTS header missing on API responses", origin),
                Finding("sec-cache-1", rest, ApiReviewSeverity.Low, ApiReviewFindingType.Security, "sec-cache", "Cache-Control not set to no-store", rest.Target.Url),
                Finding("err-stack-1", gql, ApiReviewSeverity.Info, ApiReviewFindingType.Errors, "err-stack", "Error response exposes framework name", gql.Target.Url),
            ],
            Coverage = new ApiReviewCoverage { RestOperationsTotal = 1, RestOperationsReviewed = 1, SecurityChecks = 29, TargetsCompleted = 2 },
            ManualReviewItems = ["Write operations: side effects.", "Access control between roles/tenants.", "Business correctness of returned data."],
            Limitations = ["Automated API review is read-only."],
        };
    }

    private IRenderedComponent<ApiQualityReview> Landing(bool authenticated, ObservedNetworkEndpoint[]? endpoints = null, bool requiresAuth = true)
    {
        Register(authenticated, endpoints ?? Mixed(), requiresAuth);
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-decide]"));
        return page;
    }

    private async Task<IRenderedComponent<ApiQualityReview>> Result()
    {
        var page = Landing(authenticated: true);
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse());
        await page.InvokeAsync(() => page.Find("[data-testid=aqr-run]").Click());
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-result-summary]"));
        return page;
    }

    private static void Tab(IRenderedComponent<ApiQualityReview> page, string tab) => page.Find($"[data-testid=aqr-tab-{tab}]").Click();
    private static IReadOnlyList<string> Groups(IRenderedComponent<ApiQualityReview> page) =>
        page.FindAll("[data-testid=aqr-decision-group]").Select(g => g.GetAttribute("data-group")!).ToList();

    // ── Pre-run decision ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanRunWithLimitations_SeparatesReadyLimitationsAndInformational_AndRunSitsInTheDecision()
    {
        var page = Landing(authenticated: true);
        page.WaitForAssertion(() => page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review can run with limitations"));
        Groups(page).Should().Contain(["ready", "limitation"]).And.NotContain("blocking");
        page.Find("[data-testid=aqr-decision-group][data-group=limitation]").TextContent.Should().Contain("REST contract validation unavailable");
        page.Find("[data-testid=aqr-decision-group][data-group=ready]").TextContent.Should().Contain("selected");
        page.Find("[data-testid=aqr-readiness] [data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-testid=aqr-run-row]").TextContent.Should().Contain("Read-only automated review");
    }

    [Fact]
    public void CannotRun_ShowsOnlyTheBlocker_NotLimitations()
    {
        var page = Landing(authenticated: false, endpoints: [Ep("/api/autorisasjon/roller", auth: true)]);
        page.WaitForAssertion(() => page.Find("#aqr-readiness-heading").TextContent.Should().Contain("Review cannot run"));
        Groups(page).Should().Equal("blocking");
        page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void CanRun_WithAContractlessPublicGraphQlOnly_HasNoBlockingGroup()
    {
        var page = Landing(authenticated: false, endpoints: [Ep("/api/public/graphql", auth: false, ObservedTrafficCategory.GraphQl, GraphQlOperationType.Query, "HentStatus")], requiresAuth: false);
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-readiness]").GetAttribute("data-readiness").Should().BeOneOf("Ready", "Limited"));
        Groups(page).Should().NotContain("blocking");
        page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Diagnostics_AreOneCollapsedSection_HoldingEveryAdvancedDetail()
    {
        var page = Landing(authenticated: true);
        page.Find("[data-testid=aqr-diagnostics-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-diagnostics-toggle]").TextContent.Should().Contain("Diagnostics & setup");
        var body = page.Find("[data-testid=aqr-diagnostics-body]");
        body.HasAttribute("hidden").Should().BeTrue();
        foreach (var id in new[] { "aqr-contracts-disclosure", "aqr-schema-artifacts-disclosure", "aqr-access-details", "aqr-readonly-disclosure", "aqr-target-details" })
            body.QuerySelector($"[data-testid={id}]").Should().NotBeNull($"{id} lives inside Diagnostics & setup");
        page.Find("[data-testid=aqr-additional-checks]").TextContent.Should().Contain("Optional active checks").And.Contain("Not required to run");
    }

    [Fact]
    public void ContractSchemaPreparation_IsOneConcept_WithTechnologySpecificSources()
    {
        var page = Landing(authenticated: true);
        page.FindAll("[data-testid=aqr-targets-table] > thead th").Select(th => th.TextContent).Should().Contain("Contract/schema").And.NotContain("Full endpoint");
        var rest = page.Find("[data-testid=aqr-target][data-type=Rest] [data-testid=aqr-target-contract]").TextContent;
        rest.Should().Contain("OpenAPI").And.Contain("Not configured");
        var gql = page.Find("[data-testid=aqr-target][data-type=GraphQl] [data-testid=aqr-target-contract]").TextContent;
        gql.Should().Contain("Runtime introspection").And.Contain("Fallback: none");
        page.Find("[data-testid=aqr-add-openapi]").TextContent.Should().Be("Configure contract");
        page.Find("[data-testid=aqr-add-schema]").Click();
        page.Find("[data-testid=aqr-diagnostics-toggle]").GetAttribute("aria-expanded").Should().Be("true", "configuring the schema fallback opens its setup");
        page.Find("[data-testid=aqr-schema-artifacts-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("true");
    }

    // ── Post-run Overview ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Overview_IsASummary_NoOccurrenceTable_OneManualBlock_CompactCoverage_CollapsedTechnicalDetails()
    {
        var page = await Result();
        var panel = page.Find("[data-testid=aqr-tabpanel]");
        panel.QuerySelectorAll("[data-testid=aqr-findings-table]").Should().BeEmpty();
        panel.QuerySelectorAll("[data-testid=aqr-manual-review]").Should().ContainSingle();
        page.FindAll("[data-testid=aqr-manual-review]").Should().ContainSingle();
        page.Find("[data-testid=aqr-manual-review]").TextContent.Should().Contain("not findings");
        page.FindAll("[data-testid=aqr-coverage-row]").Select(r => r.GetAttribute("data-coverage")).Should().Contain(["REST", "GraphQL", "Contracts", "Security", "Access"]);
        page.Find("[data-testid=aqr-services-table]");
        page.Find("[data-testid=aqr-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll("[data-testid=aqr-technical]").Should().ContainSingle();
        page.FindAll("[data-testid=aqr-review-details-link]").Should().BeEmpty();
    }

    [Fact]
    public async Task KeyIssues_AreLogical_FindingsListEveryOccurrenceOnce_AndCountsAgree()
    {
        var page = await Result();
        page.Find("[data-testid=aqr-issue-counts]").TextContent.Should().StartWith("3 logical issues · 4 source findings");
        var hsts = page.FindAll("[data-testid=aqr-key-issue]").Single(li => li.TextContent.Contains("HSTS"));
        hsts.GetAttribute("data-sources").Should().Be("2");
        hsts.QuerySelector("[data-testid=aqr-key-issue-occurrences]")!.TextContent.Should().Be("2 occurrences");

        Tab(page, "findings");
        var rows = page.FindAll("[data-testid=aqr-finding-row]");
        rows.Select(r => r.GetAttribute("data-finding-id")).Should().BeEquivalentTo(["sec-no-hsts-1", "sec-no-hsts-2", "sec-cache-1", "err-stack-1"]).And.OnlyHaveUniqueItems();
        page.FindAll("[data-testid=aqr-findings-table]").Should().ContainSingle();
        page.FindAll("[data-testid=aqr-finding-row][data-finding-id^=sec-no-hsts] [data-testid=aqr-finding-issue]").Select(c => c.TextContent).Should().AllBe("1 of 2 occurrences");
        page.Find("[data-testid=aqr-findings-count]").TextContent.Should().Be("Source findings (4)");
            }

    [Fact]
    public async Task KeyIssueOccurrences_OpenFindingsFilteredToThatLogicalIssue_AndDetailsExplainTheRelation()
    {
        var page = await Result();
        page.FindAll("[data-testid=aqr-key-issue]").Single(li => li.TextContent.Contains("HSTS")).QuerySelector("[data-testid=aqr-key-issue-occurrences]")!.Click();
        page.Find("[data-testid=aqr-tab-findings]").GetAttribute("aria-selected").Should().Be("true");
        page.FindAll("[data-testid=aqr-finding-row]").Select(r => r.GetAttribute("data-finding-id")).Should().BeEquivalentTo(["sec-no-hsts-1", "sec-no-hsts-2"]);
        page.Find("[data-testid=aqr-findings-count]").TextContent.Should().Be("Source findings (2 of 4)");
        page.FindAll("[data-testid=aqr-finding-toggle]")[0].Click();
        page.Find("[data-testid=aqr-finding-logical-issue]").TextContent.Should().Contain("HSTS header missing on API responses").And.Contain("2 occurrences");
        page.Find("[data-testid=aqr-filter-clear]").Click();
        page.FindAll("[data-testid=aqr-finding-row]").Should().HaveCount(4);
    }

    [Fact]
    public async Task FindingFilters_BySeverityServiceAndType_WithAnEmptyFilteredState()
    {
        var page = await Result();
        Tab(page, "findings");
        page.Find("[data-testid=aqr-filter-severity]").Change("Info");
        page.FindAll("[data-testid=aqr-finding-row]").Select(r => r.GetAttribute("data-finding-id")).Should().Equal("err-stack-1");
        page.Find("[data-testid=aqr-filter-type]").Change("Security");
        page.Find("[data-testid=aqr-no-filtered-findings]").TextContent.Should().Contain("No source findings match the selected filters");
        page.Find("[data-testid=aqr-filter-clear]").Click();
        page.Find("[data-testid=aqr-filter-type]").Change("Errors");
        page.FindAll("[data-testid=aqr-finding-row]").Should().ContainSingle();
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("graphql")]
    [InlineData("security")]
    [InlineData("errors")]
    [InlineData("performance")]
    [InlineData("contracts")]
    public async Task AreaTabs_SummariseFindings_AndNeverRepeatTheOccurrenceTable(string tab)
    {
        var page = await Result();
        Tab(page, tab);
        page.FindAll("[data-testid=aqr-findings-table]").Should().BeEmpty($"{tab} links to Findings instead of repeating occurrences");
        page.FindAll("[data-testid=aqr-area-findings]").Should().NotBeEmpty();
    }

    [Fact]
    public async Task AreaSummaryCounts_AddUpToTheFindingsTotal_AndLinkToFilteredFindings()
    {
        var page = await Result();
        var total = 0;
        foreach (var tab in new[] { "contracts", "security", "errors", "performance" })
        {
            Tab(page, tab);
            total += int.Parse(page.Find("[data-testid=aqr-area-findings]").GetAttribute("data-count")!);
        }
        total.Should().Be(4, "each finding type belongs to exactly one area");
        Tab(page, "security");
        page.Find("[data-testid=aqr-area-findings-count]").TextContent.Should().Be("Findings in this area: 3");
        page.Find("[data-testid=aqr-area-findings-severity]").TextContent.Should().Be("Low 3");
        page.Find("[data-testid=aqr-view-findings]").Click();
        page.FindAll("[data-testid=aqr-finding-row]").Should().HaveCount(3);
        Tab(page, "performance");
        page.Find("[data-testid=aqr-no-findings]").TextContent.Should().Contain("No findings in this area on the assessed targets");
    }

    [Fact]
    public async Task Rest_OperationsAreCompact_WithExpandableDetails_AndAContractLink()
    {
        var page = await Result();
        Tab(page, "rest");
        page.FindAll("[data-testid=aqr-operations-table] > thead th").Select(th => th.TextContent.Trim()).Should().StartWith(["Operation", "Execution", "Status", "Response time", "Review result"]);
        page.Find("[data-testid=aqr-operation-details]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=aqr-operation-toggle]").Click();
        page.Find("[data-testid=aqr-operation-details]").HasAttribute("hidden").Should().BeFalse();
        page.Find("[data-testid=aqr-operation-details]").TextContent.Should().Contain("application/json").And.Contain("Synthetic note");
        page.Find("[data-testid=aqr-contract-badge]").TextContent.Should().Contain("No contract");
        page.Find("[data-testid=aqr-view-contract]").Click();
        page.Find("[data-testid=aqr-tab-contracts]").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public async Task GraphQl_SchemaStatusShowsTheIntrospectionReason_AndContractsOwnTheDetail()
    {
        var page = await Result();
        Tab(page, "graphql");
        page.Find("[data-testid=aqr-contract-badge]").TextContent.Should().Contain("Unavailable").And.Contain("HTTP 400; introspection disabled or rejected");
        Tab(page, "contracts");
        var rows = page.FindAll("[data-testid=aqr-contract-status-row]");
        rows.Should().HaveCount(2);
        page.FindAll("[data-testid=aqr-contract-status]").Select(s => s.TextContent).Should().Contain(["Not configured", "Unavailable"]);
        page.FindAll("[data-testid=aqr-contract-drift]").Should().OnlyContain(d => d.TextContent.Contains("No previous baseline") && d.TextContent.Contains("informational"));
        page.FindAll("[data-testid=aqr-gql-operations]").Should().BeEmpty("observed operations are listed once, on the GraphQL tab");
    }

    [Fact]
    public async Task Security_IsGroupedIntoSubsections_WithASummary_AndKeepsTheAuthenticationCaveat()
    {
        var page = await Result();
        Tab(page, "security");
        page.FindAll("[data-testid=aqr-security-summary-item]").Select(i => i.GetAttribute("data-key")).Should().Equal("authentication", "cors", "headers", "documentation");
        page.Find("[data-testid=aqr-security-summary-item][data-key=authentication] dd").TextContent.Should().Be("1 verified");
        page.Find("[data-testid=aqr-security-summary-item][data-key=headers] dd").TextContent.Should().Be("2 expected headers missing");
        page.Find("[data-testid=aqr-security-summary-item][data-key=documentation] dd").TextContent.Should().Be("Not verified");
        foreach (var id in new[] { "aqr-security-authentication", "aqr-security-cors", "aqr-security-headers", "aqr-security-documentation" }) page.Find($"[data-testid={id}]");
        page.Find("[data-testid=aqr-auth-enforcement-note]").TextContent.Should().Contain("does not verify authorization correctness");
        page.Find("[data-testid=aqr-security-about-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=aqr-security-note]").TextContent.Length.Should().BeLessThan(ApiReviewPresentation.SecurityScopeNote.Length);
    }

    [Fact]
    public async Task ErrorHandling_KeepsTheCheckTablePattern_AndItsSemanticStates()
    {
        var page = await Result();
        Tab(page, "errors");
        page.FindAll("[data-testid=aqr-checks-table] > thead th").Select(th => th.TextContent).Should().Equal("Area", "Check", "Result", "Detail", "Evidence");
        page.FindAll("[data-testid=aqr-check-row]").Should().Contain(r => r.GetAttribute("data-result") == "NotTested");
        page.Find("[data-testid=aqr-area-findings-count]").TextContent.Should().Be("Findings in this area: 1");
    }

    [Fact]
    public async Task Performance_SaysItIsNotALoadTest_AndFoldsEvidenceIntoRows()
    {
        var page = await Result();
        Tab(page, "performance");
        page.Find("[data-testid=aqr-performance-note]").TextContent.Should().Contain("review's own requests (backend gateway to the API)").And.Contain("not a load test");
        page.FindAll("[data-testid=aqr-checks-table] > thead th").Select(th => th.TextContent).Should().Equal("Check", "Result", "Detail");
        page.FindAll("[data-testid=aqr-check-evidence]").Should().NotBeEmpty();
    }
}
