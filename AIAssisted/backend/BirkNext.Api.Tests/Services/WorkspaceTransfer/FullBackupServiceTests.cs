using BirkNext.Api.Data;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.WorkspaceTransfer;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>
/// Full backup and isolated restore against real PostgreSQL. The source is a temporary database built from the migrations, never the person's
/// database. Each test checks the restored state against an independent oracle (its own counts and file bytes), not the service's inventory.
/// pg_dump/pg_restore come from <see cref="PostgresToolsTestGate"/>; without them the tests are skipped with the reason.
/// </summary>
public sealed class FullBackupServiceTests : IAsyncLifetime
{
    private const string Pass = "synthetic backup passphrase 1";
    private static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid SnapshotB = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");
    private static readonly Guid Missing = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000dead");

    private readonly string _source = TestPostgres.TemporaryName("birknext_bksrc");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _created = [];
    private string Source => TestPostgres.Temporary(_source).ConnectionString;

    public async Task InitializeAsync()
    {
        if (PostgresToolsTestGate.SkipReason is not null) return;
        await Execute(TestPostgres.Admin().ConnectionString, $"CREATE DATABASE \"{_source}\"");
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Source).Options)) await DatabaseMigrationLock.MigrateAsync(db);
        foreach (var id in new[] { SnapshotA, SnapshotB })
            await Execute(Source, $$$"""INSERT INTO iqr_source_snapshots ("Id","EnvironmentId","IntegrationId","AnalyzedAt","EvidenceJson") VALUES ('{{{id}}}','','source-analysis',now(),'{"archive":{"fileName":"Synth.zip"}}')""");
        await Execute(Source, $"""INSERT INTO source_schema_artifacts (snapshot_id,snapshot_fingerprint,source_archive_name,captured_at,is_complete,omitted_files,package_json) VALUES ('{SnapshotA}','fp','Synth.zip',now(),true,0,'[]')""");
        await Execute(Source, """INSERT INTO integration_suites ("Id","SuiteKey","Revision","SnapshotId","ScopeId","GeneratedAt","Fingerprint","DefinitionJson") VALUES (gen_random_uuid(),'api:synth:abc',1,'00000000-0000-0000-0000-000000000000','api',now(),'f','[]')""");
        Write("App_Data/critical-e2e/suggestion-reviews.json", $$$"""{"s1":{"suggestionId":"s1","snapshotId":"{{{SnapshotA}}}"},"s2":{"suggestionId":"s2","snapshotId":"{{{Missing}}}"}}""");
        Write("App_Data/test-data/registry.json", $$$"""{"assets":[{"alias":"child","snapshotId":"{{{SnapshotB}}}"}]}""");
        Write("App_Data/local-data-reset.json", """{"epoch":3,"cdcPersonPkFloors":{"env":900001}}""");
        // A secret store that must never travel.
        Write("App_Data/api-service-credentials/secret-0123456789abcdef.bin", "NOT-FOR-BACKUP");
    }

    public async Task DisposeAsync()
    {
        if (PostgresToolsTestGate.SkipReason is not null) return;
        NpgsqlConnection.ClearAllPools();
        foreach (var db in _created.Append(_source))
            await Execute(TestPostgres.Admin().ConnectionString, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_root, "content", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private FullBackupService Service(Func<IReadOnlyDictionary<string, IReadOnlyList<string>>>? migrations = null)
    {
        var content = Path.Combine(_root, "content");
        var files = new ManagedFileStores(content, Path.Combine(_root, "git"), Path.Combine(content, "App_Data", "local-data-reset.json"));
        return new FullBackupService(Source, PostgresToolsTestGate.Tools(), files, new FullBackupOptions { KeyDerivationIterations = TransferCrypto.MinIterations },
            Path.Combine(_root, "backups"), migrations ?? CodeMigrationsOfSource, NullLogger<FullBackupService>.Instance);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CodeMigrationsOfSource()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=unused").Options);
        return new Dictionary<string, IReadOnlyList<string>> { ["public.__EFMigrationsHistory"] = db.Database.GetMigrations().ToList() };
    }

    [PostgresToolsFact]
    public async Task Backup_then_isolated_restore_reproduces_database_and_files_exactly_and_never_touches_the_source()
    {
        var service = Service();
        var before = await Oracle(Source);

        var created = await service.CreateAsync(Pass, "acceptance", default);
        created.BackupCompleteness.Should().Be("COMPLETE");
        created.ConsistencyMethod.Should().Be("DATABASE_EXPORTED_SNAPSHOT; FILES_UNCHANGED_ACROSS_SNAPSHOT");
        created.Excluded.Should().Contain(e => e.Class == StorageClass.SecretStore && e.Component.Contains("api-service-credentials"));
        var backupFile = Path.Combine(service.BackupDirectory, created.Backup.FileName);
        var raw = await File.ReadAllBytesAsync(backupFile);
        System.Text.Encoding.UTF8.GetString(raw).Should().NotContain("suggestionId").And.NotContain("NOT-FOR-BACKUP", "the payload is encrypted and secret stores are excluded");
        Directory.GetFiles(service.BackupDirectory, "*.partial").Should().BeEmpty();

        var inspection = await service.InspectAsync(created.Backup.FileName, Pass, default);
        inspection.Verdict.Should().Be("PASS", string.Join(" | ", inspection.Checks.Select(c => $"{c.Name}:{c.Status}:{c.Detail}")));
        inspection.Manifest!.ManagedFileInventory.Select(f => $"{f.Store}/{f.RelativePath}").Should().BeEquivalentTo(
            "critical-e2e/suggestion-reviews.json", "test-data/registry.json", "local-data-reset/local-data-reset.json");
        inspection.Manifest.FileSnapshotReferences.Should().Be(3);
        inspection.Manifest.DanglingFileReferences.Should().Equal(Missing.ToString());

        var report = await service.RestoreIsolatedAsync(created.Backup.FileName, Pass, default);
        _created.Add(report.RestoredDatabase);
        report.Verdict.Should().Be("VERIFIED_WITH_WARNINGS", string.Join(" | ", report.Checks.Select(c => $"{c.Name}:{c.Status}:{c.Detail}")));
        report.Checks.Where(c => c.Status == TransferCheckStatus.Warning).Select(c => c.Name).Should().BeEquivalentTo("File → database references", "Secrets");
        report.RestoredDatabase.Should().MatchRegex("^birknext_restore_[0-9a-f]{12}$").And.NotBe(_source);

        // Independent oracle: counts and key rows read directly from both databases; files compared byte by byte.
        var restored = await Oracle(TestPostgres.Temporary(report.RestoredDatabase).ConnectionString);
        restored.Should().BeEquivalentTo(before);
        (await Oracle(Source)).Should().BeEquivalentTo(before, "the source database is never written");
        File.ReadAllText(Path.Combine(report.RestoredFilesDirectory, "critical-e2e", "suggestion-reviews.json"))
            .Should().Be(File.ReadAllText(Path.Combine(_root, "content", "App_Data", "critical-e2e", "suggestion-reviews.json")));
        Directory.EnumerateFiles(report.RestoredFilesDirectory, "*", SearchOption.AllDirectories).Should().NotContain(f => f.Contains("api-service-credentials"));
        Directory.GetDirectories(Path.Combine(service.BackupDirectory, "restores", report.RestoreId.ToString("N"))).Select(Path.GetFileName).Should().Equal(["files"], "the decrypted staging copy is removed");
        service.Restores().Should().ContainSingle(r => r.RestoreId == report.RestoreId);
    }

    [PostgresToolsFact]
    public async Task A_wrong_passphrase_or_a_tampered_backup_fails_without_creating_a_database()
    {
        var service = Service();
        var created = await service.CreateAsync(Pass, null, default);
        var restoresBefore = await RestoreDatabases();

        var wrong = await service.RestoreIsolatedAsync(created.Backup.FileName, "another passphrase 99", default);
        wrong.Verdict.Should().Be("FAILED");
        wrong.Checks.Should().Contain(c => c.Name == "Decryption and authenticity" && c.Status == TransferCheckStatus.Fail);

        var path = Path.Combine(service.BackupDirectory, created.Backup.FileName);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x40;
        var tamperedName = "tampered-" + created.Backup.FileName;
        await File.WriteAllBytesAsync(Path.Combine(service.BackupDirectory, tamperedName), bytes);
        var tampered = await service.RestoreIsolatedAsync(tamperedName, Pass, default);
        tampered.Verdict.Should().Be("FAILED");

        (await RestoreDatabases()).Should().BeEquivalentTo(restoresBefore, "nothing is restored from an unauthenticated backup");
    }

    [PostgresToolsFact]
    public async Task A_backup_from_a_newer_build_is_refused_before_any_restore()
    {
        var service = Service();
        var created = await service.CreateAsync(Pass, null, default);
        var older = Service(() => new Dictionary<string, IReadOnlyList<string>> { ["public.__EFMigrationsHistory"] = CodeMigrationsOfSource()["public.__EFMigrationsHistory"].SkipLast(1).ToList() });
        var restoresBefore = await RestoreDatabases();
        var report = await older.RestoreIsolatedAsync(created.Backup.FileName, Pass, default);
        report.Verdict.Should().Be("FAILED");
        report.Checks.Should().Contain(c => c.Name == "Compatibility" && c.Status == TransferCheckStatus.Fail && c.Detail.Contains("newer BirkNext"));
        (await RestoreDatabases()).Should().BeEquivalentTo(restoresBefore);
    }

    [PostgresToolsFact]
    public async Task A_file_changing_during_capture_is_retried_and_labelled_when_it_keeps_changing()
    {
        var service = Service();
        var path = Path.Combine(_root, "content", "App_Data", "test-data", "registry.json");
        service.AfterSnapshotExported = attempt => { if (attempt == 1) File.AppendAllText(path, " "); };
        (await service.CreateAsync(Pass, null, default)).BackupCompleteness.Should().Be("COMPLETE", "the second attempt saw a stable file");

        service.AfterSnapshotExported = _ => File.AppendAllText(path, " ");
        var drifting = await service.CreateAsync(Pass, null, default);
        drifting.BackupCompleteness.Should().Be("COMPLETE_WITH_FILE_DRIFT");
        drifting.ConsistencyMethod.Should().EndWith("FILES_CHANGED_DURING_CAPTURE");
        drifting.Warnings.Should().ContainSingle().Which.Should().Contain("not point-in-time");
    }

    [PostgresToolsFact]
    public async Task Refusals_leave_no_backup_file_behind()
    {
        var service = Service();
        (await FluentActions.Awaiting(() => service.CreateAsync("short", null, default)).Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("PASSPHRASE");
        service.FreeBytes = _ => 1024;
        (await FluentActions.Awaiting(() => service.CreateAsync(Pass, null, default)).Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("INSUFFICIENT_DISK_SPACE");
        var broken = new FullBackupService(Source, new PostgresTools(new PostgresToolOptions { Mode = "ContainerExec", Container = "no-such-container-" + Guid.NewGuid().ToString("N")[..6] }),
            new ManagedFileStores(_root, _root, Path.Combine(_root, "x.json")), new FullBackupOptions(), service.BackupDirectory, CodeMigrationsOfSource, NullLogger<FullBackupService>.Instance);
        (await FluentActions.Awaiting(() => broken.CreateAsync(Pass, null, default)).Should().ThrowAsync<TransferRequestException>()).Which.Code.Should().Be("TOOLS_UNAVAILABLE");
        (Directory.Exists(service.BackupDirectory) ? Directory.GetFiles(service.BackupDirectory) : []).Should().BeEmpty();
        FluentActions.Invoking(() => service.ResolveBackup("../escape.bnbackup")).Should().Throw<TransferRequestException>().Which.Code.Should().Be("BAD_NAME");
    }

    private static async Task<Dictionary<string, string>> Oracle(string connection)
    {
        var result = new Dictionary<string, string>();
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        foreach (var (key, sql) in new[]
        {
            ("snapshots", """SELECT string_agg("Id"::text, ',' ORDER BY "Id") FROM iqr_source_snapshots"""),
            ("schema-artifacts", "SELECT string_agg(snapshot_id::text || ':' || snapshot_fingerprint, ',') FROM source_schema_artifacts"),
            ("suites", """SELECT string_agg("SuiteKey" || '@' || "Revision", ',') FROM integration_suites"""),
            ("migrations", """SELECT count(*)::text || '/' || max("MigrationId") FROM "__EFMigrationsHistory" """),
            ("tables", "SELECT count(*)::text FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')"),
            ("fks", "SELECT count(*)::text FROM pg_constraint WHERE contype = 'f'"),
        })
        {
            await using var cmd = new NpgsqlCommand(sql, db);
            result[key] = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        }
        return result;
    }

    private static async Task<List<string>> RestoreDatabases()
    {
        var names = new List<string>();
        await using var db = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE 'birknext\\_restore\\_%'", db);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private static async Task Execute(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }
}
