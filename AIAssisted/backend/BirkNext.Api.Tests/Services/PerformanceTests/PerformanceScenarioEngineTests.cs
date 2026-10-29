using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Smart Scenario Engine: contracts tell what is valid (OpenAPI/GraphQL from Source Analysis), discovery tells what is used (observed, never
/// copied identifiers or tokens), central Test Data supplies values (aliases; resolved by the backend at run time), the user approves what
/// becomes load. Deterministic and explainable; nothing runs.
/// </summary>
public sealed class PerformanceScenarioEngineTests
{
    private const string PersonsOpenApi = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Persons API", "version": "1.4.0" },
          "components": {
            "securitySchemes": { "Bearer": { "type": "http", "scheme": "bearer" } },
            "parameters": { "Tenant": { "name": "tenant", "in": "query", "required": true, "schema": { "type": "string" } } },
            "schemas": { "CreatePersonRequest": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } } }
          },
          "security": [ { "Bearer": [] } ],
          "paths": {
            "/api/person/{personId}": {
              "parameters": [ { "name": "personId", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" }, "example": "x" } ],
              "get": { "operationId": "getPerson", "tags": ["Person"], "summary": "Get a person", "parameters": [ { "$ref": "#/components/parameters/Tenant" } ],
                       "responses": { "200": { "description": "ok" }, "404": { "description": "no" } } }
            },
            "/api/person": {
              "post": { "operationId": "createPerson", "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreatePersonRequest" } } } },
                        "responses": { "201": { "description": "created" } } }
            },
            "/api/person/search": {
              "get": { "operationId": "searchPersons", "parameters": [ { "name": "q", "in": "query", "required": true, "schema": { "type": "string" } }, { "name": "page", "in": "query", "schema": { "type": "integer" } } ],
                       "security": [], "responses": { "200": { "description": "ok" } } }
            },
            "/api/report": {
              "post": { "operationId": "submitReport", "requestBody": { "content": { "application/xml": { "schema": { "type": "string" } } } }, "responses": { "202": { "description": "ok" } } }
            }
          }
        }
        """;

    private const string PersonsYaml = """
        openapi: 3.0.1
        info:
          title: Persons API
          version: 1.4.0
        paths:
          /api/person/{personId}:
            get:
              operationId: getPerson
              parameters:
                - name: personId
                  in: path
                  required: true
                  schema:
                    type: string
              responses:
                '200':
                  description: ok
        """;

    private const string ChildSchema = """
        type Query { child(childId: ID!): Child  children(filter: ChildFilter!): [Child!]!  version: String! }
        type Mutation { grantAccess(childId: ID!): Boolean! }
        input ChildFilter { level: AccessLevel }
        enum AccessLevel { OPEN KODE7 KODE6 }
        type Child { id: ID! name: String }
        """;

    private static SourceContract OpenApi(string id = "persons", string name = "Persons API", string file = "Persons/openapi.json", string? producer = "Persons.Api") => new()
    {
        Id = id, Type = SourceContractType.OpenApi, Name = name, File = file, Producer = producer, Operations = ContractAnalyzer.ReadOpenApiOperations("openapi.json", PersonsOpenApi),
        Types = [new ContractTypeShape("CreatePersonRequest", "object", [new ContractField("name", "string", true)])],
    };

    private static SourceContract Other(string id, string name, string path) => new()
    {
        Id = id, Type = SourceContractType.OpenApi, Name = name, File = $"{name}/openapi.json", Operations = [new ContractOperation($"GET {path}", "GET", path, [])],
    };

    private static SourceContract GraphQl() => ContractAnalyzer.ReadGraphQl("Child/schema.graphql", ChildSchema)! with { Id = "child-schema", Name = "Child schema" };

    private static ObservedNetworkEndpoint Seen(string method, string path, int count = 44, int status = 200, bool auth = true, string host = "m2lbdev.example.test") => new()
    {
        Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = host, Port = 443, Method = method, Path = path, Count = count, LastStatus = status, AuthObserved = auth,
        LastObservedAt = new DateTimeOffset(2026, 10, 23, 8, 54, 0, TimeSpan.Zero), PagePath = "/barn",
    };

    private static TestDataAssetSummary Asset(string alias, TestDataKind kind = TestDataKind.Scalar, string? media = null, TestDataApproval approval = TestDataApproval.Approved,
        string? environment = null, params string[] capabilities) => new()
    {
        Id = alias, Alias = alias, Kind = kind, MediaType = media, Approval = approval, Environment = environment, Capabilities = [.. capabilities], Source = "Configured catalog",
    };

    private const string Target = "https://m2lbdev.example.test";

    // ── Contract extraction (shared Source Analysis analyzer) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenApi_Operations_CarryParametersBodyResponsesTagsAndSecurity_NeverValues()
    {
        var ops = ContractAnalyzer.ReadOpenApiOperations("openapi.json", PersonsOpenApi);
        var get = ops.Single(o => o.OperationId == "getPerson");
        get.Should().Match<ContractOperation>(o => o.Detailed && o.Kind == "GET" && o.Path == "/api/person/{personId}" && o.Summary == "Get a person" && o.Tags.Single() == "Person");
        get.ParameterDetails.Select(p => (p.Name, p.In, p.Required, p.Type)).Should().BeEquivalentTo([("personId", "path", true, (string?)"string:uuid"), ("tenant", "query", true, (string?)"string")],
            "path-level and $ref parameters are resolved");
        get.ParameterDetails.Single(p => p.Name == "personId").HasExample.Should().BeTrue("only THAT an example exists is recorded, never its value");
        get.ResponseCodes.Should().Equal("200", "404");
        get.Security.Should().Equal("Bearer (bearer)");
        var create = ops.Single(o => o.OperationId == "createPerson");
        create.Should().Match<ContractOperation>(o => o.RequestContentTypes.Single() == "application/json" && o.RequestSchema == "CreatePersonRequest" && o.RequestBodyRequired);
        ops.Single(o => o.OperationId == "searchPersons").Security.Should().BeEmpty("an operation-level empty security overrides the root requirement");
        JsonSerializer.Serialize(ops).Should().NotContain("\"x\"", "example values are never stored");
        ContractAnalyzer.ReadOpenApiOperations("openapi.yaml", PersonsYaml).Single().ParameterDetails.Single().Should().Be(new ContractParameter("personId", "path", true, "string"));
    }

    [Fact]
    public void GraphQl_Schema_CarriesArgumentsResultTypesAndEnumValues()
    {
        var schema = GraphQl();
        var child = schema.Operations.Single(o => o.OperationId == "child");
        child.Should().Match<ContractOperation>(o => o.Kind == "query" && o.ResultType == "Child" && o.Detailed);
        child.ParameterDetails.Single().Should().Be(new ContractParameter("childId", "argument", true, "ID!"));
        schema.Operations.Should().Contain(o => o.Kind == "mutation" && o.OperationId == "grantAccess");
        schema.Types.Single(t => t.Name == "AccessLevel").Values.Should().Equal("OPEN", "KODE7", "KODE6");
        schema.Types.Single(t => t.Name == "ChildFilter").Kind.Should().Be("input_object");
    }

    // ── Scoping and correlation ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scope_ObservedTrafficResolvesTheTargetsContract_UnrelatedContractsAreNotRecommended()
    {
        IReadOnlyList<SourceContract> contracts = [OpenApi(), Other("hendelse", "Hendelse API", "/api/hendelse"), Other("autorisasjon", "Autorisasjon API", "/api/tilgang")];
        var onTarget = PerformanceScenarioEngine.OnTarget([Seen("GET", "/api/person/123"), Seen("GET", "/api/hendelse", host: "other.example.test")], Target);
        var scope = PerformanceScenarioEngine.Scope(contracts, Target, onTarget, null);
        scope.State.Should().Be(ScenarioContractScopeState.Resolved);
        scope.Relevant.Select(c => c.Id).Should().Equal("persons");
        scope.Other.Should().HaveCount(2);

        var unresolved = PerformanceScenarioEngine.Scope(contracts, Target, [], null);
        unresolved.State.Should().Be(ScenarioContractScopeState.Unresolved);
        unresolved.Summary.Should().StartWith("Contract source available · target-specific contract unresolved");
        unresolved.Relevant.Should().BeEmpty("a monorepo's contracts are never all recommended");
        PerformanceScenarioEngine.Scope(contracts, Target, [], ["hendelse"]).Should().Match<ScenarioContractScope>(s => s.State == ScenarioContractScopeState.Selected && s.Relevant.Single().Id == "hendelse");
        PerformanceScenarioEngine.Scope([OpenApi(), Other("x", "Billing", "/b")], "https://persons-dev.example.test", [], null).State.Should().Be(ScenarioContractScopeState.Candidate);
    }

    [Fact]
    public void Correlation_MatchesParameterizedPaths_AndObservedOnlyPathsNeverKeepIdentifiers()
    {
        PerformanceScenarioEngine.Matches("/api/person/{personId}", "/api/person/123").Should().BeTrue();
        PerformanceScenarioEngine.Matches("/person/{id}", "/api/person/6b1f0a3e-4f2b-4a8e-9a77-0c1f2e3d4b5a").Should().BeTrue("a base path prefix from the server URL is allowed");
        PerformanceScenarioEngine.Matches("/api/person/{personId}", "/api/persons/123").Should().BeFalse();
        PerformanceScenarioEngine.Template("/api/case/12345678901/notes/6b1f0a3e-4f2b-4a8e-9a77-0c1f2e3d4b5a").Should().Be("/api/case/{id}/notes/{id2}");

        var catalog = PerformanceScenarioEngine.Build([OpenApi()], Guid.NewGuid(), "M2LB.zip", Target, "Development",
            [Seen("GET", "/api/person/123", count: 40), Seen("GET", "/api/person/456", count: 4), Seen("GET", "/internal/case/12345678901")], [Asset("person-id-baseline", capabilities: "personId")], null, true);
        var get = catalog.Operations.Single(o => o.OperationId == "getPerson");
        get.Evidence.Should().Be(ScenarioEvidence.DeclaredAndObserved);
        get.Observation!.Count.Should().Be(44, "both observed variants of the same template correlate");
        catalog.Operations.Single(o => o.OperationId == "createPerson").Evidence.Should().Be(ScenarioEvidence.DeclaredOnly);
        var observedOnly = catalog.Operations.Single(o => o.Evidence == ScenarioEvidence.ObservedOnly);
        observedOnly.Path.Should().Be("/internal/case/{id}");
        observedOnly.Level.Should().Be(ScenarioRecommendationLevel.ReviewRequired);
        observedOnly.Reasons.Should().Contain("observed only — potential undocumented API");
        var step = PerformanceScenarioEngine.ToStep(observedOnly);
        JsonSerializer.Serialize(step).Should().NotContain("12345678901").And.NotContain("123").And.NotContain("Bearer", "observed identifiers and tokens are never copied into a scenario");
    }

    // ── Recommendation ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Recommendation_IsDeterministicAndExplained()
    {
        IReadOnlyList<ObservedNetworkEndpoint> seen = [Seen("GET", "/api/person/123"), Seen("GET", "/api/person/search")];
        IReadOnlyList<TestDataAssetSummary> assets = [Asset("person-id-baseline", capabilities: "personId"), Asset("tenant-dev", capabilities: "tenant"), Asset("q-term", capabilities: "q")];
        var a = PerformanceScenarioEngine.Build([OpenApi()], null, "M2LB.zip", Target, "Development", seen, assets, null, authenticated: true);
        var b = PerformanceScenarioEngine.Build([OpenApi()], null, "M2LB.zip", Target, "Development", seen.Reverse().ToList(), assets.Reverse().ToList(), null, authenticated: true);
        a.Operations.Select(o => (o.Key, o.Level, o.Score)).Should().Equal(b.Operations.Select(o => (o.Key, o.Level, o.Score)), "the same evidence always gives the same order");
        var get = a.Operations.Single(o => o.OperationId == "getPerson");
        a.Operations.Take(2).Should().OnlyContain(o => o.Level == ScenarioRecommendationLevel.Recommended, "declared + observed + Test Data + auth rank first");
        get.Level.Should().Be(ScenarioRecommendationLevel.Recommended);
        PerformanceScenarioEngine.Because(get).Should().StartWith("Recommended because declared in OpenAPI · Persons API (M2LB.zip) · observed 44×")
            .And.Contain("dynamic parameters {personId}, {tenant} — runtime value configured on the step").And.Contain("Test Data suggestion available for {personId}, {tenant}").And.Contain("authentication reference configured");
        get.Observation!.Count.Should().Be(44);
        a.Operations.Single(o => o.OperationId == "createPerson").Should().Match<ScenarioOperation>(o => o.Level == ScenarioRecommendationLevel.NotRunnable && o.NotRunnableReason!.Contains("POST"));
        var unauthenticated = PerformanceScenarioEngine.Build([OpenApi()], null, null, Target, null, seen, assets, null, authenticated: false).Operations.Single(o => o.OperationId == "getPerson");
        unauthenticated.Level.Should().Be(ScenarioRecommendationLevel.ReviewRequired);
        unauthenticated.Reasons.Should().Contain(r => r.StartsWith("requires authentication (bearer observed)"));
    }

    // ── Test Data ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataTypes_FollowTheOperation_AndTheAssetKind()
    {
        TestDataDataTypes.ForMediaType("application/json; charset=utf-8").Should().Be(TestDataDataType.JsonPayload);
        TestDataDataTypes.ForMediaType("application/xml").Should().Be(TestDataDataType.XmlPayload);
        TestDataDataTypes.ForMediaType("multipart/form-data").Should().Be(TestDataDataType.FileFixture);
        TestDataDataTypes.Of(TestDataKind.Scalar, null).Should().Be(TestDataDataType.SimpleValues);
        TestDataDataTypes.Of(TestDataKind.EntityReference, null).Should().Be(TestDataDataType.SimpleValues);
        TestDataDataTypes.Of(TestDataKind.Structured, "application/json").Should().Be(TestDataDataType.JsonPayload);
        TestDataDataTypes.Of(TestDataKind.Message, "application/xml").Should().Be(TestDataDataType.XmlPayload);
        TestDataDataTypes.Of(TestDataKind.File, "application/pdf").Should().Be(TestDataDataType.FileFixture);
        TestDataDataTypes.Of(TestDataKind.ExpectedResult, "application/json").Should().Be(TestDataDataType.ExpectedResult, "an expected result is never an input payload");
        TestDataDataTypes.Of(TestDataKind.ReferenceData, null).Should().Be(TestDataDataType.ExpectedResult);

        var ops = PerformanceScenarioEngine.Build([OpenApi()], null, null, Target, null, [], [], ["persons"], true).Operations;
        ops.Single(o => o.OperationId == "getPerson").Needs.Select(n => (n.Variable, n.In, n.DataType)).Should().BeEquivalentTo([("personId", "path", TestDataDataType.SimpleValues), ("tenant", "query", TestDataDataType.SimpleValues)]);
        ops.Single(o => o.OperationId == "createPerson").Needs.Single().Should().Match<ScenarioDataNeed>(n => n.In == "body" && n.DataType == TestDataDataType.JsonPayload && n.Schema == "CreatePersonRequest");
        ops.Single(o => o.OperationId == "submitReport").Needs.Single().DataType.Should().Be(TestDataDataType.XmlPayload);
    }

    [Fact]
    public void Matching_RecommendsOneNamedApprovedAsset_OffersSeveral_AndNeverUnapprovedOrOtherEnvironments()
    {
        var need = new ScenarioDataNeed("personId", "path", TestDataDataType.SimpleValues, true, null, "string");
        PerformanceScenarioEngine.Match(need, [Asset("person-id-baseline", capabilities: "personId"), Asset("case-id")], "Development")
            .Should().Match<ScenarioDataMatch>(m => m.State == ScenarioDataMatchState.Recommended && m.Recommended == "person-id-baseline" && m.Aliases.Count == 2);
        PerformanceScenarioEngine.Match(need, [Asset("a-person"), Asset("b-person")], null).Should().Match<ScenarioDataMatch>(m => m.State == ScenarioDataMatchState.Possible && m.Recommended == null);
        var none = PerformanceScenarioEngine.Match(need, [Asset("person-x", approval: TestDataApproval.Unapproved), Asset("person-qa", environment: "QA"), Asset("person-json", TestDataKind.Structured, "application/json")], "Development");
        none.State.Should().Be(ScenarioDataMatchState.Missing);
        none.Detail.Should().Be("Test Data missing for {personId} (Simple values / parameters).");
        var op = PerformanceScenarioEngine.Build([OpenApi()], null, null, Target, "Development", [], [], ["persons"], true).Operations.Single(o => o.OperationId == "getPerson");
        PerformanceScenarioEngine.TestDataHref(op, op.Needs[0], "Development", "def-1").Should()
            .Be("/test-data?consumer=performance-test-review&consumerId=def-1&operation=GET%20%2Fapi%2Fperson%2F%7BpersonId%7D&variable=personId&dataType=SimpleValues&in=path&contentType=string%3Auuid&environment=Development");
    }

    // ── Steps ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Steps_AreRelativeWithPlaceholders_MapAliases_AndSuggestOneDocumentedSuccessCode()
    {
        var op = PerformanceScenarioEngine.Build([OpenApi()], Guid.Parse("11111111-1111-1111-1111-111111111111"), "M2LB.zip", Target, null, [Seen("GET", "/api/person/1")],
            [Asset("person-id-baseline", capabilities: "personId"), Asset("tenant-dev", capabilities: "tenant")], null, true).Operations.Single(o => o.OperationId == "getPerson");
        var step = PerformanceScenarioEngine.ToStep(op);
        step.Should().Match<HttpPerformanceStep>(s => s.Method == "GET" && s.RelativePath == "/api/person/{personId}" && s.ThinkTimeMs == 500 && s.Name == "getperson");
        step.ExpectedStatusCodes.Should().Equal(new[] { 200 }, "only the documented 2xx is suggested; 404 is not accepted automatically");
        step.QueryParameters.Should().Equal(new PerformanceNameValue("tenant", "{tenant}"));
        step.DataMappings.Should().BeNull("a recommended Test Data match is a suggestion on the step, never mapped silently");
        PerformanceScenarioEngine.ToStep(op, new Dictionary<string, string> { ["personId"] = "person-id-baseline" }).DataMappings!.Should().ContainSingle()
            .Which.Should().Match<ScenarioDataMapping>(m => m.Variable == "personId" && m.AssetAliases.Single() == "person-id-baseline" && m.Source == RuntimeValueSource.TestData, "an explicit choice is kept");
        step.Source!.Should().Match<ScenarioStepSource>(s => s.Evidence == ScenarioEvidence.DeclaredAndObserved && s.ContractName == "Persons API" && s.OperationFingerprint!.StartsWith("GET /api/person/{personId}"));
        PerformanceScenarioEngine.SuggestName([op]).Should().Be("Person lookup baseline");
        step.RelativePath.Should().NotContain(Target, "the base URL is never duplicated into a step");
    }

    [Fact]
    public void GraphQl_QueriesBecomeVariableDocuments_MutationsAndInputObjectsAreNotRunnable()
    {
        IReadOnlyList<ObservedNetworkEndpoint> seen = [new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.GraphQl, Host = "m2lbdev.example.test", Port = 443, Method = "POST", Path = "/graphql", OperationName = "child", Count = 5, LastObservedAt = DateTimeOffset.UnixEpoch, LastStatus = 200 }];
        var catalog = PerformanceScenarioEngine.Build([GraphQl()], null, null, Target, null, seen, [Asset("child-kode7", capabilities: "childId")], null, true);
        var child = catalog.Operations.Single(o => o.OperationId == "child");
        child.Should().Match<ScenarioOperation>(o => o.Runnable && o.Evidence == ScenarioEvidence.DeclaredAndObserved && o.Use == "GraphQL query");
        var step = PerformanceScenarioEngine.ToStep(child, types: GraphQl().Types);
        step.GraphQlQuery.Should().Be("query PerfChild($childId: ID!) { child(childId: $childId) { __typename } }");
        step.GraphQlVariablesJson.Should().Be("""{"childId":"{childId}"}""", "variables are placeholders — no literal identifier is embedded");
        step.DataMappings.Should().BeNull("the variable is a dynamic parameter configured on the step");
        PerformanceScenarioEngine.ToStep(child, new Dictionary<string, string> { ["childId"] = "child-kode7" }, GraphQl().Types).DataMappings!.Single()
            .Should().Match<ScenarioDataMapping>(m => m.Variable == "childId" && m.In == "variable" && m.AssetAliases.Single() == "child-kode7");
        PerformanceScenarioEngine.ToStep(catalog.Operations.Single(o => o.OperationId == "version"), types: GraphQl().Types).GraphQlQuery.Should().Be("query PerfVersion { version }");
        catalog.Operations.Single(o => o.OperationId == "grantAccess").Should().Match<ScenarioOperation>(o => !o.Runnable && o.Level == ScenarioRecommendationLevel.NotRunnable);
        catalog.Operations.Single(o => o.OperationId == "children").NotRunnableReason.Should().Contain("Input-object");
        PerformanceScenarioEngine.SuggestName([child]).Should().Be("GraphQL child query baseline");
    }

    [Fact]
    public void Drift_ShowsChangedSections_NeverRewritesTheStep()
    {
        var before = PerformanceScenarioEngine.Build([OpenApi()], null, null, Target, null, [], [], ["persons"], true).Operations.Single(o => o.OperationId == "createPerson").Signature;
        var changedContract = OpenApi() with { Operations = [.. OpenApi().Operations.Select(o => o.OperationId == "createPerson" ? o with { RequestSchema = "CreatePersonRequestV2", ResponseCodes = ["201", "409"] } : o)] };
        var after = PerformanceScenarioEngine.Describe(changedContract, changedContract.Operations.Single(o => o.OperationId == "createPerson")).Signature;
        var drift = PerformanceScenarioEngine.Drift(before, after);
        drift.Select(d => d.Section).Should().BeEquivalentTo(["body", "responses"]);
        PerformanceScenarioEngine.DriftText(drift).Should().StartWith("Contract changed · review required: request body changed (application/json CreatePersonRequest → application/json CreatePersonRequestV2)");
        PerformanceScenarioEngine.Drift(before, before).Should().BeEmpty();
    }

    // ── Central Test Data at run time ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Provider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
    }

    private static TestDataAsset Value(string alias, string value, TestDataApproval approval = TestDataApproval.Approved, TestDataKind kind = TestDataKind.Scalar) => new()
    {
        Id = alias, Alias = alias, Kind = kind, Approval = approval, Value = new TestDataValue(value), Source = "Configured",
    };

    private static PerformanceTestDefinition Mapped(params ScenarioDataMapping[] mappings) => PerformanceTestReviewTests.Definition(d => d with
    {
        Scenario = d.Scenario with { TestDataProfileId = null, Steps = [d.Scenario.Steps[0] with { DataMappings = [.. mappings] }] },
    });

    [Fact]
    public void CentralTestData_IsCheckedForReadiness_AndResolvedOnlyForTheRun()
    {
        var catalog = new TestDataCatalog([new Provider(Value("pay-1", "P-1"), Value("pay-2", "P-2"), Value("draft", "D", TestDataApproval.Unapproved))]);
        var ok = Mapped(new ScenarioDataMapping("paymentId", ["pay-1", "pay-2"], TestDataDataType.SimpleValues, "path"));
        PerformanceCentralTestData.Issues(ok, catalog, DateTimeOffset.UtcNow).Should().BeEmpty();
        PerformanceTestSafety.Check(ok, null, new PerformanceTestOptions()).Should().NotContain(i => i.Key == "testdata" || i.Key == "placeholder", "a mapped placeholder is covered by central Test Data");
        var profile = PerformanceCentralTestData.Resolve(ok, catalog)!;
        profile.Columns.Should().Equal("paymentId");
        profile.Rows.Select(r => r.Single()).Should().Equal("P-1", "P-2");
        JsonSerializer.Serialize(ok).Should().NotContain("P-1", "the definition keeps aliases, never values");

        PerformanceCentralTestData.Issues(Mapped(new ScenarioDataMapping("paymentId", ["draft"], TestDataDataType.SimpleValues, "path")), catalog, DateTimeOffset.UtcNow)
            .Should().ContainSingle(i => i.Contains("draft") && i.Contains("unapproved"));
        PerformanceCentralTestData.Issues(Mapped(new ScenarioDataMapping("paymentId", ["gone"], TestDataDataType.SimpleValues, "path")), catalog, DateTimeOffset.UtcNow)
            .Should().ContainSingle(i => i.StartsWith("Test Data review required: gone"));
        PerformanceCentralTestData.Issues(Mapped(new ScenarioDataMapping("body", ["pay-1"], TestDataDataType.JsonPayload, "body")), catalog, DateTimeOffset.UtcNow)
            .Should().ContainSingle(i => i.Contains("needs JSON payload"));
        var unmapped = PerformanceTestReviewTests.Definition(d => d with { Scenario = d.Scenario with { TestDataProfileId = null } });
        PerformanceTestSafety.Check(unmapped, null, new PerformanceTestOptions()).Should().ContainSingle(i => i.Key == "runtime" && i.Message.Contains("paymentId: runtime value source not configured"));
        var testDataChosen = PerformanceTestReviewTests.Definition(d => d with { Scenario = d.Scenario with { TestDataProfileId = null, Steps = [.. d.Scenario.Steps.Select(s => s with { DataMappings = [new ScenarioDataMapping("paymentId", [], TestDataDataType.SimpleValues, "path")] })] } });
        PerformanceTestSafety.Check(testDataChosen, null, new PerformanceTestOptions()).Should().ContainSingle(i => i.Key == "testdata" && i.Message.StartsWith("Test Data missing for {paymentId}"), "Test Data is named only when it is the chosen source");
    }

    [Fact]
    public void LegacyDefinitions_KeepTheirExactFingerprints()
    {
        var legacy = PerformanceTestReviewTests.Definition();
        var legacyComparison = PerformanceTestStore.Fingerprint(new
        {
            Target = legacy.TargetOrigin.TrimEnd('/').ToLowerInvariant(), legacy.TargetType, Steps = legacy.Scenario.Steps, legacy.Workload,
            Environment = legacy.EnvironmentType.Trim().ToLowerInvariant(), legacy.Scenario.TestDataProfileId,
        });
        PerformanceTestStore.ComparisonFingerprint(legacy).Should().Be(legacyComparison, "new optional properties never change an existing baseline's comparability");
        JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().NotContainAny("deliveryPipelineIds", "dataMappings", "source\"", "disabled", "contractSnapshotId");
        var annotated = legacy with { Scenario = legacy.Scenario with { Steps = [legacy.Scenario.Steps[0] with { Source = new ScenarioStepSource { OperationKey = "openapi:x#GET /a" } }] } };
        PerformanceTestStore.ComparisonFingerprint(annotated).Should().Be(legacyComparison, "provenance is not workload");
    }

    [Fact]
    public void SetupProfile_OperationRefWithoutMethodAndPath_RequiresReview_AndTestDataRefsAreAliases()
    {
        const string profile = """
            version: 1
            profile:
              name: "Scenario refs"
            performance:
              definition:
                name: "Person lookup baseline"
              scenario:
                selection: "sequential"
                steps:
                  - operationRef:
                      type: "openapi"
                      operationId: "getPerson"
                      method: "GET"
                      path: "/api/person/{personId}"
                    testDataRef:
                      mappings:
                        personId: "person-id-baseline,person-id-2"
                    expectedStatusCodes: [200]
                  - operationRef:
                      type: "openapi"
                      operationId: "unknownOp"
            """;
        var (root, _, issues) = SetupProfileParser.Parse("p.yaml", profile);
        issues.AddRange(SetupProfileSchema.Validate(root!));
        issues.Should().NotContain(i => i.Severity == SetupIssueSeverity.Error || i.Severity == SetupIssueSeverity.Rejected);
        var options = new PerformanceTestOptions();
        using var db = new BirkNext.Api.Data.AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BirkNext.Api.Data.AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new PerformanceSetupProfileService(new PerformanceTestStore(db), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<PerformanceSetupProfileService>.Instance);
        var preview = service.PreviewAsync("dev", new SetupProfileRequest("p.yaml", profile), new PerformanceSetupTarget("https://m2lb-dev.example.test", "Development", "Development"), default).Result;
        preview.Issues.Should().Contain(i => i.Message.StartsWith("Requires review: operation unknownOp cannot be resolved"));
        preview.Changes.Single(c => c.Id == "performance.definition").Proposed.Should().Contain("1 step(s)", "the unresolved step is not silently converted into a manual path");
    }
}
