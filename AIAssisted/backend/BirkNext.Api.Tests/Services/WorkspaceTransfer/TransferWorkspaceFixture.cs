using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>
/// One BirkNext installation for transfer tests: a temporary migrated database plus its own file stores (Critical E2E, Test Data) in a
/// temporary directory. <see cref="Seed"/> builds a synthetic Altinn → MU → MM → Skolenærvær → Bufdata workspace. <see cref="Ledger"/> is
/// the independent expectation, written by hand from the seed and never read back from BirkNext.
/// </summary>
public sealed class TransferInstallation : IAsyncDisposable
{
    public const string Env = "qa-synth";
    public static readonly Guid MuSnapshot = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static readonly Guid M2lbV1 = Guid.Parse("20000000-0000-4000-8000-000000000001");
    public static readonly Guid M2lbV2 = Guid.Parse("20000000-0000-4000-8000-000000000002");
    public static readonly Guid AltinnSnapshot = Guid.Parse("30000000-0000-4000-8000-000000000001");
    public static readonly Guid ContractId = Guid.Parse("40000000-0000-4000-8000-000000000001");
    public static readonly Guid TestId = Guid.Parse("40000000-0000-4000-8000-000000000002");
    public const string ContractKey = "skoleservice-rapportinntak";

    private readonly string _database;
    public string Root { get; }
    public string ConnectionString => TestPostgres.Temporary(_database).ConnectionString;
    public CriticalE2EStore Flows { get; }
    public TestDataRegistryStore TestData { get; }
    public AuthoredTestDataStore Authored { get; }

    private TransferInstallation(string database, string root)
    {
        _database = database;
        Root = root;
        Flows = new CriticalE2EStore(Path.Combine(root, "critical-e2e"), NullLogger<CriticalE2EStore>.Instance);
        TestData = new TestDataRegistryStore(Path.Combine(root, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        Authored = new AuthoredTestDataStore(Path.Combine(root, "test-data"));
    }

    public static async Task<TransferInstallation> CreateAsync(string prefix)
    {
        var name = TestPostgres.TemporaryName(prefix);
        await Execute(TestPostgres.Admin().ConnectionString, $"CREATE DATABASE \"{name}\"");
        var installation = new TransferInstallation(name, Path.Combine(Path.GetTempPath(), "birknext-transfer-tests", Guid.NewGuid().ToString("N")));
        await using var db = installation.Db();
        await DatabaseMigrationLock.MigrateAsync(db);
        return installation;
    }

    public AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero); }

