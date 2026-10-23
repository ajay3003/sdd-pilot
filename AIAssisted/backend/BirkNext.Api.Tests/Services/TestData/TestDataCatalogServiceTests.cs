using System.Text.Json;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// The authoritative Test Data catalog: candidates are proposed with a reason and never approved; registration re-derives the candidate from its
/// exact snapshot and stores metadata only; approval is explicit and bound to a version; consumers are generic (any type, including one this
/// code has never heard of); matching suggests and never binds; a binding is an explicit reference to an approved, in-scope asset version; a
/// contract change or a changed asset marks the asset for review without modifying it.
/// </summary>
public sealed class TestDataCatalogServiceTests : IDisposable
{
    private const string Secret = "SECRET-VALUE-12345678901";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-catalog-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Older = Guid.Parse("7d000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Newer = Guid.Parse("7d000000-0000-0000-0000-0000000000a2");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SourceContract OpenApi(params string[] reportFields) => new()
    {
        Id = "api", Type = SourceContractType.OpenApi, Name = "Reports API", File = "api/openapi.json", Format = "json",
        Operations =
        [
            new ContractOperation("POST /reports", "operation", "/reports", []) { Detailed = true, RequestSchema = "Report", RequestContentTypes = ["application/json"] },
            new ContractOperation("GET /reports/{reportId}", "operation", "/reports/{reportId}", ["reportId"])
                { Detailed = true, ParameterDetails = [new("reportId", "path", true, "string"), new("page", "query", false, "integer")] },
        ],
        Types = [new ContractTypeShape("Report", "object", [.. reportFields.Select(f => new ContractField(f, "string", true))]), new ContractTypeShape("ReportStatus", "enum", []) { Values = ["Open", "Closed"] }],
    };

    private static SourceContract GraphQl() => new()
    {
        Id = "gql", Type = SourceContractType.GraphQlSchema, Name = "Schema", File = "schema.graphql", Format = "graphql",
        Operations =
        [
            new ContractOperation("mutation createReport", "mutation", null, ["input"]) { Detailed = true, ParameterDetails = [new("input", "argument", true, "CreateReportInput!")], ResultType = "Report" },
            new ContractOperation("query report", "query", null, ["id"]) { Detailed = true, ParameterDetails = [new("id", "argument", true, "ID!")] },
        ],
        Types = [new ContractTypeShape("CreateReportInput", "input", [new("title", "String!", true), new("fodselsnummer", "String!", true)]), new ContractTypeShape("Report", "object", [new("id", "ID!", true)])],
    };

    private static IqrSourceSnapshot Snapshot(Guid id, DateTimeOffset at, params SourceContract[] contracts) => new()
    {
        Id = id, Archive = new("receiver.zip", "sha-" + id.ToString("N")[..8], 3), AnalyzedAt = at,
        EvidenceDomains = new SourceEvidenceDomainsSnapshot { SourceSnapshotId = id, Contracts = new ContractEvidence { Domain = SourceEvidenceDomain.Contracts, AnalyzerVersion = 2, Contracts = [.. contracts] } },
    };

    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> Snapshots { get; } = [];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. Snapshots.OrderByDescending(s => s.AnalyzedAt)]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(Snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }

    private sealed class Provider : ITestDataProvider
    {
        public List<TestDataAsset> Items { get; } = [];
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "In-test configured assets.";
        public IReadOnlyList<TestDataAsset> Assets() => Items;
        public IReadOnlyList<TestDataSet> Sets() => [];
    }

