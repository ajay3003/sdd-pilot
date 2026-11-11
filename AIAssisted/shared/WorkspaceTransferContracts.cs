namespace BirkNext.WorkspaceTransfer;

// Backup & Transfer: four operations with separate safety models.
//   Full backup / isolated restore — PostgreSQL custom-format dump + managed file stores, encrypted, restored only into a NEW database.
//   Project / integration / diagnostic packages — portable .birknext ZIP of derived, non-secret workspace state, imported two-phase.
// No contract here carries a secret value; passphrases travel only in requests and are never echoed or persisted.

/// <summary>Where a piece of persistent state lives (audit §2).</summary>
public enum StorageClass { InDatabase, ManagedFilesystem, ExternalReference, RegenerableCache, SecretStore }

/// <summary>The four transfer operations.</summary>
public enum TransferKind { FullBackup, ProjectWorkspace, IntegrationWorkspace, Diagnostic }

/// <summary>Plain, non-secret metadata in front of the encrypted payload. Authenticated (it is the AEAD associated data of every chunk), so a
/// changed header fails decryption; before decryption it is only a claim.</summary>
public sealed record BackupPublicHeader
{
    public string Format { get; init; } = "birknext-backup";
    public int FormatVersion { get; init; } = 1;
    public Guid BackupId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ApplicationVersion { get; init; } = "";
    public string ApplicationCommit { get; init; } = "unknown";
    public string DatabaseProvider { get; init; } = "PostgreSQL";
    public string DatabaseServerVersion { get; init; } = "";
    public string DumpToolVersion { get; init; } = "";
    /// <summary>Last applied migration per EF context at the backup snapshot.</summary>
    public IReadOnlyDictionary<string, string> MigrationHeads { get; init; } = new Dictionary<string, string>();
    public string Encryption { get; init; } = "AES-256-GCM chunked (STREAM), 1 MiB chunks";
    public string KeyDerivation { get; init; } = "PBKDF2-HMAC-SHA256";
    public int KeyDerivationIterations { get; init; }
    public string Note { get; init; } = "";
}

public sealed record BackupManagedFile(string Store, string RelativePath, long Bytes, string Sha256, DateTimeOffset CapturedAt);
public sealed record BackupTableCount(string Schema, string Table, long Rows);
public sealed record BackupExclusion(string Component, StorageClass Class, string Reason);

public sealed record BackupDatabaseInventory
{
    public IReadOnlyList<BackupTableCount> Tables { get; init; } = [];
    /// <summary>Applied migration ids per EF history table ("public.__EFMigrationsHistory", "delivery_reporting.__ef_migrations_history", …).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Migrations { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public int ForeignKeys { get; init; }
    /// <summary>The exported snapshot the counts and the dump were both taken from.</summary>
    public string SnapshotId { get; init; } = "";
}

/// <summary>The manifest inside the encrypted payload (manifest.json).</summary>
public sealed record BackupManifest
{
    public int BackupFormatVersion { get; init; } = 1;
    public Guid BackupId { get; init; }
    public string ApplicationVersion { get; init; } = "";
    public string ApplicationCommit { get; init; } = "unknown";
    public string DatabaseProvider { get; init; } = "PostgreSQL";
    public string DatabaseVersion { get; init; } = "";
    public string DumpToolVersion { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public DateTimeOffset BackupTimestamp { get; init; }
    public string BackupScope { get; init; } = "Full installation: every BirkNext-owned table and managed file store; no secret store";
    public IReadOnlyList<string> IncludedComponents { get; init; } = [];
    public IReadOnlyList<BackupExclusion> ExcludedComponents { get; init; } = [];
    public BackupDatabaseInventory Database { get; init; } = new();
    public IReadOnlyList<BackupManagedFile> ManagedFileInventory { get; init; } = [];
    /// <summary>"DATABASE_EXPORTED_SNAPSHOT; FILES_STABLE_ACROSS_DUMP" or "…; FILES_CHANGED_DURING_DUMP" — never claimed point-in-time across both when files moved.</summary>
    public string ConsistencyMethod { get; init; } = "";
    public IReadOnlyList<string> ConsistencyNotes { get; init; } = [];
    public string EncryptionMetadata { get; init; } = "";
    /// <summary>COMPLETE, or COMPLETE_WITH_FILE_DRIFT when a file store changed during every capture attempt.</summary>
    public string BackupCompleteness { get; init; } = "";
    public IReadOnlyList<string> RequiredDependencies { get; init; } = [];
    /// <summary>Source snapshot ids referenced from the managed files (e.g. suggestion reviews, Test Data assets) at the snapshot, and those
    /// with no row in the database at that moment. A restore must reproduce exactly the same picture.</summary>
    public int FileSnapshotReferences { get; init; }
    public IReadOnlyList<string> DanglingFileReferences { get; init; } = [];
}

public sealed record BackupCreateRequest(string Passphrase, string? Note = null);

public sealed record BackupSummary(string FileName, Guid BackupId, DateTimeOffset CreatedAt, long Bytes, string ApplicationVersion, string DatabaseServerVersion,
    IReadOnlyDictionary<string, string> MigrationHeads, string Note);

public sealed record BackupCreateResult(BackupSummary Backup, string ConsistencyMethod, string BackupCompleteness, int Tables, long Rows, int ManagedFiles,
    IReadOnlyList<BackupExclusion> Excluded, IReadOnlyList<string> Warnings);

public enum CheckStatus { Pass, Warning, Fail, NotRun }
public sealed record TransferCheck(string Name, CheckStatus Status, string Detail);

/// <summary>Inspection of a backup: with a passphrase the payload is decrypted and every hash verified; without one only the header claim is shown.</summary>
public sealed record BackupInspection(string FileName, BackupPublicHeader Header, bool Decrypted, BackupManifest? Manifest, IReadOnlyList<TransferCheck> Checks, string Verdict);

public sealed record RestoreRequest(string FileName, string Passphrase);

/// <summary>An isolated restore: a new database and a separate file directory. The active installation is never touched; there is no cutover.</summary>
public sealed record RestoreReport
{
    public Guid RestoreId { get; init; }
    public string BackupFileName { get; init; } = "";
    public Guid BackupId { get; init; }
    public string RestoredDatabase { get; init; } = "";
    public string RestoredFilesDirectory { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyList<TransferCheck> Checks { get; init; } = [];
    /// <summary>VERIFIED (every check passed), VERIFIED_WITH_WARNINGS, or FAILED.</summary>
    public string Verdict { get; init; } = "";
    public string CutoverPolicy { get; init; } = "No cutover from BirkNext. Pointing the installation at the restored database is a separate administrative change in a maintenance window, after this report.";
}
