using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// API Quality Review engine: consumes discovered/configured targets (no guessed paths), executes read-only checks publicly or through
/// the authenticated gateway (never a raw bearer), fails fast without an authenticated context, validates live shapes against OpenAPI,
/// detects drift, reviews GraphQL via the learned endpoint (mutations listed, never executed), flags CORS/error-leak/security issues, and
/// keeps Blocked/Not tested distinct from a zero-finding pass.
/// </summary>
public sealed partial class ApiReviewEngineTests
{
    private const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<string> Bodies = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => Json(HttpStatusCode.NotFound, "{\"title\":\"Not Found\",\"status\":404,\"type\":\"about:blank\"}", "application/problem+json");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body is not null) Bodies.Add(body);
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string contentType = "application/json", Action<HttpResponseMessage>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=31536000");
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        headers?.Invoke(response);
        return response;
    }

    private static ApiReviewEngine Engine(Fixture fixture, IAuthenticatedReviewGateway? gateway = null) =>
        new(new HttpClient(fixture), gateway ?? new FakeGateway(false), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), NullLogger<ApiReviewEngine>.Instance);

    private static ApiReviewTarget Rest(bool auth = false, string basePath = "/api/children", string? contract = null, params (string Method, string Path)[] ops) => new()
    {
        TargetId = "rest-1", EnvironmentId = "dev", ApiType = ApiReviewTargetType.Rest, Scheme = "https", Host = "api-dev.example.test", Port = 443, BasePath = basePath, ServiceName = "Children API",
        Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = auth, ContractSource = contract, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
        Operations = (ops.Length == 0 ? [("GET", basePath)] : ops).Select(o => new ApiReviewOperation { Method = o.Method, Path = o.Path, ObservedCount = 3, AuthObserved = auth, LastStatus = 200 }).ToList(),
    };

    private static ApiReviewTarget GraphQl(string path = "/api/graphql-v2", bool auth = false, params (GraphQlOperationType Type, string Name)[] ops) => new()
    {
        TargetId = "gql-1", EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Scheme = "https", Host = "api-dev.example.test", Port = 443, BasePath = path, ServiceName = "GraphQL",
        Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = auth, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
        Operations = ops.Select(o => new ApiReviewOperation { Method = "POST", Path = path, OperationType = o.Type, OperationName = o.Name, ObservedCount = 5, AuthObserved = auth }).ToList(),
    };

    private static ApiReviewRunRequest Request(AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy, bool production = false, params ApiReviewTarget[] targets) => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "M2LB DEV", EnvironmentType = production ? "Production" : "Development", TargetUrl = "https://m2lbdev.example.test/", AuthenticatedTestingMethod = method, IsProduction = production, ContextIdentityDigest = Fp },
        Identity = new AuthenticatedReviewIdentity(method, "dev", Fp), Targets = targets.ToList(), Policy = new ApiReviewPolicy { ErrorHandlingProbes = !production, IntrospectionExpectedDisabled = production }, FrontendOrigin = "https://m2lbdev.example.test",
    };

    // ── Public REST ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicRest_GetReviewExecutes_NoAuthDependency_ZeroFindingsIsZeroNotNull()
    {
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/children" when req.Method == HttpMethod.Get => Encoded(HttpStatusCode.OK, "{\"items\":[{\"id\":1,\"name\":\"x\"}],\"totalCount\":1}", "br"),
            "/api/children" when req.Method == HttpMethod.Options => Json(HttpStatusCode.NoContent, "", headers: r => { r.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://m2lbdev.example.test"); r.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true"); }),
            _ => Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404}", "application/problem+json"),
        } };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));

        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewAccessMode.PublicHttp, target.AccessMode);
        Assert.Equal(ApiReviewTargetStatus.Completed, target.Status);
        var op = Assert.Single(target.Operations);
        Assert.True(op.Executed); Assert.Equal(200, op.StatusCode); Assert.Equal(ApiReviewCheckResult.Pass, op.Result);
        Assert.Equal(0, target.FindingCount);
        Assert.Empty(report.Findings);
        Assert.Contains(target.Checks, c => c.CheckId == "cors-policy" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "errors-unknown-route" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "errors-format" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "rest-compression" && c.Result == ApiReviewCheckResult.Pass);
        Assert.All(fixture.Requests, r => Assert.True(r.Method == HttpMethod.Get || r.Method == HttpMethod.Options));
        Assert.All(fixture.Requests, r => Assert.Null(r.Headers.Authorization));
        Assert.Equal(1, report.Coverage.PublicExecuted); Assert.Equal(0, report.Coverage.AuthenticatedPlanned);
        Assert.DoesNotContain("\"name\":\"x\"", JsonSerializer.Serialize(report));
    }

    // ── Authenticated REST via gateway ──────────────────────────────────────────

    [Fact]
    public async Task AuthenticatedRest_UsesGateway_TokenNeverExposed_ReviewCompletes()
    {
        var fixture = new Fixture();
        var gateway = new FakeGateway(true);
        var report = await Engine(fixture, gateway).RunAsync(Request(AuthenticatedTestingMethod.LocalHttpsProxy, false, Rest(auth: true)));

        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewAccessMode.AuthenticatedHttp, target.AccessMode);
        Assert.Equal(ApiReviewTargetStatus.Completed, target.Status);
        Assert.True(gateway.RestCalls >= 2, "operation GET + unknown-route probe go through the gateway");
        Assert.DoesNotContain(fixture.Requests, r => r.Method == HttpMethod.Get);
        Assert.All(target.Operations.Where(o => o.Executed), o => Assert.Equal(ApiReviewAccessMode.AuthenticatedHttp, o.AccessMode));
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Bearer", json); Assert.DoesNotContain("Authorization", json); Assert.DoesNotContain("eyJ", json); Assert.DoesNotContain(FakeGateway.SecretToken, json);
        Assert.Equal(1, report.Coverage.AuthenticatedExecuted);
    }

    [Fact]
    public async Task AuthContextMissing_FailsFast_BlockedWithAction_NoTimeout_FindingCountNull()
    {
        var fixture = new Fixture();
        var watch = Stopwatch.StartNew();
        var report = await Engine(fixture, new FakeGateway(false)).RunAsync(Request(AuthenticatedTestingMethod.LocalHttpsProxy, false, Rest(auth: true), GraphQl(auth: true)));
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 2000, "fail fast, no network wait");
        Assert.Empty(fixture.Requests);
        Assert.All(report.Targets, t =>
        {
            Assert.Equal(ApiReviewAccessMode.Unavailable, t.AccessMode);
            Assert.Equal(ApiReviewTargetStatus.Blocked, t.Status);
            Assert.Null(t.FindingCount);
            Assert.Contains("Local HTTPS Proxy", t.RequiredAction);
            Assert.All(t.Operations, o => Assert.Equal(ApiReviewCheckResult.Blocked, o.Result));
        });
        Assert.Equal(2, report.Coverage.TargetsBlocked);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task ManualOnly_AuthTargetUnavailable_PublicTargetStillRuns()
    {
        var fixture = new Fixture { Respond = (_, _) => Json(HttpStatusCode.OK, "[]") };
        var publicTarget = Rest(auth: false, basePath: "/api/public") with { TargetId = "rest-pub" };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManualOnly, false, Rest(auth: true), publicTarget));
        Assert.Equal(ApiReviewAccessMode.ManualOnly, report.Targets.Single(t => t.Target.TargetId == "rest-1").AccessMode);
        Assert.Equal(ApiReviewTargetStatus.Blocked, report.Targets.Single(t => t.Target.TargetId == "rest-1").Status);
        Assert.Equal(ApiReviewTargetStatus.Completed, report.Targets.Single(t => t.Target.TargetId == "rest-pub").Status);
    }

    // ── GraphQL ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GraphQl_UsesDiscoveredNonStandardPath_PostTypename_MutationListedNeverExecuted()
    {
        var schema = IntrospectionWith(queryFields: ["children", "roles"], mutationFields: ["deleteChild"], deprecated: ["Child.oldStatus"]);
        var fixture = new Fixture { Respond = (req, body) =>
        {
            if (req.RequestUri!.AbsolutePath != "/api/graphql-v2" || req.Method != HttpMethod.Post) return Json(HttpStatusCode.NotFound, "{}");
            if (body?.Contains("__schema") == true) return Json(HttpStatusCode.OK, schema);
            if (body?.Contains("__birkNextUnknownFieldProbe") == true) return Json(HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"Unknown field\",\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}]}");
            return Json(HttpStatusCode.OK, "{\"data\":{\"__typename\":\"Query\"}}");
        } };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, GraphQl(ops: [(GraphQlOperationType.Query, "GetChildren"), (GraphQlOperationType.Query, "GetPlacements"), (GraphQlOperationType.Mutation, "DeleteChild")])));

        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewTargetStatus.Completed, target.Status);
        // Every request to the API host goes to the discovered path (the frontend host only serves its public build manifest).
        Assert.All(fixture.Requests.Where(r => r.RequestUri!.Host == "api-dev.example.test"), r => Assert.Equal("/api/graphql-v2", r.RequestUri!.AbsolutePath));
        Assert.All(fixture.Requests.Where(r => r.RequestUri!.Host != "api-dev.example.test"), r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.AbsolutePath == "/graphql");
        Assert.All(fixture.Requests.Where(r => r.Method == HttpMethod.Post), _ => { });
        Assert.DoesNotContain(fixture.Bodies, b => System.Text.RegularExpressions.Regex.IsMatch(b, @"""query"":""\s*mutation"));
        Assert.Contains(fixture.Bodies, b => b.Contains("query { __typename }"));
        Assert.Contains(target.Checks, c => c.CheckId == "gql-reachability" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "gql-mutations" && c.Result == ApiReviewCheckResult.ManualReview && c.Evidence.Contains("deleteChild"));
        Assert.Contains(target.Checks, c => c.CheckId == "gql-error-shape" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "gql-introspection" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Equal(2, target.Contract!.OperationCount); Assert.Equal(1, target.Contract.MutationCount); Assert.Equal(1, target.Contract.DeprecatedCount);
        // No document was captured for these operations, so compatibility is Not assessed — never guessed from the operation name.
        Assert.All(target.GraphQlOperationMatches, m => Assert.Equal(ApiReviewCheckResult.NotTested, m.Result));
        Assert.All(target.GraphQlCompatibility!.Operations, o => Assert.Equal(GraphQlOperationCompatibility.NoDocumentReason, o.NotAssessedReason));
        // GraphQL Payload is recorded in the policy but never evaluated: the only executed request is the safe __typename probe.
        Assert.DoesNotContain(target.Checks.Concat(target.Operations.SelectMany(o => o.Checks)), c => c.CheckId.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Findings, f => f.Id.StartsWith("gql-observed-mutation") && f.Result == ApiReviewCheckResult.ManualReview);
        // Every finding names its typed rule without the per-observation suffix, so the UI can group by rule.
        Assert.All(report.Findings, f => Assert.True(f.RuleId.Length > 0 && f.Id.StartsWith(f.RuleId + "-", StringComparison.Ordinal), f.Id));
        Assert.Contains(report.Findings, f => f.RuleId == "gql-observed-mutation");
        Assert.Contains(report.Findings, f => f.Id.StartsWith("gql-deprecated-fields"));
        Assert.Equal(0, report.Coverage.GraphQlOperationsMatched); Assert.Equal(3, report.Coverage.GraphQlOperationsObserved);
        Assert.DoesNotContain("query {", JsonSerializer.Serialize(report.Targets.Select(t => t.Target)));
    }

    [Fact]
    public async Task GraphQl_IntrospectionDisabled_IsPolicyObservationNotFailure()
    {
        var fixture = new Fixture { Respond = (_, body) => body?.Contains("__schema") == true ? Json(HttpStatusCode.OK, "{\"errors\":[{\"message\":\"introspection disabled\"}]}") : Json(HttpStatusCode.OK, "{\"data\":{\"__typename\":\"Query\"}}") };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, GraphQl()));
        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewTargetStatus.Completed, target.Status);
        Assert.False(target.Contract!.IntrospectionEnabled);
        Assert.Contains(target.Checks, c => c.CheckId == "gql-introspection" && c.Result == ApiReviewCheckResult.Pass);
        Assert.Contains(target.Checks, c => c.CheckId == "gql-schema" && c.Result == ApiReviewCheckResult.NotTested);
        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("gql-introspection"));
    }

    [Fact]
    public async Task GraphQl_SchemaDrift_RemovedRootField_IsBreaking()
    {
        var schema = IntrospectionWith(queryFields: ["children"], mutationFields: [], deprecated: []);
        var fixture = new Fixture { Respond = (_, body) => body?.Contains("__schema") == true ? Json(HttpStatusCode.OK, schema) : Json(HttpStatusCode.OK, "{\"data\":{\"__typename\":\"Query\"}}") };
        var request = Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, GraphQl(ops: [(GraphQlOperationType.Query, "GetRoles")]));
        request = request with { Baselines = [new ApiReviewBaseline { TargetId = "gql-1", RecordedAt = DateTimeOffset.UtcNow.AddDays(-1), GraphQlRootFields = ["children", "roles"], GraphQlSchemaHash = "old" }] };
        var report = await Engine(fixture).RunAsync(request);
        var drift = Assert.Single(report.Findings, f => f.Id.StartsWith("drift-gql-root-field-removed"));
        Assert.Equal(ApiReviewDriftClassification.Breaking, drift.Drift);
        Assert.Equal(ApiReviewSeverity.High, drift.Severity);
        Assert.Contains("Removed root field: roles", drift.Evidence);
        // No document captured: compatibility is Not assessed rather than a name-based guess.
        Assert.Contains(report.Targets[0].GraphQlOperationMatches, m => m.Operation == "Query GetRoles" && m.Result == ApiReviewCheckResult.NotTested);
    }

    // ── Contract validation & drift ─────────────────────────────────────────────

    [Fact]
    public async Task OpenApiContract_LiveShapeMismatch_ProducesTwoStructuralFindings_NoBodyPersisted()
    {
        const string openApi = """
            {"openapi":"3.0.3","info":{"title":"Children","version":"1.0"},"servers":[{"url":"https://api-dev.example.test"}],
             "components":{"securitySchemes":{"bearer":{"type":"http","scheme":"bearer"}}},"security":[{"bearer":[]}],
             "paths":{"/api/children":{"get":{"operationId":"getChildren","summary":"List","responses":{"200":{"description":"ok","content":{"application/json":{"schema":{"type":"object","required":["id","name"],"properties":{"id":{"type":"integer"},"name":{"type":"string"}}}}}},"404":{"description":"nf"}}}}}}
            """;
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/swagger/v1/swagger.json" => Json(HttpStatusCode.OK, openApi),
            "/api/children" when req.Method == HttpMethod.Get => Json(HttpStatusCode.OK, "{\"id\":\"abc-secret-value\",\"extra\":true}"),
            _ => Json(HttpStatusCode.NotFound, "{\"title\":\"Not Found\",\"status\":404,\"type\":\"x\"}", "application/problem+json"),
        } };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest(contract: "https://api-dev.example.test/swagger/v1/swagger.json")));

        var target = Assert.Single(report.Targets);
        Assert.True(target.Contract!.Available); Assert.Equal("3.0.3", target.Contract.Version); Assert.Equal(1, target.Contract.OperationCount);
        var missing = Assert.Single(report.Findings, f => f.Id.StartsWith("contract-missing-required"));
        Assert.Contains("Path: $.name", missing.Evidence); Assert.Equal(ApiReviewDriftClassification.Breaking, missing.Drift); Assert.Equal(ApiReviewSeverity.High, missing.Severity);
        var type = Assert.Single(report.Findings, f => f.Id.StartsWith("contract-type-mismatch"));
        Assert.Contains("Path: $.id", type.Evidence); Assert.Contains("Expected: integer", type.Evidence); Assert.Contains("Observed: string", type.Evidence);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("contract-undocumented-property") && f.Drift == ApiReviewDriftClassification.NonBreaking);
        Assert.Equal(2, report.Findings.Count(f => f.Type == ApiReviewFindingType.Contract && f.Severity == ApiReviewSeverity.High));
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("abc-secret-value", json);
        Assert.True(target.Operations[0].ContractMatched);
        Assert.NotNull(target.Baseline); Assert.Equal(target.Contract.Hash, target.Baseline!.ContractHash);
        Assert.Contains("GET /api/children", target.Baseline.OperationShapes.Keys);
        Assert.All(target.Baseline.OperationShapes.Values.SelectMany(s => s), e => Assert.DoesNotContain("secret", e.Path + e.Type));
    }

    [Fact]
    public async Task RestDrift_RemovedPropertyAndTypeChange_AreBreaking_AddedIsNonBreaking()
    {
        var fixture = new Fixture { Respond = (req, _) => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/children" ? Json(HttpStatusCode.OK, "{\"id\":\"1\",\"added\":true}") : Json(HttpStatusCode.NotFound, "{\"title\":\"nf\",\"status\":404,\"type\":\"x\"}", "application/problem+json") };
        var request = Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()) with
        {
            Baselines = [new ApiReviewBaseline { TargetId = "rest-1", RecordedAt = DateTimeOffset.UtcNow.AddDays(-2), OperationShapes = new() { ["GET /api/children"] = [new("$", "object", false), new("$.id", "integer", false), new("$.name", "string", false)] }, OperationStatuses = new() { ["GET /api/children"] = 200 } }],
        };
        var report = await Engine(fixture).RunAsync(request);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("drift-removed-property") && f.Drift == ApiReviewDriftClassification.Breaking && f.Evidence.Contains("Removed: $.name"));
        Assert.Contains(report.Findings, f => f.Id.StartsWith("drift-type-change") && f.Drift == ApiReviewDriftClassification.Breaking && f.Evidence.Contains("$.id: integer → string"));
        Assert.Contains(report.Findings, f => f.Id.StartsWith("drift-added-property") && f.Drift == ApiReviewDriftClassification.NonBreaking);
        Assert.Contains(report.Targets[0].Operations[0].Checks, c => c.CheckId == "drift-shape" && c.Result == ApiReviewCheckResult.Fail);
    }

    // ── Errors, CORS, security ──────────────────────────────────────────────────

    [Fact]
    public async Task ErrorLeakage_StackTraceInResponse_IsHighFinding_ContentRedacted()
    {
        const string leak = "System.NullReferenceException: Object reference not set\n   at Bufdir.M2LB.Api.Controllers.ChildrenController.Get(Int32 id) in C:\\build\\src\\ChildrenController.cs:line 42";
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath.Contains("birknext-unknown-route") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(leak, Encoding.UTF8, "text/plain") } : Json(HttpStatusCode.OK, "{\"ok\":true}") };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));
        var finding = report.Findings.First(f => f.Id.StartsWith("errors-leak"));
        Assert.Equal(ApiReviewSeverity.High, finding.Severity);
        Assert.Contains("Indicator: stack-trace", finding.Evidence); Assert.Contains("Indicator: exception-type", finding.Evidence); Assert.Contains("Indicator: source-file-path", finding.Evidence);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("ChildrenController", json); Assert.DoesNotContain("NullReferenceException", json); Assert.DoesNotContain("C:\\\\build", json);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("errors-unknown-route-5xx"));
    }

    [Fact]
    public async Task Cors_WildcardWithCredentials_IsHigh_SafePolicyNoFinding()
    {
        var unsafeFixture = new Fixture { Respond = (req, _) => Json(req.Method == HttpMethod.Options ? HttpStatusCode.NoContent : HttpStatusCode.OK, req.Method == HttpMethod.Options ? "" : "{\"ok\":true}", headers: r => { r.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*"); r.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true"); r.Headers.TryAddWithoutValidation("Access-Control-Allow-Methods", "*"); }) };
        var unsafeReport = await Engine(unsafeFixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));
        var finding = Assert.Single(unsafeReport.Findings, f => f.Id.StartsWith("cors-wildcard-credentials"));
        Assert.Equal(ApiReviewSeverity.High, finding.Severity);
        Assert.Contains(unsafeReport.Findings, f => f.Id.StartsWith("cors-any-method"));
        Assert.DoesNotContain("exploit", finding.Description, StringComparison.OrdinalIgnoreCase);

        var safeFixture = new Fixture { Respond = (req, _) => Json(req.Method == HttpMethod.Options ? HttpStatusCode.NoContent : HttpStatusCode.OK, req.Method == HttpMethod.Options ? "" : "{\"ok\":true}", headers: r => { r.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "https://m2lbdev.example.test"); r.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true"); }) };
        var safeReport = await Engine(safeFixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));
        Assert.DoesNotContain(safeReport.Findings, f => f.Id.StartsWith("cors-"));
        Assert.Contains(safeReport.Targets[0].Checks, c => c.CheckId == "cors-policy" && c.Result == ApiReviewCheckResult.Pass);
        var preflight = safeFixture.Requests.Single(r => r.Method == HttpMethod.Options);
        Assert.Equal("https://m2lbdev.example.test", preflight.Headers.GetValues("Origin").Single());
    }

    [Fact]
    public async Task SecurityHeaders_MissingHstsAndNosniff_AreLowFindings_ServerDisclosureInfo()
    {
        var fixture = new Fixture { Respond = (_, _) => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") }; r.Headers.TryAddWithoutValidation("Server", "Kestrel"); return r; } };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));
        Assert.Contains(report.Findings, f => f.Id.StartsWith("sec-no-hsts") && f.Severity == ApiReviewSeverity.Low);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("sec-no-xcto") && f.Severity == ApiReviewSeverity.Low);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("sec-no-cache-control") && f.Severity == ApiReviewSeverity.Low);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("sec-server-disclosure") && f.Severity == ApiReviewSeverity.Info);
        Assert.Contains(report.Targets[0].Checks, c => c.CheckId == "rest-rate-limit-headers" && c.Result == ApiReviewCheckResult.ManualReview);
    }

    [Fact]
    public async Task UnexpectedlyPublic_AuthEndpointAnswersAnonymously_IsHigh_ProperRejectionIsPass()
    {
        // Observed traffic carried a bearer, but the environment has no authenticated context: the public probe must still be honest.
        var open = new Fixture { Respond = (_, _) => Json(HttpStatusCode.OK, "{\"secret\":1}") };
        var openTarget = Rest(auth: true) with { AuthRequired = false, Operations = [new ApiReviewOperation { Method = "GET", Path = "/api/children", AuthObserved = true, ObservedCount = 2 }] };
        var openReport = await Engine(open).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, openTarget));
        Assert.Contains(openReport.Findings, f => f.Id.StartsWith("rest-unexpectedly-public") && f.Severity == ApiReviewSeverity.High);

        var closed = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath.Contains("unknown-route") ? Json(HttpStatusCode.NotFound, "{\"title\":\"nf\",\"status\":404,\"type\":\"x\"}", "application/problem+json") : Json(HttpStatusCode.Unauthorized, "{\"title\":\"Unauthorized\",\"status\":401,\"type\":\"x\"}", "application/problem+json") };
        var closedReport = await Engine(closed).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, openTarget));
        Assert.Contains(closedReport.Targets[0].Operations[0].Checks, c => c.CheckId == "rest-auth-enforced" && c.Result == ApiReviewCheckResult.Pass);
        Assert.DoesNotContain(closedReport.Findings, f => f.Id.StartsWith("rest-unexpectedly-public"));
    }

    [Fact]
    public async Task ProductionPolicy_NoErrorProbes_ReadOnly_IntrospectionWarned()
    {
        var schema = IntrospectionWith(["children"], [], []);
        var fixture = new Fixture { Respond = (req, body) => req.RequestUri!.AbsolutePath == "/api/graphql-v2" ? (body?.Contains("__schema") == true ? Json(HttpStatusCode.OK, schema) : Json(HttpStatusCode.OK, "{\"data\":{\"__typename\":\"Query\"}}")) : Json(HttpStatusCode.OK, "{\"ok\":true}") };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, true, Rest(), GraphQl()));
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.AbsolutePath.Contains("birknext-unknown-route"));
        Assert.DoesNotContain(fixture.Bodies, b => b.Contains("__birkNextUnknownFieldProbe"));
        Assert.Contains(report.Targets.SelectMany(t => t.Checks), c => c.CheckId == "errors-unknown-route" && c.Result == ApiReviewCheckResult.NotTested);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("gql-introspection-enabled") && f.Severity == ApiReviewSeverity.Medium);
        Assert.All(fixture.Requests, r => Assert.True(r.Method == HttpMethod.Get || r.Method == HttpMethod.Options || (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/graphql-v2")));
    }

    [Fact]
    public async Task WriteOperationsObserved_AreListedForManualReview_NeverExecuted()
    {
        var fixture = new Fixture { Respond = (_, _) => Json(HttpStatusCode.OK, "{\"ok\":true}") };
        var target = Rest(ops: [("GET", "/api/children"), ("POST", "/api/children"), ("DELETE", "/api/children/1")]);
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, target));
        Assert.DoesNotContain(fixture.Requests, r => r.Method == HttpMethod.Post || r.Method == HttpMethod.Delete);
        Assert.Equal(2, report.Targets[0].Operations.Count(o => o.Result == ApiReviewCheckResult.ManualReview && !o.Executed));
        Assert.Equal(2, report.Coverage.UnsafeOperationsNotExecuted);
        Assert.Contains(report.ManualReviewItems, m => m.Contains("2 observed write operation(s)"));
    }

    [Fact]
    public async Task UnreachableTarget_IsNotTested_NotZeroFindings()
    {
        var fixture = new Fixture { Respond = (_, _) => throw new HttpRequestException("connection refused", null, null) };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()));
        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewTargetStatus.NotTested, target.Status);
        Assert.Null(target.FindingCount);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("rest-not-executed"));
    }

    // ── Pure helpers ────────────────────────────────────────────────────────────

    [Fact]
    public void JsonShape_RecordsPathsAndTypesOnly_NeverValues()
    {
        using var doc = JsonDocument.Parse("{\"id\":7,\"name\":\"Kari Nordmann\",\"tags\":[\"a\",\"b\"],\"child\":{\"born\":null,\"status\":\"ACTIVE\"},\"items\":[{\"x\":1.5},{\"x\":null}]}");
        var shape = JsonBodyInspector.Shape(doc.RootElement);
        Assert.Contains(shape, s => s.Path == "$.id" && s.Type == "integer");
        Assert.Contains(shape, s => s.Path == "$.name" && s.Type == "string");
        Assert.Contains(shape, s => s.Path == "$.tags[*]" && s.Type == "string");
        Assert.Contains(shape, s => s.Path == "$.child.born" && s.Type == "" && s.Nullable);
        Assert.Contains(shape, s => s.Path == "$.items[*].x" && s.Type == "number" && s.Nullable);
        Assert.DoesNotContain("Kari", JsonSerializer.Serialize(shape)); Assert.DoesNotContain("ACTIVE", JsonSerializer.Serialize(shape));
        Assert.Equal(["exception-type", "stack-trace"], JsonBodyInspector.LeakIndicators("System.InvalidOperationException: boom\n   at A.B.C() in x").OrderBy(x => x));
        Assert.Empty(JsonBodyInspector.LeakIndicators("{\"title\":\"Not Found\",\"status\":404}"));
        Assert.True(JsonBodyInspector.IsProblemDetails("application/problem+json", []));
    }

    [Fact]
    public void OpenApiReview_DuplicatesMissingOperationIdsAndSecurity()
    {
        const string doc = """{"openapi":"3.0.1","info":{"title":"T","version":"1"},"paths":{"/a/{id}":{"get":{"operationId":"getA","responses":{"200":{"description":"ok"}}}},"/a/{key}":{"get":{"operationId":"getA","responses":{"200":{"description":"ok"},"404":{"description":"nf"}}}},"/b":{"get":{"parameters":[{"name":"page","in":"query"}],"responses":{"200":{"description":"ok"}}}}}}""";
        var review = OpenApiDocumentReview.Review(doc, "https://x/swagger.json", "t");
        Assert.True(review.Valid); Assert.Equal(3, review.Operations.Count);
        Assert.Contains(review.Findings, f => f.Id.StartsWith("oas-duplicate-operations") && f.Evidence.Count == 2);
        Assert.Contains(review.Findings, f => f.Id.StartsWith("oas-missing-operationid") && f.Evidence.Contains("GET /b"));
        Assert.Contains(review.Findings, f => f.Id.StartsWith("oas-no-security-schemes") && f.Severity == ApiReviewSeverity.Medium);
        Assert.Contains(review.Findings, f => f.Id.StartsWith("oas-missing-error-responses"));
        Assert.Contains(review.Findings, f => f.Id.StartsWith("oas-missing-descriptions") && f.Severity == ApiReviewSeverity.Info);
        Assert.Contains(review.Checks, c => c.CheckId == "oas-pagination" && c.Result == ApiReviewCheckResult.Pass);
        Assert.True(OpenApiDocumentReview.TemplateMatches("/api/children/{id}", "/api/children/42"));
        Assert.False(OpenApiDocumentReview.TemplateMatches("/api/children/{id}", "/api/children"));
        Assert.False(OpenApiDocumentReview.Review("not json", "s", "t").Valid);
    }

    [Fact]
    public void GraphQlOperationMatching_NormalizesClientNames()
    {
        var roots = new[] { "children", "roles", "placementById", "searchChildren" };
        Assert.Equal("children", GraphQlSchemaReview.MatchRootField("GetChildren", roots));
        Assert.Equal("roles", GraphQlSchemaReview.MatchRootField("RolesQuery", roots));
        Assert.Equal("placementById", GraphQlSchemaReview.MatchRootField("get_placement_by_id", roots));
        Assert.Null(GraphQlSchemaReview.MatchRootField("Dashboard", roots));
        Assert.Null(GraphQlSchemaReview.MatchRootField(null, roots));
    }

    // ── Performance thresholds: one setting per check, evaluated and displayed from the same values ─────────────

    [Theory]
    [InlineData(400 * 1024, ApiReviewCheckResult.Pass)]
    [InlineData(600 * 1024, ApiReviewCheckResult.Warning)]
    public void RestPayload_UsesTheRestPayloadSetting_NotTheLargerGraphQlOne(long bytes, ApiReviewCheckResult expected)
    {
        var policy = new ApiReviewPolicy { RestPayloadWarningBytes = 500L * 1024, GraphQlPayloadWarningBytes = 1024L * 1024 };
        Assert.Equal(expected, policy.RestPayloadResult(bytes));
        Assert.Equal(512_000, policy.RestPayloadThreshold);
        Assert.Equal("512,000 bytes (500 KB)", ApiReviewPolicy.Bytes(policy.RestPayloadThreshold));
        Assert.Equal("1,048,576 bytes (1 MB)", ApiReviewPolicy.Bytes(policy.GraphQlPayloadWarningBytes!.Value));
    }

    [Fact]
    public void LegacyPolicy_WithoutPerTypeThresholds_StillReadsItsOwnSingleThreshold() =>
        Assert.Equal(1024 * 1024, new ApiReviewPolicy { LargePayloadBytes = 1024 * 1024 }.RestPayloadThreshold);

    [Theory]
    [InlineData(1200, ApiReviewCheckResult.Pass)]
    [InlineData(1499, ApiReviewCheckResult.Pass)]
    [InlineData(1500, ApiReviewCheckResult.Pass)]   // within threshold: "warning > 1500 ms"
    [InlineData(1501, ApiReviewCheckResult.Warning)]
    [InlineData(1600, ApiReviewCheckResult.Warning)]
    [InlineData(9000, ApiReviewCheckResult.Warning)]   // still Warning: one threshold, no Poor/Fail tier
    public void SingleRequestLatency_IsOneThreshold_NoPoorTierIsInvented(double ms, ApiReviewCheckResult expected)
    {
        var policy = new ApiReviewPolicy { SlowWarningMs = 1500, SlowPoorMs = null };
        Assert.Equal(expected, policy.LatencyResult(ms));
        Assert.Equal("warning > 1500 ms", policy.LatencyPolicyText);
    }

    [Theory]
    [InlineData(400, ApiReviewCheckResult.Pass)]
    [InlineData(700, ApiReviewCheckResult.Warning)]
    [InlineData(1200, ApiReviewCheckResult.Fail)]
    public void AHistoricalTwoTierPolicy_IsEvaluatedAndShownAsItWas(double ms, ApiReviewCheckResult expected)
    {
        var policy = new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000 };
        Assert.Equal(expected, policy.LatencyResult(ms));
        Assert.Equal("warning > 500 ms · poor > 1000 ms", policy.LatencyPolicyText);
    }

    [Theory]
    [InlineData(400 * 1024, ApiReviewCheckResult.Pass)]
    [InlineData(600 * 1024, ApiReviewCheckResult.Warning)]
    public async Task Engine_RestPayloadCheck_EvaluatesAndStatesTheSameThreshold(int bytes, ApiReviewCheckResult expected)
    {
        var body = "{\"a\":\"" + new string('x', bytes) + "\"}";
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath == "/api/children" ? Json(HttpStatusCode.OK, body) : Json(HttpStatusCode.NotFound, "{}", "application/problem+json") };
        var request = Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()) with
        {
            Policy = new ApiReviewPolicy { ErrorHandlingProbes = false, SlowWarningMs = 1500, RestPayloadWarningBytes = 500L * 1024, GraphQlPayloadWarningBytes = 1024L * 1024, LargePayloadBytes = 500L * 1024 },
        };
        var report = await Engine(fixture).RunAsync(request);
        var payload = report.Targets.Single().Operations.SelectMany(o => o.Checks).Single(c => c.CheckId == "rest-payload");
        var size = body.Length;
        Assert.Equal(size > 512_000 ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, payload.Result);
        Assert.Equal(expected, payload.Result);
        Assert.Contains("REST payload warning > 512,000 bytes (500 KB)", payload.Detail);
        Assert.DoesNotContain("1,048,576", payload.Detail);
        var latency = report.Targets.Single().Operations.SelectMany(o => o.Checks).Single(c => c.CheckId == "rest-latency");
        Assert.Contains("(warning > 1500 ms)", latency.Detail);
        Assert.DoesNotContain("poor", latency.Detail);
    }

    [Fact]
    public async Task Compression_AuthenticatedGatewayRequestIsNotAssessed_PublicRequestKeepsTheRule()
    {
        var gateway = new FakeGateway(true);
        var authenticated = await Engine(new Fixture(), gateway).RunAsync(Request(AuthenticatedTestingMethod.LocalHttpsProxy, false, Rest(auth: true)));
        var auth = authenticated.Targets.Single().Checks.Single(c => c.CheckId == "rest-compression");
        Assert.Equal(ApiReviewCheckResult.NotTested, auth.Result);
        Assert.Contains("does not advertise Accept-Encoding", auth.Detail);

        // Public path, response above the compression minimum and not encoded → Warning (the rule), with the evidence stated.
        var large = "{\"items\":\"" + new string('x', 4096) + "\"}";
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath == "/api/children" ? Json(HttpStatusCode.OK, large) : Json(HttpStatusCode.NotFound, "{}", "application/problem+json") };
        var publicReport = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()) with { Policy = new ApiReviewPolicy { ErrorHandlingProbes = false, CompressionMinimumBytes = 1024 } });
        var pub = publicReport.Targets.Single().Checks.Single(c => c.CheckId == "rest-compression");
        Assert.Equal(ApiReviewCheckResult.Warning, pub.Result);
        Assert.StartsWith("Request advertised gzip/br; 1 compressible response was not encoded at or above the compression minimum payload (1,024 bytes (1 KB)).", pub.Detail);
        Assert.Contains("Request advertised: gzip, br", pub.Evidence);
        Assert.Contains(fixture.Requests, r => r.Headers.TryGetValues("Accept-Encoding", out var v) && string.Join(",", v).Contains("gzip"));
        // A performance Warning is a check result, not automatically a finding — on either path.
        Assert.DoesNotContain(authenticated.Findings, f => f.RuleId.Contains("compression", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(publicReport.Findings, f => f.RuleId.Contains("compression", StringComparison.OrdinalIgnoreCase));
    }

    // ── Compression minimum payload (§42–45) ─────────────────────────────────────────────────────────────────────

    private static readonly ApiReviewPolicy CompressionPolicy = new() { CompressionMinimumBytes = 1024 };

    private static ApiReviewCheck Compress(params ApiReviewEngine.CompressionSample[] samples) => ApiReviewEngine.CompressionCheck(samples, CompressionPolicy);

    [Fact]
    public void Compression_NotRequested_IsNotTested() =>
        Assert.Equal(ApiReviewCheckResult.NotTested, Compress(new ApiReviewEngine.CompressionSample("GET /a", ApiReviewAccessMode.AuthenticatedHttp, "application/json", 50_000, null)).Result);

    [Fact]
    public void Compression_TinyUncompressedPayload_IsNotApplicable_NeverWarning()
    {
        var check = Compress(new ApiReviewEngine.CompressionSample("GET /a", ApiReviewAccessMode.PublicHttp, "application/json", 61, null));
        Assert.Equal(ApiReviewCheckResult.NotApplicable, check.Result);
        Assert.Contains("61 bytes", check.Detail);
        Assert.Contains("below the compression minimum payload (1,024 bytes (1 KB))", check.Detail);
    }

    [Fact]
    public void Compression_LargeUncompressedPayload_IsWarning() =>
        Assert.Equal(ApiReviewCheckResult.Warning, Compress(new ApiReviewEngine.CompressionSample("GET /a", ApiReviewAccessMode.PublicHttp, "application/json", 1024, null)).Result);

    [Fact]
    public void Compression_LargeCompressedPayload_IsPass()
    {
        var check = Compress(new ApiReviewEngine.CompressionSample("GET /a", ApiReviewAccessMode.PublicHttp, "application/json", 800, "br"), new ApiReviewEngine.CompressionSample("GET /b", ApiReviewAccessMode.PublicHttp, "application/json", 90, null));
        Assert.Equal(ApiReviewCheckResult.Pass, check.Result);
        Assert.StartsWith("Content-Encoding: br on 1 of 2 responses", check.Detail);
    }

    [Fact]
    public void Compression_IsEvaluatedOverEveryResponse_NotOnlyTheFirst() =>
        Assert.Equal(ApiReviewCheckResult.Warning, Compress(new ApiReviewEngine.CompressionSample("GET /small", ApiReviewAccessMode.PublicHttp, "application/json", 40, null), new ApiReviewEngine.CompressionSample("GET /big", ApiReviewAccessMode.PublicHttp, "application/json", 90_000, null)).Result);

    [Fact]
    public void Compression_AlreadyCompressedContentTypes_AreNotApplicable()
    {
        var check = Compress(new ApiReviewEngine.CompressionSample("GET /img", ApiReviewAccessMode.PublicHttp, "image/png", 500_000, null), new ApiReviewEngine.CompressionSample("GET /zip", ApiReviewAccessMode.PublicHttp, "application/zip", 500_000, null));
        Assert.Equal(ApiReviewCheckResult.NotApplicable, check.Result);
        Assert.Contains("image/png", check.Detail);
    }

    [Fact]
    public void Compression_UnknownSize_IsNotTested() =>
        Assert.Equal(ApiReviewCheckResult.NotTested, Compress(new ApiReviewEngine.CompressionSample("GET /a", ApiReviewAccessMode.PublicHttp, "application/json", null, null)).Result);

    // ── Average API Latency (§35–38) ─────────────────────────────────────────────────────────────────────────────

    private static readonly ApiReviewPolicy AveragePolicy = new() { SlowWarningMs = 1500, AverageLatencyWarningMs = 500 };

    [Fact]
    public void AverageLatency_BelowThreshold_IsPass()
    {
        var check = ApiReviewEngine.AverageLatencyCheck([200, 300, 400], AveragePolicy, "review request")!;
        Assert.Equal(ApiReviewCheckResult.Pass, check.Result);
        Assert.StartsWith("Mean 300 ms over 3 review requests (warning > 500 ms, Average API Latency).", check.Detail);
    }

    [Fact]
    public void AverageLatency_AboveThreshold_IsWarning_NoPoorTier()
    {
        Assert.Equal(ApiReviewCheckResult.Warning, ApiReviewEngine.AverageLatencyCheck([400, 700], AveragePolicy, "review request")!.Result);
        Assert.Equal(ApiReviewCheckResult.Warning, ApiReviewEngine.AverageLatencyCheck([9000, 9000], AveragePolicy, "review request")!.Result);
    }

    [Fact]
    public void AverageLatency_OneSample_IsNotAssessed_AndUnmeasuredTimingsAreNotSamples()
    {
        var check = ApiReviewEngine.AverageLatencyCheck([420, null], AveragePolicy, "review request")!;
        Assert.Equal(ApiReviewCheckResult.NotTested, check.Result);
        Assert.Contains("at least two request samples are required", check.Detail);
    }

    [Fact]
    public void AverageLatency_OlderPolicyWithoutTheThreshold_AddsNoCheck() =>
        Assert.Null(ApiReviewEngine.AverageLatencyCheck([100, 200], new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000 }, "review request"));

    [Fact]
    public async Task AverageAndSingleRequestLatency_AreEvaluatedIndependently()
    {
        // Two operations at gateway timing 12 ms each; an average threshold of 5 ms warns while each request passes 1500 ms.
        var gateway = new FakeGateway(true);
        var request = Request(AuthenticatedTestingMethod.LocalHttpsProxy, false, Rest(auth: true, ops: [("GET", "/api/children"), ("GET", "/api/children/1")])) with
        {
            Policy = new ApiReviewPolicy { ErrorHandlingProbes = false, SlowWarningMs = 1500, AverageLatencyWarningMs = 5, RestPayloadWarningBytes = 500L * 1024 },
        };
        var target = (await Engine(new Fixture(), gateway).RunAsync(request)).Targets.Single();
        var perRequest = target.Operations.Where(o => o.Executed).SelectMany(o => o.Checks).Where(c => c.CheckId == "rest-latency").ToList();
        Assert.Equal(2, perRequest.Count);
        Assert.All(perRequest, c => Assert.Equal(ApiReviewCheckResult.Pass, c.Result));
        var average = target.Checks.Single(c => c.CheckId == "rest-average-latency");
        Assert.Equal(ApiReviewCheckResult.Warning, average.Result);
        Assert.StartsWith("Mean 12 ms over 2 review requests (warning > 5 ms", average.Detail);
        Assert.DoesNotContain(target.Checks.Concat(target.Operations.SelectMany(o => o.Checks)), c => c.CheckId == "rest-latency" && c.Detail.Contains("Average"));
    }

    // ── GraphQL payload (§39–41) ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<ApiReviewTargetResult> GraphQlPayloadTarget(params ApiReviewOperation[] ops)
    {
        var fixture = new Fixture { Respond = (req, body) => body?.Contains("__schema") == true
            ? Json(HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Introspection disabled\"}]}") : Json(HttpStatusCode.OK, "{\"data\":{\"__typename\":\"Query\"}}") };
        var target = GraphQl() with { Operations = ops.Select(o => o with { Path = GraphQl().BasePath }).ToList() };
        var request = Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, target) with
        {
            Policy = new ApiReviewPolicy { ErrorHandlingProbes = false, SlowWarningMs = 1500, GraphQlPayloadWarningBytes = 1024L * 1024, AverageLatencyWarningMs = 500 },
        };
        return (await Engine(fixture).RunAsync(request)).Targets.Single();
    }

    private static ApiReviewOperation Gql(string name, long? bytes, bool historical = false) =>
        new() { Method = "POST", OperationType = GraphQlOperationType.Query, OperationName = name, ObservedCount = 3, ObservedResponseBytes = bytes, ObservedResponseSamples = bytes is null ? 0 : 3, Historical = historical };

    [Theory]
    [InlineData(900 * 1024, ApiReviewCheckResult.Pass)]
    [InlineData(1100 * 1024, ApiReviewCheckResult.Warning)]
    public async Task GraphQlPayload_UsesObservedBusinessResponses(long bytes, ApiReviewCheckResult expected)
    {
        var target = await GraphQlPayloadTarget(Gql("HentRoller", bytes), Gql("HentBarn", 2048));
        var check = target.Checks.Single(c => c.CheckId == "gql-payload");
        Assert.Equal(expected, check.Result);
        Assert.Contains("(Query HentRoller)", check.Detail);
        Assert.Contains("GraphQL payload warning > 1,048,576 bytes (1 MB)", check.Detail);
        Assert.Contains(check.Evidence, e => e.StartsWith("Source: Endpoint Discovery"));
    }

    [Fact]
    public async Task GraphQlPayload_OnlyTheTypenameProbe_IsNotAssessed_WithTheReason()
    {
        var target = await GraphQlPayloadTarget(Gql("HentRoller", null));
        var check = target.Checks.Single(c => c.CheckId == "gql-payload");
        Assert.Equal(ApiReviewCheckResult.NotTested, check.Result);
        Assert.Contains("The safe __typename probe is not used for payload-quality assessment.", check.Detail);
        Assert.Equal(ApiReviewCheckResult.NotTested, target.Checks.Single(c => c.CheckId == "gql-average-latency").Result);
    }

    [Fact]
    public async Task GraphQlPayload_HistoricalOnlyEvidence_IsNotACurrentMeasurement()
    {
        var target = await GraphQlPayloadTarget(Gql("HentRoller", null), Gql("GammelSok", 5L * 1024 * 1024, historical: true));
        var check = target.Checks.Single(c => c.CheckId == "gql-payload");
        Assert.Equal(ApiReviewCheckResult.NotTested, check.Result);
        Assert.Contains("1 operation(s) have historical evidence only", check.Detail);
    }

    [Fact]
    public void ProfileIdentity_IsItsOwnValue_AndDefaultsToNotRecordedForOlderReports()
    {
        var legacy = JsonSerializer.Deserialize<ApiReviewPolicy>("{\"slowWarningMs\":500,\"slowPoorMs\":1000}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ApiReviewPerformanceProfile.NotRecorded, legacy.PerformanceProfile);
        Assert.Null(legacy.AverageLatencyWarningMs);
        Assert.Null(legacy.CompressionMinimumBytes);
        var roundTrip = JsonSerializer.Deserialize<ApiReviewPolicy>(JsonSerializer.Serialize(new ApiReviewPolicy { PerformanceProfile = ApiReviewPerformanceProfile.Strict }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ApiReviewPerformanceProfile.Strict, roundTrip.PerformanceProfile);
    }

    private static string IntrospectionWith(string[] queryFields, string[] mutationFields, string[] deprecated)
    {
        static object Field(string name, bool deprecatedField, string type = "Child") => new { name, description = (string?)null, args = new object[] { new { name = "first", description = (string?)null, type = new { kind = "SCALAR", name = "Int", ofType = (object?)null }, defaultValue = (string?)null } }, type = new { kind = "LIST", name = (string?)null, ofType = new { kind = "OBJECT", name = type, ofType = (object?)null } }, isDeprecated = deprecatedField, deprecationReason = deprecatedField ? "old" : null };
        var types = new List<object>
        {
            new { kind = "OBJECT", name = "Query", description = "root", fields = queryFields.Select(f => Field(f, false)).ToArray(), inputFields = (object?)null, interfaces = Array.Empty<object>(), enumValues = (object?)null, possibleTypes = (object?)null },
            new { kind = "OBJECT", name = "Child", description = (string?)null, fields = new object[] { new { name = "id", description = (string?)null, args = Array.Empty<object>(), type = new { kind = "NON_NULL", name = (string?)null, ofType = new { kind = "SCALAR", name = "ID", ofType = (object?)null } }, isDeprecated = false, deprecationReason = (string?)null }, new { name = "oldStatus", description = (string?)null, args = Array.Empty<object>(), type = new { kind = "SCALAR", name = "String", ofType = (object?)null }, isDeprecated = deprecated.Contains("Child.oldStatus"), deprecationReason = deprecated.Contains("Child.oldStatus") ? "use status" : null } }, inputFields = (object?)null, interfaces = Array.Empty<object>(), enumValues = (object?)null, possibleTypes = (object?)null },
            new { kind = "SCALAR", name = "String", description = (string?)null, fields = (object?)null, inputFields = (object?)null, interfaces = (object?)null, enumValues = (object?)null, possibleTypes = (object?)null },
            new { kind = "SCALAR", name = "ID", description = (string?)null, fields = (object?)null, inputFields = (object?)null, interfaces = (object?)null, enumValues = (object?)null, possibleTypes = (object?)null },
            new { kind = "SCALAR", name = "Int", description = (string?)null, fields = (object?)null, inputFields = (object?)null, interfaces = (object?)null, enumValues = (object?)null, possibleTypes = (object?)null },
        };
        if (mutationFields.Length > 0)
            types.Add(new { kind = "OBJECT", name = "Mutation", description = (string?)null, fields = mutationFields.Select(f => (object)new { name = f, description = (string?)null, args = Array.Empty<object>(), type = new { kind = "SCALAR", name = "Boolean", ofType = (object?)null }, isDeprecated = false, deprecationReason = (string?)null }).ToArray(), inputFields = (object?)null, interfaces = Array.Empty<object>(), enumValues = (object?)null, possibleTypes = (object?)null });
        return JsonSerializer.Serialize(new { data = new { __schema = new { queryType = new { name = "Query" }, mutationType = mutationFields.Length > 0 ? new { name = "Mutation" } : null, subscriptionType = (object?)null, types, directives = Array.Empty<object>() } } });
    }

    /// <summary>Gateway fake: executes "authenticated" checks without ever surfacing the credential; counts calls.</summary>
    private sealed class FakeGateway(bool available) : IAuthenticatedReviewGateway
    {
        public const string SecretToken = "eyJhbGciOiJSUzI1NiJ9.SECRET-TOKEN-NEVER-IN-REPORT.sig";
        public int RestCalls { get; private set; }
        public int GraphQlCalls { get; private set; }

        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => available && identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy
            ? new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, ObservedHost = "api-dev.example.test", Reason = "Authenticated via Local HTTPS Proxy" }
            : new() { Method = identity.Method, ContextStatus = identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy ? AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic : AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "Public only" };

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default)
        {
            RestCalls++;
            if (httpMethod is not ("GET" or "HEAD" or "OPTIONS")) throw new InvalidOperationException("unsafe method reached the gateway");
            var unknown = url.Contains("birknext-unknown-route");
            return Task.FromResult(new AuthenticatedReviewExecutionOutcome
            {
                Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy,
                Result = new AuthenticatedApiExecutionResult
                {
                    StatusCode = unknown ? 404 : 200, ContentType = unknown ? "application/problem+json" : "application/json", ContentLength = 64, ElapsedMs = 12, Outcome = "HTTP 200", JsonValid = true, ProblemDetails = unknown,
                    BodyShape = unknown ? [new("$", "object", false), new("$.title", "string", false), new("$.status", "integer", false)] : [new("$", "object", false), new("$.items[*].id", "integer", false)],
                    SecurityHeaders = new Dictionary<string, string> { ["cache-control"] = "no-store", ["strict-transport-security"] = "max-age=1", ["x-content-type-options"] = "nosniff", ["access-control-allow-origin"] = "https://m2lbdev.example.test" },
                },
                Message = "HTTP 200"
            });
        }

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default)
        {
            GraphQlCalls++;
            ManagedEdgePolicyGuard(query);
            return Task.FromResult(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, Result = new AuthenticatedApiExecutionResult { StatusCode = 200, ContentType = "application/json", GraphQlHasData = true, GraphQlErrorCount = 0, JsonValid = true, Outcome = "ok" }, Message = "ok" });
        }

        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedGraphQlSchemaOutcome { Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, StatusCode = 200, IntrospectionDisabled = true, Message = "disabled" });

        private static void ManagedEdgePolicyGuard(string query) { if (query.Contains("mutation", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("mutation reached the gateway"); }
    }
}
