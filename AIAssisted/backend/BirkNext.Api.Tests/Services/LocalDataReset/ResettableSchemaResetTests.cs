using BirkNext.Api.Data;
using BirkNext.Api.Data.TaskAlignment;
using BirkNext.Api.Services.LocalDataReset;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.LocalDataReset;

/// <summary>
/// Separate-schema stores registered in <see cref="ResettableSchemas"/> (Task Alignment runs) are emptied by a full local data reset, are
/// listed in the plan/result, and stay untouched when the database reset itself is refused or fails. The database step is faked; the
/// separate schema runs on the EF in-memory provider (the relational path deletes the same model tables with one DELETE each).
/// </summary>
/// <summary>Local data reset holds a process-wide gate (one reset at a time, by design): tests that drive a reset run in this non-parallel
/// collection, otherwise a reset in one class sees another class's reset in progress and correctly reports "Blocked".</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalDataResetCollection { public const string Name = "Local data reset (process-wide gate)"; }

[Collection(LocalDataResetCollection.Name)]
public sealed class ResettableSchemaResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-schema-reset-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbName = "task-alignment-" + Guid.NewGuid().ToString("N");
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public ResettableSchemaResetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeDatabaseReset(bool success, string message = "ok") : ILocalDatabaseReset
    {
        public Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync() =>
            Task.FromResult((success, message, success ? 1 : 0, success ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
    }

    private (LocalDataResetCoordinator Coordinator, ServiceProvider Services) Build(bool databaseResetSucceeds)
    {
        var services = new ServiceCollection()
            .AddDbContext<TaskAlignmentDbContext>(o => o.UseInMemoryDatabase(_dbName))
            .AddDbContext<BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext>(o => o.UseInMemoryDatabase(_dbName + "-scopes"))
            .BuildServiceProvider();
        var state = new LocalDataResetState(Path.Combine(_dir, "local-data-reset.json"));
        var database = new FakeDatabaseReset(databaseResetSucceeds, databaseResetSucceeds ? "ok" : "Reset failed. See server logs for details.");
        return (new LocalDataResetCoordinator(database, _db, state, services, NullLogger<LocalDataResetCoordinator>.Instance), services);
    }

    private static async Task SeedRunsAsync(IServiceProvider services, int count)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskAlignmentDbContext>();
        for (var i = 0; i < count; i++)
            db.Runs.Add(new TaskAlignmentRunRecord
            {
                Id = Guid.NewGuid(), ProjectKey = "import:abc", TasksPath = "shop/specs/001/tasks.md", TasksHash = new string('a', 64), SpecHash = new string('b', 64),
                RulesVersion = "r1", DesignInputsKey = "none", RecordVersion = "1", Completeness = "Complete", AnalyzedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    private static int CountRuns(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TaskAlignmentDbContext>().Runs.Count();
    }

    [Fact]
    public void Registry_ContainsTaskAlignment_AndTheCoordinatorListsItAsCleared()
    {
        var entry = Assert.Single(ResettableSchemas.All, s => s.ContextType == typeof(TaskAlignmentDbContext));
        Assert.Equal("task_alignment", entry.Schema);
        Assert.Contains(entry.Description, LocalDataResetCoordinator.Cleared);
        Assert.All(ResettableSchemas.All, s => Assert.True(typeof(DbContext).IsAssignableFrom(s.ContextType)));
        Assert.Equal(ResettableSchemas.All.Count, ResettableSchemas.All.Select(s => s.Domain).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FullReset_RemovesTaskAlignmentRuns_AndReportsTheDomain()
    {
        var (coordinator, services) = Build(databaseResetSucceeds: true);
        await SeedRunsAsync(services, 3);

        var result = await coordinator.ResetAsync();

        Assert.Equal("Completed", result.Status);
        Assert.Equal(0, CountRuns(services));
        Assert.Contains(result.Domains, d => d.Domain == "Task Alignment" && d.Status == "Removed");
        Assert.Contains(result.ClearedDomains, d => d.Contains("Task Alignment run history", StringComparison.Ordinal));
        Assert.Contains(result.PreservedDomains, d => d.Contains("migration history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedOrRefusedDatabaseReset_LeavesTaskAlignmentRunsUntouched()
    {
        var (coordinator, services) = Build(databaseResetSucceeds: false);
        await SeedRunsAsync(services, 2);

        var result = await coordinator.ResetAsync();

        Assert.False(result.Success);
        Assert.Equal(2, CountRuns(services));
    }

    [Fact]
    public async Task FullReset_IsIdempotent_OnAnEmptyTaskAlignmentSchema()
    {
        var (coordinator, services) = Build(databaseResetSucceeds: true);
        Assert.Equal("Completed", (await coordinator.ResetAsync()).Status);
        Assert.Equal("Completed", (await coordinator.ResetAsync()).Status);
        Assert.Equal(0, CountRuns(services));
    }

    [Fact]
    public async Task DeliveryOnlyPlan_ListsTaskAlignmentAsNotTouched()
    {
        // PlanAsync reads Delivery counts first; a missing delivery store is caught and logged, so the plan still lists what stays.
        var plan = await new DataResetService(new Guard(), null!, null!, NullLogger<DataResetService>.Instance)
            .PlanAsync(BirkNext.Maintenance.DataResetMode.DeliveryManagement, CancellationToken.None);
        Assert.Contains("Task Alignment", plan.Preserved);
    }

    private static async Task SeedScopeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext>();
        var id = Guid.NewGuid();
        db.Scopes.Add(new BirkNext.Api.Data.ScopeRegistry.ScopeRecord { Id = id, ProjectKey = "import:abc", Name = "Report intake", Purpose = "Test", State = "Approved", CurrentRevision = 1, ApprovedRevision = 1 });
        db.Revisions.Add(new BirkNext.Api.Data.ScopeRegistry.ScopeRevisionRecord { Id = Guid.NewGuid(), ScopeId = id, Number = 1, State = "Approved", CreatedBy = "tester", ApprovedBy = "lead" });
        await db.SaveChangesAsync();
    }

    private static int CountScopes(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext>();
        return db.Scopes.Count() + db.Revisions.Count();
    }

    [Fact]
    public async Task OrdinaryReset_KeepsSharedTestAreasAndSaysWhy()
    {
        var (coordinator, services) = Build(databaseResetSucceeds: true);
        await SeedScopeAsync(services);

        var result = await coordinator.ResetAsync(includeProtected: false);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, CountScopes(services));
        Assert.DoesNotContain(result.Domains, d => d.Domain == "Shared test areas");
        Assert.Contains(LocalDataResetCoordinator.SharedTestAreasKept, result.PreservedDomains);
        Assert.DoesNotContain(result.ClearedDomains, d => d.Contains("scope_registry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetEverything_RemovesSharedTestAreasWithTheirRevisions()
    {
        var (coordinator, services) = Build(databaseResetSucceeds: true);
        await SeedScopeAsync(services);

        var result = await coordinator.ResetAsync(includeProtected: true);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(0, CountScopes(services));
        Assert.Contains(result.Domains, d => d.Domain == "Shared test areas" && d.Status == "Removed");
        Assert.Contains(result.ClearedDomains, d => d.Contains("scope_registry", StringComparison.Ordinal));
        Assert.DoesNotContain(LocalDataResetCoordinator.SharedTestAreasKept, result.PreservedDomains);
    }

    [Fact]
    public async Task Plans_StateTheSharedTestAreaPolicyPerScope()
    {
        var service = new DataResetService(new Guard(), null!, null!, NullLogger<DataResetService>.Instance);
        var keep = await service.PlanAsync(BirkNext.Maintenance.DataResetMode.FullPreserveProtected, CancellationToken.None);
        var all = await service.PlanAsync(BirkNext.Maintenance.DataResetMode.FullIncludingProtected, CancellationToken.None);
        Assert.Contains(LocalDataResetCoordinator.SharedTestAreasKept, keep.Preserved);
        Assert.DoesNotContain(keep.Removed, r => r.Contains("scope_registry", StringComparison.Ordinal));
        Assert.Contains(all.Removed, r => r.Contains("scope_registry", StringComparison.Ordinal));
        Assert.True(Assert.Single(ResettableSchemas.All, s => s.Schema == "scope_registry").OnlyWhenResettingEverything);
    }

    private sealed class Guard : ILocalDatabaseReset
    {
        public Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync() => throw new NotSupportedException();
        public string? ResetRefusal() => null;
    }
}
