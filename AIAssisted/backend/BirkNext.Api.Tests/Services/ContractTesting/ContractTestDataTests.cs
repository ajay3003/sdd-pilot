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
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// Automatic contract test data: typed synthetic values from the stored contract (allOf, $ref, oneOf, const, enum, bounds, lengths, patterns,
/// arrays, readOnly; GraphQL types), validated by the contract validator, kept in Test Data, approved and pinned only when certain, never
/// overwriting a tester's data, version-pinned across contract versions — and the values that reach a loopback target are exactly the dataset's,
/// with their JSON types (the target's own ledger is the oracle, not the run summary).
/// </summary>
public sealed class ContractTestDataTests : IAsyncLifetime
{
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<(string Method, string PathAndQuery, string Body)> _seen = new();
    private ServiceProvider _services = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-ctd-" + Guid.NewGuid().ToString("N"));
    private AuthoredTestDataStore _authored = null!;
    private TestDataRegistryStore _registry = null!;
    private TestDataCatalog _catalog = null!;

    public const string Orders = """
        { "openapi": "3.0.3", "info": { "title": "Synthetic Order API", "version": "1" },
          "paths": {
            "/orders": { "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewOrder" } } } },
                "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "type": "object", "required": ["id"], "properties": { "id": { "type": "string" } } } } } }, "400": { "description": "bad" } } } },
            "/orders/{orderId}": { "get": { "parameters": [ { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } } ],
                "responses": { "200": { "description": "ok" } } } },
            "/codes": { "get": { "parameters": [ { "name": "code", "in": "query", "required": true, "schema": { "type": "string", "pattern": "^[A-Z]{2}-[0-9]{3}$" } } ],
                "responses": { "200": { "description": "ok" } } } } },
          "components": { "schemas": {
            "NewOrder": { "allOf": [ { "$ref": "#/components/schemas/Base" }, { "type": "object", "required": ["quantity", "lines", "delivery"], "properties": {
                "quantity": { "type": "integer", "minimum": 1, "maximum": 10, "exclusiveMinimum": true },
                "price": { "type": "number", "minimum": 0.5 },
                "lines": { "type": "array", "minItems": 2, "items": { "$ref": "#/components/schemas/Line" } },
                "delivery": { "oneOf": [ { "$ref": "#/components/schemas/Pickup" }, { "$ref": "#/components/schemas/Shipping" } ] },
                "status": { "type": "string", "enum": ["NEW", "OPEN"] },
                "contact": { "type": "string", "format": "email" },
                "createdAt": { "type": "string", "format": "date-time", "readOnly": true },
                "fodselsnummer": { "type": "string" },
                "apiToken": { "type": "string" } } } ] },
            "Base": { "type": "object", "required": ["kind", "reference"], "properties": { "kind": { "type": "string", "const": "order" },
                "reference": { "type": "string", "minLength": 12, "maxLength": 20 } } },
            "Line": { "type": "object", "required": ["sku", "count"], "properties": { "sku": { "type": "string", "pattern": "^\\d{6}$" }, "count": { "type": "integer", "minimum": 1 } } },
            "Pickup": { "type": "object", "required": ["mode", "date"], "properties": { "mode": { "type": "string", "enum": ["pickup"] }, "date": { "type": "string", "format": "date" } }, "additionalProperties": false },
            "Shipping": { "type": "object", "required": ["mode", "address"], "properties": { "mode": { "type": "string", "enum": ["shipping"] }, "address": { "type": "string" } }, "additionalProperties": false } } } }
        """;

