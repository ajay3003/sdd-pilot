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
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
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

    /// <summary>The same reset, keeping the tables of <paramref name="keepEntityTypes"/> (system settings). Refuses — deleting nothing — when a kept
    /// table depends on a table that would be emptied.</summary>
    Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync(IReadOnlyCollection<Type> keepEntityTypes) =>
        keepEntityTypes.Count == 0 ? ResetLocalDatabaseAsync() : Task.FromResult((false, "Reset refused: keeping settings is not supported here.", 0, (DateTimeOffset?)null));

    /// <summary>Why no reset may run here (policy, non-local database, production host), or null when it may. Every reset scope checks it.</summary>
    string? ResetRefusal() => null;
}

public sealed class LocalDataResetState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Snapshot _state;

    public sealed record Snapshot(int Epoch, DateTimeOffset? LastResetAt, Dictionary<string, int> CdcPersonPkFloors, Dictionary<string, long>? SyntheticIdentityFloors = null,
        bool LastResetKeptSettings = false);

    public LocalDataResetState(IHostEnvironment environment, IConfiguration configuration)
        : this(configuration["LocalDataReset:StatePath"] is { Length: > 0 } configured ? configured : System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "local-data-reset.json")) { }

    public LocalDataResetState(string path)
    {
        _path = path;
        _state = Read(path) ?? new Snapshot(0, null, new(StringComparer.Ordinal));
    }

    public int Epoch { get { lock (_gate) return _state.Epoch; } }
    /// <summary>Where a reset writes the backup of what it removes on explicit request (manual test plans): beside the state file, outside the database.</summary>
    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "reset-backups");
    public DateTimeOffset? LastResetAt { get { lock (_gate) return _state.LastResetAt; } }
    /// <summary>The last reset kept system settings ("Reset project data, keep settings"): a browser tab from before it keeps its Target Environments.</summary>
    public bool LastResetKeptSettings { get { lock (_gate) return _state.LastResetKeptSettings; } }
    public int? CdcPersonPkFloor(string environmentId) { lock (_gate) return _state.CdcPersonPkFloors.TryGetValue(environmentId, out var f) ? f : null; }
    /// <summary>Highest synthetic identity value already handed out for the environment and scope before the last reset (generic Active Event ledger).</summary>
    public long? SyntheticIdentityFloor(string environmentId, string scope) { lock (_gate) return _state.SyntheticIdentityFloors?.TryGetValue(IdentityKey(environmentId, scope), out var f) == true ? f : null; }
    public static string IdentityKey(string environmentId, string scope) => $"{environmentId}|{scope}";

    /// <summary>True when a workspace write carries the current epoch. A missing epoch is accepted only before the first reset (older clients).</summary>
    public bool Accepts(int? clientEpoch) => clientEpoch is { } e ? e == Epoch : Epoch == 0;

    /// <summary>Advances the epoch and records the CDC key floors. Throws when the file cannot be written (the caller reports it).</summary>
    public int Advance(DateTimeOffset at, IReadOnlyDictionary<string, int> cdcFloors, IReadOnlyDictionary<string, long>? identityFloors = null, bool keptSettings = false)
    {
        lock (_gate)
        {
            var floors = new Dictionary<string, int>(_state.CdcPersonPkFloors, StringComparer.Ordinal);
            foreach (var (env, pk) in cdcFloors) floors[env] = floors.TryGetValue(env, out var old) ? Math.Max(old, pk) : pk;
            var identities = new Dictionary<string, long>(_state.SyntheticIdentityFloors ?? [], StringComparer.Ordinal);
            foreach (var (key, value) in identityFloors ?? new Dictionary<string, long>()) identities[key] = identities.TryGetValue(key, out var old) ? Math.Max(old, value) : value;
            var next = new Snapshot(_state.Epoch + 1, at, floors, identities, keptSettings);
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
    /// <summary>What the reset did to Test Data (protected defaults preserved with their current values; everything local removed).</summary>
    public BirkNext.TestData.TestDataResetSummary? TestData { get; init; }
    /// <summary>Per domain outcome after the database: "Removed" or "Failed".</summary>
    public List<BirkNext.Maintenance.DataResetDomainResult> Domains { get; init; } = [];
    public int TestPlansPreserved { get; init; }
    public int TestPlansRemoved { get; init; }
    /// <summary>File name of the backup written before manual test plans were removed.</summary>
    public string? TestPlanBackup { get; init; }
}

/// <summary>
/// Runs a local data reset end to end: refuses while a CDC or performance run is executing (database rows or this process), deletes every
/// application data table in one transaction (<see cref="AdminService.ResetLocalDatabaseAsync"/>), advances the reset epoch, then clears the
/// backend state that is not in the database (Critical E2E files, owned browser sessions, captured credentials, test contexts, diagnostic and
/// reachability caches). A failure after the database commit is reported as a warning — the database, the authoritative store, is clean.
/// </summary>
public sealed class LocalDataResetCoordinator(ILocalDatabaseReset admin, AppDbContext db, LocalDataResetState state, IServiceProvider services, ILogger<LocalDataResetCoordinator> logger)
{
    /// <summary>The contexts this coordinator clears in dedicated steps (the application database, Delivery Management, Canonical interpretation,
    /// Snapshot Registry); every other context is cleared through <see cref="ResettableSchemas.All"/>. PersistenceOwnershipTests checks that the
    /// two lists together name every registered context exactly once.</summary>
    public static readonly IReadOnlyList<Type> DedicatedResetContexts =
    [
        typeof(AppDbContext), typeof(BirkNext.Api.Data.DeliveryReporting.DeliveryReportingDbContext),
        typeof(BirkNext.Api.Data.CanonicalInterpretation.CanonicalInterpretationDbContext), typeof(BirkNext.Api.Data.SnapshotRegistry.SnapshotRegistryDbContext),
    ];

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
        "Test Data: registered, source-candidate and observed assets (App_Data/test-data registry), their approvals, requirements and bindings, session identities and generated XML fixtures",
        "BirkNext-owned authenticated browser sessions, the running local HTTPS proxy session (and the dedicated browser it opened), Browser Companion pairings, captured API credentials, temporary security-classification test contexts",
        "Diagnostic, reachability and review caches",
        "Project Import archives staged for commit (memory and staging directory)",
        "Delivery Management: reporting projects, delivery snapshots, comparisons and saved Delivery Reports (report history)",
        "Canonical interpretation: canonical entities, aliases, relations, interpretation snapshots and the shared review decisions made on them",
        "Snapshot Registry metadata: lineage, current/approved pointers and retention classes (rebuilt from the remaining stores on next use)",
        .. ResettableSchemas.All.Where(s => !s.OnlyWhenResettingEverything).Select(s => s.Description),
    ];

    /// <summary>Removed only by the explicitly typed "Reset everything" scope (user-authored, reviewed data such as shared test areas).</summary>
    public static readonly IReadOnlyList<string> ClearedOnlyWhenResettingEverything =
        [.. ResettableSchemas.All.Where(s => s.OnlyWhenResettingEverything).Select(s => s.Description), AzureDevOpsTestPlansRemovedLine];

    /// <summary>
    /// System settings "Reset project data, keep settings" keeps (2026-11-22): configuration a tester sets up once per installation or Target
    /// Environment, not project results. Each type is one table of the application database; none depends on a table the reset empties.
    /// </summary>
    public static readonly IReadOnlyList<Type> SettingsEntityTypes =
    [
        typeof(BirkNext.Api.Models.IntegrationPlatformRecord), typeof(BirkNext.Api.Models.IntegrationDefinitionRecord),
        typeof(BirkNext.Api.Models.IntegrationEnvironmentStateRecord), typeof(BirkNext.Api.Models.ExternalIntegrationRecord),
        typeof(BirkNext.Api.Models.AltinnEnvironmentSettingsRecord),
        typeof(BirkNext.Api.Models.ExecutionRetentionSettingsRow), typeof(BirkNext.Api.Models.SetupLibraryItemRecord),
        typeof(BirkNext.Api.Models.SetupLibraryVersionRecord), typeof(BirkNext.Api.Models.ParquetRuleSetRecord),
        typeof(BirkNext.Api.Models.SourceRepositoryAssociationRecord),
    ];

    /// <summary>What "Reset project data, keep settings" keeps beyond the ordinary preserved list, in the tester's words.</summary>
    public static readonly IReadOnlyList<string> SettingsKept =
    [
        "Target Environments: names, URLs, authentication setup, thresholds, security expectations and feature settings (their link to the deleted project is cleared)",
        "Integrations per Target Environment (platforms, integrations, external systems) and Altinn settings",
        "The execution evidence retention setting, the Setup Library and approved Parquet rule sets",
        "Remembered \"same repository\" decisions; Git Sources and their cloned copies",
    ];

    /// <summary>
    /// Azure DevOps – Testplaner (2026-11-25): each project's export settings and default test plan destination. Kept by every reset except Reset
    /// everything (the user's rule: this setup is removed only by RESET DATA → RESET EVERYTHING). The system default destination and unchecked
    /// creates (App_Data/azure-devops/test-plans.json) follow the same rule; the Azure DevOps connections themselves are installation state and are
    /// kept by every reset, like the machine's Azure sign-in.
    /// </summary>
    public static readonly IReadOnlyList<Type> ResetEverythingOnlyEntityTypes = [typeof(BirkNext.Api.Models.AdoExportProfileRecord)];

    public const string AzureDevOpsTestPlansKept =
        "Azure DevOps – Testplaner: the saved test plan destinations (system and per project), unchecked creates and each project's export settings — only Reset everything removes them";

    public const string AzureDevOpsTestPlansRemovedLine =
        "Azure DevOps – Testplaner: the saved test plan destinations (system and per project), unchecked creates and each project's export settings (nothing is changed in Azure DevOps)";

    public const string AzureDevOpsConnectionsKept =
        "Azure DevOps connections (organization and the encrypted access token): kept by every reset";

    /// <summary>
    /// Manual test plans and everything that belongs to them (2026-11-24): user-authored, reviewed work that is not derived from any import, so
    /// no reset scope removes it by default. Only "Reset everything" with an explicit opt-in and the reviewed count removes these tables, after
    /// writing a backup. None of them has a foreign key to a table the reset empties (AdminService refuses the reset otherwise).
    /// </summary>
    public static readonly IReadOnlyList<Type> TestPlanEntityTypes =
    [
        typeof(BirkNext.Api.Models.TestPlanDraftRecord), typeof(BirkNext.Api.Models.TestPlanDraftRevisionRecord), typeof(BirkNext.Api.Models.TestPlanSnapshotRecord),
        typeof(BirkNext.Api.Models.ManualTestRunRecord), typeof(BirkNext.Api.Models.ManualRunAttachmentRecord),
        // User story grouping and US → FR review decisions (2026-11-24): user-authored review work like the plans.
        typeof(BirkNext.Api.Models.StoryGroupingRecord),
        // Links to Azure DevOps test cases (2026-11-25): without them a later «Send direkte» would create duplicates.
        typeof(BirkNext.Api.Models.AdoTestCaseLinkRecord),
        // Results fetched from Azure DevOps (phase 2): execution history like the manual runs.
        typeof(BirkNext.Api.Models.ExternalTestResultRecord),
    ];

    /// <summary>The preserved line for manual test plans.</summary>
    public const string TestPlansKept =
        "Manual test plans (Manuell testing) with their revisions, snapshots, design decisions and manual test runs, and the user story grouping and requirement review decisions: kept by every reset unless Reset everything is run with «Also remove manual test plans»";

    /// <summary>The removed line when manual test plans were explicitly included.</summary>
    public const string TestPlansRemovedLine =
        "Manual test plans with their revisions, snapshots, design decisions and manual test runs (a backup was written first to App_Data/reset-backups)";

    /// <summary>Why shared test areas survive an ordinary reset (listed as preserved; removed only by "Reset everything").</summary>
    public const string SharedTestAreasKept =
        "Shared test areas (scopes) with their revisions, approvals and review history: they hold reviewed test boundaries, so only Reset everything removes them";

    public static readonly IReadOnlyList<string> Preserved =
    [
        "Database schema and migration history",
        "Application and installation settings (appsettings, feature visibility, logging)",
        "Provider capability and configuration (k6 image, Podman, Azure/AI providers)",
        "Sample project catalog files",
        "The HTTPS inspection certificate (Windows user store), local proxy port configuration and capability, browser profiles and the Azure sign-in of this machine",
        "Uploaded or external files outside the database (Source Analysis archives are never stored on disk)",
        "The highest synthetic CDC key per environment and the highest reserved synthetic identity per scope (so test keys are never reused)",
        "Protected Test Data defaults (skolenaervaer-reporter-default, skolenaervaer-child-default) with their current, possibly edited, values; Test Data supplied by backend configuration",
        SharedTestAreasKept,
        TestPlansKept,
        AzureDevOpsTestPlansKept,
        AzureDevOpsConnectionsKept,
    ];

    public Task<LocalDataResetResult> ResetAsync(CancellationToken ct = default) => ResetAsync(includeProtected: false, ct);

    public Task<LocalDataResetResult> ResetAsync(bool includeProtected, CancellationToken ct = default) => ResetAsync(includeProtected, keepSettings: false, ct);

    /// <param name="includeProtected">True only for the explicitly confirmed "Reset everything" scope: protected Test Data defaults return to their
    /// product values and canonical fixtures are deleted. Otherwise protected Test Data survives with its current values.</param>
    /// <param name="keepSettings">"Reset project data, keep settings": the <see cref="SettingsEntityTypes"/> tables are not emptied.</param>
    public Task<LocalDataResetResult> ResetAsync(bool includeProtected, bool keepSettings, CancellationToken ct = default) =>
        ResetAsync(includeProtected, keepSettings, removeTestPlans: false, ct);

    /// <param name="removeTestPlans">Also empty the <see cref="TestPlanEntityTypes"/> tables. Refused unless <paramref name="includeProtected"/>;
    /// a backup of every row (including logically deleted plans) is written first, and nothing is deleted when it cannot be written.</param>
    public async Task<LocalDataResetResult> ResetAsync(bool includeProtected, bool keepSettings, bool removeTestPlans, CancellationToken ct = default)
    {
        if (removeTestPlans && !includeProtected)
            return new() { Status = "Refused", Message = "Manual test plans can only be removed by Reset everything. Nothing was deleted.", PreservedDomains = [.. Preserved] };
        if (!await Gate.WaitAsync(0, ct)) return new() { Status = "Blocked", Message = "A local data reset is already in progress." };
        try
        {
            if (services.GetService<PerformanceTestExecutionService>()?.HasActiveRuns == true || services.GetService<BirkNext.Api.Services.ActiveEventTesting.ActiveEventRunCoordinator>()?.HasRunning == true)
                return new() { Status = "Blocked", Message = "Reset is blocked while a CDC or performance test is running. Cancel it first." };

            // Test Data before the database is cleared (generated fixtures and consumer references live there), so the summary has real counts.
            var testDataBefore = services.GetService<TestDataLifecycleService>() is { } preview ? await TryAsync(() => preview.PreviewResetAsync(ct)) : null;
            var floors = await CdcFloorsAsync(ct);
            var identityFloors = await SyntheticIdentityFloorsAsync(ct);
            var testPlans = await db.TestPlanDrafts.AsNoTracking().CountAsync(ct);
            string? backup = null;
            if (removeTestPlans)
            {
                try { backup = await WriteTestPlanBackupAsync(ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
                {
                    logger.LogError(ex, "Local data reset refused: the manual test plan backup could not be written");
                    return new() { Status = "Refused", Message = $"Reset refused: the backup of {testPlans} manual test plan(s) could not be written ({ex.GetType().Name}). Nothing was deleted.", PreservedDomains = [.. Preserved] };
                }
            }
            IReadOnlyCollection<Type> keep = [.. (keepSettings ? SettingsEntityTypes : []), .. (includeProtected ? [] : ResetEverythingOnlyEntityTypes),
                .. (removeTestPlans ? [] : TestPlanEntityTypes)];
            var (success, message, deleted, at) = await admin.ResetLocalDatabaseAsync(keep);
            if (!success)
                return new() { Status = message.StartsWith("Reset failed", StringComparison.Ordinal) ? "Failed" : message.StartsWith("Reset blocked", StringComparison.Ordinal) ? "Blocked" : "Refused", Message = message, PreservedDomains = [.. Preserved] };

            var warnings = new List<string>();
            var domains = new List<BirkNext.Maintenance.DataResetDomainResult> { new("Application database", "Removed", $"{deleted} records") };
            var epoch = state.Epoch;
            try { epoch = state.Advance(at ?? DateTimeOffset.UtcNow, floors, identityFloors, keepSettings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Local data reset: the reset epoch could not be saved");
                warnings.Add("The reset epoch could not be saved, so a browser tab that still shows the previous project could save it again. Close other BirkNext tabs.");
            }

            await Step(warnings, "Critical E2E flows and history", () =>
            {
                services.GetService<ICriticalE2EStore>()?.ClearAll();
                // Suggestion reviews go with the flows and source snapshots they review (they survived a reset before audit 2026-11-15 ARCH-01).
                services.GetService<BirkNext.Api.Services.CriticalE2E.Suggestions.ICriticalE2ESuggestionStore>()?.Clear();
                return Task.CompletedTask;
            });
            await Step(warnings, "authenticated browser sessions", async () => { if (services.GetService<AuthenticatedBrowserSessionManager>() is { } m) await m.DisposeAllForResetAsync(); });
            // The project-bound proxy SESSION stops; the certificate, port configuration and proxy capability are installation state and stay.
            await Step(warnings, "local HTTPS proxy session", async () => { if (services.GetService<LocalHttpsProxyService>() is { } proxy) await proxy.StopForLocalDataResetAsync(); });
            await Step(warnings, "Browser Companion pairings", () => { services.GetService<BrowserCompanionService>()?.UnpairAll(); return Task.CompletedTask; });
            await Step(warnings, "captured API credentials", () => { services.GetService<TransientAuthenticatedApiContextStore>()?.InvalidateAll(); return Task.CompletedTask; });
            await Step(warnings, "security classification test contexts", () => { services.GetService<ClassificationTestContextStore>()?.ClearAll(); return Task.CompletedTask; });
            await Step(warnings, "staged project imports", () => { services.GetService<BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore>()?.Clear(); return Task.CompletedTask; });
            await Step(warnings, "browser diagnostic results", () => { services.GetService<BrowserAutomationEvidenceStore>()?.Clear(); return Task.CompletedTask; });
            await Step(warnings, "data quality diagnostics", () => { services.GetService<BirkNext.Api.Services.DataQuality.DataQualityRunStore>()?.ClearAll(); return Task.CompletedTask; });
            await Step(warnings, "performance reachability checks", () => { services.GetService<PerformanceTestProviderRegistry>()?.ClearReachability(); return Task.CompletedTask; });
            await Step(warnings, "review caches", () => { (services.GetService<IMemoryCache>() as MemoryCache)?.Compact(1.0); return Task.CompletedTask; });
            // Reset everything only: the system default test plan destination and unchecked creates (the connections stay).
            if (includeProtected)
                await Step(warnings, "Azure DevOps test plan settings", () =>
                {
                    services.GetService<BirkNext.Api.Services.DeliveryReporting.AzureDevOps.AzureDevOpsTestPlanSettingsStore>()?.Clear();
                    return Task.CompletedTask;
                });
            TestDataResetSummary? testData = null;
            await Step(warnings, "Test Data assets", async () =>
            {
                if (services.GetService<TestDataLifecycleService>() is { } lifecycle)
                    testData = includeProtected
                        ? await lifecycle.CompleteFullResetIncludingProtectedAsync(testDataBefore ?? new TestDataResetPreview(), ct)
                        : await lifecycle.CompleteFullResetAsync(testDataBefore ?? new TestDataResetPreview(), ct);
            }, domains, "Test Data");
            // Delivery Management keeps its own schema (and DbContext): the application database transaction never covered it.
            await Step(warnings, "Delivery Management data", async () =>
            {
                using var scope = services.CreateScope();
                if (scope.ServiceProvider.GetService<BirkNext.Api.Services.DeliveryReporting.DeliveryReportingDataReset>() is { } delivery) await delivery.ResetAsync(ct);
            }, domains, "Delivery Management");
            // Canonical interpretation keeps its own schema too; it is derived from Source Analysis plus review decisions, so it goes with them.
            await Step(warnings, "Canonical interpretation data", async () =>
            {
                using var scope = services.CreateScope();
                if (scope.ServiceProvider.GetService<BirkNext.Api.Services.CanonicalInterpretation.CanonicalInterpretationDataReset>() is { } canonical) await canonical.ResetAsync(ct);
            }, domains, "Canonical interpretation");
            await Step(warnings, "Snapshot Registry", async () =>
            {
                using var scope = services.CreateScope();
                // Every table of the context, dependents first, refusing any table outside its schema (was three hard-coded DELETEs, so a table
                // added later would have survived the reset).
                if (scope.ServiceProvider.GetService<BirkNext.Api.Data.SnapshotRegistry.SnapshotRegistryDbContext>() is { } snapshots && snapshots.Database.IsRelational())
                    await ResettableSchemas.DeleteAllRowsAsync(snapshots, BirkNext.Api.Data.SnapshotRegistry.SnapshotRegistryDbContext.Schema, ct);
            }, domains, "Snapshot Registry");
            // Separate-schema stores from the registry (ResettableSchemas.All): each its own transaction on the local database, history tables untouched.
            foreach (var store in ResettableSchemas.For(includeProtected))
                await Step(warnings, $"{store.Domain} data", async () =>
                {
                    using var scope = services.CreateScope();
                    if (scope.ServiceProvider.GetService(store.ContextType) is DbContext context)
                    {
                        var removed = await ResettableSchemas.DeleteAllRowsAsync(context, store.Schema, ct);
                        logger.LogWarning("Local data reset: {Removed} rows removed from the {Schema} schema ({Domain})", removed, store.Schema, store.Domain);
                    }
                }, domains, store.Domain);

            logger.LogWarning("Local data reset {Epoch} completed: {Deleted} database rows, {Warnings} warning(s)", epoch, deleted, warnings.Count);
            return new()
            {
                Status = warnings.Count == 0 ? "Completed" : "CompletedWithWarnings",
                Message = (warnings.Count == 0
                    ? $"Local data reset completed. {deleted} database records and all project, workspace, review and evidence state were removed. Installation settings and provider capabilities were preserved."
                    : $"Local data reset completed with {warnings.Count} warning(s). The database was cleared ({deleted} records).")
                    + (testData is { } td ? $" Test Data: {td.ProtectedDefaultsPreserved} protected default(s) preserved with their current values, {td.AssetsRemoved} asset(s) removed ({td.GeneratedFixturesRemoved} generated fixture(s))." : "")
                    + (removeTestPlans ? $" Manual test plans: {testPlans} removed (backup {backup})." : $" Manual test plans: {testPlans} kept."),
                TestPlansPreserved = removeTestPlans ? 0 : testPlans, TestPlansRemoved = removeTestPlans ? testPlans : 0, TestPlanBackup = backup,
                ResetAt = at, ResetEpoch = epoch, DatabaseCleared = true, DeletedRows = deleted, BackendStateCleared = warnings.Count == 0,
                ClearedDomains = [.. Cleared, .. (includeProtected ? ClearedOnlyWhenResettingEverything : []), .. (removeTestPlans ? new[] { TestPlansRemovedLine } : [])],
                PreservedDomains = [.. (includeProtected ? Preserved.Where(p => !p.StartsWith("Protected Test Data", StringComparison.Ordinal) && p != SharedTestAreasKept && p != AzureDevOpsTestPlansKept) : Preserved)
                        .Where(p => !removeTestPlans || p != TestPlansKept),
                    .. (keepSettings ? SettingsKept : [])],
                Warnings = warnings, TestData = testData, Domains = domains,
            };
        }
        finally { Gate.Release(); }
    }

    /// <summary>Manual test plans that exist now (active and archived; logically deleted ones are hidden by the query filter).</summary>
    public Task<int> CountTestPlansAsync(CancellationToken ct) => db.TestPlanDrafts.AsNoTracking().CountAsync(ct);

    /// <summary>Writes every row of the <see cref="TestPlanEntityTypes"/> tables (logically deleted plans included) to one JSON file in
    /// <see cref="LocalDataResetState.BackupDirectory"/>, flushed to disk before anything is deleted. Returns the file name.</summary>
    private async Task<string> WriteTestPlanBackupAsync(CancellationToken ct)
    {
        var backup = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["resetEpochBefore"] = state.Epoch,
            ["test_plan_drafts"] = await db.TestPlanDrafts.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct),
            ["test_plan_draft_revisions"] = await db.TestPlanDraftRevisions.AsNoTracking().ToListAsync(ct),
            ["test_plan_snapshots"] = await db.TestPlanSnapshots.AsNoTracking().ToListAsync(ct),
            ["manual_test_runs"] = await db.ManualTestRuns.AsNoTracking().ToListAsync(ct),
            ["manual_run_attachments"] = await db.ManualRunAttachments.AsNoTracking().ToListAsync(ct),
            ["story_groupings"] = await db.StoryGroupings.AsNoTracking().ToListAsync(ct),
        };
        Directory.CreateDirectory(state.BackupDirectory);
        var name = $"test-plans-before-reset-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(state.BackupDirectory, name);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, backup, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, ct);
            await stream.FlushAsync(ct);
        }
        logger.LogWarning("Local data reset: manual test plans backed up to {Backup}", path);
        return name;
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

    private async Task<T?> TryAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return await action(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local data reset: the Test Data preview could not be read");
            return null;
        }
    }

    private async Task Step(List<string> warnings, string what, Func<Task> action, List<BirkNext.Maintenance.DataResetDomainResult>? domains = null, string? domain = null)
    {
        try
        {
            await action();
            if (domains is not null && domain is not null) domains.Add(new(domain, "Removed"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local data reset: {What} could not be cleared", what);
            warnings.Add($"The {what} could not be cleared; they no longer refer to any stored project.");
            if (domains is not null && domain is not null) domains.Add(new(domain, "Failed", $"{what} could not be cleared ({ex.GetType().Name})."));
        }
    }
}
