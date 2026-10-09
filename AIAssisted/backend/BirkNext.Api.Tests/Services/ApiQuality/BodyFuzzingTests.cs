using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ApiQuality.Fuzzing;
using BirkNext.Api.Services.ApiQuality.Security;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeSecurity;
using Microsoft.Extensions.Logging.Abstractions;
using static BirkNext.Api.Tests.Services.ApiQuality.RuntimeSecurityTestSupport;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Explicit opt-in request-body fuzzing: contract body extraction, deterministic bounded cases, the opt-in/trust/cleanup gates, the
/// backend guard (DELETE, GraphQL, size, content type), execution outcomes against synthetic responses and production blocking.
/// </summary>
public sealed class BodyFuzzingTests
{
    private static readonly OpenApiExtractor Extractor = new(NullLogger<OpenApiExtractor>.Instance);

    internal const string OpenApi = """
        {"openapi":"3.0.1","info":{"title":"Search API","version":"1"},
         "paths":{
          "/api/search":{"post":{"operationId":"search","requestBody":{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/SearchRequest"}}}},
                                 "responses":{"200":{"description":"ok"},"400":{"description":"bad"},"422":{"description":"invalid"}}}},
          "/api/items/{id}":{"put":{"requestBody":{"content":{"application/json":{"schema":{"type":"object","properties":{"name":{"type":"string"}}}}}},"responses":{"200":{"description":"ok"}}},
                             "delete":{"responses":{"204":{"description":"gone"}}}},
          "/api/notes":{"post":{"responses":{"201":{"description":"created"}}}}
         },
         "components":{"schemas":{"SearchRequest":{"type":"object","required":["term","kind"],"additionalProperties":false,
           "properties":{"term":{"type":"string","minLength":1,"maxLength":20},"kind":{"type":"string","enum":["Open","Closed"]},
                         "page":{"type":"integer","minimum":1,"maximum":50},"childId":{"type":"string","format":"uuid"},"from":{"type":"string","format":"date"}}}}}}
        """;

    private static NormalizedContract Contract() => Extractor.Extract(OpenApi).Contract!;
    private static readonly ApiReviewTarget Target = Rest(contract: ApiOrigin + "/swagger/v1/swagger.json", basePath: "/");
    private static ApiFuzzingSettings Settings(int perOperation = 8, int total = 50) => new() { Level = ApiFuzzingLevel.ContractFuzzing, BodyFuzzing = true, MaxCasesPerOperation = perOperation, MaxTotalRequests = total, RequestDelayMs = 100 };
    private static BodyFuzzOperationOptIn OptIn(string method = "POST", string path = "/api/search", BodyFuzzingPolicy policy = BodyFuzzingPolicy.ReadOnlyBodySafe, string? cleanup = null) =>
        new() { Method = method, Path = path, Policy = policy, CleanupStrategyId = cleanup };

    // ── Contract extraction ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extractor_ResolvesTheLocalBodySchema_WithConstraintsAndSourceRefs()
    {
        var body = Contract().Operations.Single(o => o.Method == "POST" && o.Path == "/api/search").RequestBody!;
        Assert.True(body.Required);
        Assert.False(body.AllowsAdditionalProperties);
        Assert.Equal(["term", "kind", "page", "childId", "from"], body.Fields.Select(f => f.Name));
        var term = body.Fields[0];
        Assert.True(term.Required);
        Assert.Equal(20, term.MaxLength);
        Assert.Equal("#/components/schemas/SearchRequest/properties/term", term.SourceRef);
        Assert.Equal(["Open", "Closed"], body.Fields[1].EnumValues);
        Assert.Equal(50, body.Fields[2].Maximum);
        Assert.Null(Contract().Operations.Single(o => o.Path == "/api/notes").RequestBody);
    }

    // ── Case generation and gates ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Generator_OnlyOptedInOperations_DeleteNever_StateChangingBlocked()
    {
        var result = ApiBodyFuzzCaseGenerator.Rest(Target, Contract(), Settings(), [OptIn(), OptIn("PUT", "/api/items/{id}", BodyFuzzingPolicy.StateChangingWithCleanup, "undo-items"), OptIn("POST", "/api/notes")], []);
        Assert.Equal(ApiFuzzSafetyClassification.ReadOnlyEligible, result.Operations.Single(o => o.OperationId == "POST /api/search (body)").Classification);
        Assert.Equal(ApiFuzzSafetyClassification.StateChangingWithoutCleanup, result.Operations.Single(o => o.OperationId == "PUT /api/items/{id} (body)").Classification);
        Assert.Equal(ApiFuzzSafetyClassification.UnsafeMethod, result.Operations.Single(o => o.OperationId == "DELETE /api/items/{id} (body)").Classification);
        Assert.Equal(ApiFuzzSafetyClassification.NoRequestBodySchema, result.Operations.Single(o => o.OperationId == "POST /api/notes (body)").Classification);
        Assert.All(result.Cases, c => Assert.Equal("POST", c.Method));
        Assert.All(result.Cases, c => Assert.Equal(BodyFuzzingPolicy.ReadOnlyBodySafe, c.BodyPolicy));
    }

