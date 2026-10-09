using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ApiQuality.Fuzzing;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Safe fuzzing in API Quality Review: contract-derived deterministic cases, a backend guard on every request, a central request budget,
/// typed outcomes (4xx is handled validation, not a defect), leak detection without storing bodies, de-duplicated findings, and a
/// backend environment decision that blocks Production and unknown environments before anything is sent. All values are synthetic.
/// </summary>
public sealed class ApiSafeFuzzingTests
{
    private const string Origin = "https://api-dev.example.test";
    private const string SecretInBody = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWNyZXQtdXNlciJ9.c2lnbmF0dXJlLXNlY3JldA";

    internal const string OpenApi = """
    {
      "openapi": "3.0.1", "info": { "title": "Fixture API", "version": "1" },
      "components": {
        "parameters": { "Page": { "name": "page", "in": "query", "schema": { "type": "integer", "minimum": 1, "maximum": 100 } } },
        "schemas": { "Status": { "type": "string", "enum": ["Active", "Closed"] } }
      },
      "paths": {
        "/api/children": {
          "get": {
            "operationId": "listChildren",
            "parameters": [
              { "name": "status", "in": "query", "required": true, "schema": { "$ref": "#/components/schemas/Status" } },
              { "name": "name", "in": "query", "schema": { "type": "string", "maxLength": 20 } },
              { "name": "X-Tenant", "in": "header", "required": true, "schema": { "type": "string", "enum": ["north", "south"] } },
              { "name": "Authorization", "in": "header", "schema": { "type": "string" } }
            ],
            "responses": { "200": { "description": "ok" }, "400": { "description": "bad" }, "422": { "description": "invalid" } }
          },
          "post": { "operationId": "createChild", "responses": { "201": { "description": "created" } } }
        },
        "/api/children/{id}": {
          "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } } ],
          "get": { "operationId": "getChild", "responses": { "200": { "description": "ok" }, "404": { "description": "not found" } } },
          "delete": { "operationId": "deleteChild", "responses": { "204": { "description": "gone" } } }
        },
        "/api/reports": {
          "get": {
            "operationId": "listReports",
            "parameters": [
              { "$ref": "#/components/parameters/Page" },
              { "name": "since", "in": "query", "schema": { "type": "string", "format": "date" } }
            ],
            "responses": { "200": { "description": "ok" }, "400": { "description": "bad" } }
          }
        },
        "/api/auth/token": { "get": { "responses": { "200": { "description": "token" } } } }
      }
    }
    """;

    internal const string Sdl = """
    type Query { children(status: Status!, first: Int, id: UUID): [Child] child(id: ID!): Child version: String }
    type Mutation { deleteChild(id: ID!): Boolean }
    type Child { id: ID! name: String }
    enum Status { ACTIVE CLOSED }
    scalar UUID
    """;

    private static readonly OpenApiExtractor Extractor = new(NullLogger<OpenApiExtractor>.Instance);

    private static NormalizedContract Contract() => Extractor.Extract(OpenApi).Contract!;
    private static GraphQlNormalizedContract Schema() => GraphQlSdlSchema.FromSdl(Sdl, out _)!;

    private static ApiReviewTarget RestTarget(bool auth = false) => new()
    {
        TargetId = "rest-fixture", ApiType = ApiReviewTargetType.Rest, Scheme = "https", Host = "api-dev.example.test", Port = 443, BasePath = "/api", ServiceName = "Fixture REST",
        ContractSource = $"{Origin}/swagger/v1/swagger.json", AuthRequired = auth, Selected = true, Operations = [new ApiReviewOperation { Method = "GET", Path = "/api/children", AuthObserved = auth }],
    };

    private static ApiReviewTarget GraphQlTarget() => new()
    {
        TargetId = "gql-fixture", ApiType = ApiReviewTargetType.GraphQl, Scheme = "https", Host = "api-dev.example.test", Port = 443, BasePath = "/graphql", ServiceName = "Fixture GraphQL", Selected = true,
    };

    private static ApiFuzzingSettings Settings(ApiFuzzingLevel level = ApiFuzzingLevel.SafeSecurityFuzzing, int total = 50, int perOperation = 8, int delay = 100, int timeout = 5, bool stopOn5xx = false) => new()
    {
        Level = level, MaxTotalRequests = total, MaxCasesPerOperation = perOperation, RequestDelayMs = delay, RequestTimeoutSeconds = timeout, StopOnUnexpected5xx = stopOn5xx,
    };

