using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// The REST contract-driven workflow end to end against a synthetic loopback API: import (YAML upload, URL, unchanged re-import, new version),
/// stable operation identity, draft generation, the Test Data picker (compatible datasets, missing required values), pinned binding,
/// readiness (contract, Test Data, environment, authentication), a run through Execution Evidence with operation/Test Data/definition
/// references, the latest result per test, and the impact of a new contract version. The server's observations are the oracle.
/// </summary>
public sealed class ContractTestingTests : IAsyncLifetime
{
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<(string Method, string PathAndQuery, string Body, string? Authorization)> _seen = new();
    private ServiceProvider _services = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-cdt-" + Guid.NewGuid().ToString("N"));
    private AuthoredTestDataStore _authored = null!;
    private TestDataRegistryStore _registry = null!;
    private TestDataCatalog _catalog = null!;

    public const string V1Yaml = """
        openapi: 3.0.3
        info:
          title: Synthetic Children API
          version: "1.0"
        paths:
          /children/{id}:
            get:
              operationId: getChild
              parameters:
                - name: id
                  in: path
                  required: true
                  schema:
                    type: integer
              responses:
                "200":
                  description: ok
                  content:
                    application/json:
                      schema:
                        $ref: "#/components/schemas/Child"
                "404":
                  description: missing
          /children:
            get:
              parameters:
                - name: status
                  in: query
                  required: true
                  schema:
                    type: string
                    enum:
                      - active
                      - inactive
              responses:
                "200":
                  description: ok
                  content:
                    application/json:
                      schema:
                        type: array
                        items:
                          $ref: "#/components/schemas/Child"
                "400":
                  description: bad
            post:
              requestBody:
                required: true
                content:
                  application/json:
                    schema:
                      $ref: "#/components/schemas/NewChild"
              responses:
                "201":
                  description: created
                  content:
                    application/json:
                      schema:
                        $ref: "#/components/schemas/Child"
                "400":
                  description: bad
          /admin/reports:
            get:
              security:
                - bearer: []
              responses:
                "200":
                  description: ok
        components:
          securitySchemes:
            bearer:
              type: http
              scheme: bearer
          schemas:
            Child:
              type: object
              required:
                - id
                - name
              properties:
                id:
                  type: integer
                name:
                  type: string
                age:
                  type: integer
            NewChild:
              type: object
              required:
                - name
                - age
              properties:
                name:
                  type: string
                age:
                  type: integer
                nickname:
                  type: string
        """;

    /// <summary>v2: GET /children gains a required query parameter, NewChild a required field, Child loses age, /admin/reports is removed.</summary>
    public const string V2Json = """
        { "openapi": "3.0.3", "info": { "title": "Synthetic Children API", "version": "2.0" },
          "paths": {
            "/children/{id}": { "get": { "operationId": "getChild", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],
              "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } }, "404": { "description": "missing" } } } },
            "/children": {
              "get": { "parameters": [ { "name": "status", "in": "query", "required": true, "schema": { "type": "string", "enum": ["active", "inactive"] } },
                                       { "name": "school", "in": "query", "required": true, "schema": { "type": "string" } } ],
                "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Child" } } } } }, "400": { "description": "bad" } } },
              "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewChild" } } } },
                "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } }, "400": { "description": "bad" } } } } },
          "components": { "schemas": {
            "Child": { "type": "object", "required": ["id", "name"], "properties": { "id": { "type": "integer" }, "name": { "type": "string" } } },
            "NewChild": { "type": "object", "required": ["name", "age", "birthYear"], "properties": { "name": { "type": "string" }, "age": { "type": "integer" }, "nickname": { "type": "string" }, "birthYear": { "type": "integer" } } } } } }
        """;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        _authored = new AuthoredTestDataStore(_dir);
        _registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        _catalog = new TestDataCatalog([new AuthoredTestDataProvider(_authored)], null, _registry);
        var services = new ServiceCollection();
        var db = $"cdt-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient(ContractSourcePolicy.CreateHandler())));
        services.AddSingleton(_authored);
        services.AddSingleton(_catalog);
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        string body;
        using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        if (path != "/openapi.yaml") _seen.Enqueue((c.Request.HttpMethod, c.Request.Url.PathAndQuery, body, c.Request.Headers["Authorization"]));
        async Task Send(int status, string text, string type = "application/json")
        {
            c.Response.StatusCode = status; c.Response.ContentType = type;
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        var query = c.Request.QueryString;
        switch (path)
        {
            case "/openapi.yaml": await Send(200, V1Yaml, "application/yaml"); break;
            case "/children/7": await Send(200, """{"id":7,"name":"Testbarn"}"""); break;
            case "/children" when c.Request.HttpMethod == "GET":
                if (query["status"] is "active" or "inactive") await Send(200, """[{"id":7,"name":"Testbarn"}]"""); else await Send(400, """{"error":"status is required"}""");
                break;
            case "/children" when c.Request.HttpMethod == "POST":
                var json = JsonNode.Parse(body.Length == 0 ? "{}" : body) as JsonObject;
                if (json?["name"] is not null && json["age"] is JsonValue age && age.TryGetValue<int>(out _)) await Send(201, """{"id":8,"name":"Testbarn"}""");
                else await Send(400, """{"error":"name and a numeric age are required"}""");
                break;
            case "/admin/reports": await Send(401, """{"error":"unauthorized"}"""); break;
            case "/graphql": await Send(200, GraphQlAnswer(body)); break;
            default: await Send(404, """{"error":"no route"}"""); break;
        }
    }

