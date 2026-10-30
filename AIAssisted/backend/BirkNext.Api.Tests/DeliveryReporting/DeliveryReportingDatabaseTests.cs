using System.Data.Common;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Delivery Reporting against a real PostgreSQL (a temporary database on the server the API tests use: ConnectionStrings__Default, else the
/// local development server). Proves the schema boundary: migrations create delivery_reporting only, the workflow writes and reads only
/// delivery_reporting, and a generated report stays the same when snapshots change later.
/// </summary>
public sealed class DeliveryReportingDatabaseTests
{
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    /// <summary>Records every SQL command the reporting context sends.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Commands.Add(command.CommandText); return base.ReaderExecutingAsync(command, eventData, result, ct); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Commands.Add(command.CommandText); return base.NonQueryExecutingAsync(command, eventData, result, ct); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        { Commands.Add(command.CommandText); return base.ScalarExecutingAsync(command, eventData, result, ct); }
    }

    private sealed class Harness(DeliveryReportingDbContext db, Clock clock)
    {
        private static readonly DeliveryImportStagingStore Staging = new(TimeProvider.System);
        public DeliveryReportingDbContext Db { get; } = db;
        public Clock Clock { get; } = clock;
        public DeliverySnapshotStore Store { get; } = new(db, clock);
        public DeliveryReportingImportService Imports { get; } = new(db, Staging, clock);
        public WeeklyReportService Reports { get; } = new(db, new DeliverySnapshotStore(db, clock), clock, new ConfigurationBuilder().Build());

        public async Task<Guid> ProjectAsync(string name) => (await Store.CreateProjectAsync(new(name, null), default)).Project!.Id;

        public async Task<DeliverySnapshotSummary> ImportAsync(Guid projectId, byte[] zip, DateTimeOffset effectiveAt, DeliverySnapshotRole role, string label, bool confirm = false)
        {
            var preview = (await Imports.PreviewAsync(projectId, "m2lb.zip", zip, default)).Preview!;
            var outcome = await Imports.CommitAsync(preview.StagingId, new DeliveryImportCommitRequest
                { ProjectId = projectId, EffectiveAt = effectiveAt, Role = role, Label = label, ConfirmDuplicate = confirm }, default);
            outcome.StatusCode.Should().Be(200, outcome.Result.Error ?? outcome.Result.DuplicateWarning);
            return outcome.Result.Snapshot!;
        }
    }

    private static DeliveryReportingDbContext Reporting(string connectionString, CommandLog? log = null)
    {
        var options = new DbContextOptionsBuilder<DeliveryReportingDbContext>();
        DeliveryReportingDbContext.Configure(options, connectionString);
        if (log is not null) options.AddInterceptors(log);
        return new DeliveryReportingDbContext(options.Options);
    }

    private static AppDbContext App(string connectionString) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task CleanDatabase_ReportingMigrationCreatesOnlyTheDeliveryReportingSchema()
    {
        await WithDatabase(async target =>
        {
            await using var db = Reporting(target);
            await DatabaseMigrationLock.MigrateAsync(db);

            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            var tables = await Tables(target);
            tables.Where(t => t.Schema == "public").Should().BeEmpty("the reporting migration never creates anything in public");
            tables.Where(t => t.Schema == DeliveryReportingDbContext.Schema).Select(t => t.Name).Should().BeEquivalentTo([
                "__ef_migrations_history", "project", "report_source", "report_snapshot", "feature_snapshot", "task_snapshot", "artifact_snapshot",
                "evidence_reference", "snapshot_comparison", "feature_delta", "status_event", "weekly_report", "weekly_report_item",
                "phase_snapshot", "user_story_snapshot", "acceptance_criterion_snapshot"]);
        });
    }

    [Fact]
    public async Task ExistingDatabase_Upgrade_AndTheWholeWorkflow_LeaveBirkNextSchemasUntouched()
    {
        await WithDatabase(async target =>
        {
            await using (var app = App(target)) await DatabaseMigrationLock.MigrateAsync(app);
            var before = await PublicState(target);

            var log = new CommandLog();
            await using var db = Reporting(target, log);
            await DatabaseMigrationLock.MigrateAsync(db);
            log.Commands.Clear();
            var h = new Harness(db, new Clock(Friday));

            var project = await h.ProjectAsync("M2LB");
            var monday = await h.ImportAsync(project, Zip(ChildSearch(8, 3)), Monday, DeliverySnapshotRole.Baseline, "Monday");
            var friday = await h.ImportAsync(project, Zip(ChildSearch(8, 7)), Friday, DeliverySnapshotRole.Current, "Friday");
            var report = (await h.Reports.GenerateAsync(new(project, monday.Id, friday.Id), default)).Report!;
            await h.Reports.ExportAsync(project, report.ReportId, DeliveryExportFormat.Html, new(), default);
            await h.Store.SnapshotsAsync(project, default);
            await h.Store.DeleteSnapshotAsync(project, monday.Id, default);
            await h.Reports.DeleteAsync(project, report.ReportId, default);

            (await PublicState(target)).Should().BeEquivalentTo(before, "Delivery Reporting never writes or alters BirkNext tables");
            log.Commands.Should().NotBeEmpty();
            var references = log.Commands.SelectMany(TableReferences).ToList();
            references.Should().NotBeEmpty().And.OnlyContain(r => r.StartsWith(DeliveryReportingDbContext.Schema + "."),
                "every command reads or writes delivery_reporting tables only");
        });
    }

    [Fact]
    public async Task HistoricalBaseline_DuplicateSource_ProjectIsolation_AndFrozenReports()
    {
        await WithDatabase(async target =>
        {
            await using var db = Reporting(target);
            await DatabaseMigrationLock.MigrateAsync(db);
            var h = new Harness(db, new Clock(Friday));
            var m2lb = await h.ProjectAsync("M2LB");
            var other = await h.ProjectAsync("Other project");

            // Imported on Friday as a baseline effective three days earlier (a historical git archive).
            var baselineZip = Zip(ChildSearch(8, 3), "0123456789abcdef0123456789abcdef01234567");
            var threeDaysEarlier = Friday.AddDays(-3);
            var baseline = await h.ImportAsync(m2lb, baselineZip, threeDaysEarlier, DeliverySnapshotRole.Baseline, "Tuesday (git archive)");
            baseline.EffectiveAt.Should().Be(threeDaysEarlier);
            baseline.ImportedAt.Should().Be(Friday);
            baseline.SourceType.Should().Be(DeliverySourceType.GitArchive);
            baseline.GitCommit.Should().StartWith("0123456789");

            // The same archive again: warned, refused without confirmation, allowed with it.
            var preview = (await h.Imports.PreviewAsync(m2lb, "m2lb-again.zip", baselineZip, default)).Preview!;
            preview.DuplicateWarning.Should().Contain("already been imported");
            var refused = await h.Imports.CommitAsync(preview.StagingId, new DeliveryImportCommitRequest { ProjectId = m2lb, EffectiveAt = threeDaysEarlier }, default);
            refused.StatusCode.Should().Be(409);
            (await h.Store.SnapshotsAsync(m2lb, default)).Should().ContainSingle("nothing is stored without confirmation");
            // Another project holds its own history: the same archive is not a duplicate there.
            (await h.Imports.PreviewAsync(other, "m2lb.zip", baselineZip, default)).Preview!.DuplicateWarning.Should().BeNull();

            var current = await h.ImportAsync(m2lb, Zip(ChildSearch(8, 7)), Friday, DeliverySnapshotRole.Current, "Friday");
            var foreign = await h.ImportAsync(other, Zip(ChildSearch(8, 1)), Monday, DeliverySnapshotRole.Baseline, "Other Monday");
            (await h.Store.SnapshotsAsync(m2lb, default)).Select(s => s.Id).Should().BeEquivalentTo([baseline.Id, current.Id]);
            (await h.Reports.GenerateAsync(new(m2lb, foreign.Id, current.Id), default)).StatusCode.Should().Be(400, "snapshots never mix across reporting projects");

            var suggestion = (await h.Store.SuggestBaselineAsync(m2lb, current.Id, h.Reports.Zone, default))!;
            suggestion.SnapshotId.Should().Be(baseline.Id);

            var report = (await h.Reports.GenerateAsync(new(m2lb, baseline.Id, current.Id), default)).Report!;
            var story = report.Stories.Single();
            story.Movement.Should().Be(DeliveryMovement.Progressed);
            story.TaskDelta.Should().Be(4);
            var markdown = (await h.Reports.ExportAsync(m2lb, report.ReportId, DeliveryExportFormat.Markdown, new(), default))!.Content;
            var html = (await h.Reports.ExportAsync(m2lb, report.ReportId, DeliveryExportFormat.Html, new(true, true), default))!.Content;

            // Later: more progress imported, a new report, the baseline relabelled and then deleted. The first report does not change.
            h.Clock.Now = Friday.AddDays(1);
            var saturday = await h.ImportAsync(m2lb, Zip(ChildSearch(8, 8)), Friday.AddDays(1), DeliverySnapshotRole.AdHoc, "Saturday");
            (await h.Reports.GenerateAsync(new(m2lb, current.Id, saturday.Id), default)).Report!.Stories.Single().Movement.Should().Be(DeliveryMovement.Completed);
            await h.Store.UpdateSnapshotAsync(m2lb, baseline.Id, new("Renamed baseline", DeliverySnapshotRole.AdHoc), default);
            (await h.Store.DeleteSnapshotAsync(m2lb, baseline.Id, default)).Should().BeTrue();

            (await h.Reports.ExportAsync(m2lb, report.ReportId, DeliveryExportFormat.Markdown, new(), default))!.Content.Should().Be(markdown);
            (await h.Reports.ExportAsync(m2lb, report.ReportId, DeliveryExportFormat.Html, new(true, true), default))!.Content.Should().Be(html);
            (await h.Reports.ListAsync(m2lb, default)).Should().HaveCount(2);
            (await h.Reports.ListAsync(other, default)).Should().BeEmpty();

            // The deleted snapshot's rows are gone; the other project's snapshot is untouched.
            (await db.FeatureSnapshots.CountAsync(f => f.SnapshotId == baseline.Id)).Should().Be(0);
            (await db.TaskSnapshots.CountAsync(t => t.SnapshotId == baseline.Id)).Should().Be(0);
            (await h.Store.SnapshotsAsync(other, default)).Should().ContainSingle(s => s.Id == foreign.Id);
            (await db.WeeklyReports.SingleAsync(r => r.Id == report.ReportId)).BaselineSnapshotId.Should().BeNull();
        });
    }

    /// <summary>Schema-qualified table references of one SQL command (FROM/JOIN/INTO/UPDATE targets).</summary>
    private static IEnumerable<string> TableReferences(string sql) =>
        Regex.Matches(sql, @"\b(?:FROM|JOIN|INTO|UPDATE)\s+((?:""?[\w]+""?\.)?""?[\w]+""?)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace("\"", ""))
            .Where(r => !r.Equals("pg_advisory_lock", StringComparison.OrdinalIgnoreCase));

    private static async Task<List<(string Schema, string Name)>> Tables(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select table_schema, table_name from information_schema.tables where table_schema not in ('pg_catalog','information_schema')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<(string, string)>();
        while (await reader.ReadAsync()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    /// <summary>Every non-reporting table with its row count and column count.</summary>
    private static async Task<Dictionary<string, (long Rows, long Columns)>> PublicState(string connectionString)
    {
        var state = new Dictionary<string, (long, long)>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var (schema, name) in (await Tables(connectionString)).Where(t => t.Schema != DeliveryReportingDbContext.Schema))
        {
            await using var rows = new NpgsqlCommand($"select count(*) from \"{schema}\".\"{name}\"", connection);
            await using var columns = new NpgsqlCommand("select count(*) from information_schema.columns where table_schema = @s and table_name = @n", connection);
            columns.Parameters.AddWithValue("s", schema);
            columns.Parameters.AddWithValue("n", name);
            state[$"{schema}.{name}"] = ((long)(await rows.ExecuteScalarAsync())!, (long)(await columns.ExecuteScalarAsync())!);
        }
        return state;
    }

    private static async Task WithDatabase(Func<string, Task> body)
    {
        var database = "birknext_delivery_reporting_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database, Pooling = false }.ConnectionString;
        await Execute(admin, $"CREATE DATABASE \"{database}\"");
        try { await body(target); }
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
