using System.Text.Json;
using Path = System.IO.Path;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.SecurityClassification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BirkNext.Api.Services.LocalDataReset;

/// <summary>
/// State that must outlive a local data reset, kept in <c>App_Data/local-data-reset.json</c> (outside the database the reset empties):
///   • the reset epoch — increments on every reset; workspace writes that carry an older epoch are refused, so a browser tab that still
///     holds the previous project cannot recreate it (an auto-save would otherwise create a new workspace from its payload);
///   • the last reset time;
///   • the highest synthetic CDC PersonPK per environment — deleting the run rows must never let a later CDC run reuse a key that already
///     exists in the real Event Hub / downstream system.
/// </summary>
/// <summary>The database half of a local data reset (implemented by <see cref="AdminService"/>): policy checks plus one transactional delete.</summary>
public interface ILocalDatabaseReset
{
    Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync();
}

public sealed class LocalDataResetState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Snapshot _state;

    public sealed record Snapshot(int Epoch, DateTimeOffset? LastResetAt, Dictionary<string, int> CdcPersonPkFloors, Dictionary<string, long>? SyntheticIdentityFloors = null);

    public LocalDataResetState(IHostEnvironment environment, IConfiguration configuration)
        : this(configuration["LocalDataReset:StatePath"] is { Length: > 0 } configured ? configured : Path.Combine(environment.ContentRootPath, "App_Data", "local-data-reset.json")) { }

    public LocalDataResetState(string path)
    {
        _path = path;
        _state = Read(path) ?? new Snapshot(0, null, new(StringComparer.Ordinal));
    }

    public int Epoch { get { lock (_gate) return _state.Epoch; } }
    public DateTimeOffset? LastResetAt { get { lock (_gate) return _state.LastResetAt; } }
    public int? CdcPersonPkFloor(string environmentId) { lock (_gate) return _state.CdcPersonPkFloors.TryGetValue(environmentId, out var f) ? f : null; }
    /// <summary>Highest synthetic identity value already handed out for the environment and scope before the last reset (generic Active Event ledger).</summary>
    public long? SyntheticIdentityFloor(string environmentId, string scope) { lock (_gate) return _state.SyntheticIdentityFloors?.TryGetValue(IdentityKey(environmentId, scope), out var f) == true ? f : null; }
    public static string IdentityKey(string environmentId, string scope) => $"{environmentId}|{scope}";

    /// <summary>True when a workspace write carries the current epoch. A missing epoch is accepted only before the first reset (older clients).</summary>
    public bool Accepts(int? clientEpoch) => clientEpoch is { } e ? e == Epoch : Epoch == 0;

    /// <summary>Advances the epoch and records the CDC key floors. Throws when the file cannot be written (the caller reports it).</summary>
    public int Advance(DateTimeOffset at, IReadOnlyDictionary<string, int> cdcFloors, IReadOnlyDictionary<string, long>? identityFloors = null)
    {
        lock (_gate)
        {
            var floors = new Dictionary<string, int>(_state.CdcPersonPkFloors, StringComparer.Ordinal);
            foreach (var (env, pk) in cdcFloors) floors[env] = floors.TryGetValue(env, out var old) ? Math.Max(old, pk) : pk;
            var identities = new Dictionary<string, long>(_state.SyntheticIdentityFloors ?? [], StringComparer.Ordinal);
            foreach (var (key, value) in identityFloors ?? new Dictionary<string, long>()) identities[key] = identities.TryGetValue(key, out var old) ? Math.Max(old, value) : value;
            var next = new Snapshot(_state.Epoch + 1, at, floors, identities);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
            _state = next;
            return next.Epoch;
        }
    }

    private static Snapshot? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) is { } s ? s with { CdcPersonPkFloors = new(s.CdcPersonPkFloors ?? [], StringComparer.Ordinal), SyntheticIdentityFloors = new(s.SyntheticIdentityFloors ?? [], StringComparer.Ordinal) } : null : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Structured result of a local data reset. Frontend clearing is a separate client step after a successful result.</summary>
