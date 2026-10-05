using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.LocalDataReset;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Local data reset across the database, the reset epoch (stale-tab protection), the CDC key floor and the backend state outside the
/// database. The database step is faked here: these tests never touch a real database. The transactional table deletion is covered by
/// the live smoke against an isolated database (docs/local-data-reset.md).
/// </summary>
public sealed class LocalDataResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-reset-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public LocalDataResetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeDatabaseReset(bool success = true, string message = "ok", TaskCompletionSource? hold = null) : ILocalDatabaseReset
    {
        public int Calls;
        public async Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync()
        {
            Interlocked.Increment(ref Calls);
            if (hold is not null) await hold.Task;
            return (success, message, success ? 42 : 0, success ? DateTimeOffset.UtcNow : null);
        }
    }

    private string StatePath => Path.Combine(_dir, "local-data-reset.json");

    private (LocalDataResetCoordinator Coordinator, ServiceProvider Services, CriticalE2EStore E2E, ClassificationTestContextStore Contexts) Build(ILocalDatabaseReset database, LocalDataResetState? state = null)
    {
        var e2e = new CriticalE2EStore(Path.Combine(_dir, "critical-e2e"), NullLogger<CriticalE2EStore>.Instance);
        var contexts = new ClassificationTestContextStore();
        var services = new ServiceCollection()
            .AddSingleton<ICriticalE2EStore>(e2e)
            .AddSingleton(contexts)
            .AddSingleton(new BrowserAutomationEvidenceStore())
            .BuildServiceProvider();
        var coordinator = new LocalDataResetCoordinator(database, _db, state ?? new LocalDataResetState(StatePath), services, NullLogger<LocalDataResetCoordinator>.Instance);
        return (coordinator, services, e2e, contexts);
    }

    [Fact]
    public void Epoch_StartsAtZero_AdvancesAndSurvivesARestart()
    {
        var state = new LocalDataResetState(StatePath);
        Assert.Equal(0, state.Epoch);
        Assert.True(state.Accepts(null), "before any reset, older clients without an epoch are accepted");

        Assert.Equal(1, state.Advance(DateTimeOffset.UtcNow, new Dictionary<string, int> { ["env-a"] = 900_010 }));
        Assert.Equal(2, state.Advance(DateTimeOffset.UtcNow, new Dictionary<string, int> { ["env-a"] = 900_005, ["env-b"] = 900_100 }));

        var restarted = new LocalDataResetState(StatePath);
        Assert.Equal(2, restarted.Epoch);
        Assert.Equal(900_010, restarted.CdcPersonPkFloor("env-a"));
        Assert.Equal(900_100, restarted.CdcPersonPkFloor("env-b"));
        Assert.NotNull(restarted.LastResetAt);
        Assert.True(restarted.Accepts(2));
        Assert.False(restarted.Accepts(1), "a write from before the last reset is refused");
        Assert.False(restarted.Accepts(null), "after a reset, a write without an epoch is refused");
    }

    [Fact]
    public async Task Reset_ClearsBackendStateOutsideTheDatabase_AndAdvancesTheEpoch()
    {
        var (coordinator, _, e2e, contexts) = Build(new FakeDatabaseReset());
        e2e.Save(new BirkNext.CriticalE2E.CriticalE2EFlowDefinition { Id = "flow-1", EnvironmentId = "m2lb-dev", Name = "Gradert tilgang", Module = "Gradert tilgang" });
        contexts.Set("child", "m2lb-dev", new ClassificationTestContext { Environment = "DEV" });

        var result = await coordinator.ResetAsync();

        Assert.Equal("Completed", result.Status);
        Assert.True(result.Success);
        Assert.True(result.DatabaseCleared);
        Assert.True(result.BackendStateCleared);
        Assert.Equal(1, result.ResetEpoch);
        Assert.Empty(e2e.Flows("m2lb-dev"));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(_dir, "critical-e2e", "flows.json")).Trim());
        Assert.Null(contexts.Get("child", "m2lb-dev"));
        Assert.Contains(result.PreservedDomains, d => d.Contains("installation settings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ClearedDomains, d => d.Contains("domain extensions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reset_IsIdempotent_OnAnEmptyApplication()
    {
        var (coordinator, _, _, _) = Build(new FakeDatabaseReset());
        Assert.Equal("Completed", (await coordinator.ResetAsync()).Status);
        var second = await coordinator.ResetAsync();
        Assert.Equal("Completed", second.Status);
        Assert.Equal(2, second.ResetEpoch);
    }

    [Fact]
    public async Task FailedDatabaseReset_ChangesNothingElse()
    {
        var state = new LocalDataResetState(StatePath);
        var (coordinator, _, e2e, _) = Build(new FakeDatabaseReset(success: false, message: "Reset failed. See server logs for details."), state);
        e2e.Save(new BirkNext.CriticalE2E.CriticalE2EFlowDefinition { Id = "flow-1", EnvironmentId = "env", Name = "Flow", Module = "M" });

        var result = await coordinator.ResetAsync();

        Assert.Equal("Failed", result.Status);
        Assert.False(result.Success);
        Assert.Equal(0, state.Epoch);
        Assert.Single(e2e.Flows("env"));
    }

    [Fact]
    public async Task RefusedReset_IsReportedAsRefused()
    {
        var (coordinator, _, _, _) = Build(new FakeDatabaseReset(success: false, message: "Reset is available only for a Local database on a non-production backend."));
        var result = await coordinator.ResetAsync();
        Assert.Equal("Refused", result.Status);
    }

    [Fact]
    public async Task ConcurrentReset_IsBlocked_NotRunTwice()
    {
        var hold = new TaskCompletionSource();
        var database = new FakeDatabaseReset(hold: hold);
        var (coordinator, _, _, _) = Build(database);

        var first = coordinator.ResetAsync();
        var second = await coordinator.ResetAsync();
        hold.SetResult();
        var completed = await first;

        Assert.Equal("Blocked", second.Status);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(1, database.Calls);
    }

    [Fact]
    public async Task CdcKeyFloor_SurvivesTheReset_SoSyntheticKeysAreNeverReused()
    {
        _db.ActiveCdcRuns.Add(new ActiveCdcRunRecord { Id = Guid.NewGuid(), EnvironmentId = "m2lb-dev", IntegrationId = "person", SyntheticPersonPk = 900_007, SyntheticPersonPkControl = 900_008, Status = "Completed", ResultJson = "{}" });
        await _db.SaveChangesAsync();
        var state = new LocalDataResetState(StatePath);
        var (coordinator, _, _, _) = Build(new FakeDatabaseReset(), state);

        await coordinator.ResetAsync();
        _db.ActiveCdcRuns.RemoveRange(_db.ActiveCdcRuns); // what the real database reset does
        await _db.SaveChangesAsync();

        Assert.Equal(900_008, state.CdcPersonPkFloor("m2lb-dev"));
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton(state)
            .BuildServiceProvider();
        var store = new ActiveCdcRunStore(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActiveCdcRunStore>.Instance);
        Assert.Equal(900_008, await store.MaxPersonPkAsync("m2lb-dev", 900_000, 999_999, default));
        Assert.Null(await store.MaxPersonPkAsync("other-env", 900_000, 999_999, default));
    }

    private static WorkspacePersistenceController Controller(LocalDataResetState state, Mock<IWorkspacePersistenceService> service)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(state).BuildServiceProvider() };
        return new WorkspacePersistenceController(service.Object, NullLogger<WorkspacePersistenceController>.Instance) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task AutoSave_FromBeforeTheLastReset_IsRefused_AndCreatesNothing()
    {
        var state = new LocalDataResetState(StatePath);
        state.Advance(DateTimeOffset.UtcNow, new Dictionary<string, int>());
        var service = new Mock<IWorkspacePersistenceService>(MockBehavior.Strict);
        var controller = Controller(state, service);

        var stale = await controller.AutoSave(new WorkspacePersistenceController.AutoSaveRequest { ProjectName = "m2lb", ResetEpoch = 0 });
        var missing = await controller.AutoSave(new WorkspacePersistenceController.AutoSaveRequest { ProjectName = "m2lb" });
        var saveCurrent = await controller.SaveCurrent(new WorkspacePersistenceController.SaveRequest { Name = "old", ResetEpoch = 0 });

        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(stale.Result).StatusCode);
        Assert.IsType<ConflictObjectResult>(missing.Result);
        Assert.IsType<ConflictObjectResult>(saveCurrent.Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AutoSave_WithTheCurrentEpoch_IsAccepted()
    {
        var state = new LocalDataResetState(StatePath);
        state.Advance(DateTimeOffset.UtcNow, new Dictionary<string, int>());
        var service = new Mock<IWorkspacePersistenceService>();
        service.Setup(s => s.AutoSaveAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<List<WorkspaceArtifactDto>>(), It.IsAny<string?>()))
            .ReturnsAsync(new SavedWorkspace { Id = Guid.NewGuid(), Name = "Auto" });
        var controller = Controller(state, service);

        var result = await controller.AutoSave(new WorkspacePersistenceController.AutoSaveRequest { ProjectName = "paymenthub", ResetEpoch = 1 });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task AutoSave_WithNothingToSave_AfterAReset_CreatesNoWorkspace()
    {
        var state = new LocalDataResetState(StatePath);
        state.Advance(DateTimeOffset.UtcNow, new Dictionary<string, int>());
        var service = new Mock<IWorkspacePersistenceService>(MockBehavior.Strict);
        service.Setup(s => s.GetCurrentWorkspaceIdAsync()).ReturnsAsync((Guid?)null);
        var controller = Controller(state, service);

        var result = await controller.AutoSave(new WorkspacePersistenceController.AutoSaveRequest { ProjectName = "", ResetEpoch = 1, SddLifecycleJson = "{}" });

        Assert.IsType<NoContentResult>(result.Result);
        service.Verify(s => s.AutoSaveAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<List<WorkspaceArtifactDto>>(), It.IsAny<string?>()), Times.Never);
    }
}