    public (WorkspacePackageService Service, AppDbContext Db) Service()
    {
        var db = Db();
        var kit = new AltinnTestKit();
        var clock = new Clock();
        var session = new AltinnSessionStore(kit.Options, clock);
        var catalog = new Mock<IIntegrationCatalogService>();
        catalog.Setup(c => c.GetAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        var integrations = new ExternalIntegrationService(db, [], catalog.Object, session, new AltinnEnvironmentSettingsCache(), clock);
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var setup = new EnvironmentSetupService(db, integrations, catalog.Object, new TestDataCatalog([]), sources.Object, Array.Empty<IIntegrationJourneyPack>(), clock);
        return (new WorkspacePackageService(db, Flows, TestData, Authored, setup, [], new WorkspacePackageOptions(), Path.Combine(Root, "transfer"),
            NullLogger<WorkspacePackageService>.Instance), db);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute(TestPostgres.Admin().ConnectionString, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    public static async Task Execute(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Evidence(Guid id, string archive, string sha, string key, string display) =>
        $$$"""{"id":"{{{id}}}","integrationId":"source-analysis","archive":{"fileName":"{{{archive}}}","sha256":"{{{sha}}}","filesAnalyzed":3},"repository":{"key":"{{{key}}}","displayName":"{{{display}}}","basis":"test"}}""";

    /// <summary>The synthetic workspace (no real identifier, no real host, no secret; one fake secret only where a test asks for it).</summary>
    public async Task SeedAsync(bool withSecretInSource = false)
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        await using var db = Db();
        db.IqrSourceSnapshots.AddRange(
            new IqrSourceSnapshotRecord { Id = MuSnapshot, EnvironmentId = "", IntegrationId = "source-analysis", AnalyzedAt = t0, EvidenceJson = Evidence(MuSnapshot, "MU.Synth.zip", new string('a', 64), "mu-synth", "MU synth") },
            new IqrSourceSnapshotRecord { Id = M2lbV1, EnvironmentId = "", IntegrationId = "source-analysis", AnalyzedAt = t0.AddDays(1), EvidenceJson = Evidence(M2lbV1, "M2LB.Synth.zip", new string('b', 64), "m2lb-synth", "M2LB synth") },
            new IqrSourceSnapshotRecord { Id = M2lbV2, EnvironmentId = "", IntegrationId = "source-analysis", AnalyzedAt = t0.AddDays(2), EvidenceJson = Evidence(M2lbV2, "M2LB.Synth.zip", new string('c', 64), "m2lb-synth", "M2LB synth") },
            new IqrSourceSnapshotRecord { Id = AltinnSnapshot, EnvironmentId = "", IntegrationId = "source-analysis", AnalyzedAt = t0, EvidenceJson = Evidence(AltinnSnapshot, "Altinn.Synth.zip", new string('d', 64), "altinn-synth", "Altinn app synth") });
        db.SourceSchemaArtifacts.Add(new SourceSchemaArtifactRecord { SnapshotId = M2lbV2, SnapshotFingerprint = "fp-m2lb-v2", SourceArchiveName = "M2LB.Synth.zip", CapturedAt = t0, IsComplete = true, OmittedFiles = 0,
            PackageJson = """[{"path":"Skjema/skolenaervaer.v2.0.1.flat.xsd","targetNamespace":"https://example.invalid/skolenaervaer.v2.0.1.flat"}]""" });
        db.SourceContractDocuments.Add(new SourceContractDocumentRecord { Id = Guid.Parse("50000000-0000-4000-8000-000000000001"), SnapshotId = M2lbV2, Path = "SkoleService/rapportinntak.openapi.yaml", Sha256 = new string('e', 64),
            Text = withSecretInSource ? "openapi: 3.0.0\nx-token: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.c2lnbmF0dXJl" : "openapi: 3.0.0\ninfo: {title: Rapportinntak, version: 1.0.0}", CapturedAt = t0 });
        db.ApiContracts.Add(new ApiContractRow { Id = ContractId, Key = ContractKey, Name = "Rapportinntak", Protocol = "OpenAPI", CreatedAt = t0, UpdatedAt = t0, ProjectKey = "m2lb-synth" });
        db.ApiContractVersions.AddRange(
            new ApiContractVersionRow { Id = Guid.NewGuid(), ContractId = ContractId, Version = 1, Sha256 = "sha-v1", Source = "SourceAnalysis", SourceLabel = "M2LB v1", Format = "OpenAPI", DocumentText = "v1", ContractJson = "{}", ImportedAt = t0, SourceSnapshotId = M2lbV1, SourceProjectKey = "m2lb-synth" },
            new ApiContractVersionRow { Id = Guid.NewGuid(), ContractId = ContractId, Version = 2, Sha256 = "sha-v2", Source = "SourceAnalysis", SourceLabel = "M2LB v2", Format = "OpenAPI", DocumentText = "v2", ContractJson = "{}", ImportedAt = t0.AddDays(2), SourceSnapshotId = M2lbV2, SourceProjectKey = "m2lb-synth" });
        db.ContractTests.Add(new ContractTestRow { Id = TestId, ContractId = ContractId, Operation = "POST /reports", Name = "Accepts a valid report", Kind = "Positive", Revision = 2, DocumentJson = """{"assert":"status==202"}""", CreatedAt = t0, UpdatedAt = t0 });
        db.ContractTestRevisions.AddRange(
            new ContractTestRevisionRow { Id = Guid.NewGuid(), TestId = TestId, Revision = 1, DocumentJson = """{"assert":"status==200"}""", CreatedAt = t0 },
            new ContractTestRevisionRow { Id = Guid.NewGuid(), TestId = TestId, Revision = 2, DocumentJson = """{"assert":"status==202"}""", CreatedAt = t0.AddDays(1) });
        db.ContractEndpointBindings.Add(new ContractEndpointBindingRow { Id = Guid.NewGuid(), ContractId = ContractId, EnvironmentId = Env, BasePath = "/api/skole", Authentication = "None", UpdatedAt = t0 });
        db.ContractReviewDecisions.Add(new ContractReviewDecisionRow { Id = Guid.NewGuid(), Kind = "binding", ContractId = ContractId, ProjectKey = "m2lb-synth", SubjectKey = Env, Detail = "Bound for QA", DecidedBy = "Tester A", DecidedAt = t0 });
        db.ParquetRuleSets.Add(new ParquetRuleSetRecord { Id = Guid.NewGuid(), ProjectKey = "m2lb-synth", ContractKey = "bufdata-leveranse", ContractVersion = "1", Version = 1, Name = "Bufdata leveranse", RulesJson = """[{"column":"periode","rule":"required"}]""", ApprovedBy = "Tester A", ApprovedAt = t0 });
        var suiteId = Guid.NewGuid();
        db.IntegrationSuites.Add(new IntegrationSuiteRecord { Id = suiteId, SuiteKey = "skolenaervaer:m2lb-synth:abc123def456", Revision = 1, SnapshotId = M2lbV2, ScopeId = "skolenaervaer", EnvironmentId = Env, GeneratedAt = t0, Fingerprint = "suite-fp-1",
            DefinitionJson = """[{"testId":"kode6-shielded","title":"Kode 6 child is shielded","kind":"Negative"},{"testId":"kode7-address","title":"Kode 7 address withheld","kind":"Negative"}]""" });
        var runId = Guid.NewGuid();
        db.IntegrationSuiteRuns.Add(new IntegrationSuiteRunRecord { Id = Guid.NewGuid(), SuiteKey = "skolenaervaer:m2lb-synth:abc123def456", SuiteId = suiteId, RunId = runId, Kind = "contract-tests", StartedAt = t0.AddDays(3), Initiator = "Tester A", TestIdsJson = """["kode6-shielded"]""" });
        db.ExecutionRuns.Add(new ExecutionRunRow { Id = runId, EnvironmentId = Env, Name = "Suite run", State = "Passed", StartedAt = t0.AddDays(3), CompletedAt = t0.AddDays(3), Planned = 1, Passed = 1, SummaryJson = "{}" });
        db.ExecutionCalls.Add(new ExecutionCallRow { Id = Guid.NewGuid(), RunId = runId, Sequence = 1, Name = "POST /reports", Protocol = "HTTP", Method = "POST", State = "Passed", HttpStatus = 202, AttemptCount = 1, DocumentJson = """{"status":202}""" });
        // A contract-test run outside any suite: it is evidence of the project's test through its DefinitionRef.
        var contractRun = Guid.NewGuid();
        db.ExecutionRuns.Add(new ExecutionRunRow { Id = contractRun, EnvironmentId = Env, Name = "Contract test run", State = "Passed", StartedAt = t0.AddDays(4), CompletedAt = t0.AddDays(4), Planned = 1, Passed = 1, SummaryJson = "{}" });
        db.ExecutionCalls.Add(new ExecutionCallRow { Id = Guid.NewGuid(), RunId = contractRun, Sequence = 1, Name = "POST /reports", Protocol = "HTTP", Method = "POST", State = "Passed", HttpStatus = 202, AttemptCount = 1,
            DocumentJson = """{"status":202}""", DefinitionRef = $"contract-test:{TestId}@r2" });
        db.CompositeSourceContexts.Add(new CompositeSourceContextRecord { Id = Guid.NewGuid(), ContextKey = "skolenaervaer:m2lb-synth", Revision = 1, ScopeId = "skolenaervaer", OwningProject = "m2lb-synth", Fingerprint = "ctx-fp-1", CreatedAt = t0, CreatedBy = "Tester A",
            DefinitionJson = $$$"""{"stages":[{"stage":"Altinn → MU","projectKey":"altinn-synth","snapshotId":"{{{AltinnSnapshot}}}"},{"stage":"MU → MM","projectKey":"mu-synth","snapshotId":"{{{MuSnapshot}}}"},{"stage":"MM → Skolenærvær","projectKey":"m2lb-synth","snapshotId":"{{{M2lbV2}}}"},{"stage":"Skolenærvær → Bufdata","projectKey":"m2lb-synth","snapshotId":"{{{M2lbV2}}}"}]}""" });
        db.SourceRepositoryAssociations.Add(new SourceRepositoryAssociationRecord { Id = Guid.NewGuid(), RepositoryKeyA = "mu-synth", RepositoryA = "MU synth", RepositoryKeyB = "m2lb-synth", RepositoryB = "M2LB synth", DeclaredBy = "Tester A", DeclaredAt = t0 });
        foreach (var (id, name) in new[] { ("altinn", "Altinn"), ("mu", "MU"), ("mm", "MM"), ("skolenaervaer", "Skolenærvær"), ("bufdata", "Bufdata") })
            db.IntegrationDefinitions.Add(new IntegrationDefinitionRecord { EnvironmentId = Env, Id = id, PlatformId = "m2lb-platform", DisplayName = name, Enabled = true, DocumentJson = $$$"""{"id":"{{{id}}}","displayName":"{{{name}}}","endpoint":"https://{{{id}}}.example.invalid"}""", UserModified = true, UpdatedAt = t0 });
        db.IntegrationPlatforms.Add(new IntegrationPlatformRecord { EnvironmentId = Env, Id = "m2lb-platform", Name = "M2LB platform", DocumentJson = """{"id":"m2lb-platform"}""", UpdatedAt = t0 });
        db.IntegrationEvidenceBindings.AddRange(
            new IntegrationEvidenceBindingRecord { EnvironmentId = Env, IntegrationId = "mm", SnapshotId = M2lbV2, DocumentJson = "{}", UpdatedAt = t0 },
            new IntegrationEvidenceBindingRecord { EnvironmentId = Env, IntegrationId = "mu", SnapshotId = MuSnapshot, DocumentJson = "{}", UpdatedAt = t0 });
        db.IntegrationContractArtifacts.Add(new IntegrationContractArtifactRecord { EnvironmentId = Env, IntegrationId = "mu", Role = "message", Content = """{"type":"object"}""", ContentHash = "cah", DocumentJson = "{}", ImportedAt = t0 });
        await db.SaveChangesAsync();

        Flows.Save(new CriticalE2EFlowDefinition
        {
            Id = "flow-skole-open", Module = "Skole", Name = "Skole page opens", ProfileId = Env, EnvironmentId = Env, Enabled = true, RequiredForRelease = true, Mode = CriticalE2EExecutionMode.CompanionBrowser,
            Steps = [new CriticalE2EStepDefinition { StepId = "open", BrowserAction = CompanionActionKind.Navigate, Value = "/skole" },
                     new CriticalE2EStepDefinition { StepId = "heading", BrowserAction = CompanionActionKind.AssertVisible, Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "page-title" }, IsFinalAssertion = true }],
            Tags = ["suggestion:abc"],
        });
        Flows.RegisterModule(Env, new CriticalE2ERegisteredModule { Name = "Skole" });
        TestData.Register(new RegisteredTestDataAsset { Alias = "skole-child", Label = "Synthetic child", DataType = TestDataDataType.SimpleValues, EnvironmentId = Env, Fields = ["name"], Origin = "Manual", StructureFingerprint = "child-fp", RegisteredAt = t0 });
        TestData.Register(new RegisteredTestDataAsset { Alias = "m2lb-report-xml", Label = "Report XML", DataType = TestDataDataType.XmlPayload, SnapshotId = M2lbV2, RepositoryKey = "m2lb-synth", Fields = ["Fravaer"], Origin = "SourceCandidate", StructureFingerprint = "xml-fp", RegisteredAt = t0 });
        Authored.Create(new TestDataResourceDraft { Alias = "kode6-case", Label = "Kode 6 case", EnvironmentId = Env, Content = new TestDataResourceContent { Kind = TestDataResourceKind.Json, Json = "{\"address\":\"Shielded\"}" } });
        Authored.AddVersion("kode6-case", new TestDataResourceDraft { Alias = "kode6-case", Label = "Kode 6 case", EnvironmentId = Env, ExpectedVersion = 1, Content = new TestDataResourceContent { Kind = TestDataResourceKind.Json, Json = "{\"address\":\"Shielded v2\"}" } });

        // Connected Systems through the existing setup pipeline (portable, non-secret fields only; enabled at the origin).
        var (service, sdb) = Service();
        await using (sdb)
        {
            var setup = typeof(WorkspacePackageService).GetField("_setup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service) as EnvironmentSetupService;
            var result = await setup!.ApplyAsync(Env, new SetupApplyPackageRequest(ConnectedSystemsYaml, "systems.yaml"), "fixture", CancellationToken.None);
            if (!result.Applied) throw new InvalidOperationException("Fixture setup failed: " + result.Outcome + " " + string.Join(" ", result.Preflight.Issues.Select(i => i.Message)));
        }
    }

    public const string ConnectedSystemsYaml = """
        schemaVersion: 1
        package:
          name: Skolenaervaer systems
          scope: Environment
        systems:
          - name: Skolenaervaer intake
            provider: RestApi
            enabled: true
            settings:
              baseUrl: https://skole.example.invalid/api
              authScheme: None
          - name: MU queue
            provider: AzureServiceBus
            enabled: true
            settings:
              namespace: sb-synth.example.invalid
              entityType: Queue
              entity: skolenaervaer
        """;

    /// <summary>The Target Environment profile the browser would send, with fields that must be stripped.</summary>
    public const string ProfileJson = """{"id":"qa-synth","name":"QA synth","environmentType":"Test","targetUrl":"https://app.example.invalid","apiKey":"should-never-travel","bearerToken":"x","nested":{"clientSecret":"y","label":"ok"}}""";

    /// <summary>Independent expectations for the seed (hand-written).</summary>
    public static class Ledger
    {
        public static readonly string[] M2lbSnapshots = [M2lbV1.ToString(), M2lbV2.ToString()];
        public static readonly string[] AllSnapshots = [MuSnapshot.ToString(), M2lbV1.ToString(), M2lbV2.ToString(), AltinnSnapshot.ToString()];
        public static readonly (int Version, string Sha)[] ContractVersions = [(1, "sha-v1"), (2, "sha-v2")];
        public static readonly int[] TestRevisions = [1, 2];
        public const string Suite = "skolenaervaer:m2lb-synth:abc123def456@1";
        public const string Composite = "skolenaervaer:m2lb-synth@1";
        public static readonly string[] IntegrationIds = ["altinn", "bufdata", "mm", "mu", "skolenaervaer"];
        public static readonly string[] Systems = ["MU queue", "Skolenaervaer intake"];
        public const int AuthoredVersions = 2;
    }
}