    private sealed class NoSchemas : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult<TestDataSchemaPackage?>(null);
    }

    private (TestDataCatalogService Service, TestDataRegistryStore Registry, Sources Sources, Provider Configured) Create()
    {
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var sources = new Sources();
        var configured = new Provider();
        var catalog = new TestDataCatalog([configured, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry);
        var service = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        return (service, registry, sources, configured);
    }

    private static TestDataConsumer Consumer(string type, string id, string? parent = null) => new() { ConsumerType = type, ConsumerId = id, DisplayName = id, Parent = parent };

    private static TestDataAsset Configured(string alias, string value, string? environment = null, TestDataApproval approval = TestDataApproval.Unapproved) => new()
    {
        Id = $"configured:{alias}", Alias = alias, Kind = TestDataKind.Scalar, ValueType = TestDataValueType.String, Environment = environment, Approval = approval,
        Source = "Configured", Sensitivity = TestDataSensitivity.Sensitive, Value = new TestDataValue(value), Fields = ["personId"],
    };

    [Fact]
    public async Task Candidates_come_from_openapi_graphql_and_enums_each_with_a_reason_and_none_approved()
    {
        var (_, registry, sources, _) = Create();
        sources.Snapshots.Add(Snapshot(Older, DateTimeOffset.Parse("2026-10-01T08:00:00Z"), OpenApi("title", "fodselsnummer"), GraphQl()));
        var candidates = (await new TestDataCandidateService(sources, registry).CandidatesAsync("", Older, default))!;

        candidates.Should().Contain(c => c.Id == "op:api:POST /reports:body" && c.DataType == TestDataDataType.JsonPayload && c.Reason.Contains("requires Report"));
        candidates.Should().Contain(c => c.Id.EndsWith(":param:reportId") && c.DataType == TestDataDataType.SimpleValues && c.Reason.Contains("path parameter reportId"));
        candidates.Should().NotContain(c => c.Id.EndsWith(":param:page"), "an optional query parameter is not a required input");
        candidates.Should().Contain(c => c.Id.EndsWith(":param:input") && c.Label == "CreateReportInput input" && c.Reason.Contains("GraphQL"));
        candidates.Should().Contain(c => c.Id.EndsWith(":param:id") && c.Reason.Contains("variable $id"));
        candidates.Should().Contain(c => c.Id.EndsWith(":result") && c.DataType == TestDataDataType.ExpectedResult);
        candidates.Should().Contain(c => c.Id == "enum:api:ReportStatus" && c.Structure.SequenceEqual(new[] { "Open", "Closed" }));
        candidates.Should().OnlyContain(c => c.Reason.Length > 0 && c.ExistingAssetId == null && c.State == TestDataLifecycleState.Candidate);
        candidates.SelectMany(c => c.Structure).Where(s => s.Contains("fodselsnummer")).Should().OnlyContain(s => s.EndsWith(TestDataLifecycle.ControlledReference), "identities are references, never generated");
    }

    [Fact]
    public async Task Registering_a_candidate_rederives_it_from_its_exact_snapshot_stores_no_value_and_is_not_approved()
    {
        var (service, registry, sources, _) = Create();
        sources.Snapshots.Add(Snapshot(Older, DateTimeOffset.Parse("2026-10-01T08:00:00Z"), OpenApi("title", "fodselsnummer")));

        var (asset, _, error) = await service.RegisterAsync(new() { Alias = "report-body", DataType = TestDataDataType.JsonPayload, SnapshotId = Older, CandidateId = "op:api:POST /reports:body" }, default);

        error.Should().BeNull();
        asset!.Fields.Should().Contain(f => f.StartsWith("title"));
        asset.SchemaRef.Should().Be("api/openapi.json#Report");
        asset.SnapshotId.Should().Be(Older);
        var summary = (await service.ViewAsync(default)).Assets.Single(a => a.Alias == "report-body");
        summary.Status.Should().Be(TestDataAssetStatus.NeedsReview, "a reviewed candidate starts as Needs review — never approved by registration");
        summary.DataType.Should().Be(TestDataDataType.JsonPayload);
        summary.Environment.Should().BeNull("Shared");
        summary.Provenance.Should().Contain(p => p.Label == "Candidate reason");
        summary.ValueAvailable.Should().BeFalse();

        (await service.RegisterAsync(new() { Alias = "report-body-2", DataType = TestDataDataType.JsonPayload, SnapshotId = Older, CandidateId = "op:api:POST /reports:body" }, default))
            .Existing.Should().Be("registered:report-body", "the same candidate is never registered twice");
        (await service.RegisterAsync(new() { Alias = "x", SnapshotId = Guid.NewGuid(), CandidateId = "op:api:POST /reports:body" }, default)).Error.Should().Contain("unavailable");
        (await service.RegisterAsync(new() { Alias = "Not An Alias" }, default)).Error.Should().Contain("lowercase");
        ((await new TestDataCandidateService(sources, registry).CandidatesAsync("", Older, default))!).Single(c => c.Id == "op:api:POST /reports:body").ExistingAssetId.Should().Be("registered:report-body");
        File.ReadAllText(Path.Combine(_dir, "registry.json")).Should().NotContain(Secret);
    }

    [Fact]
    public async Task Manual_registration_keeps_names_only_and_drops_anything_after_an_equals_sign()
    {
        var (service, registry, _, _) = Create();
        (await service.RegisterAsync(new() { Alias = "child-ref", DataType = TestDataDataType.SimpleValues, EnvironmentId = "QA", Fields = ["childId=" + Secret, "kommune"] }, default)).Error.Should().BeNull();
        registry.Assets().Single().Fields.Should().Equal("childId", "kommune");
        File.ReadAllText(Path.Combine(_dir, "registry.json")).Should().NotContain(Secret);
        (await service.ViewAsync(default)).Assets.Single().Status.Should().Be(TestDataAssetStatus.Draft);
    }

    [Fact]
    public async Task Approval_is_explicit_and_bound_to_the_version_a_changed_asset_needs_review_and_deprecated_is_not_suggested()
    {
        var (service, _, _, configured) = Create();
        configured.Items.Add(Configured("person-id", Secret));
        (await service.ViewAsync(default)).Assets.Single().Status.Should().Be(TestDataAssetStatus.Draft);

        service.Approve("configured:person-id").Should().BeNull();
        var approved = (await service.ViewAsync(default)).Assets.Single();
        approved.Status.Should().Be(TestDataAssetStatus.Approved);
        approved.ApprovedFingerprint.Should().NotBeNull().And.NotContain(Secret);

        configured.Items[0] = Configured("person-id", Secret + "-changed");
        (await service.ViewAsync(default)).Assets.Single().Status.Should().Be(TestDataAssetStatus.NeedsReview, "approval applies to exactly the approved version");

        service.Approve("configured:person-id");
        service.Deprecate("configured:person-id").Should().BeNull();
        (await service.ViewAsync(default)).Assets.Single().Status.Should().Be(TestDataAssetStatus.Deprecated);
        var requirement = new ConsumerTestDataRequirement { Consumer = Consumer("Review", "aqr"), RequestedDataType = TestDataDataType.SimpleValues };
        (await service.MatchesAsync(requirement, false, default)).Should().BeEmpty("deprecated assets are not suggested");
        (await service.MatchesAsync(requirement, true, default)).Should().ContainSingle();
        service.Approve("configured:person-id").Should().Contain("deprecated");
    }

    [Fact]
    public async Task Consumers_are_generic_requirements_match_without_binding_and_binding_is_explicit_across_consumers()
    {
        var (service, registry, _, configured) = Create();
        configured.Items.Add(Configured("person-id", Secret));
        configured.Items.Add(Configured("qa-person", Secret, environment: "QA"));
        service.Approve("configured:person-id");
        service.Approve("configured:qa-person");

        // A consumer type this code has never seen registers a need — no consumer list, no consumer-specific logic.
        var future = Consumer("Quantum Review", "qr-7", parent: "Future module");
        var (requirement, error) = service.AddRequirement(new() { Consumer = future, Usage = "Path parameter", RequestedDataType = TestDataDataType.SimpleValues, RequestedFields = ["personId"], EnvironmentId = "TT02" });
        error.Should().BeNull();
        service.AddRequirement(new() { Consumer = future, Usage = "Path parameter", RequestedDataType = TestDataDataType.SimpleValues, RequestedFields = ["personId"], EnvironmentId = "TT02" })
            .Requirement!.Id.Should().Be(requirement!.Id, "the same need registered again is the same requirement");
        service.AddRequirement(new() { Consumer = new() { DisplayName = "nameless" } }).Error.Should().Contain("names its consumer");

        var matches = await service.MatchesAsync(requirement, false, default);
        matches.Should().Contain(m => m.Alias == "person-id" && m.State == TestDataMatchState.Compatible);
        matches.Should().Contain(m => m.Alias == "qa-person" && m.State == TestDataMatchState.Conflict && m.Reason.Contains("never reused across environments"));
        registry.Usages().Should().BeEmpty("matching never binds");
        (await service.ConsumersAsync(default)).Requirements.Single().Status.Should().Be(TestDataRequirementStatus.Missing);

        (await service.BindAsync(new() { AssetId = "configured:qa-person", Consumer = future, RequirementId = requirement.Id }, default)).Error.Should().Contain("scoped to QA");
        var (usage, bindError) = await service.BindAsync(new() { AssetId = "configured:person-id", Consumer = future, RequirementId = requirement.Id, Mapping = "{personId} ← personId" }, default);
        bindError.Should().BeNull();
        usage!.AssetFingerprint.Should().NotBeNullOrEmpty().And.NotContain(Secret);
        usage.EnvironmentId.Should().Be("TT02");
        (await service.BindAsync(new() { AssetId = "configured:person-id", Consumer = Consumer("Test pack", "skolenaervaer", "Skolenærvær Testing"), Usage = "Identity reference" }, default)).Error.Should().BeNull();

        var view = await service.ConsumersAsync(default);
        view.Requirements.Single().Status.Should().Be(TestDataRequirementStatus.Resolved);
        view.Requirements.Single().BoundAssetId.Should().Be("configured:person-id");
        view.Usages.Should().HaveCount(2).And.OnlyContain(u => u.Status == TestDataUsageStatus.Resolved);
        (await service.ViewAsync(default)).Assets.Single(a => a.Alias == "person-id").Consumers.Should().Be(2);
        view.Overview.RequiredResolved.Should().Be(1);
        JsonSerializer.Serialize(view).Should().NotContain(Secret);
        File.ReadAllText(Path.Combine(_dir, "registry.json")).Should().NotContain(Secret);

        // The bound asset changes: the usage and requirement need review; nothing is rebound or modified automatically.
        configured.Items[0] = Configured("person-id", Secret + "-v2");
        var changed = await service.ConsumersAsync(default);
        changed.Usages.Should().OnlyContain(u => u.Status == TestDataUsageStatus.NeedsReview);
        changed.Requirements.Single().Status.Should().Be(TestDataRequirementStatus.NeedsReview);
        registry.Usages().Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_approved_assets_are_bound()
    {
        var (service, _, _, configured) = Create();
        configured.Items.Add(Configured("draft-id", Secret));
        var (usage, error) = await service.BindAsync(new() { AssetId = "configured:draft-id", Consumer = Consumer("Review", "aqr") }, default);
        usage.Should().BeNull();
        error.Should().Contain("only approved assets are bound");
    }

    [Fact]
    public async Task A_contract_change_in_the_latest_snapshot_marks_the_registered_asset_for_review_without_modifying_it()
    {
        var (service, registry, sources, _) = Create();
        sources.Snapshots.Add(Snapshot(Older, DateTimeOffset.Parse("2026-10-01T08:00:00Z"), OpenApi("title")));
        await service.RegisterAsync(new() { Alias = "report-body", DataType = TestDataDataType.JsonPayload, SnapshotId = Older, CandidateId = "op:api:POST /reports:body" }, default);
        service.Approve("registered:report-body");
        var current = (await service.ViewAsync(default)).Assets.Single();
        current.Status.Should().Be(TestDataAssetStatus.Approved);
        current.ContractStatus.Should().Be(TestDataContractStatus.Valid);

        sources.Snapshots.Add(Snapshot(Newer, DateTimeOffset.Parse("2026-10-22T08:00:00Z"), OpenApi("title")));
        (await service.ViewAsync(default)).Assets.Single().ContractStatus.Should().Be(TestDataContractStatus.Compatible);

        sources.Snapshots.RemoveAt(1);
        sources.Snapshots.Add(Snapshot(Newer, DateTimeOffset.Parse("2026-10-22T08:00:00Z"), OpenApi("title", "dueDate")));
        var drifted = (await service.ViewAsync(default)).Assets.Single();
        drifted.ContractStatus.Should().Be(TestDataContractStatus.NeedsReview);
        drifted.ContractNote.Should().Contain("review required").And.Contain("not modified");
        drifted.Status.Should().Be(TestDataAssetStatus.NeedsReview);
        registry.Assets().Single().SnapshotId.Should().Be(Older, "the asset is never moved to the newest snapshot automatically");
        (await service.ConsumersAsync(default)).Overview.ContractMismatches.Should().Be(1);
    }

    [Fact]
    public void Rules_label_scope_and_derive_status_from_facts()
    {
        TestDataCatalogRules.ScopeLabel(null).Should().Be("Shared");
        TestDataCatalogRules.ScopeLabel(" TT02 ").Should().Be("TT02");
        TestDataCatalogRules.Status(TestDataApproval.Approved, false, "a", "b", false).Should().Be(TestDataAssetStatus.NeedsReview);
        TestDataCatalogRules.Status(TestDataApproval.Approved, true, "a", "a", false).Should().Be(TestDataAssetStatus.Deprecated);
        TestDataCatalogRules.Status(TestDataApproval.Unapproved, false, null, null, true).Should().Be(TestDataAssetStatus.NeedsReview);
        TestDataCatalogRules.Status(TestDataApproval.Unapproved, false, null, null, false).Should().Be(TestDataAssetStatus.Draft);
        TestDataCatalogRules.AliasPattern.IsMatch("person-id-1").Should().BeTrue();
        TestDataCatalogRules.AliasPattern.IsMatch("Person Id").Should().BeFalse();

        var schemaAsset = new TestDataAssetSummary { Id = "s", Alias = "report", DataType = TestDataDataType.XmlPayload, Status = TestDataAssetStatus.Approved, SchemaRef = "receiver/schemas/report.xsd" };
        var requirement = new ConsumerTestDataRequirement { RequestedDataType = TestDataDataType.XmlPayload, SchemaRef = "schemas/report.xsd" };
        TestDataCatalogRules.Match(requirement, [schemaAsset]).Single().State.Should().Be(TestDataMatchState.Exact);
        TestDataCatalogRules.Match(requirement with { SchemaRef = null }, [schemaAsset]).Single().State.Should().Be(TestDataMatchState.Possible);
        TestDataCatalogRules.Match(requirement with { RequestedDataType = TestDataDataType.JsonPayload }, [schemaAsset]).Should().BeEmpty();
    }
}
