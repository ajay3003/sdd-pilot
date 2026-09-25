using System.Text.Json;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// API review targets come from Endpoint Discovery (grouped into services), saved configuration and contracts — never guessed paths;
/// static/CDN/telemetry/auth/WebSocket traffic is excluded; verified targets are selected by default; Run eligibility is explicit about
/// why it is disabled; DEV evidence never leaks into QA targets; the run request is an immutable snapshot; history persists safely.
/// </summary>
public sealed class ApiReviewTargetResolverTests
{
    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/appsettings.Dev.json")]
    [InlineData("/_framework/app.js")]
    [InlineData("/_content/app.css")]
    [InlineData("/authentication/login-callback")]
    [InlineData("/swagger.json")]
    [InlineData("/openapi.json")]
    [InlineData("/birknext-unknown-route-probe-old")]
    public void TechnicalResourcesAreNeverDefaultApiTargets(string path)
    {
        var endpoint = Ep(ObservedTrafficCategory.Rest, path);
        Assert.Empty(ApiReviewTargetResolver.Resolve(Context(), Discovery(endpoint)));
        Assert.Empty(ApiReviewTargetResolver.Resolve(Context(rest: Origin + path), new()));
    }

    [Fact]
    public void ProvenanceGuardRejectsProbesAndUnknownButRetainsRealGraphQl()
    {
        var endpoint = Ep(ObservedTrafficCategory.GraphQl, "/api/autorisasjon/graphql");
        Assert.Single(ApiReviewTargetResolver.Resolve(Context(), Discovery(endpoint)));
        Assert.Empty(ApiReviewTargetResolver.Resolve(Context(), Discovery(endpoint with { Provenance = RequestProvenance.DiscoveryProbe })));
        Assert.Empty(ApiReviewTargetResolver.Resolve(Context(), Discovery(endpoint with { Provenance = RequestProvenance.Unknown })));
    }
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private static ObservedNetworkEndpoint Ep(ObservedTrafficCategory cat, string path, string method = "GET", int count = 3, bool auth = true, GraphQlOperationType op = GraphQlOperationType.None, string? opName = null, ObservedEndpointConfidence confidence = ObservedEndpointConfidence.Verified, string host = "api-dev.bufetat.no", string pagePath = "/barn/1") => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = host, Port = 443, Path = path, Method = method, AuthObserved = auth, LastStatus = 200, Count = count, FirstObservedAt = T0, LastObservedAt = T0.AddMinutes(1),
        OperationType = op, OperationName = opName, Confidence = confidence, PageOrigin = Origin, PagePath = pagePath,
    };

    private static FrontendAnalysisContext Context(string id = "dev", string? rest = null, string? gql = null, string? swagger = null, string? health = null, bool requiresAuth = false, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy)
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = id.ToUpperInvariant(), EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", RestBaseUrl = rest, GraphQlEndpoint = gql, SwaggerUrl = swagger, HealthEndpoint = health };
        profile.Authentication.AuthenticatedTestingMethod = method;
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = Origin + "/", RestBaseUrl = rest, GraphQlEndpoint = gql, SwaggerUrl = swagger, HealthEndpoint = health, RequiresAuthentication = requiresAuth, ReviewIdentity = new AuthenticatedReviewIdentity(method, id, "FP") };
    }

    private static EndpointDiscoverySnapshot Discovery(params ObservedNetworkEndpoint[] endpoints)
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, endpoints, T0);
        return snapshot;
    }

    [Fact]
    public void DiscoveredTraffic_GroupsRestIntoServicesAndGraphQlIntoEndpoints_ExcludesNonApiCategories()
    {
        var discovery = Discovery(
            Ep(ObservedTrafficCategory.Rest, "/api/children"), Ep(ObservedTrafficCategory.Rest, "/api/children/42"), Ep(ObservedTrafficCategory.Rest, "/api/children/search", "POST", count: 1),
            Ep(ObservedTrafficCategory.Rest, "/api/roles", count: 5), Ep(ObservedTrafficCategory.Rest, "/api/v2/placements/7", pagePath: "/placements"),
            Ep(ObservedTrafficCategory.GraphQl, "/api/graphql-v2", "POST", count: 7, op: GraphQlOperationType.Query, opName: "GetChildren"),
            Ep(ObservedTrafficCategory.GraphQl, "/api/graphql-v2", "POST", count: 5, op: GraphQlOperationType.Query, opName: "GetRoles"),
            Ep(ObservedTrafficCategory.StaticAsset, "/_framework/dotnet.wasm", host: "m2lbdev.bufetat.no", auth: false),
            Ep(ObservedTrafficCategory.Telemetry, "/v2/track", "POST", auth: false), Ep(ObservedTrafficCategory.Authentication, "/oauth2/token", "POST", host: "login.microsoftonline.com", auth: false),
            Ep(ObservedTrafficCategory.WebSocket, "/hub", "WS", auth: false));

        var targets = ApiReviewTargetResolver.Resolve(Context(), discovery);

        targets.Should().HaveCount(4);
        var children = targets.Single(t => t.ServiceName == "Children API");
        children.ApiType.Should().Be(ApiReviewTargetType.Rest); children.BasePath.Should().Be("/api/children"); children.Host.Should().Be("api-dev.bufetat.no");
        children.Operations.Select(o => o.Display).Should().BeEquivalentTo(["GET /api/children", "GET /api/children/42", "POST /api/children/search"]);
        children.Operations.Should().Contain(o => o.Method == "POST" && !o.IsSafe);
        children.AuthRequired.Should().BeTrue(); children.Selected.Should().BeTrue(); children.Source.Should().Be(ApiReviewTargetSource.DiscoveredTraffic);
        targets.Should().Contain(t => t.ServiceName == "Roles API" && t.BasePath == "/api/roles");
        targets.Should().Contain(t => t.ServiceName == "Placements API" && t.BasePath == "/api/v2/placements");
        var gql = targets.Single(t => t.ApiType == ApiReviewTargetType.GraphQl);
        gql.BasePath.Should().Be("/api/graphql-v2", "the learned path, never /graphql");
        gql.Url.Should().Be("https://api-dev.bufetat.no/api/graphql-v2");
        gql.Operations.Select(o => (o.OperationName, o.ObservedCount)).Should().BeEquivalentTo([("GetChildren", 7), ("GetRoles", 5)]);
        gql.Operations.Should().OnlyContain(o => o.IsSafe);
        targets.Should().NotContain(t => t.Host == "m2lbdev.bufetat.no" || t.Host == "login.microsoftonline.com" || t.BasePath == "/hub" || t.BasePath.Contains("track"));
        targets.Select(t => t.TargetId).Should().OnlyHaveUniqueItems();
        JsonSerializer.Serialize(targets).Should().NotContainAny("Bearer", "Authorization", "?", "token=");
    }

    [Fact]
    public void CandidateOnlyTraffic_IsListedButNotSelectedByDefault()
    {
        var targets = ApiReviewTargetResolver.Resolve(Context(), Discovery(Ep(ObservedTrafficCategory.Rest, "/internal/config", auth: false, confidence: ObservedEndpointConfidence.Candidate)));
        targets.Should().ContainSingle().Which.Selected.Should().BeFalse();
    }

    [Fact]
    public void ConfiguredEndpoints_BecomeTargets_MergeWithDiscoveredAndAttachContract()
    {
        var discovery = Discovery(Ep(ObservedTrafficCategory.Rest, "/api/children"));
        var context = Context(rest: "https://api-dev.bufetat.no/api", gql: "https://api-dev.bufetat.no/gql", swagger: "https://api-dev.bufetat.no/swagger/v1/swagger.json?x=1", health: "https://api-dev.bufetat.no/health", requiresAuth: true);
        var targets = ApiReviewTargetResolver.Resolve(context, discovery);

        var children = targets.Single(t => t.BasePath == "/api/children");
        children.ContractSource.Should().Be("https://api-dev.bufetat.no/swagger/v1/swagger.json", "query string stripped");
        children.Selected.Should().BeTrue();
        children.Operations.Should().Contain(o => o.Method == "GET" && o.Path == "/health" && o.Source == ApiReviewTargetSource.Configured, "the configured health endpoint joins the REST service on the same origin");
        var gql = targets.Single(t => t.ApiType == ApiReviewTargetType.GraphQl);
        gql.BasePath.Should().Be("/gql"); gql.Source.Should().Be(ApiReviewTargetSource.Configured); gql.AuthRequired.Should().BeTrue(); gql.Selected.Should().BeTrue();
        targets.Should().NotContain(t => t.BasePath == "/graphql" || t.BasePath == "/health" && t.Source != ApiReviewTargetSource.Configured);
    }

    [Fact]
    public void NoDiscoveryNoConfiguration_YieldsNoTargets_NeverGuessedPaths()
    {
        ApiReviewTargetResolver.Resolve(Context(), new EndpointDiscoverySnapshot()).Should().BeEmpty();
    }

    [Fact]
    public void ContractOnlyConfiguration_YieldsContractTarget()
    {
        var targets = ApiReviewTargetResolver.Resolve(Context(swagger: "https://api-dev.bufetat.no/swagger.json"), new EndpointDiscoverySnapshot());
        targets.Should().ContainSingle().Which.Should().Match<ApiReviewTarget>(t => t.Source == ApiReviewTargetSource.Contract && t.ContractSource == "https://api-dev.bufetat.no/swagger.json" && t.Selected);
    }

    [Fact]
    public void TargetIsolation_DevEvidenceNeverAppearsForQa()
    {
        var discovery = new EndpointDiscoveryService();
        var js = new Mock<IJSRuntime>();
        discovery.MergeObservedAsync(js.Object, "dev", [Ep(ObservedTrafficCategory.Rest, "/api/children")]).GetAwaiter().GetResult();
        ApiReviewTargetResolver.Resolve(Context("dev"), discovery.GetSnapshot("dev")).Should().ContainSingle();
        ApiReviewTargetResolver.Resolve(Context("qa"), discovery.GetSnapshot("qa")).Should().BeEmpty();
        ApiReviewTargetResolver.Resolve(Context("dev"), discovery.GetSnapshot("dev")).Single().EnvironmentId.Should().Be("dev");
    }

    [Fact]
    public void BasePathHeuristic_SkipsApiAndVersionPrefixes()
    {
        ApiReviewTargetResolver.BasePathOf("/api/v1/children/42/placements").Should().Be("/api/v1/children");
        ApiReviewTargetResolver.BasePathOf("/children").Should().Be("/children");
        ApiReviewTargetResolver.BasePathOf("/api").Should().Be("/api");
        ApiReviewTargetResolver.BasePathOf("/").Should().Be("/");
        ApiReviewTargetResolver.ServiceName("/api/v1/childPlacements", "h").Should().Be("Child Placements API");
        ApiReviewTargetResolver.Id(ApiReviewTargetType.Rest, Origin, "/api/children").Should().Be(ApiReviewTargetResolver.Id(ApiReviewTargetType.Rest, Origin.ToUpperInvariant(), "/api/children/"));
    }

    // ── Run eligibility (root cause of the disabled Run button) ────────────────

    private static ApiReviewTarget Target(string id, bool auth) => new() { TargetId = id, ApiType = ApiReviewTargetType.Rest, Host = "api", BasePath = "/api/x", ServiceName = id, AuthRequired = auth, Selected = true };
    private static AuthenticatedReviewCapabilities Caps(bool authenticated) => new() { Method = AuthenticatedTestingMethod.LocalHttpsProxy, PublicApi = true, AuthenticatedApi = authenticated, AuthenticatedRest = authenticated, AuthenticatedGraphQlQuery = authenticated };

    [Fact]
    public void RunEligibility_RootCause_DiscoveredTargetsEnableRunWithoutConfiguredRestBaseUrl()
    {
        // Before: Run was disabled unless RestBaseUrl/GraphQlEndpoint/SwaggerUrl/HealthEndpoint were configured on the profile, even with discovered endpoints and a ready auth context.
        var context = Context(); // no configured API URLs at all
        var targets = ApiReviewTargetResolver.Resolve(context, Discovery(Ep(ObservedTrafficCategory.Rest, "/api/children")));
        context.HasRestBaseUrl.Should().BeFalse(); context.HasGraphQlEndpoint.Should().BeFalse();
        var eligibility = ApiReviewRunEligibility.Evaluate(context, targets, targets.Select(t => t.TargetId).ToHashSet(), Caps(true));
        eligibility.Enabled.Should().BeTrue(eligibility.Reason);
    }

    [Fact]
    public void RunEligibility_NoTargets_DisabledWithReasonAndDiscoveryAction()
    {
        var e = ApiReviewRunEligibility.Evaluate(Context(), [], [], Caps(false));
        e.Enabled.Should().BeFalse(); e.Reason.Should().Be(ApiReviewRunEligibility.NoTargetsReason); e.ActionText.Should().Contain("Endpoint Discovery"); e.ActionHref.Should().Be(ApiReviewRunEligibility.TargetEnvironmentsHref);
    }

    [Fact]
    public void RunEligibility_PublicTarget_EnabledWithoutAuth()
    {
        ApiReviewRunEligibility.Evaluate(Context(method: AuthenticatedTestingMethod.ManagedEdgeCdp), [Target("a", false)], ["a"], Caps(false)).Enabled.Should().BeTrue();
    }

    [Fact]
    public void RunEligibility_AuthOnlyTargets_MissingContext_DisabledFastWithAction()
    {
        var e = ApiReviewRunEligibility.Evaluate(Context(), [Target("a", true)], ["a"], Caps(false));
        e.Enabled.Should().BeFalse(); e.Reason.Should().Be(ApiReviewRunEligibility.NoAuthContextReason); e.ActionText.Should().Be(ApiReviewRunEligibility.NoAuthContextAction);
        ApiReviewRunEligibility.Evaluate(Context(), [Target("a", true)], ["a"], Caps(true)).Enabled.Should().BeTrue();
        ApiReviewRunEligibility.Evaluate(Context(method: AuthenticatedTestingMethod.ManualOnly), [Target("a", true)], ["a"], Caps(false)).Reason.Should().Contain("manual-verification only");
        ApiReviewRunEligibility.Evaluate(Context(), [Target("a", true), Target("b", false)], ["a", "b"], Caps(false)).Should().Match<ApiReviewRunEligibility>(x => x.Enabled && x.Reason.Contains("1 of 2"));
        ApiReviewRunEligibility.Evaluate(Context(), [Target("a", false)], [], Caps(false)).Reason.Should().Be("Select at least one API target.");
        ApiReviewRunEligibility.Evaluate(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment." }, [Target("a", false)], ["a"], null).Reason.Should().Be("No active Target Environment.");
    }

    // ── History persistence ────────────────────────────────────────────────────

    [Fact]
    public async Task History_PersistsBaselinesAndSummariesPerEnvironment_NoSecrets_BoundedRuns()
    {
        var js = new Mock<IJSRuntime>();
        string? stored = null;
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("birkNextStorage.setItem", It.IsAny<object?[]>()))
            .Callback<string, object?[]>((_, args) => stored = (string?)args[1]).ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());
        var service = new ApiReviewHistoryService();
        var report = new ApiReviewReport
        {
            Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "DEV" }, GeneratedAt = T0,
            Targets = [new ApiReviewTargetResult { Target = Target("t1", true), Status = ApiReviewTargetStatus.Completed, FindingCount = 1, Baseline = new ApiReviewBaseline { TargetId = "t1", ContractHash = "abc", OperationShapes = new() { ["GET /api/x"] = [new("$.id", "integer", false)] } } },
                       new ApiReviewTargetResult { Target = Target("t2", true), Status = ApiReviewTargetStatus.Blocked, FindingCount = null, Baseline = new ApiReviewBaseline { TargetId = "t2" } }],
            Findings = [new ApiReviewFinding { Id = "f", Severity = ApiReviewSeverity.High, Title = "x" }],
        };
        for (var i = 0; i < 12; i++) await service.RecordAsync(js.Object, "dev", report);

        var history = service.For("dev");
        history.Baselines.Should().ContainKey("t1").And.NotContainKey("t2", "blocked targets never overwrite a baseline");
        history.Runs.Should().HaveCount(ApiReviewHistoryService.MaxRuns);
        history.Runs[0].Should().Match<ApiReviewRunSummary>(r => r.High == 1 && r.Blocked == 1 && r.Completed == 1);
        service.For("qa").Baselines.Should().BeEmpty();
        stored.Should().NotBeNull().And.NotContainAny("Bearer", "Authorization", "Cookie", "eyJ", "sessionId");
        stored.Should().Contain("abc");

        var restored = new ApiReviewHistoryService();
        var js2 = new Mock<IJSRuntime>();
        js2.Setup(j => j.InvokeAsync<string?>("birkNextStorage.getItem", It.IsAny<object?[]>())).ReturnsAsync(stored);
        await restored.LoadAsync(js2.Object);
        restored.For("dev").Baselines["t1"].ContractHash.Should().Be("abc");
        restored.For("dev").LastReport!.Environment.EnvironmentId.Should().Be("dev");
    }

    // ── Export ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_IncludesTargetServicesAccessContractsCoverageFindingsAndStates_NoSecrets()
    {
        var report = new ApiReviewReport
        {
            Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "M2LB DEV", EnvironmentType = "Development", TargetUrl = Origin + "/", AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy, ContextIdentityDigest = "FP" },
            Access = Caps(true), GeneratedAt = T0, StartedAt = T0,
            Targets =
            [
                new ApiReviewTargetResult { Target = Target("t1", true) with { ServiceName = "Children API", ContractSource = "https://api/swagger.json" }, AccessMode = ApiReviewAccessMode.AuthenticatedHttp, AccessReason = "gateway", Status = ApiReviewTargetStatus.Completed, FindingCount = 1,
                    Contract = new ApiReviewContractSummary { Kind = "OpenAPI", Available = true, Status = ApiReviewCheckResult.Pass, Version = "3.0.3", Hash = "deadbeef" },
                    Operations = [new ApiReviewOperationResult { Display = "GET /api/children", Executed = true, StatusCode = 200, Result = ApiReviewCheckResult.Pass, Checks = [new ApiReviewCheck { CheckId = "c", Area = ApiReviewFindingType.Contract, Title = "Shape", Result = ApiReviewCheckResult.NotTested, Detail = "no body" }] }] },
                new ApiReviewTargetResult { Target = Target("t2", true), AccessMode = ApiReviewAccessMode.Unavailable, AccessReason = "Authenticated API context not available.", RequiredAction = "Start the Local HTTPS Proxy", Status = ApiReviewTargetStatus.Blocked, FindingCount = null },
            ],
            Findings = [new ApiReviewFinding { Id = "x", Severity = ApiReviewSeverity.High, Type = ApiReviewFindingType.Contract, Endpoint = "GET /api/children", Check = "Required properties", Title = "Missing required property $.name", Evidence = ["Path: $.name"], Recommendation = "Fix", Drift = ApiReviewDriftClassification.Breaking }],
            Coverage = new ApiReviewCoverage { RestOperationsReviewed = 1, RestOperationsTotal = 1, ContractChecks = 1, SecurityChecks = 2, AuthenticatedPlanned = 2, AuthenticatedExecuted = 1, TargetsBlocked = 1 },
            ManualReviewItems = ["Write operations"], Limitations = ["Read-only"],
        };
        var html = new ReportExportService().ExportApiReview(report, "BirkNext");
        html.Should().Contain("M2LB DEV").And.Contain("Children API").And.Contain("Authenticated HTTP via Local HTTPS Proxy").And.Contain("deadbeef").And.Contain("3.0.3")
            .And.Contain("Blocked").And.Contain("Not tested").And.Contain("Missing required property $.name").And.Contain("Breaking").And.Contain("Write operations").And.Contain("1 / 1");
        html.Should().NotContainAny("Bearer", "Authorization", "Cookie", "eyJ");
    }

    [Fact]
    public void RunRequest_IsImmutableSnapshotOfTheActiveEnvironment_WithOnlySelectedTargets()
    {
        var context = Context(rest: "https://api-dev.bufetat.no/api", requiresAuth: true);
        var targets = ApiReviewTargetResolver.Resolve(context, Discovery(Ep(ObservedTrafficCategory.Rest, "/api/children"), Ep(ObservedTrafficCategory.Rest, "/api/roles")));
        var selected = new HashSet<string> { targets.First(t => t.BasePath == "/api/children").TargetId };
        var history = new ApiReviewHistory { Baselines = { [selected.Single()] = new ApiReviewBaseline { TargetId = selected.Single(), ContractHash = "h" }, ["other"] = new ApiReviewBaseline { TargetId = "other" } } };

        var request = BirkNext.Web.Pages.ApiQualityReview.BuildRequest(context, context.ReviewIdentity!, targets, selected, history);

        request.Environment.Should().Match<ApiReviewEnvironmentSnapshot>(e => e.EnvironmentId == "dev" && e.Name == "DEV" && e.TargetUrl == Origin + "/" && e.RequiresAuthentication && e.ContextIdentityDigest == "FP" && !e.IsProduction);
        request.Targets.Should().ContainSingle().Which.BasePath.Should().Be("/api/children");
        request.Baselines.Should().ContainSingle().Which.ContractHash.Should().Be("h");
        request.Policy.Should().Match<ApiReviewPolicy>(p => p.ReadOnly && p.ErrorHandlingProbes && !p.IntrospectionExpectedDisabled && p.SlowWarningMs == 1500 && p.SlowPoorMs == null
            && p.RestPayloadWarningBytes == 500L * 1024 && p.GraphQlPayloadWarningBytes == 1024L * 1024);
        request.FrontendOrigin.Should().Be(Origin);
        // Mutating the live context afterwards does not affect the captured snapshot.
        context.ActiveProfile.Name = "CHANGED"; context.TargetUrl = "https://qa.example.test/";
        request.Environment.Name.Should().Be("DEV"); request.Environment.TargetUrl.Should().Be(Origin + "/");
        JsonSerializer.Serialize(request).Should().NotContainAny("Bearer", "Authorization", "Cookie");
    }

    // ── The M2LB DEV scope, after configuration documents stopped being REST APIs ──
    // What the classifier produced before the fix: /appsettings.json and /appsettings.Dev.json arrived as Verified REST
    // endpoints, so the resolver made each one its own REST service and API Quality Review offered to review a settings
    // file. The classifier now categorises them OtherHttp; this pins what the resolver therefore sees.

    [Fact]
    public void ConfigurationDocumentsDoNotBecomeApiTargets()
    {
        var discovery = Discovery(
            Ep(ObservedTrafficCategory.OtherHttp, "/appsettings.json", host: "m2lbdev.bufetat.no", auth: false),
            Ep(ObservedTrafficCategory.OtherHttp, "/appsettings.Dev.json", host: "m2lbdev.bufetat.no", auth: false),
            Ep(ObservedTrafficCategory.Rest, "/api/autorisasjon", host: "m2lbdev.bufetat.no"),
            Ep(ObservedTrafficCategory.GraphQl, "/api/autorisasjon/graphql", "POST", count: 6, op: GraphQlOperationType.Query, opName: "GetRoles", host: "m2lbdev.bufetat.no"));

        var targets = ApiReviewTargetResolver.Resolve(Context(), discovery);

        targets.Should().HaveCount(2, "one REST service and one GraphQL endpoint; a settings file is neither");
        targets.Count(t => t.ApiType == ApiReviewTargetType.Rest).Should().Be(1);
        targets.Count(t => t.ApiType == ApiReviewTargetType.GraphQl).Should().Be(1);
        targets.Should().NotContain(t => t.BasePath.Contains("appsettings", StringComparison.OrdinalIgnoreCase));
        targets.Should().NotContain(t => t.ServiceName.Contains("Appsettings", StringComparison.OrdinalIgnoreCase));
        targets.SelectMany(t => t.Operations).Should().NotContain(o => o.Path.Contains("appsettings", StringComparison.OrdinalIgnoreCase));

        // The API targets that must survive.
        targets.Should().Contain(t => t.ApiType == ApiReviewTargetType.Rest && t.BasePath == "/api/autorisasjon");
        targets.Should().Contain(t => t.ApiType == ApiReviewTargetType.GraphQl && t.BasePath == "/api/autorisasjon/graphql");
        // Both API targets were observed with authentication, which is the "2 require authentication" the summary reports
        // — a count that was right by accident before, when two of the four targets were configuration files.
        targets.Count(t => t.AuthRequired).Should().Be(2);
    }

    /// <summary>
    /// The resolver already excludes non-API categories; this states that a configuration document is one of them, so a
    /// future change that re-admits OtherHttp cannot quietly put settings files back into the review.
    /// </summary>
    [Fact]
    public void NonApiCategoriesIncludingServedDocumentsAreNeverApiTargets()
    {
        var discovery = Discovery(
            Ep(ObservedTrafficCategory.OtherHttp, "/appsettings.json", auth: false),
            Ep(ObservedTrafficCategory.OtherHttp, "/operasjonskatalog", auth: false),
            Ep(ObservedTrafficCategory.StaticAsset, "/_framework/dotnet.wasm", auth: false),
            Ep(ObservedTrafficCategory.Telemetry, "/v2/track", "POST", auth: false),
            Ep(ObservedTrafficCategory.Authentication, "/oauth2/token", "POST", host: "login.microsoftonline.com", auth: false),
            Ep(ObservedTrafficCategory.WebSocket, "/hub", "WS", auth: false));

        ApiReviewTargetResolver.Resolve(Context(), discovery).Should().BeEmpty();
    }
}