public sealed record LocalDataResetResult
{
    /// <summary>"Completed", "CompletedWithWarnings", "Blocked", "Refused" or "Failed".</summary>
    public string Status { get; init; } = "Failed";
    public bool Success => Status is "Completed" or "CompletedWithWarnings";
    public string Message { get; init; } = "";
    public Guid ResetId { get; init; } = Guid.NewGuid();
    public DateTimeOffset? ResetAt { get; init; }
    public int ResetEpoch { get; init; }
    public bool DatabaseCleared { get; init; }
    public int DeletedRows { get; init; }
    public bool BackendStateCleared { get; init; }
    public List<string> ClearedDomains { get; init; } = [];
    public List<string> PreservedDomains { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Runs a local data reset end to end: refuses while a CDC or performance run is executing (database rows or this process), deletes every
/// application data table in one transaction (<see cref="AdminService.ResetLocalDatabaseAsync"/>), advances the reset epoch, then clears the
/// backend state that is not in the database (Critical E2E files, owned browser sessions, captured credentials, test contexts, diagnostic and
/// reachability caches). A failure after the database commit is reported as a warning — the database, the authoritative store, is clean.
/// </summary>
public sealed class LocalDataResetCoordinator(ILocalDatabaseReset admin, AppDbContext db, LocalDataResetState state, IServiceProvider services, ILogger<LocalDataResetCoordinator> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static readonly IReadOnlyList<string> Cleared =
    [
        "Saved workspaces, SDD artifacts, lifecycle state (revisions, baselines, questions, decisions, traceability, CodeLinks, test evidence incl. TRX)",
        "Scenarios, reviews, traceability links and suggestions, code mappings, project documents",
        "Source Analysis snapshots and all derived evidence (architecture, database, observability, technology inventory, CI/CD)",
        "Target-environment integration catalogs, applied templates (e.g. M2LB) and domain extensions, contracts, GraphQL schemas, message flows",
        "Integration, dependency, SCIM, security classification, security expectation and Azure environment results",
        "Performance test definitions, data profiles, runs, baselines and drift history; Active CDC run history",
        "Critical E2E flows and run history (App_Data/critical-e2e)",
        "BirkNext-owned authenticated browser sessions, the running local HTTPS proxy session (and the dedicated browser it opened), Browser Companion pairings, captured API credentials, temporary security-classification test contexts",
        "Diagnostic, reachability and review caches",
        "Project Import archives staged for commit (memory and staging directory)",
    ];

    public static readonly IReadOnlyList<string> Preserved =
    [
        "Database schema and migration history",
        "Application and installation settings (appsettings, feature visibility, logging)",
        "Provider capability and configuration (k6 image, Podman, Azure/AI providers)",
        "Sample project catalog files",
        "The HTTPS inspection certificate (Windows user store), local proxy port configuration and capability, browser profiles and the Azure sign-in of this machine",
        "Uploaded or external files outside the database (Source Analysis archives are never stored on disk)",
        "The highest synthetic CDC key per environment and the highest reserved synthetic identity per scope (so test keys are never reused)",
    ];

    public async Task<LocalDataResetResult> ResetAsync(CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct)) return new() { Status = "Blocked", Message = "A local data reset is already in progress." };
        try
        {
            if (services.GetService<PerformanceTestExecutionService>()?.HasActiveRuns == true || services.GetService<BirkNext.Api.Services.ActiveEventTesting.ActiveEventRunCoordinator>()?.HasRunning == true)
                return new() { Status = "Blocked", Message = "Reset is blocked while a CDC or performance test is running. Cancel it first." };

            var floors = await CdcFloorsAsync(ct);
            var identityFloors = await SyntheticIdentityFloorsAsync(ct);
            var (success, message, deleted, at) = await admin.ResetLocalDatabaseAsync();
            if (!success)
                return new() { Status = message.StartsWith("Reset failed", StringComparison.Ordinal) ? "Failed" : "Refused", Message = message, PreservedDomains = [.. Preserved] };

            var warnings = new List<string>();
            var epoch = state.Epoch;
            try { epoch = state.Advance(at ?? DateTimeOffset.UtcNow, floors, identityFloors); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Local data reset: the reset epoch could not be saved");
                warnings.Add("The reset epoch could not be saved, so a browser tab that still shows the previous project could save it again. Close other BirkNext tabs.");
            }

            await Step(warnings, "Critical E2E flows and history", () => { services.GetService<ICriticalE2EStore>()?.ClearAll(); return Task.CompletedTask; });
            await Step(warnings, "authenticated browser sessions", async () => { if (services.GetService<AuthenticatedBrowserSessionManager>() is { } m) await m.DisposeAllForResetAsync(); });
            // The project-bound proxy SESSION stops; the certificate, port configuration and proxy capability are installation state and stay.
            await Step(warnings, "local HTTPS proxy session", async () => { if (services.GetService<LocalHttpsProxyService>() is { } proxy) await proxy.StopForLocalDataResetAsync(); });
            await Step(warnings, "Browser Companion pairings", () => { services.GetService<BrowserCompanionService>()?.UnpairAll(); return Task.CompletedTask; });
            await Step(warnings, "captured API credentials", () => { services.GetService<TransientAuthenticatedApiContextStore>()?.InvalidateAll(); return Task.CompletedTask; });
            await Step(warnings, "security classification test contexts", () => { services.GetService<ClassificationTestContextStore>()?.ClearAll(); return Task.CompletedTask; });
            await Step(warnings, "staged project imports", () => { services.GetService<BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore>()?.Clear(); return Task.CompletedTask; });
            await Step(warnings, "browser diagnostic results", () => { services.GetService<BrowserAutomationEvidenceStore>()?.Clear(); return Task.CompletedTask; });
            await Step(warnings, "performance reachability checks", () => { services.GetService<PerformanceTestProviderRegistry>()?.ClearReachability(); return Task.CompletedTask; });
            await Step(warnings, "review caches", () => { (services.GetService<IMemoryCache>() as MemoryCache)?.Compact(1.0); return Task.CompletedTask; });

            logger.LogWarning("Local data reset {Epoch} completed: {Deleted} database rows, {Warnings} warning(s)", epoch, deleted, warnings.Count);
            return new()
            {
                Status = warnings.Count == 0 ? "Completed" : "CompletedWithWarnings",
                Message = warnings.Count == 0
                    ? $"Local data reset completed. {deleted} database records and all project, workspace, review and evidence state were removed. Installation settings and provider capabilities were preserved."
                    : $"Local data reset completed with {warnings.Count} warning(s). The database was cleared ({deleted} records).",
                ResetAt = at, ResetEpoch = epoch, DatabaseCleared = true, DeletedRows = deleted, BackendStateCleared = warnings.Count == 0,
                ClearedDomains = [.. Cleared], PreservedDomains = [.. Preserved], Warnings = warnings,
            };
        }
        finally { Gate.Release(); }
    }

