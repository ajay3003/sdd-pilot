using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.LocalDataReset;
using BirkNext.Maintenance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.LocalDataReset;

/// <summary>
/// Manual test plans are user-authored work: no reset scope removes them by default. Only "Reset everything" with an explicit opt-in, the count
/// the tester reviewed and a backup written first removes them; every refusal deletes nothing. The database step is faked (it records the tables
/// it was asked to keep); AdminService's generic keep-tables mechanism is the one "Reset project data, keep settings" already uses.
/// </summary>
[Collection(LocalDataResetCollection.Name)]
public sealed class TestPlanResetProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-plan-reset-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public TestPlanResetProtectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class RecordingDatabaseReset : ILocalDatabaseReset
    {
        public int Calls;
        public IReadOnlyCollection<Type>? Kept;
        public Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync() => ResetLocalDatabaseAsync([]);
        public Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync(IReadOnlyCollection<Type> keepEntityTypes)
        {
            Calls++;
            Kept = keepEntityTypes;
            return Task.FromResult((true, "ok", 7, (DateTimeOffset?)DateTimeOffset.UtcNow));
        }
    }

    private (LocalDataResetCoordinator Coordinator, RecordingDatabaseReset Database, LocalDataResetState State) Build(string? statePath = null)
    {
        var database = new RecordingDatabaseReset();
        var state = new LocalDataResetState(statePath ?? Path.Combine(_dir, "local-data-reset.json"));
        var services = new ServiceCollection().BuildServiceProvider();
        return (new LocalDataResetCoordinator(database, _db, state, services, NullLogger<LocalDataResetCoordinator>.Instance), database, state);
    }

    private DataResetService Service(LocalDataResetCoordinator coordinator, RecordingDatabaseReset database) =>
        new(database, coordinator, null!, NullLogger<DataResetService>.Instance);

    private async Task SeedPlansAsync(int active, bool withDeleted = false)
    {
        for (var i = 0; i < active; i++)
        {
            var id = Guid.NewGuid();
            _db.TestPlanDrafts.Add(new TestPlanDraftRecord { Id = id, GuideKey = "g", PlanName = $"Testplan {i}", ContentJson = "{}", CreatedBy = "t", UpdatedBy = "t", Revision = 1 });
            _db.TestPlanDraftRevisions.Add(new TestPlanDraftRevisionRecord { Id = Guid.NewGuid(), DraftId = id, Revision = 1, PlanName = $"Testplan {i}", ContentJson = "{}", Reason = "Created", SavedBy = "t" });
            _db.ManualTestRuns.Add(new ManualTestRunRecord { Id = Guid.NewGuid(), DraftId = id, ProjectKey = "p", CaseId = "c1", State = "Completed", Outcome = "Passed", ContentJson = "{}", Tester = "t" });
        }
        if (withDeleted)
            _db.TestPlanDrafts.Add(new TestPlanDraftRecord { Id = Guid.NewGuid(), GuideKey = "g", PlanName = "Deleted plan", ContentJson = "{}", CreatedBy = "t", UpdatedBy = "t", Lifecycle = "Deleted" });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Every_ordinary_reset_keeps_the_manual_test_plan_tables()
    {
        await SeedPlansAsync(2);
        foreach (var (includeProtected, keepSettings) in new[] { (false, false), (true, false), (false, true) })
        {
            var (coordinator, database, _) = Build();
            var result = await coordinator.ResetAsync(includeProtected, keepSettings);
            result.Success.Should().BeTrue();
            database.Kept.Should().Contain(LocalDataResetCoordinator.TestPlanEntityTypes, $"includeProtected={includeProtected}, keepSettings={keepSettings}");
            result.TestPlansPreserved.Should().Be(2);
            result.TestPlansRemoved.Should().Be(0);
            result.PreservedDomains.Should().Contain(LocalDataResetCoordinator.TestPlansKept);
            result.Message.Should().Contain("Manual test plans: 2 kept.");
        }
    }

    [Fact]
    public async Task The_legacy_reset_endpoint_path_keeps_test_plans_too()
    {
        var (coordinator, database, _) = Build();
        await coordinator.ResetAsync();
        database.Kept.Should().Contain(LocalDataResetCoordinator.TestPlanEntityTypes);
    }

    [Fact]
    public async Task Keep_settings_keeps_both_settings_and_plans()
    {
        var (coordinator, database, _) = Build();
        await coordinator.ResetAsync(includeProtected: false, keepSettings: true);
        database.Kept.Should().Contain(LocalDataResetCoordinator.SettingsEntityTypes).And.Contain(LocalDataResetCoordinator.TestPlanEntityTypes);
    }

    [Fact]
    public async Task Removing_plans_outside_reset_everything_is_refused_and_deletes_nothing()
    {
        await SeedPlansAsync(1);
        var (coordinator, database, state) = Build();
        var result = await coordinator.ResetAsync(includeProtected: false, keepSettings: false, removeTestPlans: true);
        result.Status.Should().Be("Refused");
        database.Calls.Should().Be(0);
        state.Epoch.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_removal_writes_a_complete_backup_first_then_empties_the_plan_tables()
    {
        await SeedPlansAsync(2, withDeleted: true);
        var (coordinator, database, state) = Build();

        var result = await coordinator.ResetAsync(includeProtected: true, keepSettings: false, removeTestPlans: true);

        result.Success.Should().BeTrue();
        database.Kept.Should().NotContain(LocalDataResetCoordinator.TestPlanEntityTypes);
        result.TestPlansRemoved.Should().Be(2, "the count is the plans the tester can see (logically deleted ones are not listed)");
        result.TestPlanBackup.Should().StartWith("test-plans-before-reset-").And.EndWith(".json");
        result.ClearedDomains.Should().Contain(LocalDataResetCoordinator.TestPlansRemovedLine);
        result.PreservedDomains.Should().NotContain(LocalDataResetCoordinator.TestPlansKept);

        using var backup = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(state.BackupDirectory, result.TestPlanBackup!)));
        backup.RootElement.GetProperty("test_plan_drafts").GetArrayLength().Should().Be(3, "the backup includes logically deleted plans");
        backup.RootElement.GetProperty("test_plan_draft_revisions").GetArrayLength().Should().Be(2);
        backup.RootElement.GetProperty("manual_test_runs").GetArrayLength().Should().Be(2);
        backup.RootElement.GetProperty("test_plan_drafts").EnumerateArray().Select(d => d.GetProperty("planName").GetString()).Should().Contain("Testplan 0");
    }

    [Fact]
    public async Task When_the_backup_cannot_be_written_the_reset_is_refused_and_deletes_nothing()
    {
        await SeedPlansAsync(1);
        var stateDir = Path.Combine(_dir, "state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "reset-backups"), "a file where the backup directory should be");
        var (coordinator, database, state) = Build(Path.Combine(stateDir, "local-data-reset.json"));

        var result = await coordinator.ResetAsync(includeProtected: true, keepSettings: false, removeTestPlans: true);

        result.Status.Should().Be("Refused");
        result.Message.Should().Contain("Nothing was deleted");
        database.Calls.Should().Be(0);
        state.Epoch.Should().Be(0);
        (await _db.TestPlanDrafts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Plan_shows_the_plan_count_as_kept_for_every_full_scope_and_offers_removal_only_for_reset_everything()
    {
        await SeedPlansAsync(3);
        var (coordinator, database, _) = Build();
        var service = Service(coordinator, database);
        foreach (var mode in new[] { DataResetMode.FullIncludingProtected, DataResetMode.FullPreserveProtected, DataResetMode.ProjectDataKeepSettings })
        {
            var plan = await service.PlanAsync(mode, default);
            plan.TestPlanCount.Should().Be(3);
            plan.Counts["Manual test plans kept"].Should().Be(3);
            plan.Preserved.Should().Contain(LocalDataResetCoordinator.TestPlansKept);
            plan.CanRemoveTestPlans.Should().Be(mode == DataResetMode.FullIncludingProtected);
        }
    }

    [Fact]
    public async Task Reset_everything_without_the_opt_in_keeps_plans()
    {
        await SeedPlansAsync(1);
        var (coordinator, database, _) = Build();
        var result = await Service(coordinator, database).ResetAsync(new DataResetRequest(DataResetMode.FullIncludingProtected, DataResetService.FullConfirmation), default);
        result.Status.Should().Be("Completed");
        result.TestPlansPreserved.Should().Be(1);
        database.Kept.Should().Contain(LocalDataResetCoordinator.TestPlanEntityTypes);
    }

    [Theory]
    [InlineData(DataResetMode.FullPreserveProtected, "CONFIRM")]
    [InlineData(DataResetMode.ProjectDataKeepSettings, "CONFIRM")]
    [InlineData(DataResetMode.DeliveryManagement, "CONFIRM")]
    public async Task The_opt_in_is_refused_for_every_other_scope(DataResetMode mode, string confirmation)
    {
        var (coordinator, database, _) = Build();
        var result = await Service(coordinator, database).ResetAsync(new DataResetRequest(mode, confirmation, RemoveTestPlans: true, ExpectedTestPlans: 0), default);
        result.Status.Should().Be("Refused");
        database.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_stale_or_missing_reviewed_count_is_refused_and_deletes_nothing(int? reviewed)
    {
        await SeedPlansAsync(2);
        var (coordinator, database, state) = Build();
        var result = await Service(coordinator, database).ResetAsync(
            new DataResetRequest(DataResetMode.FullIncludingProtected, DataResetService.FullConfirmation, RemoveTestPlans: true, ExpectedTestPlans: reviewed), default);
        result.Status.Should().Be("Refused");
        result.Message.Should().Contain("now 2 manual test plan(s)");
        database.Calls.Should().Be(0);
        state.Epoch.Should().Be(0);
    }

    [Fact]
    public async Task The_reviewed_count_with_the_opt_in_removes_plans()
    {
        await SeedPlansAsync(2);
        var (coordinator, database, _) = Build();
        var result = await Service(coordinator, database).ResetAsync(
            new DataResetRequest(DataResetMode.FullIncludingProtected, DataResetService.FullConfirmation, RemoveTestPlans: true, ExpectedTestPlans: 2), default);
        result.Status.Should().Be("Completed");
        result.TestPlansRemoved.Should().Be(2);
        result.TestPlanBackup.Should().NotBeNull();
        database.Kept.Should().NotContain(LocalDataResetCoordinator.TestPlanEntityTypes);
    }

    [Fact]
    public void Plan_tables_have_no_foreign_key_to_a_table_the_reset_empties()
    {
        // AdminService refuses a keep-tables reset when a kept table depends on an emptied one; this keeps the default (keep plans) runnable.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var planTables = LocalDataResetCoordinator.TestPlanEntityTypes.Select(t => db.Model.FindEntityType(t)!.GetTableName()).ToHashSet();
        planTables.Should().HaveCount(5);
        var fks = db.Model.GetRelationalModel().Tables.Where(t => planTables.Contains(t.Name)).SelectMany(t => t.ForeignKeyConstraints);
        fks.Where(fk => !planTables.Contains(fk.PrincipalTable.Name)).Should().BeEmpty();
    }
}
