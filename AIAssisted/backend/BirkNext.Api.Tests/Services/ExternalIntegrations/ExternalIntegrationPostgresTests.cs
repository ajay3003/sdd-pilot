using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;

namespace BirkNext.Api.Tests.Services.ExternalIntegrations;

/// <summary>
/// The all-or-nothing apply against the real database provider (PostgreSQL, a fresh migrated database per test): a failure in the second
/// system rolls back every earlier write in every table and is reported Failed; a successful apply commits across tables; the in-memory
/// Altinn settings the execution policy reads never keep a rolled-back value; library versions are unique per item.
/// </summary>
public sealed class ExternalIntegrationPostgresTests : IAsyncLifetime
{
    private const string Env = "pg-env";
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    private readonly string _database = "birknext_external_" + Guid.NewGuid().ToString("N")[..12];
    private string Admin => new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
    private string Target => new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = _database, Pooling = false }.ConnectionString;
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Target).Options);

    public async Task InitializeAsync()
    {
        await Execute(Admin, $"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute(Admin, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A REST API provider whose provider-typed apply fails: injected after earlier systems were written in the same unit of work.</summary>
    private sealed class FailingRestHandler : IExternalProviderHandler
    {
        public ExternalProviderType Type => ExternalProviderType.RestApi;
        public Task<Dictionary<string, string>> CurrentSettingsAsync(ExternalIntegration integration, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>(integration.Settings));
        public Task<ExternalIntegrationView> AssessAsync(ExternalIntegration integration, bool detect, CancellationToken ct) => Task.FromResult(new ExternalIntegrationView { Integration = integration });
        public Task<IReadOnlyList<SetupDependency>> SetupRequirementsAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SetupDependency>>([]);
        public Task<IReadOnlyList<string>> ApplyAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["Injected failure"]);
    }

    private static readonly string Child = Person();

    private static string Person()
    {
        for (var individual = 100; individual < 1000; individual++)
            for (var k = 0; k < 100; k++)
                if ($"120512{individual:000}{k:00}" is var candidate && BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer.SyntheticPersonIdentifier.IsTestPerson(candidate)) return candidate;
        throw new InvalidOperationException();
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero); }

    private (EnvironmentSetupService Setup, SetupLibraryService Library, AltinnEnvironmentSettingsCache Cache, AltinnTestingPolicy Policy) Services(AppDbContext db, bool failRest)
    {
        var kit = new AltinnTestKit();
        kit.Options.ApprovedApps = [AltinnTestKit.AppId];
        var flows = new IntegrationJourneyHarness.FakeMessageFlows();
        var cache = new AltinnEnvironmentSettingsCache();
        var clock = new Clock();
        var session = new AltinnSessionStore(kit.Options, clock);
        // The Altinn provider requires a reporter and a child in Test Data (fail-closed); identifiers stay in backend memory.
        session.AddIdentity(new("reporter", "reporter-a", "Reporter A", AltinnTestKit.ReporterIdentifier, "reporter-a")).Should().BeNull();
        session.AddIdentity(new("child", "child-a", "Child A", Child)).Should().BeNull();
        var policy = new AltinnTestingPolicy(kit.Options, session, cache);
        var catalog = new Mock<IIntegrationCatalogService>();
        catalog.Setup(c => c.GetAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        var trusted = new Mock<ITrustedExecutionEnvironmentRegistry>();
        string reason = "Not trusted (test).";
        trusted.Setup(t => t.Resolve(It.IsAny<string>(), out reason)).Returns((TrustedExecutionEnvironment?)null);
        var store = new AltinnEnvironmentSettingsStore(db, cache, clock);
        var altinn = new AltinnEnvironmentService(store, flows, policy, kit.Contracts(flows), catalog.Object, trusted.Object, kit.Factory);
        IExternalProviderHandler[] handlers = failRest
            ? [new AltinnProviderHandler(altinn, store, flows, kit.Contracts(flows), policy), new FailingRestHandler()]
            : [new AltinnProviderHandler(altinn, store, flows, kit.Contracts(flows), policy)];
        var integrations = new ExternalIntegrationService(db, handlers, catalog.Object, session, cache, clock);
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var setup = new EnvironmentSetupService(db, integrations, catalog.Object, new TestDataCatalog([new AltinnTestDataProvider(kit.Options)]), sources.Object, Array.Empty<IIntegrationJourneyPack>(), clock);
        return (setup, new SetupLibraryService(db, setup, clock), cache, policy);
    }

    private const string TwoSystems = """
        schemaVersion: 1
        package:
          name: Two systems
          scope: Environment
        systems:
          - name: Altinn – Skolenærvær
            provider: Altinn
            settings:
              environment: Test
              appId: buf/skolenaervaer
              clientId: birknext-test-client
          - name: Person API
            provider: RestApi
            settings:
              baseUrl: https://person.qa.example/api
        """;

    [Fact]
    public async Task A_failure_in_a_later_system_rolls_back_every_earlier_write_and_is_reported_failed()
    {
        await using var db = Context();
        var (setup, _, cache, policy) = Services(db, failRest: true);
        (await setup.PreflightAsync(Env, new(TwoSystems, "two.yaml"), CancellationToken.None)).CanApply.Should().BeTrue();

        var result = await setup.ApplyAsync(Env, new(TwoSystems, "two.yaml"), "Imported from file: Two systems", CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Outcome.Should().StartWith("Failed:").And.Contain("Injected failure").And.Contain("No setup was applied");
        await using var check = Context();
        (await check.ExternalIntegrations.CountAsync()).Should().Be(0, "the Altinn system written first is rolled back");
        (await check.AltinnEnvironmentSettings.CountAsync()).Should().Be(0, "the Altinn settings written first are rolled back");
        cache.Get(Env).Should().BeNull("the execution policy never reads a rolled-back value");
        policy.ClientId(new AltinnTestConfiguration { TargetEnvironmentId = Env }, out _, out _).Should().BeNull();
    }

    [Fact]
    public async Task A_successful_apply_commits_across_every_table()
    {
        await using var db = Context();
        var (setup, _, cache, _) = Services(db, failRest: false);
        var result = await setup.ApplyAsync(Env, new(TwoSystems, "two.yaml"), "Imported from file: Two systems", CancellationToken.None);
        result.Applied.Should().BeTrue(result.Outcome);

        await using var check = Context();
        (await check.ExternalIntegrations.Select(r => r.Id).OrderBy(id => id).ToListAsync()).Should().Equal($"{Env}:altinn", $"{Env}:restapi:person-api");
        (await check.AltinnEnvironmentSettings.SingleAsync()).DocumentJson.Should().Contain("buf/skolenaervaer").And.Contain("birknext-test-client");
        cache.Get(Env)!.AppIdentifier.Should().Be("buf/skolenaervaer");
    }

    [Fact]
    public async Task Library_versions_persist_and_are_unique_per_item()
    {
        await using var db = Context();
        var (_, library, _, _) = Services(db, failRest: false);
        var (item, problem) = await library.SaveAsync(new("Two systems", null, Content: TwoSystems), CancellationToken.None);
        problem.Should().BeNull();
        await library.NewVersionAsync(item!.Id, new("Two systems", null, Content: TwoSystems.Replace("person.qa.example", "person.test.example"), ChangeSummary: "Moved"), CancellationToken.None);

        await using var check = Context();
        (await check.SetupLibraryVersions.Where(v => v.ItemId == item.Id).Select(v => v.VersionNumber).OrderBy(n => n).ToListAsync()).Should().Equal(1, 2);
        check.SetupLibraryVersions.Add(new BirkNext.Api.Models.SetupLibraryVersionRecord { Id = Guid.NewGuid(), ItemId = item.Id, VersionNumber = 2, Payload = "x", ManifestJson = "[]" });
        var duplicate = async () => await check.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>("a version number is never reused or overwritten");
    }
}