    public const string GraphQlV1 = """
        "A registered child"
        type Child { id: ID! name: String! age: Int status: Status }
        enum Status { ACTIVE INACTIVE }
        input NewChildInput { name: String! age: Int! nickname: String }
        type Query { child(id: ID!): Child children(status: Status): [Child!]! forbiddenReport: String }
        type Mutation { createChild(input: NewChildInput!): Child! }
        """;

    /// <summary>v2: NewChildInput gains a required birthYear, Status loses INACTIVE, Child loses age, forbiddenReport is removed.</summary>
    public const string GraphQlV2 = """
        type Child { id: ID! name: String! status: Status }
        enum Status { ACTIVE }
        input NewChildInput { name: String! age: Int! nickname: String birthYear: Int! }
        type Query { child(id: ID!): Child children(status: Status): [Child!]! }
        type Mutation { createChild(input: NewChildInput!): Child! }
        """;

    /// <summary>A minimal synthetic GraphQL server: validates required variables the way a real server does (errors with HTTP 200).</summary>
    private static string GraphQlAnswer(string body)
    {
        var request = JsonNode.Parse(body)!.AsObject();
        var query = request["query"]?.GetValue<string>() ?? "";
        var variables = request["variables"] as JsonObject ?? [];
        static string Error(string message, string? code = null) =>
            new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = message, ["extensions"] = code is null ? null : new JsonObject { ["code"] = code } }) }.ToJsonString();
        if (query.Contains("forbiddenReport", StringComparison.Ordinal)) return """{"data":{"forbiddenReport":null},"errors":[{"message":"Forbidden","path":["forbiddenReport"],"extensions":{"code":"FORBIDDEN"}}]}""";
        if (query.StartsWith("mutation", StringComparison.Ordinal))
            return variables["input"] is JsonObject input && input["name"] is JsonValue && input["age"] is JsonValue age && age.GetValueKind() == JsonValueKind.Number
                ? """{"data":{"createChild":{"id":"8","name":"Testbarn","age":7,"status":"ACTIVE"}}}""" : Error("Variable \"$input\" got invalid value; field \"name\" or \"age\" of required type was not provided.", "BAD_USER_INPUT");
        if (query.Contains("child(id:", StringComparison.Ordinal))
            return variables["id"] is JsonValue ? """{"data":{"child":{"id":"7","name":"Testbarn","age":7,"status":"ACTIVE"}}}""" : Error("Variable \"$id\" of required type \"ID!\" was not provided.");
        if (query.Contains("children", StringComparison.Ordinal)) return """{"data":{"children":[{"id":"7","name":"Testbarn","age":7,"status":"ACTIVE"}]}}""";
        return Error("Unknown operation.");
    }

    private T Scoped<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private ContractTestEnvironment Local => new() { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };

    private void Dataset(string alias, params TestDataTypedEntry[] members)
    {
        var (outcome, _) = _authored.Create(new TestDataResourceDraft
        {
            Alias = alias, Label = alias, Content = new() { Kind = TestDataResourceKind.Dataset, Members = [.. members.Select(m => new TestDataDatasetMember(m.Name, null, m))] },
        });
        outcome.Should().Be(AuthoredTestDataStore.Outcome.Saved);
        var asset = _catalog.Assets().Single(a => a.Alias == alias);
        _registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = asset.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(asset), ApprovedAt = DateTimeOffset.UtcNow });
    }

    private async Task<ExecutionRunDetail> WaitAsync(Guid runId)
    {
        for (var i = 0; i < 600 && ExecutionRunService.IsRunning(runId); i++) await Task.Delay(50);
        using var scope = _services.CreateScope();
        return (await Scoped<ExecutionEvidenceStore>(scope).GetAsync(runId, new(Take: 200), default))!;
    }

    [Fact]
    public async Task Import_creates_immutable_versions_with_stable_operation_identity_and_never_duplicates()
    {
        using var scope = _services.CreateScope();
        var registry = Scoped<ContractRegistryService>(scope);

        var v1 = await registry.ImportAsync(new() { FileName = "children.yaml", Content = V1Yaml }, default);
        v1.Imported.Should().BeTrue(v1.Message); v1.Key.Should().Be("synthetic-children-api"); v1.Version.Should().Be(1);
        var detail = (await registry.GetAsync("synthetic-children-api", null, default))!;
        detail.Document.Operations.Select(o => o.Name).Should().BeEquivalentTo(["GET /children/{id}", "GET /children", "POST /children", "GET /admin/reports"]);
        detail.Versions.Single().Format.Should().Be("yaml");
        detail.Document.Operations.Single(o => o.Name == "POST /children").RequestSchema.Should().Be("NewChild");
        detail.Document.Operations.Single(o => o.Name == "GET /children/{id}").ResponseSchemas["200"].Should().Be("Child");

        (await registry.ImportAsync(new() { FileName = "again.yaml", Content = V1Yaml }, default)).Unchanged.Should().BeTrue("the identical document is already the latest version");
        var byUrl = await registry.ImportAsync(new() { Url = _server.Url("/openapi.yaml") }, default);
        byUrl.Unchanged.Should().BeTrue(byUrl.Message);
        (await registry.ImportAsync(new() { Url = "http://169.254.169.254/openapi.yaml" }, default)).Problems.Should().ContainSingle(p => p.Contains("forbidden address"));
        (await registry.ImportAsync(new() { FileName = "x.json", Content = """{"hello":"world"}""" }, default)).Imported.Should().BeFalse();

        var v2 = await registry.ImportAsync(new() { ContractKey = "synthetic-children-api", FileName = "children-v2.json", Content = V2Json }, default);
        v2.Version.Should().Be(2);
        (await registry.DocumentAsync("synthetic-children-api", 1, default))!.Operations.Should().HaveCount(4, "version 1 is never rewritten");
        (await registry.DocumentAsync("synthetic-children-api", 2, default))!.Operations.Should().HaveCount(3);
        (await registry.ListAsync(default)).Should().ContainSingle().Which.LatestVersion.Should().Be(2);
    }

    [Fact]
    public async Task The_full_REST_workflow_import_generate_bind_ready_run_evidence_and_contract_change_impact()
    {
        using var scope = _services.CreateScope();
        var registry = Scoped<ContractRegistryService>(scope);
        var tests = Scoped<ContractTestService>(scope);
        (await registry.ImportAsync(new() { FileName = "children.yaml", Content = V1Yaml }, default)).Imported.Should().BeTrue();
        const string key = "synthetic-children-api";

        // Draft generation: positive per operation, negative where a required query parameter or body field can be omitted. Idempotent.
        var (created, _) = await tests.GenerateAsync(key, null, null, default);
        created.Select(t => (t.Operation, t.Kind)).Should().BeEquivalentTo(new[]
        {
            ("GET /children/{id}", ContractTestKind.Positive), ("GET /children", ContractTestKind.Positive), ("GET /children", ContractTestKind.MissingRequiredInput),
            ("POST /children", ContractTestKind.Positive), ("POST /children", ContractTestKind.MissingRequiredInput), ("GET /admin/reports", ContractTestKind.Positive),
        });
        (await tests.GenerateAsync(key, null, null, default)).Created.Should().BeEmpty("generating twice never duplicates");
        var post = created.Single(t => t.Operation == "POST /children" && t.Kind == ContractTestKind.Positive);
        post.Inputs.Where(i => i.Required).Select(i => i.Name).Should().BeEquivalentTo(["name", "age"]);
        post.Template.Assertions.Should().Contain(a => a.Kind == AssertionKind.StatusCode && a.Expected == "201");
        post.Template.Assertions.Should().Contain(a => a.Kind == AssertionKind.JsonPath && a.Target == "$.name", "required properties of the documented 201 schema");
        var getOne = created.Single(t => t.Operation == "GET /children/{id}");
        getOne.Template.Path.Should().Be("/children/{{dataset.id}}");

        // Readiness before Test Data: the required values are listed as missing.
        var before = (await tests.ListAsync(key, Local, default)).Single(v => v.Test.Id == post.Id);
        before.Readiness.State.Should().Be(ContractTestReadinessState.NotReady);
        before.Readiness.MissingValues.Should().BeEquivalentTo(["name", "age"]);

        // Picker: the complete dataset is compatible; the one without age is offered with "age" missing; an unapproved one says so.
        Dataset("children-complete", new("id", TestDataPrimitive.Int32, "7"), new("status", TestDataPrimitive.String, "active"),
            new("name", TestDataPrimitive.String, "Testbarn"), new("age", TestDataPrimitive.Int32, "7"));
        Dataset("children-no-age", new("id", TestDataPrimitive.Int32, "7"), new("name", TestDataPrimitive.String, "Testbarn"));
        _authored.Create(new TestDataResourceDraft { Alias = "children-draft", Content = new() { Kind = TestDataResourceKind.KeyValue, Entries = [new("name", TestDataPrimitive.String, "X")] } });
        var options = await tests.DatasetOptionsAsync(post.Id, "local-synthetic", default);
        options.First().Should().Match<ContractDatasetOption>(o => o.Alias == "children-complete" && o.Compatible && o.ApprovedVersion == 1);
        options.Single(o => o.Alias == "children-no-age").MissingRequired.Should().Equal("age");
        options.Single(o => o.Alias == "children-draft").Problem.Should().Contain("No approved version");

        (await tests.BindAsync(post.Id, new("children-draft", 1), default)).Error.Should().Contain("approved version");
        foreach (var t in created) (await tests.BindAsync(t.Id, new("children-complete", 1), default)).Error.Should().BeNull();

        var views = await tests.ListAsync(key, Local, default);
        views.Where(v => v.Test.Operation != "GET /admin/reports").Should().OnlyContain(v => v.Readiness.State == ContractTestReadinessState.Ready);
        views.Single(v => v.Test.Operation == "GET /admin/reports").Readiness.Items.Should().Contain(i => i.Area == "Authentication" && i.State == ReadinessItemState.Blocked);
        (await tests.ListAsync(key, Local with { EnvironmentType = "Production", IsProduction = true }, default))
            .Should().OnlyContain(v => v.Readiness.State == ContractTestReadinessState.Blocked, "the API safety policy refuses production");

        // Run without confirming data-changing calls: POSTs are recorded as Blocked and never reach the target; /admin is not run at all.
        var all = created.Select(t => t.Id).ToList();
        var first = await tests.RunAsync(new() { TestIds = all, Environment = Local }, default);
        first.NotRun.Should().ContainSingle(n => n.Contains("GET /admin/reports"));
        var run1 = await WaitAsync(first.Run!.RunId!.Value);
        run1.Calls.Where(c => c.Definition.Method == "POST").Should().OnlyContain(c => c.State == ExecutionState.Blocked);
        _seen.Should().NotContain(s => s.Method == "POST");

        var second = await tests.RunAsync(new() { TestIds = all, Environment = Local, ConfirmMutatingCalls = true }, default);
        var run2 = await WaitAsync(second.Run!.RunId!.Value);
        run2.Run.ContractRefs.Should().Equal("synthetic-children-api@v1");
        run2.Calls.Should().HaveCount(5).And.OnlyContain(c => c.State == ExecutionState.Passed, string.Join("; ", run2.Calls.Select(c => $"{c.Definition.Name}: {c.State} {c.Reason}")));
        var postCall = run2.Calls.Single(c => c.Definition.Name == post.Name);
        postCall.Definition.OperationRef.Should().Be("synthetic-children-api@v1#POST /children");
        postCall.Definition.TestDataRefs.Should().Equal("children-complete@v1");
        postCall.Definition.DefinitionVersion.Should().Be(ContractIdentity.DefinitionRef(post.Id, 2));
        _seen.Should().Contain(s => s.Method == "POST" && s.Body.Contains("\"age\":7", StringComparison.Ordinal), "numbers from Test Data stay JSON numbers");
        _seen.Should().Contain(s => s.PathAndQuery == "/children/7");
        _seen.Should().Contain(s => s.Method == "GET" && s.PathAndQuery == "/children", "the negative test omits the required status parameter");
        _seen.Should().OnlyContain(s => s.Authorization == null);

        // The latest result per test is read back from Execution Evidence.
        var afterRun = await tests.ListAsync(key, Local, default);
        afterRun.Single(v => v.Test.Id == post.Id).Last.Should().Match<ContractTestLastResult>(l => l.RunId == run2.Run.RunId && l.State == ExecutionState.Passed && l.HttpStatus == 201);

        // A new contract version: the impact per test, readiness follows it, and regeneration surfaces the newly required value.
        (await registry.ImportAsync(new() { ContractKey = key, FileName = "v2.json", Content = V2Json }, default)).Version.Should().Be(2);
        var impacted = (await tests.ListAsync(key, Local, default)).ToDictionary(v => (v.Test.Operation, v.Test.Kind));
        impacted[("GET /admin/reports", ContractTestKind.Positive)].Impact!.Level.Should().Be(ContractTestImpactLevel.Broken);
        impacted[("GET /children", ContractTestKind.Positive)].Impact!.Level.Should().Be(ContractTestImpactLevel.NeedsUpdate);
        impacted[("GET /children", ContractTestKind.Positive)].Impact!.Reasons.Should().Contain(r => r.Contains("school"));
        impacted[("POST /children", ContractTestKind.Positive)].Impact!.Level.Should().Be(ContractTestImpactLevel.NeedsUpdate);
        impacted[("POST /children", ContractTestKind.Positive)].Impact!.Reasons.Should().Contain(r => r.Contains("NewChild.birthYear"));
        impacted[("GET /children/{id}", ContractTestKind.Positive)].Impact!.Level.Should().Be(ContractTestImpactLevel.Review, "Child lost a property the response schema documented");
        impacted[("POST /children", ContractTestKind.Positive)].Readiness.State.Should().Be(ContractTestReadinessState.NotReady);
        impacted[("GET /admin/reports", ContractTestKind.Positive)].Readiness.State.Should().Be(ContractTestReadinessState.Blocked);

        var diff = (await tests.DiffAsync(key, 1, 2, default))!;
        diff.Changes.Select(c => c.ContractChange).Should().Contain([ContractChangeClass.OperationRemoved, ContractChangeClass.ParameterAddedRequired, ContractChangeClass.FieldAddedRequired, ContractChangeClass.FieldRemoved]);
        diff.Impacts.Should().HaveCount(6);

        var regenerated = await tests.RegenerateAsync(impacted[("POST /children", ContractTestKind.Positive)].Test.Id, default);
        regenerated.Test!.ContractVersion.Should().Be(2);
        regenerated.Test.Binding.Should().Be(new ContractTestDataBinding("children-complete", 1), "the pinned Test Data is kept");
        var afterRegen = (await tests.GetAsync(regenerated.Test.Id, Local, default))!;
        afterRegen.Readiness.State.Should().Be(ContractTestReadinessState.NotReady);
        afterRegen.Readiness.MissingValues.Should().Equal("birthYear");
        (await tests.RegenerateAsync(impacted[("GET /admin/reports", ContractTestKind.Positive)].Test.Id, default)).Error.Should().Contain("does not exist in v2");
    }

    [Fact]
    public async Task The_GraphQL_workflow_reuses_the_registry_tests_Test_Data_and_evidence_with_GraphQL_semantics()
    {
        using var scope = _services.CreateScope();
        var registry = Scoped<ContractRegistryService>(scope);
        var tests = Scoped<ContractTestService>(scope);
        var imported = await registry.ImportAsync(new() { Name = "Synthetic Children GraphQL", FileName = "children.graphql", Content = GraphQlV1 }, default);
        imported.Imported.Should().BeTrue(imported.Message);
        const string key = "synthetic-children-graphql";
        var detail = (await registry.GetAsync(key, null, default))!;
        detail.Contract.Protocol.Should().Be(ApiContractProtocol.GraphQl);
        detail.Document.Operations.Select(o => o.Name).Should().BeEquivalentTo(["query child", "query children", "query forbiddenReport", "mutation createChild"]);
        detail.Document.Operations.Single(o => o.Name == "query child").ParameterDetails.Single().Should().Match<ContractParameter>(p => p.Type == "ID!" && p.Required);
        (await registry.ImportAsync(new() { ContractKey = key, FileName = "rest.yaml", Content = V1Yaml }, default)).Problems.Should().ContainSingle(p => p.Contains("GraphQL contract"));
        (await registry.ImportAsync(new() { FileName = "ops.graphql", Content = "query Child($id: ID!) { child(id: $id) { id } }" }, default)).Problems.Should().ContainSingle(p => p.Contains("operations document"));

        var (created, skipped) = await tests.GenerateAsync(key, null, null, default);
        created.Select(t => (t.Operation, t.Kind)).Should().BeEquivalentTo(new[]
        {
            ("query child", ContractTestKind.Positive), ("query child", ContractTestKind.MissingRequiredInput), ("query children", ContractTestKind.Positive),
            ("query forbiddenReport", ContractTestKind.Positive), ("mutation createChild", ContractTestKind.Positive), ("mutation createChild", ContractTestKind.MissingRequiredInput),
        }, "a nullable argument (children's status) gives no negative test");
        var child = created.Single(t => t.Operation == "query child" && t.Kind == ContractTestKind.Positive);
        child.Template.GraphQlQuery.Should().Be("query Child($id: ID!) { child(id: $id) { id name age status } }");
        child.Template.Assertions.Select(a => (a.Kind, a.Target)).Should().Contain([(AssertionKind.GraphQlErrorCount, null), (AssertionKind.GraphQlDataPath, "child.id"), (AssertionKind.GraphQlDataPath, "child.name")]);
        var create = created.Single(t => t.Operation == "mutation createChild" && t.Kind == ContractTestKind.Positive);
        create.Inputs.Select(i => (i.Name, i.Required)).Should().BeEquivalentTo([("name", true), ("age", true), ("nickname", false)], "input object fields are the inputs; nullability decides required");
        created.Single(t => t.Operation == "mutation createChild" && t.Kind == ContractTestKind.MissingRequiredInput).Template.GraphQlVariablesJson.Should().Be("""{"input":{"age":"{{dataset.age}}","nickname":"{{dataset.nickname}}"}}""");

        Dataset("gql-children", new("id", TestDataPrimitive.String, "7"), new("name", TestDataPrimitive.String, "Testbarn"), new("age", TestDataPrimitive.Int32, "7"),
            new("status", TestDataPrimitive.Enum, "ACTIVE") { EnumValues = ["ACTIVE", "INACTIVE"] });
        foreach (var t in created) (await tests.BindAsync(t.Id, new("gql-children", 1), default)).Error.Should().BeNull();
        var views = await tests.ListAsync(key, Local, default);
        views.Should().OnlyContain(v => v.Readiness.State == ContractTestReadinessState.Ready, string.Join("; ", views.SelectMany(v => v.Readiness.Items.Where(i => i.State != ReadinessItemState.Ok)).Select(i => i.Text)));

        var unconfirmed = await WaitAsync((await tests.RunAsync(new() { TestIds = [.. created.Select(t => t.Id)], Environment = Local }, default)).Run!.RunId!.Value);
        unconfirmed.Calls.Where(c => c.Definition.GraphQlQuery!.StartsWith("mutation", StringComparison.Ordinal)).Should().OnlyContain(c => c.State == ExecutionState.Blocked, "a mutation changes data");
        _seen.Should().NotContain(s => s.Body.Contains("mutation", StringComparison.Ordinal));

        var run = await WaitAsync((await tests.RunAsync(new() { TestIds = [.. created.Select(t => t.Id)], Environment = Local, ConfirmMutatingCalls = true }, default)).Run!.RunId!.Value);
        string State(string op, ContractTestKind kind) => run.Calls.Single(c => c.Definition.Name == created.Single(t => t.Operation == op && t.Kind == kind).Name).State.ToString();
        State("query child", ContractTestKind.Positive).Should().Be("Passed");
        State("query child", ContractTestKind.MissingRequiredInput).Should().Be("Passed", "the server's GraphQL error for the missing variable is the expected outcome");
        State("query children", ContractTestKind.Positive).Should().Be("Passed");
        State("mutation createChild", ContractTestKind.Positive).Should().Be("Passed");
        State("mutation createChild", ContractTestKind.MissingRequiredInput).Should().Be("Passed");
        var forbidden = run.Calls.Single(c => c.Definition.Name.StartsWith("query forbiddenReport", StringComparison.Ordinal));
        forbidden.State.Should().Be(ExecutionState.Failed, "HTTP 200 with GraphQL errors is not a pass");
        forbidden.Attempts[^1].Response!.StatusCode.Should().Be(200);
        forbidden.Attempts[^1].Response!.GraphQlErrorCodes.Should().Equal("FORBIDDEN");
        var mutation = run.Calls.Single(c => c.Definition.Name == create.Name);
        mutation.Definition.OperationRef.Should().Be("synthetic-children-graphql@v1#mutation createChild");
        mutation.Definition.TestDataRefs.Should().Equal("gql-children@v1");
        _seen.Should().Contain(s => s.Body.Contains("\"input\":{\"name\":\"Testbarn\",\"age\":7}", StringComparison.Ordinal), "typed values; the optional nickname without a value is left out");

        (await registry.ImportAsync(new() { ContractKey = key, FileName = "children-v2.graphql", Content = GraphQlV2 }, default)).Version.Should().Be(2);
        var impact = (await tests.ListAsync(key, Local, default)).Where(v => v.Test.Kind == ContractTestKind.Positive).ToDictionary(v => v.Test.Operation, v => v.Impact!);
        impact["query forbiddenReport"].Level.Should().Be(ContractTestImpactLevel.Broken);
        impact["mutation createChild"].Level.Should().Be(ContractTestImpactLevel.NeedsUpdate);
        impact["mutation createChild"].Reasons.Should().Contain(r => r.Contains("NewChildInput.birthYear"));
        impact["query children"].Level.Should().Be(ContractTestImpactLevel.NeedsUpdate, "an enum value of the argument type was removed");
        impact["query child"].Level.Should().Be(ContractTestImpactLevel.Review, "the result type lost a field");
    }

    [Fact]
    public async Task A_GraphQL_introspection_result_imports_through_the_same_mapping_as_SDL()
    {
        const string introspection = """
            { "data": { "__schema": { "queryType": { "name": "Query" }, "mutationType": null, "subscriptionType": null, "directives": [],
              "types": [
                { "kind": "OBJECT", "name": "Query", "description": null, "fields": [
                  { "name": "ping", "description": null, "args": [ { "name": "echo", "description": null, "defaultValue": null,
                      "type": { "kind": "NON_NULL", "name": null, "ofType": { "kind": "SCALAR", "name": "String", "ofType": null } } } ],
                    "type": { "kind": "SCALAR", "name": "String", "ofType": null }, "isDeprecated": false, "deprecationReason": null } ],
                  "inputFields": null, "interfaces": [], "enumValues": null, "possibleTypes": null },
                { "kind": "SCALAR", "name": "String", "description": null, "fields": null, "inputFields": null, "interfaces": null, "enumValues": null, "possibleTypes": null } ] } } }
            """;
        using var scope = _services.CreateScope();
        var result = await Scoped<ContractRegistryService>(scope).ImportAsync(new() { Name = "Ping", FileName = "schema.json", Content = introspection }, default);
        result.Imported.Should().BeTrue(result.Message);
        var detail = (await Scoped<ContractRegistryService>(scope).GetAsync(result.Key!, null, default))!;
        detail.Versions.Single().Format.Should().Be("introspection");
        detail.Document.Operations.Single().Should().Match<ContractOperation>(o => o.Name == "query ping" && o.ParameterDetails.Single().Type == "String!" && o.ResultType == "String");
    }

    [Fact]
    public async Task Contract_validation_fails_an_invalid_response_and_never_sends_an_invalid_enforced_request()
    {
        const string strict = """
            { "openapi": "3.0.3", "info": { "title": "Strict Children API", "version": "1" }, "paths": {
              "/children/{id}": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],
                "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } } } } },
              "/children": { "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewChild" } } } },
                "responses": { "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } }, "400": { "description": "bad" } } } } },
              "components": { "schemas": {
                "Child": { "type": "object", "required": ["id", "name", "age"], "properties": { "id": { "type": "integer" }, "name": { "type": "string" }, "age": { "type": "integer" } } },
                "NewChild": { "type": "object", "required": ["name", "age"], "properties": { "name": { "type": "string" }, "age": { "type": "integer", "minimum": 0 } } } } } }
            """;
        using var scope = _services.CreateScope();
        (await Scoped<ContractRegistryService>(scope).ImportAsync(new() { FileName = "strict.json", Content = strict }, default)).Imported.Should().BeTrue();
        var tests = Scoped<ContractTestService>(scope);
        var (created, _) = await tests.GenerateAsync("strict-children-api", null, null, default);
        var get = created.Single(t => t.Operation == "GET /children/{id}");
        var post = created.Single(t => t.Operation == "POST /children" && t.Kind == ContractTestKind.Positive);
        Dataset("strict-ok", new("id", TestDataPrimitive.Int32, "7"), new("name", TestDataPrimitive.String, "Testbarn"), new("age", TestDataPrimitive.Int32, "7"));
        Dataset("strict-bad", new("name", TestDataPrimitive.String, "Testbarn"), new("age", TestDataPrimitive.Int32, "-3"));
        (await tests.BindAsync(get.Id, new("strict-ok", 1), default)).Error.Should().BeNull();
        (await tests.BindAsync(post.Id, new("strict-bad", 1), default)).Error.Should().BeNull();

        var run = await WaitAsync((await tests.RunAsync(new() { TestIds = [get.Id, post.Id], Environment = Local, ConfirmMutatingCalls = true }, default)).Run!.RunId!.Value);
        var getCall = run.Calls.Single(c => c.Definition.Method == "GET");
        getCall.State.Should().Be(ExecutionState.Failed, "the synthetic API omits the required age");
        getCall.Assertions.Single(a => a.Kind == AssertionKind.ContractSchema).Outcome.Should().Be(AssertionOutcome.Failed);
        getCall.Assertions.Single(a => a.Kind == AssertionKind.StatusCode).Outcome.Should().Be(AssertionOutcome.Passed, "schema validity and the other assertions are judged separately");
        getCall.ContractValidation.Select(r => (r.Direction, r.State)).Should().Equal(("Request", ContractValidationState.Valid), ("Response", ContractValidationState.Invalid));
        getCall.ContractValidation[1].Findings.Should().ContainSingle(f => f.InstancePath == "$.age" && f.Keyword == "required" && f.SchemaPath == "#/components/schemas/Child/required");
        var postCall = run.Calls.Single(c => c.Definition.Method == "POST");
        postCall.State.Should().Be(ExecutionState.Blocked);
        postCall.Attempts.Should().BeEmpty("an invalid request is not sent under Enforce");
        postCall.Reason.Should().Contain("violates the contract").And.Contain("$.body.age");
        _seen.Should().NotContain(s => s.Method == "POST");
    }

    [Theory]
    [InlineData("//evil.example.test")]
    [InlineData("http://evil.example.test/api")]
    [InlineData("api")]
    [InlineData("/api?x=1")]
    [InlineData("/api\\x")]
    public async Task A_base_path_is_only_ever_a_path_inside_the_Target_Environment(string basePath)
    {
        using var scope = _services.CreateScope();
        (await Scoped<ContractRegistryService>(scope).ImportAsync(new() { FileName = "children.yaml", Content = V1Yaml }, default)).Imported.Should().BeTrue();
        var (created, _) = await Scoped<ContractTestService>(scope).GenerateAsync("synthetic-children-api", null, ["GET /admin/reports"], default);
        var result = await Scoped<ContractTestService>(scope).RunAsync(new() { TestIds = [created[0].Id], Environment = Local, BasePath = basePath }, default);
        result.Run.Should().BeNull();
        result.NotRun.Should().ContainSingle().Which.Should().Contain("path inside the Target Environment");
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_valid_base_path_prefixes_every_call_path()
    {
        using var scope = _services.CreateScope();
        (await Scoped<ContractRegistryService>(scope).ImportAsync(new() { FileName = "children.yaml", Content = V1Yaml }, default)).Imported.Should().BeTrue();
        var (created, _) = await Scoped<ContractTestService>(scope).GenerateAsync("synthetic-children-api", null, ["GET /children/{id}"], default);
        Dataset("ids", new TestDataTypedEntry("id", TestDataPrimitive.Int32, "7"));
        (await Scoped<ContractTestService>(scope).BindAsync(created[0].Id, new("ids", 1), default)).Error.Should().BeNull();
        var result = await Scoped<ContractTestService>(scope).RunAsync(new() { TestIds = [created[0].Id], Environment = Local, BasePath = "/api/" }, default);
        var run = await WaitAsync(result.Run!.RunId!.Value);
        run.Calls.Single().Definition.Path.Should().Be("/api/children/7");
        _seen.Should().ContainSingle(s => s.PathAndQuery == "/api/children/7");
    }

    [Fact]
    public void Materialize_keeps_types_escapes_path_values_and_drops_only_optional_inputs()
    {
        var contract = new SourceContract
        {
            Id = "c", Name = "c", Type = SourceContractType.OpenApi,
            Types = [new("NewChild", "object", [new("name", "string", true), new("age", "integer", true), new("nickname", "string", false)])],
        };
        var op = new ContractOperation("POST /schools/{code}/children", "POST", "/schools/{code}/children", ["code", "tag"])
        {
            ParameterDetails = [new("code", "path", true, "string"), new("tag", "query", false, "string"), new("Authorization", "header", true, "string")],
            RequestContentTypes = ["application/json"], RequestBodyRequired = true, RequestSchema = "NewChild", ResponseCodes = ["201", "422"], Detailed = true,
        };
        var tests = ContractTestRules.Generate(contract, "c", 1, op, DateTimeOffset.UnixEpoch);
        tests.Should().HaveCount(2);
        tests[0].Inputs.Select(i => i.Name).Should().NotContain("Authorization", "credential headers are never test inputs");
        tests[1].Template.Assertions.Single().Expected.Should().Be("422", "the documented client error is expected");

        var values = new Dictionary<string, TestDataTypedEntry>
        {
            ["code"] = new("code", TestDataPrimitive.String, "a b/c"), ["name"] = new("name", TestDataPrimitive.String, "Testbarn"), ["age"] = new("age", TestDataPrimitive.Int32, "7"),
        };
        var (call, missing) = ContractTestRules.Materialize(tests[0], values);
        missing.Should().BeEmpty();
        call!.Path.Should().Be("/schools/a%20b%2Fc/children");
        call.Query.Should().BeEmpty("the optional tag has no value");
        JsonNode.Parse(call.Body!)!.ToJsonString().Should().Be("""{"name":"Testbarn","age":7}""");

        var (none, gaps) = ContractTestRules.Materialize(tests[0], new Dictionary<string, TestDataTypedEntry> { ["code"] = values["code"] });
        none.Should().BeNull("a required value is never replaced by a default");
        gaps.Should().BeEquivalentTo(["name", "age"]);
    }
}