    [Fact]
    public void Generator_RegisteredCleanupStrategy_StillDoesNotExecuteStateChangingCases()
    {
        var result = ApiBodyFuzzCaseGenerator.Rest(Target, Contract(), Settings(), [OptIn("PUT", "/api/items/{id}", BodyFuzzingPolicy.StateChangingWithCleanup, "undo-items")], ["undo-items"]);
        var op = result.Operations.Single(o => o.OperationId == "PUT /api/items/{id} (body)");
        Assert.Equal(ApiFuzzSafetyClassification.StateChangingWithoutCleanup, op.Classification);
        Assert.Contains("not executed in this milestone", op.Reason);
        Assert.Empty(result.Cases);
    }

    [Fact]
    public void Generator_NoOptIn_NoBodyCase()
    {
        var result = ApiBodyFuzzCaseGenerator.Rest(Target, Contract(), Settings(), [], []);
        Assert.Empty(result.Cases);
        Assert.All(result.Operations, o => Assert.NotEqual(ApiFuzzSafetyClassification.ReadOnlyEligible, o.Classification));
    }

    [Fact]
    public void Generator_EveryMutationType_DeterministicBoundedFlat()
    {
        var op = Contract().Operations.Single(o => o.Path == "/api/search");
        var all = ApiBodyFuzzCaseGenerator.AllCases(Target, op, "POST /api/search", op.RequestBody!, Settings());
        foreach (var type in ApiBodyFuzzCaseGenerator.Priority) Assert.Contains(all, c => c.MutationType == type);
        Assert.Equal(all.Select(c => c.CaseId), ApiBodyFuzzCaseGenerator.AllCases(Target, op, "POST /api/search", op.RequestBody!, Settings()).Select(c => c.CaseId));
        Assert.Equal(all.Count, all.Select(c => c.CaseId).Distinct().Count());
        Assert.All(all, c => Assert.True(Encoding.UTF8.GetByteCount(c.Body!) <= Settings().MaxBodyBytes));
        foreach (var c in all.Where(c => c.MutationType != ApiFuzzMutationType.BodyMalformedJson))
        {
            using var doc = JsonDocument.Parse(c.Body!);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.All(doc.RootElement.EnumerateObject(), p => Assert.NotEqual(JsonValueKind.Object, p.Value.ValueKind));   // no nesting is generated
        }
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(all.Single(c => c.MutationType == ApiFuzzMutationType.BodyMalformedJson).Body!));
        Assert.Equal(21, JsonDocument.Parse(all.Single(c => c.MutationType == ApiFuzzMutationType.BodyStringTooLong).Body!).RootElement.GetProperty("term").GetString()!.Length);
        Assert.False(JsonDocument.Parse(all.Single(c => c.MutationType == ApiFuzzMutationType.BodyMissingRequiredField && c.Parameter == "kind").Body!).RootElement.TryGetProperty("kind", out _));
        Assert.Equal(ApiFuzzExpectedBehavior.RejectWithClientError, all.Single(c => c.MutationType == ApiFuzzMutationType.BodyUnknownField).ExpectedBehavior);   // additionalProperties: false
        Assert.True(all.Single(c => c.MutationType == ApiFuzzMutationType.BodyMissingContentType).OmitContentType);
        Assert.Equal("text/plain", all.Single(c => c.MutationType == ApiFuzzMutationType.BodyWrongContentType).ContentType);
    }

    [Fact]
    public void Generator_PerOperationCap_InterleavesDistinctMutationTypes()
    {
        var result = ApiBodyFuzzCaseGenerator.Rest(Target, Contract(), Settings(perOperation: 4), [OptIn()], []);
        Assert.Equal(4, result.Cases.Count);
        Assert.Equal(4, result.Cases.Select(c => c.MutationType).Distinct().Count());
        Assert.Equal(ApiBodyFuzzCaseGenerator.Priority.Take(4), result.Cases.Select(c => c.MutationType));
    }

    // ── Guard ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly ApiEnvironmentSafetyDecision Allowed = new() { State = ApiEnvironmentSafetyState.Allowed, Reason = "ok" };
    private static ApiSafeRequest Body(string method = "POST", string? body = "{\"term\":\"x\"}", string? contentType = "application/json", BodyFuzzApproval? approval = null, string? graphQl = null) =>
        new(method, ApiOrigin + "/api/search", [], graphQl, false, body, contentType, approval ?? new BodyFuzzApproval(true, BodyFuzzingPolicy.ReadOnlyBodySafe, "POST /api/search (body)"));

    [Fact]
    public void Guard_AllowsOnlyApprovedTrustedReadOnlyBodyCases()
    {
        var settings = new ApiFuzzingSettings().Clamped();
        Assert.Null(ApiSafeRequestGuard.Validate(Body(), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body() with { BodyApproval = null }, ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(approval: new(false, BodyFuzzingPolicy.ReadOnlyBodySafe, "op")), ApiOrigin, settings, Allowed));
        Assert.Contains("State-changing", ApiSafeRequestGuard.Validate(Body(approval: new(true, BodyFuzzingPolicy.StateChangingWithCleanup, "op")), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body("DELETE"), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(body: new string('a', settings.MaxBodyBytes + 1)), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(contentType: "application/xml"), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(graphQl: "mutation { x }"), ApiOrigin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(), "https://other.example.test", settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(Body(), ApiOrigin, settings, new ApiEnvironmentSafetyDecision { State = ApiEnvironmentSafetyState.ProductionBlocked, Reason = "prod" }));
        // A write method without a body is still refused by the read-only rule.
        Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("POST", ApiOrigin + "/api/search", []), ApiOrigin, settings, Allowed));
    }

    [Fact]
    public void Limits_AreClampedByTheBackend()
    {
        var clamped = new ApiFuzzingSettings { MaxBodyBytes = 1_000_000, MaxCasesPerOperation = 500, MaxTotalRequests = 5000, MaxConcurrency = 16, RequestDelayMs = 0, RequestTimeoutSeconds = 999 }.Clamped();
        Assert.Equal(ApiFuzzingLimits.MaxBodyBytes, clamped.MaxBodyBytes);
        Assert.Equal(ApiFuzzingLimits.MaxCasesPerOperation, clamped.MaxCasesPerOperation);
        Assert.Equal(ApiFuzzingLimits.MaxTotalRequests, clamped.MaxTotalRequests);
        Assert.Equal(1, clamped.MaxConcurrency);
        Assert.Equal(ApiFuzzingLimits.MinDelayMs, clamped.RequestDelayMs);
        Assert.Equal(ApiFuzzingLimits.MaxTimeoutSeconds, clamped.RequestTimeoutSeconds);
    }

    // ── Service: plan, trust and execution ────────────────────────────────────────────────────────

    private static ApiFuzzingService Service(Handler handler, ITrustedSecurityTargetRegistry? trust = null, IAuthenticatedReviewGateway? gateway = null) =>
        new(new HttpClient(handler), gateway ?? new Gateway(false), Extractor, new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance), ApiEnvironmentSafetyPolicy.Default,
            NullLogger<ApiFuzzingService>.Instance, trust: trust ?? Registry());

    private static ApiFuzzingRunRequest Run(string type = "Development", ApiFuzzingSettings? settings = null, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp, bool auth = false) => new()
    {
        Review = ReviewRequest(type, method: method, targets: [Target with { AuthRequired = auth }]), Settings = settings ?? Settings(), BodyOperations = [OptIn()],
    };

    private static Handler Api(Func<string?, HttpResponseMessage> search) => new()
    {
        Respond = (r, body) => r.RequestUri!.AbsolutePath switch
        {
            "/swagger/v1/swagger.json" => Text(HttpStatusCode.OK, OpenApi),
            "/api/search" => search(body),
            _ => Text(HttpStatusCode.BadRequest, "{\"title\":\"Bad\",\"status\":400}"),
        },
    };

    [Fact]
    public async Task Plan_UntrustedTarget_HasNoBodyCase_AndSaysWhy()
    {
        var handler = Api(_ => Text(HttpStatusCode.BadRequest, "{}"));
        var plan = await Service(handler, new TrustedSecurityTargetRegistry(Microsoft.Extensions.Options.Options.Create(new SecurityTestingOptions()))).PlanAsync(Run());
        Assert.DoesNotContain(plan.Cases, c => c.Location == ApiFuzzParameterLocation.Body);
        Assert.Contains(plan.Operations, o => o.Classification == ApiFuzzSafetyClassification.UntrustedTarget);
        Assert.Equal(TrustedTargetState.NotRegistered, Assert.Single(plan.BodyTrust).State);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Plan_BodyFuzzingOff_IsTheExistingReadOnlyBehaviour()
    {
        var plan = await Service(Api(_ => Text(HttpStatusCode.BadRequest, "{}"))).PlanAsync(Run(settings: Settings() with { BodyFuzzing = false }));
        Assert.DoesNotContain(plan.Cases, c => c.Location == ApiFuzzParameterLocation.Body);
        Assert.Empty(plan.BodyTrust);
    }

    [Theory]
    [InlineData(400, "{\"title\":\"Bad\",\"status\":400}", ApiFuzzOutcome.HandledValidation)]
    [InlineData(422, "{\"title\":\"Invalid\",\"status\":422}", ApiFuzzOutcome.HandledValidation)]
    [InlineData(500, "{\"error\":\"oops\"}", ApiFuzzOutcome.Unexpected5xx)]
    [InlineData(200, "{\"items\":[]}", ApiFuzzOutcome.UnexpectedAcceptance)]
    [InlineData(400, "{\"detail\":\"System.NullReferenceException at Search.Run() in C:\\\\src\\\\Search.cs:line 9\"}", ApiFuzzOutcome.PotentialInformationLeak)]
    public async Task Run_ClassifiesBodyCaseResponses(int status, string body, ApiFuzzOutcome expected)
    {
        var handler = Api(_ => Text((HttpStatusCode)status, body));
        var report = await Service(handler).RunAsync(Run(settings: Settings(perOperation: 2) with { StopOnUnexpected5xx = false }), "run-1", null);
        var bodyCases = report.Cases.Where(c => c.Location == ApiFuzzParameterLocation.Body).Select(c => c.CaseId).ToHashSet();
        Assert.NotEmpty(bodyCases);
        Assert.All(report.Results.Where(r => bodyCases.Contains(r.CaseId)), r => Assert.Equal(expected, r.Outcome));
        Assert.All(handler.Requests.Where(r => r.Method == HttpMethod.Post), r => Assert.Null(r.Headers.Authorization));
        Assert.DoesNotContain("NullReferenceException", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Run_SendsTheSyntheticBodies_AndOmitsContentTypeOnlyForThatCase()
    {
        var handler = Api(_ => Text(HttpStatusCode.BadRequest, "{\"status\":400}"));
        var report = await Service(handler).RunAsync(Run(settings: Settings(perOperation: 8)), "run-2", null);
        var posts = handler.Requests.Select((r, i) => (Request: r, Body: handler.Bodies[i])).Where(x => x.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal(8, posts.Count);
        Assert.Contains(posts, p => p.Body == "{\"birknext\": ");
        Assert.Contains(posts, p => p.Request.Content?.Headers.ContentType is null);
        Assert.Contains(posts, p => p.Request.Content?.Headers.ContentType?.MediaType == "text/plain");
        Assert.Equal(BodyFuzzingPolicy.ReadOnlyBodySafe, Assert.Single(report.BodyOperations).Policy);
    }

    [Fact]
    public async Task Run_TotalRequestCapIsEnforced()
    {
        var handler = Api(_ => Text(HttpStatusCode.BadRequest, "{\"status\":400}"));
        var report = await Service(handler).RunAsync(Run(settings: Settings(perOperation: 8, total: 3)), "run-3", null);
        Assert.True(handler.Requests.Count(r => r.RequestUri!.AbsolutePath != "/swagger/v1/swagger.json") <= 3);
        Assert.True(report.CasesOverBudget > 0 || report.CasesPlanned <= 3);
    }

    [Fact]
    public async Task Run_Production_SendsNothing()
    {
        var handler = Api(_ => Text(HttpStatusCode.OK, "{}"));
        var report = await Service(handler).RunAsync(Run("Production"), "run-4", null);
        Assert.Equal(ApiFuzzCompleteness.NotRun, report.Completeness);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Run_Authenticated_GoesThroughTheGatewayWithAServerBuiltApproval()
    {
        var gateway = new Gateway(status: 400);
        var handler = Api(_ => Text(HttpStatusCode.BadRequest, "{}"));
        var report = await Service(handler, gateway: gateway).RunAsync(Run(settings: Settings(perOperation: 2), method: AuthenticatedTestingMethod.LocalHttpsProxy, auth: true), "run-5", null);
        var bodyRequests = gateway.SafeRequests.Where(r => r.Body is not null).ToList();
        Assert.Equal(2, bodyRequests.Count);
        Assert.All(bodyRequests, r => Assert.Equal(new BodyFuzzApproval(true, BodyFuzzingPolicy.ReadOnlyBodySafe, "POST /api/search (body)"), r.BodyApproval));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.HandledValidation);
    }

    [Fact]
    public async Task Run_Cancellation_StopsScheduling()
    {
        var handler = Api(_ => Text(HttpStatusCode.BadRequest, "{}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await Service(handler).RunAsync(Run(), "run-6", null, cts.Token);
        Assert.NotEqual(ApiFuzzCompleteness.Full, report.Completeness);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }
}
