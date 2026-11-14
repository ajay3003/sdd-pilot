using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Declared Integration Journeys in the database: declaring writes revision 1, an edit appends the next revision (earlier revisions never
/// change, the id stays), a reload reads the same, projects never see each other's journeys, removing retires (history kept) and declaring
/// again reactivates the same id. The legacy JSON file is imported idempotently (run twice), ids preserved, conflicts reported and never
/// overwritten, duplicates and invalid entries reported, and the file itself is never changed.
/// </summary>
public sealed class DeclaredIntegrationJourneyPersistenceTests : IDisposable
{
    private const string Project = "import:acc-journeys", Other = "import:other";
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Chain = ["Orders.Adapter", "Orders.Api", "order-events", "Orders.Notifier"];
    private readonly string _database = $"declared-journeys-{Guid.NewGuid():N}";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-legacy-journeys-" + Guid.NewGuid().ToString("N"));
    private readonly DeclaredIntegrationJourneyMigrationReports _reports = new();

    public DeclaredIntegrationJourneyPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IntegrationJourneysDbContext Db() =>
        new(new DbContextOptionsBuilder<IntegrationJourneysDbContext>().UseInMemoryDatabase(_database).Options);

    private DeclaredIntegrationJourneyStore Store() => new(Db());

    private DeclaredIntegrationJourneyLegacyImport Importer() => new(Db(), _reports, NullLogger<DeclaredIntegrationJourneyLegacyImport>.Instance);

    private static DeclaredIntegrationJourneyRequest Request(string name, string project = Project, string by = "Kari", params string[] components) =>
        new() { ProjectKey = project, DisplayName = name, Components = components.Length == 0 ? [.. Chain] : [.. components], DeclaredBy = by, Description = "Acceptance" };

    // ── Revisions ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_edit_appends_a_revision_keeps_the_id_and_never_changes_earlier_revisions()
    {
        var first = (await Store().DeclareAsync(Request("Order notification"), Now)).Journey!;
        first.Revision.Should().Be(1);

        var edit = await Store().ReviseAsync(Project, first.JourneyId,
            Request("Order notification and archive", by: "Ola", components: [.. Chain, "Orders.Archiver"]) with { ExpectedRevision = 1 }, Now.AddHours(1));
        edit.Error.Should().BeNull();
        edit.Journey!.Should().Match<DeclaredIntegrationJourney>(j => j.Id == first.Id && j.Revision == 2 && j.DisplayName == "Order notification and archive"
            && j.DeclaredBy == "Ola" && j.CreatedBy == "Kari" && j.CreatedAt == Now);

        // Reload (new context): the current revision is the latest; history is newest first with revision 1 unchanged.
        var history = (await Store().HistoryAsync(Project, first.JourneyId))!;
        history.Journey.Revision.Should().Be(2);
        history.Journey.Components.Should().EndWith("Orders.Archiver");
        history.Revisions.Select(r => (r.Revision, r.Origin)).Should().Equal((2, DeclaredIntegrationJourneyRules.OriginEdited), (1, DeclaredIntegrationJourneyRules.OriginDeclared));
        history.Revisions[1].Should().Match<DeclaredIntegrationJourneyRevision>(r => r.DisplayName == "Order notification" && r.DeclaredBy == "Kari" && r.DeclaredAt == Now
            && r.Components.SequenceEqual(Chain));
        (await Store().ForProjectAsync(Project)).Should().ContainSingle().Which.Revision.Should().Be(2);

        var stale = await Store().ReviseAsync(Project, first.JourneyId, Request("Stale edit") with { ExpectedRevision = 1 }, Now.AddHours(2));
        stale.Status.Should().Be(409, "an edit from an older revision is refused, never merged");
        stale.Journey.Should().BeNull();
        var noop = await Store().ReviseAsync(Project, first.JourneyId, Request("Order notification and archive", by: "Per", components: [.. Chain, "Orders.Archiver"]), Now.AddHours(2));
        (noop.Status, noop.Error).Should().Be((400, "Nothing changed: no new revision was written."));
        (await Store().ReviseAsync(Project, "unknown", Request("X"), Now)).Status.Should().Be(404);
        (await Store().ReviseAsync(Other, first.JourneyId, Request("X", project: Other), Now)).Status.Should().Be(404, "another project's journey is never edited");
        (await Store().ReviseAsync(Project, first.JourneyId, Request("X", project: Other), Now)).Status.Should().Be(400);

        using var db = Db();
        (await db.Revisions.CountAsync()).Should().Be(2, "refused edits write nothing");
    }

    [Fact]
    public async Task Projects_are_isolated_and_a_retired_journey_keeps_its_history_and_can_be_reactivated()
    {
        var own = (await Store().DeclareAsync(Request("Order notification"), Now)).Journey!;
        var other = (await Store().DeclareAsync(Request("Order notification", project: Other, by: "Ola"), Now)).Journey!;
        other.Id.Should().NotBe(own.Id);
        (await Store().ForProjectAsync(Other)).Should().ContainSingle().Which.DeclaredBy.Should().Be("Ola");
        (await Store().AllAsync()).Select(j => j.Id).Should().BeEquivalentTo([own.Id, other.Id]);

        (await Store().RetireAsync(Other, own.JourneyId, "Kari", Now)).Should().BeTrue("the other project's journey with the same slug is its own");
        (await Store().ForProjectAsync(Project)).Should().ContainSingle().Which.Id.Should().Be(own.Id, "retiring in one project never touches another");
        (await Store().RetireAsync(Other, own.JourneyId, "Kari", Now)).Should().BeFalse("already retired");

        var retired = (await Store().HistoryAsync(Other, other.JourneyId))!;
        (retired.Journey.RetiredAt, retired.RetiredBy, retired.Revisions.Count).Should().Be(((DateTimeOffset?)Now, "Kari", 1));
        (await Store().FindByIdAsync(other.Id))!.RetiredAt.Should().NotBeNull();
        (await Store().ReviseAsync(Other, other.JourneyId, Request("Changed", project: Other), Now)).Status.Should().Be(409);

        var again = (await Store().DeclareAsync(Request("Order Notification", project: Other, by: "Per", components: ["A", "B"]), Now.AddDays(1))).Journey!;
        (again.Id, again.Revision, again.RetiredAt).Should().Be((other.Id, 2, (DateTimeOffset?)null), "declaring again reactivates the same id with a new revision");
        (await Store().HistoryAsync(Other, other.JourneyId))!.Revisions.Select(r => r.Origin)
            .Should().Equal(DeclaredIntegrationJourneyRules.OriginReactivated, DeclaredIntegrationJourneyRules.OriginDeclared);
    }

    [Theory]
    [InlineData("project.import:acc:order-notification", "import:acc", "order-notification")]
    [InlineData("project.plain:a", "plain", "a")]
    public void Public_ids_split_into_project_key_and_slug(string id, string project, string slug)
    {
        DeclaredIntegrationJourneyStore.TryParseId(id, out var p, out var s).Should().BeTrue();
        (p, s).Should().Be((project, slug));
        DeclaredIntegrationJourneyStore.TryParseId("skolenaervaer:submit", out _, out _).Should().BeFalse("a registered pack journey is not a declared one");
    }

    // ── Legacy JSON migration ─────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions LegacyJson = new() { WriteIndented = true };

    private static DeclaredIntegrationJourney Legacy(string project, string name, string by = "Kari", params string[] components) => new()
    {
        ProjectKey = project, JourneyId = DeclaredIntegrationJourneyRules.Slug(name), DisplayName = name, Components = components.Length == 0 ? [.. Chain] : [.. components],
        DeclaredBy = by, DeclaredAt = Now.AddDays(-3),
    };

    /// <summary>Writes the file exactly as the earlier JSON store did (PascalCase, indented, computed Id/PackId included).</summary>
    private string WriteLegacy(params DeclaredIntegrationJourney[] journeys)
    {
        var path = Path.Combine(_dir, "declared-journeys.json");
        File.WriteAllText(path, JsonSerializer.Serialize(journeys, LegacyJson));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public async Task The_legacy_file_is_imported_once_as_revision_1_with_ids_preserved_and_left_untouched()
    {
        var path = WriteLegacy(Legacy(Project, "Order notification"), Legacy(Other, "Order notification", "Ola"), Legacy(Project, "Order archive", components: ["Orders.Api", "Orders.Archiver"]));
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

        var first = await Importer().ImportAsync(path, Now);
        first.Should().Match<DeclaredIntegrationJourneyMigrationReport>(r => r.Status == "Completed" && r.Read == 3 && r.Imported == 3 && r.AlreadyPresent == 0 && r.Conflicts == 0);
        first.SourceSha256.Should().Be(Convert.ToHexString(SHA256.HashData(before.Item1)));
        _reports.Latest.Should().BeSameAs(first);

        var journeys = await Store().ForProjectAsync(Project);
        journeys.Select(j => j.Id).Should().BeEquivalentTo([$"project.{Project}:order-notification", $"project.{Project}:order-archive"], "ids are preserved");
        journeys.Should().OnlyContain(j => j.Revision == 1 && j.DeclaredAt == Now.AddDays(-3) && j.CreatedBy == "Kari");
        (await Store().HistoryAsync(Project, "order-notification"))!.Revisions.Single().Origin.Should().Be(DeclaredIntegrationJourneyRules.OriginLegacyImport);
        (await Store().ForProjectAsync(Other)).Should().ContainSingle().Which.DeclaredBy.Should().Be("Ola", "each entry stays in its own project");

        // An edit after the import, then a second start: nothing is imported again or overwritten.
        (await Store().ReviseAsync(Project, "order-notification", Request("Order notification v2"), Now.AddHours(1))).Journey!.Revision.Should().Be(2);
        var second = await Importer().ImportAsync(path, Now.AddDays(1));
        second.Should().Match<DeclaredIntegrationJourneyMigrationReport>(r => r.Status == "Completed" && r.Imported == 0 && r.AlreadyPresent == 3);
        second.Items.Single(i => i.JourneyId == "order-notification" && i.ProjectKey == Project).Detail.Should().Contain("revision 2");
        (await Store().ForProjectAsync(Project)).Single(j => j.JourneyId == "order-notification").DisplayName.Should().Be("Order notification v2");
        using (var db = Db()) (await db.Revisions.CountAsync()).Should().Be(4);

        (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)).Should().BeEquivalentTo(before, "the legacy file is only read");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Conflicts_duplicates_and_invalid_entries_are_reported_and_nothing_is_overwritten()
    {
        // The database already has a different journey with a legacy id (declared after the file was written).
        (await Store().DeclareAsync(Request("Order notification", components: ["Other.A", "Other.B"]), Now)).Journey.Should().NotBeNull();
        var path = WriteLegacy(
            Legacy(Project, "Order notification"),                                   // conflict with the database
            Legacy(Project, "Order archive"), Legacy(Project, "Order archive"),       // identical repeat
            Legacy(Project, "Order audit", components: ["A", "B"]), Legacy(Project, "Order audit", components: ["A", "C"]), // same id, different content
            Legacy(Project, "Bad url", components: ["Orders.Api", "https://orders.example"]),
            Legacy("", "No project"),
            Legacy(Project, "Bad id") with { JourneyId = "Not A Slug" });

        var report = await Importer().ImportAsync(path, Now);

        report.Status.Should().Be("CompletedWithIssues");
        (report.Read, report.Imported, report.Conflicts, report.DuplicatesInFile, report.Invalid).Should().Be((8, 1, 3, 1, 3));
        report.Items.Should().ContainSingle(i => i.JourneyId == "order-notification" && i.Outcome == DeclaredIntegrationJourneyRules.Conflict)
            .Which.Detail.Should().Contain("nothing is overwritten");
        report.Items.Where(i => i.JourneyId == "order-audit").Should().HaveCount(2).And.OnlyContain(i => i.Outcome == DeclaredIntegrationJourneyRules.Conflict);
        report.Items.Should().ContainSingle(i => i.JourneyId == "order-archive" && i.Outcome == DeclaredIntegrationJourneyRules.DuplicateInFile);

        var stored = await Store().ForProjectAsync(Project);
        stored.Select(j => j.JourneyId).Should().BeEquivalentTo(["order-notification", "order-archive"]);
        stored.Single(j => j.JourneyId == "order-notification").Components.Should().Equal(["Other.A", "Other.B"], "the database is kept");

        var again = await Importer().ImportAsync(path, Now.AddDays(1));
        (again.Imported, again.AlreadyPresent, again.Conflicts).Should().Be((0, 1, 3), "a second run is idempotent and reports the same conflicts");
    }

    [Fact]
    public async Task No_file_and_an_unreadable_file_are_reported_without_writing()
    {
        (await Importer().ImportAsync(Path.Combine(_dir, "missing.json"), Now)).Status.Should().Be("NoLegacyFile");
        var path = Path.Combine(_dir, "declared-journeys.json");
        File.WriteAllText(path, "{ not json");
        var report = await Importer().ImportAtStartupAsync(path, Now);
        report.Status.Should().Be("Unreadable");
        File.ReadAllText(path).Should().Be("{ not json");
        (await Store().AllAsync()).Should().BeEmpty();
    }

    // ── Real PostgreSQL: the migration applies and the constraints hold ──────────────────────────────────────────────────

    [Fact]
    public async Task On_PostgreSQL_the_schema_migrates_and_revisions_persist_across_contexts()
    {
        var database = TestPostgres.TemporaryName("birknext_integration_journeys");
        var admin = TestPostgres.Admin().ConnectionString;
        var target = TestPostgres.Temporary(database).ConnectionString;
        await Execute(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            IntegrationJourneysDbContext Pg()
            {
                var options = new DbContextOptionsBuilder<IntegrationJourneysDbContext>();
                IntegrationJourneysDbContext.Configure(options, target);
                return new IntegrationJourneysDbContext(options.Options);
            }
            await using (var db = Pg()) await DatabaseMigrationLock.MigrateAsync(db);

            var declared = (await new DeclaredIntegrationJourneyStore(Pg()).DeclareAsync(Request("Order notification"), Now)).Journey!;
            (await new DeclaredIntegrationJourneyStore(Pg()).ReviseAsync(Project, declared.JourneyId, Request("Order notification v2"), Now.AddMinutes(1))).Journey!.Revision.Should().Be(2);
            (await new DeclaredIntegrationJourneyStore(Pg()).DeclareAsync(Request("Order notification", project: Other), Now)).Journey.Should().NotBeNull();

            var history = (await new DeclaredIntegrationJourneyStore(Pg()).HistoryAsync(Project, declared.JourneyId))!;
            history.Revisions.Select(r => r.DisplayName).Should().Equal("Order notification v2", "Order notification");
            history.Revisions[1].Components.Should().Equal(Chain, "components round-trip through jsonb");
            (await new DeclaredIntegrationJourneyStore(Pg()).ForProjectAsync(Project)).Should().ContainSingle().Which.DisplayName.Should().Be("Order notification v2");

            await using (var db = Pg())
            {
                db.Journeys.Add(new DeclaredJourneyRecord { Id = Guid.NewGuid(), ProjectKey = Project, Slug = declared.JourneyId, DisplayName = "dup", CurrentRevision = 1, CreatedBy = "x", CreatedAt = Now, UpdatedAt = Now });
                await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>("project + slug is unique");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
