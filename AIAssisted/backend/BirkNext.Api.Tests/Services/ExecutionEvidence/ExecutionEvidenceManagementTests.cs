using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ExecutionEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.ExecutionEvidence;

/// <summary>
/// Storage management of execution evidence, on the in-memory provider and on PostgreSQL (fresh migrated temporary database): deleting one run
/// removes exactly its calls, protected and running runs survive bulk deletion and retention, confirmations must match what was shown, retention
/// is disabled by default and previewed before it is applied, storage usage is measured, a body can be removed keeping its hash, integrity is
/// re-verified, and exports carry references and verifiable hashes without credentials. Every destructive action leaves an audit entry.
/// </summary>
public abstract class ExecutionEvidenceManagementScenarios
{
    protected abstract AppDbContext Context();

    private const string Secret = "Bearer synthetic-secret-value-123456";

    protected async Task<Guid> SeedAsync(string environment, DateTimeOffset started, int calls, bool finished = true)
    {
        await using var db = Context();
        var store = new ExecutionEvidenceStore(db);
        var runId = Guid.NewGuid();
        CapturedBody Body(string text) => ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetByteCount(text), "application/json", CapturePolicy.BoundedBody, 64 * 1024, false);
        var records = Enumerable.Range(1, calls).Select(i => new ExecutionCallRecord
        {
            CallId = Guid.NewGuid(), Sequence = i, State = finished ? ExecutionState.Passed : ExecutionState.Running, Authentication = CallAuthenticationOutcome.Authenticated,
            Definition = new() { Name = $"call {i}", Method = "POST", Path = "/children", TestDataRefs = ["children@v2"], DatasetRow = $"children@v2#r-{i:D3}",
                OperationRef = "children-api@v1#POST /children", DefinitionVersion = "contract-test:aa@r3" },
            Attempts =
            [
                new()
                {
                    AttemptNumber = 1, Transport = TransportResult.ResponseReceived, Transmissions = 1, AuthenticationSession = "api-credential:dev:ClientCredentials:v1#t1",
                    Request = new() { Method = "POST", Url = "https://api.synthetic.test/children", Headers = [new("Authorization", ExecutionEvidenceRedaction.Mask, true)], Body = Body($$"""{"name":"Barn {{i}}","age":{{i}}}""") },
                    Response = new() { StatusCode = 201, Body = Body($$"""{"id":{{i}},"name":"Barn {{i}}"}""") },
                },
            ],
        }).ToList();
        await store.CreateAsync(new ExecutionRunSummary
        {
            RunId = runId, Name = $"run {started:yyyy-MM-dd}", EnvironmentId = environment, State = ExecutionState.Running, StartedAt = started, ContractRefs = ["children-api@v1"],
            AuthenticationMode = "Unattended: OAuth2 client credentials", AuthenticationReference = "api-credential:dev:ClientCredentials:v1", ChainRef = "contract-chain:cc@r1",
        }, records, default);
        if (finished) await store.CompleteAsync(runId, null, default);
        return runId;
    }

    private ExecutionEvidenceManagement Manage(AppDbContext db) => new(db);

    private async Task<int> CallCountAsync(Guid runId) { await using var db = Context(); return await db.ExecutionCalls.CountAsync(c => c.RunId == runId); }

    [Fact]
    public async Task Deleting_one_run_removes_exactly_its_calls_and_leaves_every_other_run()
    {
        var a = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-2), 3);
        var b = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-1), 2);
        await using (var db = Context())
        {
            var result = await Manage(db).DeleteAsync(new() { RunIds = [a], Confirm = 1 }, "tester", default);
            result.Should().Match<ExecutionDeletionResult>(r => r.Deleted == 1 && r.Calls == 3 && r.Bytes > 0 && r.Skipped.Count == 0);
        }
        (await CallCountAsync(a)).Should().Be(0);
        (await CallCountAsync(b)).Should().Be(2);
        await using var check = Context();
        (await check.ExecutionRuns.AnyAsync(r => r.Id == a)).Should().BeFalse();
        (await check.ExecutionRuns.AnyAsync(r => r.Id == b)).Should().BeTrue();
        (await Manage(check).AuditAsync(10, default)).Should().ContainSingle(e => e.Action == "Deleted" && e.Runs == 1 && e.Calls == 3 && e.Initiator == "tester" && e.Detail!.Contains(a.ToString()));
    }

    [Fact]
    public async Task Protected_and_running_runs_survive_bulk_deletion_and_a_stale_confirmation_deletes_nothing()
    {
        var kept = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-3), 2);
        var running = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-2), 2, finished: false);
        var done = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-1), 2);
        var other = await SeedAsync("qa", DateTimeOffset.UtcNow.AddDays(-1), 1);
        await using var db = Context();
        var manage = Manage(db);
        (await manage.ProtectAsync(kept, true, "tester", default)).Done.Should().BeTrue();
        (await manage.DeleteAsync(new() { RunIds = [kept, running, done], Confirm = 2 }, "tester", default)).Deleted.Should().Be(0, "3 runs were shown, 2 confirmed");
        var result = await manage.DeleteAsync(new() { RunIds = [kept, running, done], Confirm = 3 }, "tester", default);
        result.Deleted.Should().Be(1);
        result.Skipped.Should().BeEquivalentTo([new ExecutionDeletionSkip(kept, "Protected: remove the protection first."), new ExecutionDeletionSkip(running, "Still running: cancel it first.")]);
        var single = await manage.DeleteAsync(new() { RunIds = [kept], Confirm = 1 }, "tester", default);
        single.Deleted.Should().Be(0);
        (await CallCountAsync(kept)).Should().Be(2);
        (await CallCountAsync(running)).Should().Be(2);
        (await CallCountAsync(done)).Should().Be(0);

        var eligible = await manage.EligibleAsync("qa", default);
        eligible.RunIds.Should().Equal(other);
        (await manage.DeleteAsync(new() { EnvironmentId = "qa", AllEligible = true, Confirm = 1 }, "tester", default)).Deleted.Should().Be(1);
        (await manage.EligibleAsync("dev", default)).Should().Match<ExecutionRetentionPreview>(p => p.Runs == 0 && p.ProtectedKept == 1 && p.RunningKept == 1);
    }

    [Fact]
    public async Task Retention_is_disabled_by_default_previewed_as_a_dry_run_and_applied_only_with_a_matching_confirmation()
    {
        var old = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-40), 3);
        var oldProtected = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-45), 1);
        var recent = await SeedAsync("dev", DateTimeOffset.UtcNow.AddDays(-5), 2);
        await using var db = Context();
        var manage = Manage(db);
        (await manage.RetentionAsync(default)).Mode.Should().Be(RetentionMode.Disabled);
        var disabled = await manage.PreviewAsync(null, default);
        disabled.Runs.Should().Be(0);
        (await manage.ApplyRetentionAsync(null, 0, "tester", default)).Deleted.Should().Be(0);
        (await manage.SaveRetentionAsync(new(RetentionMode.Days, 0), "tester", default)).Error.Should().Contain("between 1 and");
        (await manage.SaveRetentionAsync(new(RetentionMode.Days, 30), "tester", default)).Settings!.Days.Should().Be(30);
        await manage.ProtectAsync(oldProtected, true, "tester", default);
        var preview = await manage.PreviewAsync(null, default);
        preview.RunIds.Should().Equal(old);
        preview.Should().Match<ExecutionRetentionPreview>(p => p.Calls == 3 && p.Bytes > 0 && p.ProtectedKept == 1);
        (await CallCountAsync(old)).Should().Be(3, "a preview deletes nothing");
        (await manage.ApplyRetentionAsync(null, 2, "tester", default)).Message.Should().Contain("changed");
        (await CallCountAsync(old)).Should().Be(3);
        (await manage.ApplyRetentionAsync(null, 1, "tester", default)).Deleted.Should().Be(1);
        (await CallCountAsync(old)).Should().Be(0);
        (await CallCountAsync(oldProtected)).Should().Be(1);
        (await CallCountAsync(recent)).Should().Be(2);
        (await manage.AuditAsync(10, default)).Select(e => e.Action).Should().Contain(["RetentionChanged", "RetentionApplied", "Protected"]);
    }

    [Fact]
    public async Task Storage_usage_is_measured_from_the_stored_documents_and_drops_after_deletion()
    {
        var a = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-3), 4);
        var b = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-1), 1);
        await using var db = Context();
        var expected = (await db.ExecutionRuns.AsNoTracking().Select(r => r.SummaryJson).ToListAsync()).Sum(s => (long)Encoding.UTF8.GetByteCount(s))
                     + (await db.ExecutionCalls.AsNoTracking().Select(c => c.DocumentJson).ToListAsync()).Sum(s => (long)Encoding.UTF8.GetByteCount(s));
        var usage = await Manage(db).UsageAsync(null, default);
        usage.Should().Match<ExecutionStorageUsage>(u => u.Runs == 2 && u.LogicalCalls == 5 && u.Attempts == 5 && u.StoredBytes == expected && u.ProtectedRuns == 0);
        usage.Location.Should().Contain("PostgreSQL").And.Contain("execution_calls");
        usage.Note.Should().Contain("No evidence files exist");
        AssertTableSize(usage.DatabaseTableBytes);
        await Manage(db).DeleteAsync(new() { RunIds = [a], Confirm = 1 }, "tester", default);
        var after = await Manage(db).UsageAsync("dev", default);
        after.Should().Match<ExecutionStorageUsage>(u => u.Runs == 1 && u.LogicalCalls == 1 && u.StoredBytes < expected && u.DatabaseTableBytes == null);
        (await CallCountAsync(b)).Should().Be(1);
    }

    protected abstract void AssertTableSize(long? bytes);

    [Fact]
    public async Task A_removed_body_keeps_its_hash_and_size_and_integrity_detects_a_changed_stored_body()
    {
        var run = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-1), 2);
        await using var db = Context();
        var manage = Manage(db);
        (await manage.IntegrityAsync(run, default))!.Should().Match<ExecutionIntegrityReport>(r => r.Intact && r.BodiesChecked == 4 && r.BodiesRemoved == 0);
        var first = await db.ExecutionCalls.AsNoTracking().FirstAsync(c => c.RunId == run && c.Sequence == 1);
        var before = JsonSerializer.Deserialize<ExecutionCallRecord>(first.DocumentJson, ExecutionEvidenceStore.Json)!.Attempts[0].Response!.Body!;
        (await manage.RemoveBodiesAsync(run, first.Id, "tester", default)).Removed.Should().Be(2);
        await using (var check = Context())
        {
            var after = JsonSerializer.Deserialize<ExecutionCallRecord>((await check.ExecutionCalls.AsNoTracking().FirstAsync(c => c.Id == first.Id)).DocumentJson, ExecutionEvidenceStore.Json)!;
            after.Attempts[0].Response!.Body!.Should().Match<CapturedBody>(b => b.Text == null && b.Completeness == CaptureCompleteness.Removed && b.Sha256 == before.Sha256 && b.ByteCount == before.ByteCount);
            after.Attempts[0].Response!.StatusCode.Should().Be(201, "metadata stays");
        }
        (await manage.IntegrityAsync(run, default))!.Should().Match<ExecutionIntegrityReport>(r => r.Intact && r.BodiesChecked == 2 && r.BodiesRemoved == 2);

        await using (var tamper = Context())
        {
            var second = await tamper.ExecutionCalls.FirstAsync(c => c.RunId == run && c.Sequence == 2);
            second.DocumentJson = second.DocumentJson.Replace("Barn 2", "Barn X", StringComparison.Ordinal);
            await tamper.SaveChangesAsync();
        }
        await using var verify = Context();
        (await Manage(verify).IntegrityAsync(run, default))!.Mismatches.Should().HaveCount(2).And.OnlyContain(m => m.StartsWith("Call 2"));
        await Manage(verify).ProtectAsync(run, true, "tester", default);
        (await Manage(verify).RemoveBodiesAsync(run, first.Id, "tester", default)).Error.Should().Contain("protected");
    }

    [Fact]
    public async Task An_export_of_selected_runs_has_references_verifiable_hashes_and_no_credentials()
    {
        var a = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-2), 2);
        var b = await SeedAsync("dev", DateTimeOffset.UtcNow.AddHours(-1), 1);
        await using var db = Context();
        var (zip, error) = await Manage(db).ExportAsync([a, b], "tester", default);
        error.Should().BeNull();
        using var archive = new ZipArchive(new MemoryStream(zip!));
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["manifest.json", "hashes.json", $"runs/{a:N}/manifest.json", $"runs/{a:N}/calls/0001.json",
            $"runs/{a:N}/calls/0002.json", $"runs/{b:N}/manifest.json", $"runs/{b:N}/calls/0001.json"]);
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(new StreamReader(archive.GetEntry("hashes.json")!.Open()).ReadToEnd())!;
        foreach (var entry in archive.Entries.Where(e => e.FullName != "hashes.json"))
        {
            using var ms = new MemoryStream(); entry.Open().CopyTo(ms);
            Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant().Should().Be(hashes[entry.FullName]);
            Encoding.UTF8.GetString(ms.ToArray()).Should().NotContain(Secret).And.NotContain("synthetic-secret");
        }
        var manifest = JsonDocument.Parse(new StreamReader(archive.GetEntry($"runs/{a:N}/manifest.json")!.Open()).ReadToEnd()).RootElement;
        manifest.GetProperty("references").GetProperty("testData").EnumerateArray().Select(x => x.GetString()).Should().Equal("children@v2");
        manifest.GetProperty("references").GetProperty("datasetRows").GetArrayLength().Should().Be(2);
        manifest.GetProperty("references").GetProperty("chain").GetString().Should().Be("contract-chain:cc@r1");
        manifest.GetProperty("authentication").GetProperty("reference").GetString().Should().Be("api-credential:dev:ClientCredentials:v1");
        manifest.GetProperty("correlation").GetString().Should().Contain("X-BirkNext-Execution-Id");
        (await Manage(db).ExportAsync([.. Enumerable.Range(0, 21).Select(_ => Guid.NewGuid())], "tester", default)).Error.Should().Contain("1 to 20");
        (await Manage(db).AuditAsync(5, default)).Should().Contain(e => e.Action == "Exported" && e.Runs == 2 && e.Calls == 3);
    }
}

public sealed class ExecutionEvidenceManagementInMemoryTests : ExecutionEvidenceManagementScenarios
{
    private readonly string _name = $"manage-{Guid.NewGuid():N}";
    protected override AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options);
    protected override void AssertTableSize(long? bytes) => bytes.Should().BeNull("the in-memory provider has no table size");
}

/// <summary>The same scenarios on PostgreSQL: single-transaction deletion, measured octet lengths and the real table size.</summary>
public sealed class ExecutionEvidenceManagementPostgresTests : ExecutionEvidenceManagementScenarios, IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_evidence_manage");
    protected override AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);
    protected override void AssertTableSize(long? bytes) => bytes.Should().BeGreaterThan(0, "PostgreSQL reports the tables' size");

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
}