    private static readonly string OrdersV2 = Orders.Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal)
        .Replace("\"required\": [\"quantity\", \"lines\", \"delivery\"]", "\"required\": [\"quantity\", \"lines\", \"delivery\", \"status\"]", StringComparison.Ordinal);

    public const string Children = """
        type Child { id: ID! name: String! age: Int }
        input NewChildInput { name: String! age: Int! nickname: String }
        type Query { child(id: ID!): Child }
        type Mutation { createChild(input: NewChildInput!): Child! }
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
        var db = $"ctd-{Guid.NewGuid():N}";
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
        services.AddSingleton(_registry);
        services.AddScoped<TestDataResourceService>();
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        services.AddScoped<ContractTestDataService>();
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
        string body;
        using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        _seen.Enqueue((c.Request.HttpMethod, c.Request.Url!.PathAndQuery, body));
        var (status, text) = c.Request.Url.AbsolutePath switch
        {
            "/orders" => (201, """{"id":"1"}"""),
            "/graphql" => (200, """{"data":{"createChild":{"id":"8","name":"x","age":3}}}"""),
            _ => (404, """{"error":"no route"}"""),
        };
        c.Response.StatusCode = status; c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(text);
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    private T Scoped<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
    private ContractTestEnvironment Local => new() { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };

    private async Task<(string Key, ContractTestDataReport Report)> ImportAndGenerate(string fileName, string content, string? key = null)
    {
        using var scope = _services.CreateScope();
        var imported = await Scoped<ContractRegistryService>(scope).ImportAsync(new() { ContractKey = key, FileName = fileName, Content = content }, default);
        imported.Imported.Should().BeTrue(imported.Message);
        await Scoped<ContractTestService>(scope).GenerateAsync(imported.Key!, imported.Version, null, default);
        var report = await Scoped<ContractTestDataService>(scope).GenerateAsync(imported.Key!, new(imported.Version), default);
        return (imported.Key!, report!);
    }

    private async Task<ExecutionRunDetail> RunAsync(Guid testId)
    {
        using var scope = _services.CreateScope();
        var run = await Scoped<ContractTestService>(scope).RunAsync(new() { TestIds = [testId], Environment = Local, ConfirmMutatingCalls = true }, default);
        run.Run.Should().NotBeNull(string.Join("; ", run.NotRun));
        for (var i = 0; i < 600 && ExecutionRunService.IsRunning(run.Run!.RunId!.Value); i++) await Task.Delay(50);
        return (await Scoped<ExecutionEvidenceStore>(scope).GetAsync(run.Run!.RunId!.Value, new(Take: 50), default))!;
    }

    // ── Rules ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_body_follows_allOf_ref_oneOf_const_enum_bounds_lengths_patterns_and_arrays_and_validates_against_the_schema()
    {
        var root = OpenApiSchemaValidator.Parse(Orders)!;
        var raw = new OpenApiSchemaValidator(root);
        var doc = ContractAnalyzerFacade(Orders);
        var op = doc.Operations.Single(o => o.Name == "POST /orders");
        var test = ContractTestRules.GenerateAny(doc, "orders", 1, op, DateTimeOffset.UnixEpoch).Single(t => t.Kind == ContractTestKind.Positive);
        var generated = ContractTestDataRules.Generate(doc, raw, test, op, 42);
        var body = generated.Single();
        body.Meta.Should().Match<ContractGeneratedValue>(m => m.Certainty == GeneratedValueCertainty.Certain && m.Type == TestDataPrimitive.Json);
        var json = JsonNode.Parse(body.Entry!.Value!)!.AsObject();
        json["kind"]!.GetValue<string>().Should().Be("order", "const");
        json["reference"]!.GetValue<string>().Length.Should().BeInRange(12, 20);
        json["quantity"]!.GetValue<int>().Should().Be(2, "exclusive minimum 1 → lowest valid integer");
        json["price"]!.GetValue<decimal>().Should().Be(0.5m);
        json["lines"]!.AsArray().Should().HaveCount(2).And.OnlyContain(l => System.Text.RegularExpressions.Regex.IsMatch(l!["sku"]!.GetValue<string>(), @"^\d{6}$"));
        json["delivery"]!["mode"]!.GetValue<string>().Should().Be("pickup", "first oneOf alternative");
        json["status"]!.GetValue<string>().Should().Be("NEW");
        json["contact"]!.GetValue<string>().Should().EndWith("@example.test");
        json.ContainsKey("createdAt").Should().BeFalse("readOnly is never sent");
        json.ContainsKey("fodselsnummer").Should().BeFalse("an identity is never generated");
        json.ContainsKey("apiToken").Should().BeFalse("a credential is never generated");

        // Independent check with the validator over the raw schema (not the generator's opinion).
        var unsupported = new HashSet<string>();
        raw.Validate(json, raw.RequestSchema("POST", "/orders", "application/json").Schema!, "#", ValidationDirection.Request, unsupported).Should().BeEmpty();

        ContractTestDataRules.Generate(doc, raw, test, op, 42).Single().Entry!.Value.Should().Be(body.Entry.Value, "seeded: deterministic");
        ContractTestDataRules.Generate(doc, raw, test, op, 43).Single().Entry!.Value.Should().NotBe(body.Entry.Value);
    }

    [Theory]
    [InlineData("fodselsnummer", GeneratedValueCertainty.Unresolved)]
    [InlineData("dNummer", GeneratedValueCertainty.Unresolved)]
    [InlineData("client_secret", GeneratedValueCertainty.Unresolved)]
    [InlineData("adressebeskyttelse", GeneratedValueCertainty.Unresolved)]
    [InlineData("barnId", GeneratedValueCertainty.NeedsReview)]
    [InlineData("correlationId", GeneratedValueCertainty.Certain)]
    [InlineData("comment", GeneratedValueCertainty.Certain)]
    public void Sensitive_fields_are_never_generated_and_record_identifiers_need_review(string name, GeneratedValueCertainty expected)
    {
        var doc = new BirkNext.SourceDomains.SourceContract
        {
            Type = BirkNext.SourceDomains.SourceContractType.OpenApi,
            Operations = [new("GET /x", "get", "/x", [name]) { ParameterDetails = [new(name, "query", true, "string")] }],
        };
        var op = doc.Operations[0];
        var test = ContractTestRules.GenerateAny(doc, "x", 1, op, DateTimeOffset.UnixEpoch).Single(t => t.Kind == ContractTestKind.Positive);
        var value = ContractTestDataRules.Generate(doc, null, test, op, 1).Single();
        value.Meta.Certainty.Should().Be(expected, value.Meta.Reason);
        if (expected == GeneratedValueCertainty.Unresolved) value.Entry.Should().BeNull();
    }

    [Fact]
    public void A_pattern_that_only_admits_an_identity_shaped_value_is_never_satisfied_by_the_generator()
    {
        // Found on the real M2LB archive (Person: a field with pattern ^\\d{11}$): Test Data refused the generated set as a possible national id.
        var doc = new BirkNext.SourceDomains.SourceContract { Type = BirkNext.SourceDomains.SourceContractType.OpenApi };
        const string text = """
            { "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "paths": { "/p": { "get": {
              "parameters": [ { "name": "ident", "in": "query", "required": true, "schema": { "type": "string", "pattern": "^\\d{11}$" } },
                              { "name": "sessionId", "in": "query", "required": true, "schema": { "type": "string" } } ],
              "responses": { "200": { "description": "ok" } } } } } }
            """;
        var parsed = BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.ReadOpenApiDocument("t.json", text)!;
        var raw = new OpenApiSchemaValidator(OpenApiSchemaValidator.Parse(text)!);
        var op = parsed.Operations.Single();
        var test = ContractTestRules.GenerateAny(parsed, "t", 1, op, DateTimeOffset.UnixEpoch).Single(t => t.Kind == ContractTestKind.Positive);
        var values = ContractTestDataRules.Generate(parsed, raw, test, op, 1);
        values.Single(v => v.Meta.Name == "ident").Should().Match<ContractTestDataRules.Generated>(v => v.Entry == null && v.Meta.Certainty == GeneratedValueCertainty.Unresolved);
        values.Single(v => v.Meta.Name == "sessionId").Entry.Should().BeNull("a credential-like name is never generated (aligned with Test Data's rule)");
        _ = doc;
    }

    // ── Service: persist, validate, approve, bind, execute ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Certain_data_is_validated_approved_and_bound_and_the_target_receives_exactly_the_dataset_with_its_types()
    {
        var (key, report) = await ImportAndGenerate("orders.json", Orders);
        var post = report.Operations.Single(o => o.Operation == "POST /orders");
        post.Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.Bound && r.Approved && r.Created && r.Validation == "Valid" && r.BoundTests.Count >= 1);
        post.Dataset.Should().Be("gen-orders-v1-post-orders@v1".Replace("orders-v1", $"{key}-v1"));

        var byId = report.Operations.Single(o => o.Operation == "GET /orders/{orderId}");
        byId.Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.NeedsReview && !r.Approved && r.BoundTests.Count == 0 && r.Summary.Contains("orderId"));
        byId.Values.Single().Reason.Should().Contain("existing record");
        var code = report.Operations.Single(o => o.Operation == "GET /codes");
        code.State.Should().Be(ContractTestDataState.NeedsReview, "the pattern is not satisfiable by the generator: a human enters a value");
        code.Values.Single().Reason.Should().Contain("pattern");

        using var scope = _services.CreateScope();
        var tests = await Scoped<ContractTestService>(scope).ListAsync(key, Local, default);
        var positive = tests.Single(t => t.Test.Operation == "POST /orders" && t.Test.Kind == ContractTestKind.Positive);
        positive.Test.Binding.Should().NotBeNull();
        positive.Readiness.Items.Single(i => i.Area == "Test Data").State.Should().Be(ReadinessItemState.Ok);

        _seen.Clear();
        var detail = await RunAsync(positive.Test.Id);
        var sent = _seen.Single(s => s.Method == "POST");
        var dataset = _authored.Find(post.Dataset!.Split('@')[0])!.Current.Content.Entries.Single(e => e.Name == "body");
        JsonNode.DeepEquals(JsonNode.Parse(sent.Body), JsonNode.Parse(dataset.Value!)).Should().BeTrue("the target received exactly the dataset value");
        JsonNode.Parse(sent.Body)!["quantity"]!.GetValueKind().Should().Be(JsonValueKind.Number, "types are preserved");
        detail.Calls.Single().State.Should().Be(ExecutionState.Passed);

        // Without the data-changing confirmation nothing is sent, and the run says why (not a bare "blocked").
        _seen.Clear();
        var unconfirmed = await Scoped<ContractTestService>(scope).RunAsync(new() { TestIds = [positive.Test.Id], Environment = Local, ConfirmMutatingCalls = false }, default);
        unconfirmed.Run!.State.Should().Be(ExecutionState.Blocked);
        unconfirmed.Run.Message.Should().Contain("can change data").And.Contain("explicitly confirmed");
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Regeneration_is_idempotent_never_overwrites_a_testers_data_and_a_new_contract_version_gets_its_own_pinned_set()
    {
        var (key, first) = await ImportAndGenerate("orders.json", Orders);
        var assetsBefore = _catalog.Assets().Count;
        using (var scope = _services.CreateScope())
        {
            var again = (await Scoped<ContractTestDataService>(scope).GenerateAsync(key, new(1), default))!;
            again.Operations.Single(o => o.Operation == "POST /orders").State.Should().Be(ContractTestDataState.AlreadyBound);
            again.Operations.Single(o => o.Operation == "GET /orders/{orderId}").Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.NeedsReview && !r.Created);
        }
        _catalog.Assets().Count.Should().Be(assetsBefore, "no duplicate datasets");

        // A tester confirms the record identifier: a new version of the generated set, approved by the tester → reused and pinned, never overwritten.
        var alias = first.Operations.Single(o => o.Operation == "GET /orders/{orderId}").Dataset!.Split('@')[0];
        using (var scope = _services.CreateScope())
        {
            var update = await Scoped<TestDataResourceService>(scope).UpdateAsync(alias, new TestDataResourceDraft
            {
                Alias = alias, ExpectedVersion = 1, ChangeNote = "Tester: an order that exists in the synthetic target",
                Content = new() { Kind = TestDataResourceKind.KeyValue, Entries = [new("orderId", TestDataPrimitive.Uuid, "11111111-2222-3333-4444-555555555555")] },
            }, default);
            update.Saved.Should().BeTrue(update.Message);
            var asset = _catalog.Assets().Single(a => a.Alias == alias);
            _registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = asset.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(asset), ApprovedAt = DateTimeOffset.UtcNow });
            var reviewed = (await Scoped<ContractTestDataService>(scope).GenerateAsync(key, new(1), default))!.Operations.Single(o => o.Operation == "GET /orders/{orderId}");
            reviewed.Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.Bound && r.Dataset == $"{alias}@v2");
            reviewed.Values.Single().Origin.Should().Be(GeneratedValueOrigin.ReviewedUserValue);
            _authored.Find(alias)!.Current.Content.Entries.Single().Value.Should().Be("11111111-2222-3333-4444-555555555555", "the tester's value is never overwritten");
        }

        // Contract v2 (status becomes required): new tests on v2 get a new set; v1 tests stay pinned to the v1 set.
        using (var scope = _services.CreateScope())
        {
            var v2 = await Scoped<ContractRegistryService>(scope).ImportAsync(new() { ContractKey = key, FileName = "orders-v2.json", Content = OrdersV2 }, default);
            v2.Version.Should().Be(2);
            await Scoped<ContractTestService>(scope).GenerateAsync(key, 2, null, default);
            var report = (await Scoped<ContractTestDataService>(scope).GenerateAsync(key, new(2), default))!;
            report.Operations.Should().BeEmpty("existing tests move to v2 only through the reviewed update proposal; no test is silently rebound or duplicated");
            var tests = await Scoped<ContractTestService>(scope).ListAsync(key, null, default);
            tests.Where(t => t.Test.Operation == "POST /orders" && t.Test.ContractVersion == 1).Should().OnlyContain(t => t.Test.Binding!.Alias == $"gen-{key}-v1-post-orders");
        }
    }

    [Fact]
    public async Task Graphql_variables_keep_their_types_ids_need_review_and_the_target_receives_numbers_as_numbers()
    {
        var (key, report) = await ImportAndGenerate("children.graphql", Children);
        var create = report.Operations.Single(o => o.Operation == "mutation createChild");
        create.Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.Bound && r.Validation == "Valid", create.Summary);
        create.Values.Single(v => v.Name == "age").Type.Should().Be(TestDataPrimitive.Int32);
        create.Values.Single(v => v.Name == "name").Type.Should().Be(TestDataPrimitive.String);
        report.Operations.Single(o => o.Operation == "query child").Should().Match<ContractTestDataResult>(r => r.State == ContractTestDataState.NeedsReview && r.Summary.Contains("id"),
            "a GraphQL ID references an existing record");

        using var scope = _services.CreateScope();
        var positive = (await Scoped<ContractTestService>(scope).ListAsync(key, Local, default)).Single(t => t.Test.Operation == "mutation createChild" && t.Test.Kind == ContractTestKind.Positive);
        _seen.Clear();
        await RunAsync(positive.Test.Id);
        var variables = JsonNode.Parse(_seen.Single().Body)!["variables"]!["input"]!;
        variables["age"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        variables["name"]!.GetValueKind().Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Generation_sends_nothing_to_any_target()
    {
        _seen.Clear();
        await ImportAndGenerate("orders.json", Orders);
        _seen.Should().BeEmpty();
    }

    /// <summary>The contract model of a document text, as the registry parses an upload.</summary>
    private static BirkNext.SourceDomains.SourceContract ContractAnalyzerFacade(string text) =>
        BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.ReadOpenApiDocument("contract.json", text)!;
}