    private async Task<Dictionary<string, int>> CdcFloorsAsync(CancellationToken ct)
    {
        var rows = await db.ActiveCdcRuns.AsNoTracking().Select(r => new { r.EnvironmentId, r.SyntheticPersonPk, r.SyntheticPersonPkControl }).ToListAsync(ct);
        return rows.GroupBy(r => r.EnvironmentId, StringComparer.Ordinal)
            .Select(g => (g.Key, Max: g.SelectMany(r => new[] { r.SyntheticPersonPk, r.SyntheticPersonPkControl }).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max()))
            .Where(x => x.Max > 0).ToDictionary(x => x.Key, x => x.Max, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, long>> SyntheticIdentityFloorsAsync(CancellationToken ct)
    {
        var rows = await db.ActiveEventSyntheticIdentities.AsNoTracking()
            .GroupBy(r => new { r.EnvironmentId, r.Scope }).Select(g => new { g.Key.EnvironmentId, g.Key.Scope, Max = g.Max(r => r.Value) }).ToListAsync(ct);
        return rows.ToDictionary(r => LocalDataResetState.IdentityKey(r.EnvironmentId, r.Scope), r => r.Max, StringComparer.Ordinal);
    }

    private async Task Step(List<string> warnings, string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local data reset: {What} could not be cleared", what);
            warnings.Add($"The {what} could not be cleared; they no longer refer to any stored project.");
        }
    }
}