    private static ApiFuzzingRunRequest Run(ApiFuzzingSettings settings, string environmentType = "Development", bool isProduction = false, params ApiReviewTarget[] targets) => new()
    {
        Settings = settings,
        Review = new ApiReviewRunRequest
        {
            Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "env-dev", Name = "Fixture DEV", EnvironmentType = environmentType, IsProduction = isProduction, TargetUrl = "https://app-dev.example.test/", AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp },
            Identity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, null, null),
            Targets = targets.Length == 0 ? [RestTarget()] : [.. targets],
        },
    };

    // ── OpenAPI parameter model ───────────────────────────────────────────────

    [Fact]
    public void OpenApi_PathQueryHeaderParameters_WithConstraintsAndProvenance()
    {
        var contract = Contract();
        var list = contract.Operations.Single(o => o.Method == "GET" && o.Path == "/api/children");
        var reports = contract.Operations.Single(o => o.Method == "GET" && o.Path == "/api/reports");
        var page = reports.Parameters.Single(p => p.Name == "page");
        Assert.Equal(NormalizedParameterLocation.Query, page.Location);
        Assert.Equal("integer", page.Type); Assert.Equal(1, page.Minimum); Assert.Equal(100, page.Maximum); Assert.False(page.Required);
        Assert.Equal("#/components/parameters/Page", page.SourceRef);
        var status = list.Parameters.Single(p => p.Name == "status");
        Assert.True(status.Required); Assert.Equal(["Active", "Closed"], status.EnumValues);
        Assert.Equal("#/paths/~1api~1children/get/parameters/0", status.SourceRef);
        Assert.Equal("date", reports.Parameters.Single(p => p.Name == "since").Format);
        Assert.Equal(20, list.Parameters.Single(p => p.Name == "name").MaxLength);
        Assert.Equal(NormalizedParameterLocation.Header, list.Parameters.Single(p => p.Name == "X-Tenant").Location);
        Assert.Equal(["200", "400", "422"], list.ResponseCodes);

        var get = contract.Operations.Single(o => o.Method == "GET" && o.Path == "/api/children/{id}");
        var id = Assert.Single(get.Parameters);
        Assert.Equal(NormalizedParameterLocation.Path, id.Location); Assert.True(id.Required); Assert.Equal("uuid", id.Format);
        Assert.Equal("#/paths/~1api~1children~1{id}/parameters/0", id.SourceRef);
    }

    [Fact]
    public void OpenApi_31TypeArraysAndNumericExclusiveBounds()
    {
        var doc = """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{"/x":{"get":{"parameters":[{"name":"n","in":"query","schema":{"type":["integer","null"],"exclusiveMinimum":0,"exclusiveMaximum":10}}],"responses":{"200":{"description":"ok"}}}}}}""";
        var n = Extractor.Extract(doc).Contract!.Operations[0].Parameters.Single();
        Assert.Equal("integer", n.Type); Assert.True(n.Nullable); Assert.Equal(0, n.Minimum); Assert.True(n.ExclusiveMinimum); Assert.Equal(10, n.Maximum); Assert.True(n.ExclusiveMaximum);
    }

    // ── REST generator ────────────────────────────────────────────────────────

    [Fact]
    public void RestGenerator_IsDeterministic_SameIdsSameOrder()
    {
        var a = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings());
        var b = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings());
        Assert.Equal(a.Cases.Select(c => c.CaseId), b.Cases.Select(c => c.CaseId));
        Assert.All(a.Cases, c => Assert.Matches("^fz-[0-9a-f]{12}$", c.CaseId));
        Assert.Equal(a.Cases.Count, a.Cases.Select(c => c.CaseId).Distinct().Count());
    }

    [Fact]
    public void RestGenerator_DerivesTheContractMutations_OnlyForReadOnlyOperations()
    {
        var result = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings(ApiFuzzingLevel.ContractFuzzing));
        var list = result.Cases.Where(c => c.OperationId == "GET /api/children").ToList();
        var reports = result.Cases.Where(c => c.OperationId == "GET /api/reports").ToList();
        Assert.Contains(list, c => c is { Parameter: "status", MutationType: ApiFuzzMutationType.MissingRequired } && !c.Query.Any(q => q.Key == "status"));
        Assert.Contains(list, c => c is { Parameter: "status", MutationType: ApiFuzzMutationType.InvalidEnum } && c.Query.Contains(new("status", ApiFuzzCaseGenerator.InvalidEnum)));
        Assert.Contains(reports, c => c is { Parameter: "page", MutationType: ApiFuzzMutationType.WrongType });
        Assert.Contains(reports, c => c is { Parameter: "page", MutationType: ApiFuzzMutationType.ZeroNotAllowed } && c.Query.Contains(new("page", "0")));
        Assert.Contains(reports, c => c is { Parameter: "page", MutationType: ApiFuzzMutationType.NumericAboveMaximum } && c.Query.Contains(new("page", "101")));
        Assert.Contains(reports, c => c is { Parameter: "since", MutationType: ApiFuzzMutationType.InvalidDate });
        Assert.Contains(list, c => c is { Parameter: "name", MutationType: ApiFuzzMutationType.StringTooLong } && c.Query.Single(q => q.Key == "name").Value.Length == 21);
        // The mutated case still carries every other REQUIRED input with a synthetic valid value.
        Assert.All(list.Where(c => c.Parameter != "status"), c => Assert.Contains(new KeyValuePair<string, string>("status", "Active"), c.Query));
        Assert.All(list.Where(c => c.Parameter != "X-Tenant"), c => Assert.Contains(new KeyValuePair<string, string>("X-Tenant", "north"), c.Headers));
        // Credentials are never a fuzz parameter.
        Assert.DoesNotContain(result.Cases, c => c.Parameter == "Authorization" || c.Headers.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
        // Contract level has no robustness cases.
        Assert.DoesNotContain(result.Cases, c => c.MutationType is ApiFuzzMutationType.UnexpectedParameter or ApiFuzzMutationType.OversizedString or ApiFuzzMutationType.EmptyString);
        Assert.All(result.Cases, c => Assert.Contains(c.Method, new[] { "GET", "HEAD", "OPTIONS" }));
        Assert.All(result.Cases, c => Assert.StartsWith("#/", c.SourceContractRef));
    }

    [Fact]
    public void RestGenerator_PathAndHeaderCases_AndUnsafeOperationsAreSkipped()
    {
        var result = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings());
        var byId = result.Cases.Single(c => c.OperationId == "GET /api/children/{id}" && c.MutationType == ApiFuzzMutationType.InvalidUuid);
        Assert.Equal(ApiFuzzParameterLocation.Path, byId.Location);
        Assert.Equal("/api/children/not-a-uuid", byId.Path);
        Assert.Contains(result.Cases, c => c is { Parameter: "X-Tenant", MutationType: ApiFuzzMutationType.MissingRequiredHeader } && !c.Headers.Any(h => h.Key == "X-Tenant"));
        Assert.Contains(result.Cases, c => c is { Parameter: "X-Tenant", MutationType: ApiFuzzMutationType.InvalidHeaderValue });
        Assert.Contains(result.Operations, o => o.OperationId == "POST /api/children" && o.Classification == ApiFuzzSafetyClassification.UnsafeMethod && o.CaseCount == 0);
        Assert.Contains(result.Operations, o => o.OperationId == "DELETE /api/children/{id}" && o.Classification == ApiFuzzSafetyClassification.UnsafeMethod);
        Assert.Contains(result.Operations, o => o.OperationId == "GET /api/auth/token" && o.Classification == ApiFuzzSafetyClassification.UnknownSafety);
        Assert.DoesNotContain(result.Cases, c => c.OperationId.Contains("token"));
        // Security level adds bounded robustness cases, never attack payloads.
        Assert.Contains(result.Cases, c => c.MutationType == ApiFuzzMutationType.UnexpectedParameter && c.ExpectedBehavior == ApiFuzzExpectedBehavior.AcceptOrReject);
        Assert.DoesNotContain(result.Cases, c => c.Query.Concat(c.Headers).Any(kv => kv.Value.Contains("../") || kv.Value.Contains("<script") || kv.Value.Contains("' OR ")));
    }

    [Fact]
    public void RestGenerator_PerOperationCap_IsApplied()
    {
        var result = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings(perOperation: 2));
        Assert.All(result.Cases.GroupBy(c => c.OperationId), g => Assert.True(g.Count() <= 2));
    }

    // ── GraphQL generator ─────────────────────────────────────────────────────

    [Fact]
    public void GraphQlGenerator_ValidationCases_QueryOnly_Deterministic()
    {
        var a = ApiFuzzCaseGenerator.GraphQl(GraphQlTarget(), Schema(), Settings(), []);
        var b = ApiFuzzCaseGenerator.GraphQl(GraphQlTarget(), Schema(), Settings(), []);
        Assert.Equal(a.Cases.Select(c => c.CaseId), b.Cases.Select(c => c.CaseId));

        Assert.Contains(a.Cases, c => c.MutationType == ApiFuzzMutationType.GraphQlUnknownField && c.GraphQlQuery == "query { __birknextFuzzUnknownField }");
        Assert.Contains(a.Cases, c => c.MutationType == ApiFuzzMutationType.GraphQlMalformedSyntax && c.ExpectSyntaxError);
        var children = a.Cases.Where(c => c.OperationId == "query children").ToList();
        Assert.Contains(children, c => c.MutationType == ApiFuzzMutationType.GraphQlMissingRequiredArgument && c.GraphQlQuery == "query { children { __typename } }");
        Assert.Contains(children, c => c.MutationType == ApiFuzzMutationType.GraphQlNullForNonNull && c.GraphQlQuery!.Contains("status: null"));
        Assert.Contains(children, c => c.MutationType == ApiFuzzMutationType.GraphQlInvalidEnum && c.GraphQlQuery!.Contains("status: BIRKNEXT_INVALID_ENUM"));
        Assert.Contains(children, c => c is { MutationType: ApiFuzzMutationType.GraphQlWrongScalarType, Parameter: "first" } && c.GraphQlQuery!.Contains("first: \"not-an-int\"") && c.GraphQlQuery.Contains("status: ACTIVE"));
        Assert.Contains(children, c => c is { MutationType: ApiFuzzMutationType.GraphQlWrongScalarType, Parameter: "id" } && c.GraphQlQuery!.Contains("not-a-uuid"));
        Assert.Contains(a.Cases, c => c.OperationId == "query child" && c.MutationType == ApiFuzzMutationType.GraphQlWrongScalarType && c.GraphQlQuery!.Contains("id: true"));
        Assert.All(a.Cases, c => Assert.DoesNotContain("mutation", c.GraphQlQuery!, StringComparison.OrdinalIgnoreCase));
        Assert.All(a.Cases.Where(c => !c.ExpectSyntaxError), c => Assert.Null(ApiSafeRequestGuard.GraphQlQueryRejection(c.GraphQlQuery!, false, 4096)));
        Assert.Contains(a.Operations, o => o.OperationId == "mutation deleteChild" && o.Classification == ApiFuzzSafetyClassification.Mutation && o.CaseCount == 0);
    }

    [Fact]
    public void GraphQlGenerator_ObservedRootFieldsComeFirst()
    {
        var result = ApiFuzzCaseGenerator.GraphQl(GraphQlTarget(), Schema(), Settings(), ["child"]);
        var order = result.Operations.Where(o => o.OperationId.StartsWith("query ") && o.OperationId != "query (endpoint)").Select(o => o.OperationId).ToList();
        Assert.Equal("query child", order[0]);
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    private static readonly ApiEnvironmentSafetyDecision Allowed = new() { State = ApiEnvironmentSafetyState.Allowed, EnvironmentType = "Development", Reason = "ok" };

    [Theory]
    [InlineData("GET", "https://evil.example.test/api/children", "origin")]
    [InlineData("GET", "https://api-dev.example.test:8443/api/children", "origin")]
    [InlineData("GET", "https://user:pw@api-dev.example.test/api/children", "user info")]
    [InlineData("GET", "https://api-dev.example.test/api/../admin", "traversal")]
    [InlineData("POST", "https://api-dev.example.test/api/children", "read-only")]
    [InlineData("DELETE", "https://api-dev.example.test/api/children", "read-only")]
    [InlineData("PUT", "https://api-dev.example.test/api/children", "read-only")]
    public void Guard_RejectsArbitraryDestinationsAndUnsafeMethods(string method, string url, string reasonFragment)
    {
        var rejection = ApiSafeRequestGuard.Validate(new ApiSafeRequest(method, url, []), Origin, Settings().Clamped(), Allowed);
        Assert.NotNull(rejection);
        Assert.Contains(reasonFragment, rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mutation { deleteChild(id: \"1\") }")]
    [InlineData("subscription { changed }")]
    [InlineData("query A { a } query B { b }")]
    [InlineData("query ($id: ID!) { child(id: $id) { id } }")]
    public void Guard_GraphQl_MutationsSubscriptionsMultipleOpsAndVariables_AreRejected(string query)
    {
        Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("POST", $"{Origin}/graphql", [], query), Origin, Settings().Clamped(), Allowed));
        Assert.NotNull(ApiSafeRequestGuard.GraphQlQueryRejection(query, allowSyntaxError: true, 4096));
    }

    [Fact]
    public void Guard_MalformedSyntax_OnlyWhenFlagged_AndNeverWithAMutationKeyword()
    {
        Assert.Null(ApiSafeRequestGuard.GraphQlQueryRejection("query { __typename ", allowSyntaxError: true, 4096));
        Assert.NotNull(ApiSafeRequestGuard.GraphQlQueryRejection("query { __typename ", allowSyntaxError: false, 4096));
        Assert.NotNull(ApiSafeRequestGuard.GraphQlQueryRejection("mutation { x ", allowSyntaxError: true, 4096));
    }

    [Fact]
    public void Guard_SizeAndHeaderBounds()
    {
        var settings = Settings().Clamped() with { MaxParameterLength = 64, MaxPayloadBytes = 256 };
        Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("GET", $"{Origin}/api/children?name={new string('a', 400)}", []), Origin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("GET", $"{Origin}/api/{new string('a', 2100)}", []), Origin, settings, Allowed));
        Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("POST", $"{Origin}/graphql", [], "query { " + new string('a', 300) + " }"), Origin, settings, Allowed));
        foreach (var header in new[] { "Authorization", "Cookie", "X-Forwarded-For", "Ocp-Apim-Subscription-Key", "X-Api-Key", "Host", "X-Http-Method-Override" })
            Assert.NotNull(ApiSafeRequestGuard.Validate(new ApiSafeRequest("GET", $"{Origin}/api/children", [new(header, "x")]), Origin, settings, Allowed));
        Assert.Null(ApiSafeRequestGuard.Validate(new ApiSafeRequest("GET", $"{Origin}/api/children?status=Active", [new("X-Tenant", "north")]), Origin, settings, Allowed));
    }

    [Fact]
    public void Guard_BlockedEnvironment_BlocksEveryRequest()
    {
        var production = new ApiEnvironmentSafetyDecision { State = ApiEnvironmentSafetyState.ProductionBlocked, Reason = "Production" };
        Assert.Contains("Environment safety", ApiSafeRequestGuard.Validate(new ApiSafeRequest("GET", $"{Origin}/api/children", []), Origin, Settings().Clamped(), production));
    }

    [Fact]
    public void Settings_AreClampedToHardLimits_ConcurrencyIsAlwaysOne()
    {
        var clamped = new ApiFuzzingSettings { MaxTotalRequests = 10_000, MaxCasesPerOperation = 500, MaxParameterLength = 1_000_000, MaxPayloadBytes = 1_000_000, MaxConcurrency = 16, RequestDelayMs = 0, RequestTimeoutSeconds = 600 }.Clamped();
        Assert.Equal(ApiFuzzingLimits.MaxTotalRequests, clamped.MaxTotalRequests);
        Assert.Equal(ApiFuzzingLimits.MaxCasesPerOperation, clamped.MaxCasesPerOperation);
        Assert.Equal(ApiFuzzingLimits.MaxParameterLength, clamped.MaxParameterLength);
        Assert.Equal(ApiFuzzingLimits.MaxPayloadBytes, clamped.MaxPayloadBytes);
        Assert.Equal(1, clamped.MaxConcurrency);
        Assert.Equal(ApiFuzzingLimits.MinDelayMs, clamped.RequestDelayMs);
        Assert.Equal(ApiFuzzingLimits.MaxTimeoutSeconds, clamped.RequestTimeoutSeconds);
        var defaults = new ApiFuzzingSettings();
        Assert.True(defaults.MaxTotalRequests <= 50 && defaults.MaxConcurrency == 1 && defaults.RequestDelayMs > 0 && defaults.MaxPayloadBytes <= 4096);
    }

    // ── Budget ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Budget_TotalAndPerOperationCaps_SequentialWithDelay()
    {
        using var budget = new ApiFuzzRequestBudget(new ApiFuzzingSettings { MaxTotalRequests = 4, MaxCasesPerOperation = 2, RequestDelayMs = 100 }.Clamped());
        var watch = Stopwatch.StartNew();
        Assert.True(await budget.TryAcquireAsync("a", default)); budget.Release();
        Assert.True(await budget.TryAcquireAsync("a", default)); budget.Release();
        Assert.False(await budget.TryAcquireAsync("a", default));   // per-operation cap
        Assert.True(await budget.TryAcquireAsync("b", default)); budget.Release();
        Assert.True(await budget.TryAcquireAsync("c", default)); budget.Release();
        Assert.False(await budget.TryAcquireAsync("d", default));   // total cap
        watch.Stop();
        Assert.Equal(4, budget.Used);
        Assert.Equal(1, budget.MaxInFlightObserved);
        Assert.True(watch.ElapsedMilliseconds >= 280, $"three enforced 100 ms gaps, took {watch.ElapsedMilliseconds} ms");
    }

    // ── Outcome classification ────────────────────────────────────────────────

    private static ApiFuzzCase RestCase(ApiFuzzExpectedBehavior behavior = ApiFuzzExpectedBehavior.RejectWithClientError) =>
        new() { CaseId = "fz-x", Protocol = ApiFuzzProtocol.Rest, OperationId = "GET /x", ExpectedBehavior = behavior };

    private static ApiFuzzResponse Response(int status, IReadOnlyList<string>? leaks = null, int? errors = null, bool? data = null) =>
        new(true, status, "application/json", 5, true, errors, data, leaks ?? [], false, "");

    [Theory]
    [InlineData(400, ApiFuzzOutcome.HandledValidation)]
    [InlineData(422, ApiFuzzOutcome.HandledValidation)]
    [InlineData(404, ApiFuzzOutcome.ContractViolation)]   // declared 200/400/422 only
    [InlineData(409, ApiFuzzOutcome.ContractViolation)]
    [InlineData(500, ApiFuzzOutcome.Unexpected5xx)]
    [InlineData(200, ApiFuzzOutcome.UnexpectedAcceptance)]
    [InlineData(401, ApiFuzzOutcome.AuthenticationBlocked)]
    [InlineData(403, ApiFuzzOutcome.AuthorizationBlocked)]
    public void Classifier_RestAgainstDeclaredCodes(int status, ApiFuzzOutcome expected) =>
        Assert.Equal(expected, ApiFuzzOutcomeClassifier.Classify(RestCase(), Response(status), ["200", "400", "422"]).Outcome);

    [Fact]
    public void Classifier_Without4xxDeclarations_Any4xxIsHandled_And4XXRangeCounts()
    {
        Assert.Equal(ApiFuzzOutcome.HandledValidation, ApiFuzzOutcomeClassifier.Classify(RestCase(), Response(404), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.HandledValidation, ApiFuzzOutcomeClassifier.Classify(RestCase(), Response(409), ["200", "4XX"]).Outcome);
        Assert.Equal(ApiFuzzOutcome.HandledValidation, ApiFuzzOutcomeClassifier.Classify(RestCase(ApiFuzzExpectedBehavior.AcceptOrReject), Response(200), ["200"]).Outcome);
        Assert.Equal(ApiFuzzOutcome.PotentialInformationLeak, ApiFuzzOutcomeClassifier.Classify(RestCase(), Response(400, ["stack-trace"]), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.Timeout, ApiFuzzOutcomeClassifier.Classify(RestCase(), new(false, 0, null, null, null, null, null, [], true, "t"), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.ConnectionFailure, ApiFuzzOutcomeClassifier.Classify(RestCase(), new(false, 0, null, null, null, null, null, [], false, "c"), null).Outcome);
    }

    [Fact]
    public void Classifier_GraphQlReadsTheEnvelope_NotTheHttpStatus()
    {
        var c = new ApiFuzzCase { CaseId = "fz-g", Protocol = ApiFuzzProtocol.GraphQl, ExpectedBehavior = ApiFuzzExpectedBehavior.RejectWithGraphQlError };
        Assert.Equal(ApiFuzzOutcome.HandledValidation, ApiFuzzOutcomeClassifier.Classify(c, Response(200, errors: 1, data: false), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.UnexpectedAcceptance, ApiFuzzOutcomeClassifier.Classify(c, Response(200, errors: 0, data: true), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.HandledValidation, ApiFuzzOutcomeClassifier.Classify(c, Response(400, errors: 1, data: false), null).Outcome);
        Assert.Equal(ApiFuzzOutcome.Unexpected5xx, ApiFuzzOutcomeClassifier.Classify(c, Response(500), null).Outcome);
    }

    [Fact]
    public void LeakIndicators_DetectInternalsWithoutReturningText()
    {
        var indicators = JsonBodyInspector.LeakIndicators(
            "System.NullReferenceException at Birk.Children.Get() in C:\\src\\Children.cs:line 12 Npgsql.PostgresException db01.corp " + SecretInBody + " System.Private.CoreLib, Version=8.0.0.0, Culture=neutral");
        Assert.Contains("stack-trace", indicators); Assert.Contains("exception-type", indicators); Assert.Contains("source-file-path", indicators);
        Assert.Contains("sql-or-connection-string", indicators); Assert.Contains("internal-hostname", indicators); Assert.Contains("token-or-secret", indicators);
        Assert.Contains("assembly-internals", indicators);
        Assert.DoesNotContain(indicators, i => i.Contains("Children") || i.Contains("eyJ"));
        Assert.Empty(JsonBodyInspector.LeakIndicators("{\"type\":\"about:blank\",\"title\":\"Bad Request\",\"status\":400,\"errors\":{\"status\":[\"invalid\"]}}"));
    }

    // ── Execution against a deterministic fixture API ─────────────────────────

    /// <summary>
    /// A small synthetic API: clean 400/422 rejections, a 500 with a stack trace, an incorrectly accepted invalid date, a SQL-detail leak,
    /// 401/403 for tenant headers, 404 for unknown ids, and a GraphQL endpoint with clean and leaky errors. No real data.
    /// </summary>
    private sealed class FixtureApi : HttpMessageHandler
    {
        public readonly List<(string Method, string PathAndQuery, string? Body, bool HasAuthorization)> Requests = [];
        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization is not null));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            var path = request.RequestUri.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "/swagger/v1/swagger.json") return Respond(200, OpenApi);
            if (path == "/graphql") return GraphQl(body ?? "");
            if (path == "/api/children")
            {
                var tenant = request.Headers.TryGetValues("X-Tenant", out var t) ? t.First() : null;
                if (tenant is null) return Respond(403, Problem(403));
                if (tenant is not ("north" or "south")) return Respond(401, Problem(401));
                if (query["status"] is null) return Respond(400, Problem(400));
                if (query["status"] is not ("Active" or "Closed")) return Respond(422, Problem(422));
                if (query["name"] is { Length: > 20 }) return Respond(400, "{\"title\":\"Bad\",\"status\":400,\"detail\":\"Npgsql.PostgresException: value too long; Data Source=db01.corp\"}");
                return Respond(200, "{\"items\":[],\"totalCount\":0,\"token\":\"" + SecretInBody + "\"}");
            }
            if (path == "/api/reports")
            {
                if (query["page"] is "0" or "101") return Respond(500, "{\"error\":\"System.ArgumentOutOfRangeException at Birk.Reports.List() in C:\\\\src\\\\Children.cs:line 42\"}");
                if (query["page"] is { } p && !int.TryParse(p, out _)) return Respond(400, Problem(400));
                return Respond(200, "{\"items\":[],\"totalCount\":0}");   // invalid dates are (wrongly) accepted
            }
            if (path.StartsWith("/api/children/", StringComparison.Ordinal)) return Respond(404, Problem(404));
            return Respond(404, Problem(404));
        }

        private static HttpResponseMessage GraphQl(string body)
        {
            if (body.Contains("__schema")) return Respond(200, "{\"data\":{\"__schema\":{}}}");
            if (body.Contains("not-an-int")) return Respond(200, "{\"data\":{\"children\":[]}}");   // wrongly coerced
            if (body.Contains("BIRKNEXT_INVALID_ENUM")) return Respond(200, "{\"errors\":[{\"message\":\"Unexpected Execution Error\",\"extensions\":{\"stackTrace\":\"   at HotChocolate.Execution.Run() in /src/Exec.cs:line 9\"}}]}");
            if (body.Contains("__typename ") && !body.TrimEnd().EndsWith("}\"}", StringComparison.Ordinal)) return Respond(400, "{\"errors\":[{\"message\":\"Syntax error\"}]}");
            return Respond(200, "{\"errors\":[{\"message\":\"Validation failed\"}]}");
        }

        private static string Problem(int status) => $"{{\"type\":\"about:blank\",\"title\":\"Problem\",\"status\":{status}}}";
        private static HttpResponseMessage Respond(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Introspection is answered by the fixture; this extractor turns any __schema document into the SDL fixture schema.</summary>
    private sealed class SdlExtractor : IGraphQlExtractor
    {
        public GraphQlExtractionResult Extract(string introspectionJson) => GraphQlExtractionResult.SuccessResult(Schema());
    }

    private sealed class RecordingGateway : IAuthenticatedReviewGateway
    {
        public readonly List<BirkNext.Api.Services.ApiQuality.Fuzzing.ApiSafeRequest> SafeRequests = [];
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new()
        {
            Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, Reason = "ok",
        };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteSafeRequestAsync(AuthenticatedReviewIdentity identity, BirkNext.Api.Services.ApiQuality.Fuzzing.ApiSafeRequest request, CancellationToken cancellationToken = default)
        {
            SafeRequests.Add(request);
            return Task.FromResult(new AuthenticatedReviewExecutionOutcome
            {
                Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, Message = "HTTP 400",
                Result = new AuthenticatedApiExecutionResult { StatusCode = 400, ContentType = "application/problem+json", JsonValid = true, ProblemDetails = true, Outcome = "HTTP 400" },
            });
        }
    }

    private static ApiFuzzingService Service(HttpMessageHandler handler, IAuthenticatedReviewGateway? gateway = null, IApiEnvironmentSafetyPolicy? policy = null) =>
        new(new HttpClient(handler), gateway ?? new RecordingGateway(), Extractor, new SdlExtractor(), policy ?? ApiEnvironmentSafetyPolicy.Default, NullLogger<ApiFuzzingService>.Instance);

    [Fact]
    public async Task Plan_SendsNoFuzzRequest_ListsEligibilityAndPreview()
    {
        var api = new FixtureApi();
        var plan = await Service(api).PlanAsync(Run(Settings()));
        Assert.True(plan.CanRun);
        Assert.True(plan.EligibleOperations >= 2);
        Assert.Contains(plan.Operations, o => o.Classification == ApiFuzzSafetyClassification.UnsafeMethod);
        Assert.All(api.Requests, r => Assert.Equal("/swagger/v1/swagger.json", r.PathAndQuery));   // contract retrieval only
        Assert.Contains(plan.Contracts, c => c.Kind == "OpenAPI" && c.Hash is { Length: > 0 });
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("", false)]
    [InlineData("Custom", false)]
    public async Task ProductionAndUnknownEnvironments_AreBlockedBeforeAnyRequest(string type, bool isProduction)
    {
        var api = new FixtureApi();
        var service = Service(api);
        var plan = await service.PlanAsync(Run(Settings(), type, isProduction));
        Assert.False(plan.CanRun);
        Assert.StartsWith("Blocked by safety policy", plan.NotAvailableReason);
        Assert.Empty(plan.Cases);
        var report = await service.RunAsync(Run(Settings(), type, isProduction), "run-blocked", null);
        Assert.Equal(ApiFuzzCompleteness.NotRun, report.Completeness);
        Assert.Empty(api.Requests);   // not even the contract
        if (type == "Production" || isProduction) Assert.Contains(plan.Operations, o => o.Classification == ApiFuzzSafetyClassification.ProductionBlocked);
    }

    [Fact]
    public async Task Off_IsNotAvailable_AndSendsNothing()
    {
        var api = new FixtureApi();
        var plan = await Service(api).PlanAsync(Run(Settings(ApiFuzzingLevel.Off)));
        Assert.False(plan.CanRun);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task MissingContract_IsNotAvailable_NoUnknownEndpointIsFuzzed()
    {
        var api = new FixtureApi();
        var target = RestTarget() with { ContractSource = null };
        var report = await Service(api).RunAsync(Run(Settings(), targets: target), "run-nocontract", null);
        Assert.Equal(ApiFuzzCompleteness.NotRun, report.Completeness);
        Assert.Contains(report.Operations, o => o.Classification == ApiFuzzSafetyClassification.MissingContract);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Run_RestFixture_OutcomesFindingsDedupAndRedaction()
    {
        var api = new FixtureApi();
        var report = await Service(api).RunAsync(Run(Settings()), "run-rest", null);

        Assert.True(report.CasesExecuted > 0);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.HandledValidation && r.StatusCode is 400 or 422);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.Unexpected5xx && r.StatusCode == 500);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.UnexpectedAcceptance);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.PotentialInformationLeak);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.AuthorizationBlocked);   // missing tenant header → 403
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.AuthenticationBlocked);  // invalid tenant → 401
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.HandledValidation && r.StatusCode == 404);   // declared 404 for unknown id

        // Two 5xx cases on GET /api/children (page 0 and page 101) are ONE logical finding with two cases.
        var fiveXx = Assert.Single(report.Findings, f => f.RuleId == "fuzz-unexpected-5xx");
        Assert.Equal(ApiReviewSeverity.Medium, fiveXx.Severity);
        Assert.True(fiveXx.CaseIds.Count >= 2);
        Assert.Contains(report.Findings, f => f.RuleId == "fuzz-information-leak" && f.StandardsReferences.Any(r => r.ReferenceId == "A05:2021"));
        Assert.Contains(report.Findings, f => f.RuleId == "fuzz-unexpected-acceptance" && f.Severity == ApiReviewSeverity.Low);
        Assert.DoesNotContain(report.Findings, f => f.Severity is ApiReviewSeverity.Critical or ApiReviewSeverity.High);

        // Only read-only methods, never the POST/DELETE operations, never the token endpoint, never a credential.
        Assert.All(api.Requests, r => Assert.Contains(r.Method, new[] { "GET", "HEAD", "OPTIONS" }));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("/auth/token"));
        Assert.DoesNotContain(api.Requests, r => r.HasAuthorization);
        // No response body, secret or stack text in the stored report.
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("eyJ", json); Assert.DoesNotContain("Children.cs", json); Assert.DoesNotContain("db01", json); Assert.DoesNotContain("ArgumentOutOfRange", json);
        Assert.Equal(report.Cases.Count, report.CasesPlanned);
    }

    [Fact]
    public async Task Run_GraphQlFixture_EnvelopeOutcomes_NoMutationIsEverSent()
    {
        var api = new FixtureApi();
        var report = await Service(api).RunAsync(Run(Settings(), targets: GraphQlTarget()), "run-gql", null);
        Assert.True(report.CasesExecuted > 0);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.HandledValidation);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.UnexpectedAcceptance);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.PotentialInformationLeak);
        Assert.DoesNotContain(api.Requests, r => r.Body is { } b && !b.Contains("__schema") && b.Contains("mutation", StringComparison.OrdinalIgnoreCase));   // the schema introspection query only names mutationType
        Assert.All(api.Requests, r => Assert.Equal("POST", r.Method));
        Assert.Contains(report.Operations, o => o.Classification == ApiFuzzSafetyClassification.Mutation);
    }

    [Fact]
    public async Task Run_RequestCapIsEnforced_OverBudgetCasesAreReportedNotDropped()
    {
        var api = new FixtureApi();
        var report = await Service(api).RunAsync(Run(Settings(total: 3)), "run-cap", null);
        Assert.Equal(3, report.CasesPlanned);
        Assert.True(report.CasesOverBudget > 0);
        Assert.Equal(3, api.Requests.Count(r => r.PathAndQuery.StartsWith("/api/", StringComparison.Ordinal)));
        Assert.Equal(ApiFuzzCompleteness.Partial, report.Completeness);
        Assert.Contains("did not fit the request budget", report.CompletenessReason);
    }

    [Fact]
    public async Task Run_StopOnUnexpected5xx_StopsScheduling()
    {
        var api = new FixtureApi();
        var report = await Service(api).RunAsync(Run(Settings(stopOn5xx: true)), "run-stop", null);
        var firstFiveXx = report.Results.FindIndex(r => r.Outcome == ApiFuzzOutcome.Unexpected5xx);
        Assert.True(firstFiveXx >= 0);
        Assert.All(report.Results.Skip(firstFiveXx + 1), r => Assert.Equal(ApiFuzzOutcome.NotExecuted, r.Outcome));
        Assert.Equal(ApiFuzzCompleteness.Partial, report.Completeness);
    }

    [Fact]
    public async Task Run_TimeoutIsEnforcedPerRequest()
    {
        var api = new FixtureApi { Delay = TimeSpan.FromSeconds(3) };
        var watch = Stopwatch.StartNew();
        // Plan (contract fetch) is not bounded by the fuzz timeout; keep the run to two cases.
        var report = await Service(api).RunAsync(Run(Settings(total: 2, timeout: 1)), "run-timeout", null);
        watch.Stop();
        Assert.All(report.Results, r => Assert.Equal(ApiFuzzOutcome.Timeout, r.Outcome));
        Assert.Equal(ApiFuzzCompleteness.Failed, report.Completeness);   // nothing produced a response
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Run_Cancellation_StopsScheduling_RunIsPartial_FindingsKept()
    {
        var api = new FixtureApi();
        using var cts = new CancellationTokenSource();
        var executed = 0;
        var report = await Service(api).RunAsync(Run(Settings(delay: 100)), "run-cancel", partial =>
        {
            if (partial.CasesExecuted >= 3 && Interlocked.Exchange(ref executed, 1) == 0) cts.Cancel();
        }, cts.Token);
        Assert.True(report.Cancelled);
        Assert.Equal(ApiFuzzCompleteness.Partial, report.Completeness);
        Assert.InRange(report.CasesExecuted, 3, 4);
        Assert.Contains(report.Results, r => r.Outcome == ApiFuzzOutcome.NotExecuted && r.Note.StartsWith("Cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_AuthenticatedTarget_GoesThroughTheGateway_NeverThePublicClient()
    {
        var api = new FixtureApi();
        var gateway = new RecordingGateway();
        var request = Run(Settings(total: 5), targets: RestTarget(auth: true)) with { };
        request = request with
        {
            Review = request.Review with
            {
                Environment = request.Review.Environment with { AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy },
                Identity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "env-dev", "CCCC"),
            },
        };
        var report = await Service(api, gateway).RunAsync(request, "run-auth", null);
        Assert.Equal(5, gateway.SafeRequests.Count);
        Assert.All(gateway.SafeRequests, r => Assert.StartsWith(Origin, r.Url));
        Assert.All(gateway.SafeRequests, r => Assert.DoesNotContain(r.Headers, h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.StartsWith("/api/children", StringComparison.Ordinal));
        Assert.All(report.Results, r => Assert.Equal(ApiFuzzOutcome.HandledValidation, r.Outcome));
    }

    [Fact]
    public async Task Coordinator_OneRunAtATime_CancelEndsPartial()
    {
        var services = new ServiceCollection();
        services.AddScoped<IApiFuzzingService>(_ => Service(new FixtureApi { Delay = TimeSpan.FromMilliseconds(200) }));
        using var provider = services.BuildServiceProvider();
        var coordinator = new ApiFuzzingRunCoordinator(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiFuzzingRunCoordinator>.Instance);

        var started = coordinator.Start(Run(Settings(delay: 100)));
        Assert.True(started.Running);
        Assert.Throws<ApiFuzzingRunConflictException>(() => coordinator.Start(Run(Settings())));
        await Task.Delay(700);
        coordinator.Cancel(started.RunId);
        await coordinator.WaitAsync(started.RunId);
        var final = coordinator.Get(started.RunId)!;
        Assert.False(final.Running);
        Assert.True(final.Cancelled);
        Assert.Equal(ApiFuzzCompleteness.Partial, final.Completeness);
        Assert.NotNull(coordinator.Start(Run(Settings(ApiFuzzingLevel.Off))));   // a new run may start after the previous ended
    }

    [Fact]
    public void BuildRequest_BindsToTheTargetOrigin_AndEncodesQueryValues()
    {
        var c = ApiFuzzCaseGenerator.Rest(RestTarget(), Contract(), Settings()).Cases.First(c => c.Parameter == "name" && c.MutationType == ApiFuzzMutationType.StringTooLong);
        var request = ApiFuzzingService.BuildRequest(c, RestTarget());
        Assert.StartsWith($"{Origin}/api/children?", request.Url);
        Assert.Null(ApiSafeRequestGuard.Validate(request, Origin, Settings().Clamped(), Allowed));
    }
}
