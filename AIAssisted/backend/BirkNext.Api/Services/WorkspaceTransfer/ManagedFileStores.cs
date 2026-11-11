using Path = System.IO.Path;
using BirkNext.WorkspaceTransfer;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// The BirkNext-owned file stores a full backup contains: an explicit allow-list (audit §2.2), never a directory walk. Every store is a JSON file
/// written by its owner through temp-file + rename, so each file is read as one consistent version. Secret stores, caches, logs, staging, browser
/// profiles and certificates are listed as exclusions with a reason, so a restore report can say what has to be re-established.
/// </summary>
public sealed class ManagedFileStores
{
    public sealed record Store(string Name, string Root, IReadOnlyList<string> Files, string Owner);

    private readonly string _contentRoot;
    private readonly string _appData;
    private readonly string _gitSourcesDirectory;
    private readonly string _localDataResetPath;

    public ManagedFileStores(IHostEnvironment environment, IConfiguration configuration)
        : this(environment.ContentRootPath,
            configuration["GitSources:SettingsDirectory"] is { Length: > 0 } git ? git : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext"),
            configuration["LocalDataReset:StatePath"] is { Length: > 0 } reset ? reset : System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "local-data-reset.json"),
            BirkNext.Api.Services.AppDataLocation.Root(environment)) { }

    public ManagedFileStores(string contentRoot, string gitSourcesDirectory, string localDataResetPath, string? appDataRoot = null)
    {
        _contentRoot = contentRoot;
        _appData = appDataRoot ?? Path.Combine(contentRoot, "App_Data");
        _gitSourcesDirectory = gitSourcesDirectory;
        _localDataResetPath = localDataResetPath;
    }

    public IReadOnlyList<Store> Stores =>
    [
        new("critical-e2e", Path.Combine(_appData, "critical-e2e"), ["flows.json", "history.json", "modules.json", "suggestion-reviews.json"], "Critical E2E Regression (flows, run history, modules, suggestion reviews)"),
        new("test-data", Path.Combine(_appData, "test-data"),
            ["registry.json", "authored.json", "identities.json", "canonical-fixtures.json", "schema-associations.json", "protected-defaults.json"], "Test Data catalog"),
        new("local-data-reset", Path.GetDirectoryName(_localDataResetPath)!, [Path.GetFileName(_localDataResetPath)], "Local Data Reset epoch and synthetic-key floors (never reuse a synthetic key)"),
        new("installation", _appData, ["microsoft-sign-in.json"], "Microsoft sign-in configuration (public client ids only)"),
        new("git-sources", _gitSourcesDirectory, ["git-sources.json"], "Git Sources organisation and repository links (no tokens)"),
    ];

    public static IReadOnlyList<BackupExclusion> Exclusions { get; } =
    [
        new("App_Data/api-service-credentials (API client secrets per environment)", StorageClass.SecretStore,
            "Encrypted with a DPAPI-protected key ring bound to this Windows user and machine: a copy cannot be decrypted elsewhere. Re-enter credentials after a restore."),
        new("App_Data/azure-devops (Azure DevOps PAT)", StorageClass.SecretStore, "DPAPI-protected secret; reconnect after a restore."),
        new("App_Data/browser-authentication (unattended login credential)", StorageClass.SecretStore, "DPAPI-protected secret; re-enter after a restore."),
        new("App_Data/local-session (launch code)", StorageClass.SecretStore, "Bearer code; a new one is created on start."),
        new("Browser profiles, Companion builds and pairing tokens (%LOCALAPPDATA%/BirkNext)", StorageClass.SecretStore, "Session cookies and pairing tokens; pair again after a restore."),
        new("HTTPS inspection CA (Windows certificate store)", StorageClass.SecretStore, "Non-exportable private key; BirkNext creates a new one when needed."),
        new("Target Environment profiles", StorageClass.ExternalReference, "Kept in the browser (localStorage), not by the backend; export them from Target Environments or carry them in an integration package."),
        new("App_Data/parquet-sessions", StorageClass.RegenerableCache, "Two-hour explorer copies of uploaded Parquet files (may hold real rows); the durable evidence is in the database."),
        new("Project Import staging (%TEMP%)", StorageClass.RegenerableCache, "30-minute staging; original source archives are never retained after import."),
        new("logs/", StorageClass.RegenerableCache, "Diagnostic logs."),
        new("appsettings*.json", StorageClass.ExternalReference, "Deployment configuration (may hold keys a person added); managed with the deployment, not in the backup."),
    ];

    /// <summary>The files that exist now, with the store-relative path used in the backup (store/name).</summary>
    public IEnumerable<(Store Store, string File, string FullPath)> Existing() =>
        Stores.SelectMany(s => s.Files.Select(f => (s, f, Path.Combine(s.Root, f)))).Where(x => File.Exists(x.Item3));
}
