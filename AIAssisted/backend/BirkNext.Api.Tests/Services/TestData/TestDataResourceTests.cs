using System.Text.Json.Nodes;
using BirkNext.Api.Services.TestData;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>Added data: exact types, structured payloads, contract checks, references, generators, bindings, persistence and versions. Synthetic values only.</summary>
public sealed class TestDataResourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-resources-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static TestDataResourceDraft Single(string alias, TestDataPrimitive type, string? value, List<string>? enumValues = null) =>
        new() { Alias = alias, Content = new() { Kind = TestDataResourceKind.SingleValue, Value = new("", type, value) { EnumValues = enumValues } } };

    private static bool Fails(TestDataResourceDraft draft, string tier, TestDataValidationContext? context = null) =>
        TestDataResourceRules.Validate(draft, context).Checks.Any(c => c.Tier == tier && c.State == TestDataCheckState.Failed);

    // ── Primitive types ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TestDataPrimitive.Int32, "42", true)]
    [InlineData(TestDataPrimitive.Int32, "4.2", false)]
    [InlineData(TestDataPrimitive.Int32, "2147483648", false)]
    [InlineData(TestDataPrimitive.Int64, "2147483648", true)]
    [InlineData(TestDataPrimitive.Decimal, "1234.50", true)]
    [InlineData(TestDataPrimitive.Decimal, "1234,50", false)]
    [InlineData(TestDataPrimitive.Decimal, "1,234.50", false)]
    [InlineData(TestDataPrimitive.Double, "1.5e3", true)]
    [InlineData(TestDataPrimitive.Boolean, "false", true)]
    [InlineData(TestDataPrimitive.Boolean, "False", false)]
    [InlineData(TestDataPrimitive.Boolean, "0", false)]
    [InlineData(TestDataPrimitive.Uuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
    [InlineData(TestDataPrimitive.Uuid, "not-a-uuid", false)]
    [InlineData(TestDataPrimitive.Date, "2026-11-07", true)]
    [InlineData(TestDataPrimitive.Date, "07.11.2026", false)]
    [InlineData(TestDataPrimitive.Time, "13:45", true)]
    [InlineData(TestDataPrimitive.DateTime, "2026-11-07T13:45:00Z", true)]
    [InlineData(TestDataPrimitive.DateTime, "2026-11-07T13:45:00", false)]
    [InlineData(TestDataPrimitive.Duration, "PT15M", true)]
    [InlineData(TestDataPrimitive.Duration, "15 minutes", false)]
    [InlineData(TestDataPrimitive.String, "", true)]
    public void Primitive_values_are_parsed_exactly_with_invariant_formats(TestDataPrimitive type, string value, bool valid) =>
        Fails(Single("v", type, value), TestDataCheckTiers.Type).Should().Be(!valid);

    [Fact]
    public void Explicit_null_is_not_an_empty_string_and_booleans_are_not_missing()
    {
        Fails(Single("n", TestDataPrimitive.Null, null), TestDataCheckTiers.Type).Should().BeFalse();
        Fails(Single("n", TestDataPrimitive.Null, ""), TestDataCheckTiers.Type).Should().BeTrue("an empty string is a value, not null");
        Fails(Single("b", TestDataPrimitive.Boolean, null), TestDataCheckTiers.Type).Should().BeTrue("a missing boolean is not false");
        TestDataResourceRules.Payload(Single("n", TestDataPrimitive.Null, null).Content).Should().Be("null");
        TestDataResourceRules.Payload(Single("s", TestDataPrimitive.String, "").Content).Should().Be("\"\"");
        TestDataResourceRules.Payload(Single("b", TestDataPrimitive.Boolean, "false").Content).Should().Be("false");
    }

    [Fact]
    public void Enum_values_are_constrained_and_typed_payloads_keep_numbers_and_decimal_precision()
    {
        Fails(Single("e", TestDataPrimitive.Enum, "VGS", ["GRUNNSKOLE", "VGS"]), TestDataCheckTiers.Type).Should().BeFalse();
        Fails(Single("e", TestDataPrimitive.Enum, "UNI", ["GRUNNSKOLE", "VGS"]), TestDataCheckTiers.Type).Should().BeTrue();
        var kv = new TestDataResourceContent
        {
            Kind = TestDataResourceKind.KeyValue,
            Entries = [new("age", TestDataPrimitive.Int32, "7"), new("amount", TestDataPrimitive.Decimal, "1234.50"), new("active", TestDataPrimitive.Boolean, "false"),
                new("note", TestDataPrimitive.Null, null), new("id", TestDataPrimitive.String, "7")],
        };
        TestDataResourceRules.Payload(kv).Should().Be("""{"age":7,"amount":1234.50,"active":false,"note":null,"id":"7"}""");
    }

    [Fact]
    public void Key_value_collections_refuse_duplicate_keys()
    {
        var draft = new TestDataResourceDraft { Alias = "kv", Content = new() { Kind = TestDataResourceKind.KeyValue, Entries = [new("a", TestDataPrimitive.Int32, "1"), new("a", TestDataPrimitive.Int32, "2")] } };
        TestDataResourceRules.Validate(draft).Checks.Should().Contain(c => c.State == TestDataCheckState.Failed && c.Message.Contains("Duplicate key"));
    }

    // ── Structured ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_is_parsed_with_location_depth_limit_and_kept_typed()
    {
        TestDataResourceDraft J(string json) => new() { Alias = "j", Content = new() { Kind = TestDataResourceKind.Json, Json = json } };
        var bad = TestDataResourceRules.Validate(J("{\n  \"a\": 1,\n  \"b\": \n}"));
        bad.Checks.Should().Contain(c => c.Tier == TestDataCheckTiers.Syntax && c.State == TestDataCheckState.Failed && c.Message.Contains("line 4"));
        var nested = J("""{"reporter":{"name":"Kari Testesen","children":[{"age":7,"active":true},{"age":9,"active":false}],"note":null}}""");
        TestDataResourceRules.Validate(nested).Blocking.Should().BeFalse();
        TestDataResourceRules.Payload(nested.Content).Should().Contain("\"age\":7").And.Contain("\"active\":false").And.Contain("\"note\":null");
        TestDataResourceRules.Validate(J(new string('[', 40) + new string(']', 40))).Blocking.Should().BeTrue("nesting deeper than the limit is refused");
        TestDataResourceRules.Validate(nested).Tier(TestDataCheckTiers.Contract).Should().Be(TestDataCheckState.NotAssessed, "no JSON Schema: never claimed valid");
    }

    [Fact]
    public void Xml_bodies_refuse_dtds_and_external_entities()
    {
        TestDataResourceRules.XmlError("<a xmlns=\"urn:test\"><b>1</b></a>").Should().BeNull();
        TestDataResourceRules.XmlError("<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>").Should().NotBeNull();
        TestDataResourceRules.XmlError("<a><b></a>").Should().Contain("line 1");
    }

    // ── GraphQL ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ContractOperation RegisterChild = new("mutation RegisterChild", "mutation", null, ["$input", "$kinds", "$dryRun"])
    {
        Detailed = true,
        ParameterDetails = [new("$input", "argument", true, "ChildInput!"), new("$kinds", "argument", false, "[SchoolKind!]"), new("$dryRun", "argument", false, "Boolean")],
    };
    private static readonly List<ContractTypeShape> Types =
    [
        new("ChildInput", "input", [new("name", "String!", true), new("age", "Int!", true), new("reporter", "ReporterInput", false)]),
        new("ReporterInput", "input", [new("id", "ID!", true)]),
        new("SchoolKind", "enum", []) { Values = ["GRUNNSKOLE", "VGS"] },
    ];

    private static TestDataResourceDraft Variables(string json) =>
        new() { Alias = "vars", Content = new() { Kind = TestDataResourceKind.GraphQlVariables, Json = json, Operation = new(null, "graphqloperations:Ops.graphql", "mutation RegisterChild") } };

    [Fact]
    public void GraphQl_variables_are_checked_against_declared_variables_input_objects_enums_and_lists()
    {
        var context = new TestDataValidationContext { Operation = RegisterChild, Types = Types };
        TestDataResourceRules.Validate(Variables("""{"input":{"name":"Kari","age":7,"reporter":{"id":"r-1"}},"kinds":["VGS"]}"""), context).Blocking.Should().BeFalse();
        var missing = TestDataResourceRules.Validate(Variables("""{"kinds":["VGS"]}"""), context);
        missing.Checks.Should().Contain(c => c.Message.Contains("Missing required variable $input"));
        var nested = TestDataResourceRules.Validate(Variables("""{"input":{"name":"Kari","age":"7","reporter":{}}}"""), context);
        nested.Checks.Select(c => c.Message).Should().Contain(m => m.Contains("$input.age must be an Int")).And.Contain(m => m.Contains("$input.reporter.id is required"));
        var enumList = TestDataResourceRules.Validate(Variables("""{"input":{"name":"Kari","age":7},"kinds":"VGS"}"""), context);
        enumList.Checks.Should().Contain(c => c.Message.Contains("must be a list"));
        TestDataResourceRules.Validate(Variables("""{"input":{"name":"Kari","age":7},"kinds":["UNI"]}"""), context).Checks.Should().Contain(c => c.Message.Contains("must be one of SchoolKind"));
        TestDataResourceRules.Validate(Variables("""{"input":{"name":null,"age":7}}"""), context).Checks.Should().Contain(c => c.Message.Contains("is non-null"));
    }

    [Fact]
    public void Valid_json_variables_are_not_claimed_valid_without_an_operation_and_a_missing_operation_fails()
    {
        var noOperation = Variables("""{"input":{}}""") with { Content = Variables("{}").Content with { Operation = null, Json = """{"input":{}}""" } };
        TestDataResourceRules.Validate(noOperation).Tier(TestDataCheckTiers.Contract).Should().Be(TestDataCheckState.NotAssessed);
        TestDataResourceRules.Validate(Variables("""{"input":{}}"""), new() { OperationMissing = true }).Checks
            .Should().Contain(c => c.Tier == TestDataCheckTiers.Contract && c.State == TestDataCheckState.Failed && c.Message.Contains("Contract missing"));
    }

    // ── REST ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TestDataResourceDraft Rest(params TestDataRequestParameter[] parameters) => new()
    {
        Alias = "get-child",
        Content = new() { Kind = TestDataResourceKind.RestRequest, Method = "GET", Path = "/children/{id}", Parameters = [.. parameters], UseTargetAuthentication = true },
    };

    [Fact]
    public void Rest_templates_refuse_credentials_and_unfilled_path_parameters()
    {
        TestDataResourceRules.Validate(Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.Uuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301"))).Blocking.Should().BeFalse();
        TestDataResourceRules.Validate(Rest()).Checks.Should().Contain(c => c.Message.Contains("Path parameter {id} has no value"));
        Fails(Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.String, "x"), new TestDataRequestParameter("Authorization", "header", TestDataPrimitive.String, "Bearer abcdefghijkl")), TestDataCheckTiers.Sensitive).Should().BeTrue();
        Fails(Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.String, "x"), new TestDataRequestParameter("Cookie", "cookie", TestDataPrimitive.String, "s=1")), TestDataCheckTiers.Sensitive).Should().BeTrue();
    }

    [Fact]
    public void Rest_contract_checks_report_missing_required_and_type_mismatches()
    {
        var op = new ContractOperation("GET /children/{id}", "GET", "/children/{id}", ["id", "limit"])
        { Detailed = true, ParameterDetails = [new("id", "path", true, "string:uuid"), new("limit", "query", true, "integer")] };
        var context = new TestDataValidationContext { Operation = op };
        var draft = Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.String, "abc")) with { Content = Rest().Content with { Operation = new(null, "openapi:x", op.Name), Parameters = [new("id", "path", TestDataPrimitive.String, "abc")] } };
        var result = TestDataResourceRules.Validate(draft, context);
        result.Checks.Select(c => c.Message).Should().Contain(m => m.Contains("Missing required query parameter \"limit\"")).And.Contain(m => m.Contains("expected UUID"));
    }

    // ── Sensitive data ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Credentials_tokens_and_identity_numbers_are_refused_everywhere()
    {
        Fails(new() { Alias = "kv", Content = new() { Kind = TestDataResourceKind.KeyValue, Entries = [new("password", TestDataPrimitive.String, "hunter2")] } }, TestDataCheckTiers.Sensitive).Should().BeTrue();
        Fails(new() { Alias = "j", Content = new() { Kind = TestDataResourceKind.Json, Json = """{"auth":{"value":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc"}}""" } }, TestDataCheckTiers.Sensitive).Should().BeTrue();
        Fails(Single("id", TestDataPrimitive.String, "01017012345"), TestDataCheckTiers.Sensitive).Should().BeTrue("an 11-digit literal may be a national identity number");
        Fails(Single("ok", TestDataPrimitive.String, "Kari Testesen"), TestDataCheckTiers.Sensitive).Should().BeFalse();
    }

    // ── Datasets, generators, bindings ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dataset_references_must_resolve_and_cycles_are_reported()
    {
        TestDataResourceDraft D(string alias, params TestDataDatasetMember[] members) => new() { Alias = alias, Content = new() { Kind = TestDataResourceKind.Dataset, Members = [.. members] } };
        var context = new TestDataValidationContext { KnownAliases = ["child-age", "set-b"], DatasetReferences = new Dictionary<string, IReadOnlyList<string>> { ["set-b"] = ["set-a"] } };
        TestDataResourceRules.Validate(D("set-a", new TestDataDatasetMember("age", "child-age", null), new TestDataDatasetMember("active", null, new("active", TestDataPrimitive.Boolean, "true"))), context).Blocking.Should().BeFalse();
        TestDataResourceRules.Validate(D("set-a", new TestDataDatasetMember("x", "missing-alias", null)), context).Checks.Should().Contain(c => c.Message.Contains("does not exist"));
        TestDataResourceRules.Validate(D("set-a", new TestDataDatasetMember("x", "set-b", null)), context).Checks.Should().Contain(c => c.Message.Contains("cycle"));
        TestDataResourceRules.Validate(D("set-a", new TestDataDatasetMember("x", "set-a", null)), context).Checks.Should().Contain(c => c.Message.Contains("itself"));
    }

    [Fact]
    public void Generators_are_deterministic_for_a_seed_and_never_produce_identity_numbers()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        var g = new TestDataGeneratorDefinition(TestDataGeneratorKind.IntegerRange, 42) { Min = 1, Max = 100 };
        Enumerable.Range(0, 5).Select(i => TestDataGenerators.Materialize(g, i, now)).Should().Equal(Enumerable.Range(0, 5).Select(i => TestDataGenerators.Materialize(g, i, now)));
        Enumerable.Range(0, 50).Select(i => int.Parse(TestDataGenerators.Materialize(g, i, now))).Should().OnlyContain(v => v >= 1 && v <= 100);
        TestDataGenerators.Materialize(new(TestDataGeneratorKind.Sequence) { Start = 10, Step = 5 }, 3, now).Should().Be("25");
        TestDataGenerators.Materialize(new(TestDataGeneratorKind.DateOffset) { OffsetDays = -1 }, 0, now).Should().Be("2026-11-06");
        Guid.TryParseExact(TestDataGenerators.Materialize(new(TestDataGeneratorKind.Uuid, 7), 0, now), "D", out _).Should().BeTrue();
        TestDataResourceDraft Gen(TestDataGeneratorDefinition d) => new() { Alias = "g", Content = new() { Kind = TestDataResourceKind.Generated, Generator = d } };
        TestDataResourceRules.Validate(Gen(new(TestDataGeneratorKind.FakeName))).Checks.Should().Contain(c => c.Message.Contains("fixed seed"));
        TestDataResourceRules.Validate(Gen(new(TestDataGeneratorKind.Pattern, 1) { Pattern = "###########" })).Blocking.Should().BeTrue();
    }

    [Fact]
    public void Bindings_follow_precedence_never_default_missing_values_detect_cycles_and_mask_sensitive_values()
    {
        var scopes = new List<TestDataBindings.Scope>
        {
            new("environment", new Dictionary<string, string> { ["baseUrl"] = "http://localhost:5555", ["apiKey"] = "k-123", ["id"] = "env" }, new HashSet<string> { "apiKey" }),
            new("dataset", new Dictionary<string, string> { ["id"] = "dataset", ["a"] = "{{dataset.b}}", ["b"] = "{{dataset.a}}" }),
            new("step", new Dictionary<string, string> { ["id"] = "step" }),
        };
        TestDataBindings.Resolve("{{id}}", scopes).Resolved.Should().Be("step", "step outranks dataset and environment");
        TestDataBindings.Resolve("{{environment.id}}", scopes).Resolved.Should().Be("env");
        var missing = TestDataBindings.Resolve("{{dataset.childId}}", scopes);
        missing.Resolved.Should().BeNull(); missing.Unresolved.Should().Equal("{{dataset.childId}}"); missing.Preview.Should().Contain("{{dataset.childId}}");
        TestDataBindings.Resolve("{{dataset.a}}", scopes).Cycles.Should().NotBeEmpty();
        var masked = TestDataBindings.Resolve("{{environment.baseUrl}}?k={{environment.apiKey}}", scopes);
        masked.Preview.Should().Be("http://localhost:5555?k=••••••"); masked.Resolved.Should().EndWith("k-123");
    }

    // ── Persistence, versions, approval, readiness ───────────────────────────────────────────────────────────────────────

    private (AuthoredTestDataStore Store, TestDataCatalog Catalog, TestDataRegistryStore Registry, TestDataResourceService Service) Stack()
    {
        var store = new AuthoredTestDataStore(_dir);
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AuthoredTestDataProvider(store)], null, registry);
        return (store, catalog, registry, new TestDataResourceService(store, catalog));
    }

    [Fact]
    public async Task Save_is_a_draft_survives_reload_and_edits_append_versions_without_changing_history()
    {
        var (store, catalog, _, service) = Stack();
        var created = await service.CreateAsync(Single("child-age", TestDataPrimitive.Int32, "7"), default);
        created.Saved.Should().BeTrue();
        catalog.Assets().Single().Should().Match<TestDataAsset>(a => a.Approval == TestDataApproval.Unapproved && a.Kind == TestDataKind.Scalar && a.ValueType == TestDataValueType.Integer);

        var reloaded = new TestDataResourceService(new AuthoredTestDataStore(_dir), catalog);
        reloaded.Get("child-age")!.Content.Value!.Value.Should().Be("7");

        var v2 = await service.UpdateAsync("child-age", Single("child-age", TestDataPrimitive.Int32, "9") with { ExpectedVersion = 1 }, default);
        v2.Saved.Should().BeTrue(); v2.Resource!.Version.Should().Be(2);
        store.Find("child-age")!.Versions[0].Content.Value!.Value.Should().Be("7", "version 1 is never rewritten");

        var stale = await service.UpdateAsync("child-age", Single("child-age", TestDataPrimitive.Int32, "11") with { ExpectedVersion = 1 }, default);
        stale.Conflict.Should().BeTrue("a concurrent edit from version 1 must not overwrite version 2");
        var otherKind = new TestDataResourceDraft { Alias = "child-age", ExpectedVersion = 2, Content = new() { Kind = TestDataResourceKind.Json, Json = "{}" } };
        (await service.UpdateAsync("child-age", otherKind, default)).Message.Should().Contain("use Duplicate", "the kind of a resource never changes under its alias");
    }

    [Fact]
    public async Task An_approved_version_stays_current_while_a_new_version_is_a_draft_and_duplicates_start_unapproved()
    {
        var (_, catalog, registry, service) = Stack();
        await service.CreateAsync(Single("flag", TestDataPrimitive.Boolean, "true"), default);
        var v1 = catalog.Assets().Single();
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = v1.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(v1), ApprovedAt = DateTimeOffset.UtcNow });
        catalog.Assets().Single().Approval.Should().Be(TestDataApproval.Approved);

        await service.UpdateAsync("flag", Single("flag", TestDataPrimitive.Boolean, "false") with { ExpectedVersion = 1 }, default);
        var current = catalog.Assets().Single();
        current.Version.Should().Be(1, "consumers keep the approved version");
        current.Approval.Should().Be(TestDataApproval.Approved);
        current.Draft!.Version.Should().Be(2);

        var copy = await service.DuplicateAsync("flag", "flag-copy", default);
        copy.Saved.Should().BeTrue();
        catalog.Assets().Single(a => a.Alias == "flag-copy").Approval.Should().Be(TestDataApproval.Unapproved, "approval is never copied");
        (await service.CreateAsync(Single("flag", TestDataPrimitive.Boolean, "true"), default)).Saved.Should().BeFalse("aliases are unique across the catalog");
    }

    [Fact]
    public async Task Oversized_or_sensitive_content_is_never_written()
    {
        var (store, _, _, service) = Stack();
        var big = new TestDataResourceDraft { Alias = "big", Content = new() { Kind = TestDataResourceKind.Json, Json = "\"" + new string('x', TestDataResourceRules.MaxPayloadBytes) + "\"" } };
        (await service.CreateAsync(big, default)).Saved.Should().BeFalse();
        (await service.CreateAsync(new() { Alias = "secret", Content = new() { Kind = TestDataResourceKind.KeyValue, Entries = [new("client_secret", TestDataPrimitive.String, "abc")] } }, default)).Saved.Should().BeFalse();
        store.Resources().Should().BeEmpty();
        File.Exists(Path.Combine(_dir, "authored.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Readiness_needs_an_approved_current_version_and_a_compatible_operation()
    {
        var (_, catalog, registry, service) = Stack();
        await service.CreateAsync(Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.Uuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301")), default);
        var unapproved = await service.ReadinessAsync("get-child", new(null, "openapi:x", "GET /children/{id}"), default);
        unapproved!.Ready.Should().BeFalse();
        unapproved.Blockers.Should().Contain(b => b.Contains("Not approved")).And.Contain(b => b.Contains("Contract missing"),
            "without source evidence the operation cannot be found: the contract is missing, never assumed");
        var asset = catalog.Assets().Single();
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = asset.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(asset), ApprovedAt = DateTimeOffset.UtcNow });
        (await service.ReadinessAsync("get-child", new(null, "openapi:x", "GET /children/{id}"), default))!.Blockers.Should().NotContain(b => b.Contains("Not approved"));
    }

    private sealed class Sources : BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider
    {
        public BirkNext.Integrations.IqrSourceSnapshot Snapshot { get; set; } = null!;
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<BirkNext.Integrations.IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BirkNext.Integrations.IqrSourceSnapshot>>([Snapshot]);
        public Task<BirkNext.Integrations.IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult<BirkNext.Integrations.IqrSourceSnapshot?>(Snapshot.Id == snapshotId ? Snapshot : null);
    }

    private static BirkNext.Integrations.IqrSourceSnapshot Snapshot(params ContractParameter[] parameters) => new()
    {
        IntegrationId = "source-analysis", Archive = new BirkNext.Integrations.SourceArchive("Synthetic.zip", new string('c', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        Status = BirkNext.Integrations.SourceAnalysisStatus.Ready, Architecture = new BirkNext.SourceArchitecture.ArchitectureSnapshot(),
        EvidenceDomains = new SourceEvidenceDomainsSnapshot
        {
            Contracts = new ContractEvidence
            {
                Domain = SourceEvidenceDomain.Contracts,
                Contracts = [new SourceContract { Id = "openapi:api.yaml", Name = "api", Type = SourceContractType.OpenApi,
                    Operations = [new("GET /children/{id}", "GET", "/children/{id}", [.. parameters.Select(p => p.Name)]) { Detailed = true, ParameterDetails = [.. parameters] }] }],
            },
        },
    };

    [Fact]
    public async Task Readiness_checks_the_approved_version_against_the_current_contract_and_a_changed_contract_blocks_it()
    {
        var store = new AuthoredTestDataStore(_dir);
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AuthoredTestDataProvider(store)], null, registry);
        var sources = new Sources { Snapshot = Snapshot(new ContractParameter("id", "path", true, "string:uuid")) };
        var service = new TestDataResourceService(store, catalog, sources);
        var operation = new TestDataOperationRef(null, "openapi:api.yaml", "GET /children/{id}");
        var draft = Rest(new TestDataRequestParameter("id", "path", TestDataPrimitive.Uuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301")) with { Content = Rest().Content with
            { Operation = operation, Parameters = [new("id", "path", TestDataPrimitive.Uuid, "3f2504e0-4f89-11d3-9a0c-0305e82c3301")] } };
        (await service.CreateAsync(draft, default)).Saved.Should().BeTrue();
        var v1 = catalog.Assets().Single();
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = v1.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(v1), ApprovedAt = DateTimeOffset.UtcNow });
        (await service.ReadinessAsync("get-child", operation, default))!.Ready.Should().BeTrue();

        // A newer draft does not take the approved version's place.
        var v2 = draft with { ExpectedVersion = 1, Content = draft.Content with { Parameters = [new("id", "path", TestDataPrimitive.Uuid, "9b2f6c1e-1d7a-4c55-8f3e-2a6b0c9d4e71")] } };
        (await service.UpdateAsync("get-child", v2, default)).Saved.Should().BeTrue();
        var stillV1 = await service.ReadinessAsync("get-child", operation, default);
        stillV1!.Version.Should().Be(1); stillV1.Ready.Should().BeTrue();
        catalog.Assets().Single().SourceId.Should().Be(AuthoredTestDataProvider.ProviderId, "the approved older version keeps its provider identity");

        // The contract changed after the data was approved: a new required query parameter blocks readiness.
        sources.Snapshot = Snapshot(new ContractParameter("id", "path", true, "string:uuid"), new ContractParameter("limit", "query", true, "integer"));
        var changed = await service.ReadinessAsync("get-child", operation, default);
        changed!.Ready.Should().BeFalse();
        changed.Blockers.Should().Contain(b => b.Contains("Missing required query parameter \"limit\""));
    }

    [Fact]
    public void Authored_assets_are_deletable_local_data_and_the_editor_removes_them()
    {
        var store = new AuthoredTestDataStore(_dir);
        store.Create(Single("tmp", TestDataPrimitive.String, "x"));
        TestDataOwnerships.Deletable(TestDataOwnership.Authored).Should().BeTrue();
        new AuthoredTestDataEditor(store).Remove(AuthoredTestDataProvider.Asset(store.Find("tmp")!)).Should().BeTrue();
        store.Resources().Should().BeEmpty();
    }
}
