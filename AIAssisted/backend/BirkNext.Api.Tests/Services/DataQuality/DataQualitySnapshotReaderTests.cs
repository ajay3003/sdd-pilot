using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.DataQuality;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.DataQuality;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.DataQuality;

/// <summary>
/// The Data Quality snapshot reader against a temporary PostgreSQL database (2026-11-27): it reads the project's catalogs, plans (its own and
/// other imports of the same application), runs, groupings, discoveries and stored source text inside one read-only transaction, writes nothing
/// (every row count and change time is unchanged), and a full audit over it changes no business data either.
/// </summary>
public sealed class DataQualitySnapshotReaderTests : IAsyncLifetime
{
    private const string Project = "import:import-dqdb000000000001";
    private const string Other = "import:import-dqdb000000000002";
    private readonly string _database = TestPostgres.TemporaryName("birknext_dq");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedAsync()
    {
        var text = DataQualityTests.Spec("Loans", 1, "- **FR-001**: Loans.", "- **FR-002**: Receipts.");
        var catalog = DataQualityTests.Catalog(Project, 1, DateTimeOffset.Parse("2026-11-27T10:00:00Z"), ("Library/specs/001-loans/spec.md", text));
        var plan = Guid.NewGuid();
        await using var db = Context();
        db.SpecificationCatalogs.Add(new SpecificationCatalogRecord
        {
            Id = catalog.Id, ProjectKey = Project, Revision = 1, Fingerprint = "f1", ArchiveSha256 = catalog.ArchiveSha256, CapturedAt = catalog.CapturedAt,
            DocumentsJson = catalog.DocumentsJson, EntriesJson = "[]", EvidenceJson = catalog.EvidenceJson,
        });
        db.WorkspaceDocumentBodies.Add(new WorkspaceDocumentBodyRecord { Sha256 = DataQualityTests.Sha(text), Content = text, Length = text.Length, CreatedAt = DateTimeOffset.UtcNow });
        var own = DataQualityTests.Draft(plan, Project, "Own", "\"suites\":[{\"id\":\"s1\"}],\"cases\":[" + DataQualityTests.Case("c1", "tss-1", "Library/specs/001-loans/spec.md", "Accepted") + "]", application: "library");
        var other = DataQualityTests.Draft(Guid.NewGuid(), Other, "Earlier import", "\"suites\":[],\"cases\":[]", application: "library");
        var foreign = DataQualityTests.Draft(Guid.NewGuid(), "import:import-foreign0000001", "Foreign", "\"suites\":[],\"cases\":[]", application: "another");
        foreach (var d in new[] { own, other, foreign })
            db.TestPlanDrafts.Add(new TestPlanDraftRecord { Id = d.Id, GuideKey = d.GuideKey, PlanName = "x", Revision = 1, ContentJson = d.ContentJson, CreatedBy = "t", UpdatedBy = "t",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.ManualTestRuns.Add(new ManualTestRunRecord { Id = Guid.NewGuid(), ProjectKey = Project, DraftId = plan, CaseId = "occ:scenario:c1", State = "Completed", Outcome = "Passed",
            ContentJson = "{}", Tester = "t", StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.ScenarioDiscoveryResults.Add(new ScenarioDiscoveryRecord { DiscoveryId = "d1", ProjectKey = Project, SnapshotKey = "", RuleVersion = "tsd-1", ContentJson = "{\"candidates\":[]}",
            DiscoveredAt = DateTimeOffset.UtcNow, ApplicationKey = "library", BasisKind = "Current" });
        await db.SaveChangesAsync();
        return plan;
    }

    private async Task<string> FingerprintAsync()
    {
        await using var db = Context();
        var drafts = await db.TestPlanDrafts.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id + ":" + d.Revision + ":" + d.UpdatedAt.UtcTicks + ":" + d.ContentJson.Length).ToListAsync();
        var runs = await db.ManualTestRuns.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id + ":" + r.UpdatedAt.UtcTicks + ":" + r.Outcome).ToListAsync();
        return string.Join("|", drafts) + "#" + string.Join("|", runs) + "#" + await db.SpecificationCatalogs.CountAsync() + "#" + await db.WorkspaceDocumentBodies.CountAsync()
            + "#" + await db.ScenarioDiscoveryResults.CountAsync() + "#" + await db.StoryGroupings.CountAsync();
    }

    [Fact]
    public async Task DQ30_the_reader_reads_the_project_and_its_application_in_one_read_only_snapshot_and_writes_nothing()
    {
        await SeedAsync();
        var before = await FingerprintAsync();
        await using var db = Context();
        var reader = new DataQualitySnapshotReader(db);
        var marker = await reader.MarkerAsync(Project, CancellationToken.None);
        var s = await reader.ReadAsync(Project, withBodies: true, CancellationToken.None);

        s.Application.Should().Be("library");
        s.Catalogs.Should().ContainSingle();
        s.Drafts.Select(d => System.Text.Json.JsonDocument.Parse(d.ContentJson).RootElement.GetProperty("planName").GetString())
            .Should().BeEquivalentTo(["Own", "Earlier import"], "the application's other imports are included, another application is not");
        s.Runs.Should().ContainSingle();
        s.Bodies.Should().ContainSingle();
        s.Marker.Should().Be(marker);
        (await reader.MarkerAsync(Project, CancellationToken.None)).Should().Be(marker, "reading changes nothing");
        (await FingerprintAsync()).Should().Be(before);
    }

    [Fact]
    public async Task DQ31_a_full_audit_over_a_real_snapshot_changes_no_business_data()
    {
        await SeedAsync();
        var before = await FingerprintAsync();
        await using var db = Context();
        var reader = new DataQualitySnapshotReader(db);
        var engine = new DataQualityEngine(reader, (_, _) => Task.FromResult(new ManualTestingOverview { ProjectKey = Project }));
        var report = await engine.RunAsync("dq-db", Project, [DataQualityCategory.Dashboard, DataQualityCategory.RequirementIdentity, DataQualityCategory.SourceImport], _ => { }, CancellationToken.None);

        report.Consistent.Should().BeTrue();
        report.Checks.Single(c => c.Id == "source.text").Status.Should().Be(DataQualityStatus.Pass);
        report.Checks.Single(c => c.Id == "dashboard.specifications").Status.Should().Be(DataQualityStatus.Fail, "the stub API returns no specifications");
        (await FingerprintAsync()).Should().Be(before, "the audit wrote nothing to the business domain");
    }
}
