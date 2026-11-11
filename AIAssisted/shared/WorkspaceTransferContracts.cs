using System.Text.Json.Serialization;

namespace BirkNext.WorkspaceTransfer;

// Backup & Transfer: four operations with separate safety models.
//   Full backup / isolated restore — PostgreSQL custom-format dump + managed file stores, encrypted, restored only into a NEW database.
//   Project / integration / diagnostic packages — portable .birknext ZIP of derived, non-secret workspace state, imported two-phase.
// No contract here carries a secret value; passphrases travel only in requests and are never echoed or persisted.

/// <summary>Where a piece of persistent state lives (audit §2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageClass { InDatabase, ManagedFilesystem, ExternalReference, RegenerableCache, SecretStore }

/// <summary>The four transfer operations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
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

/// <summary>Readiness of one backup/restore prerequisite. Ready only after the real executable answered and the versions were compared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupReadinessStatus { Ready, Blocked, Unavailable, NotChecked, Incompatible }

public sealed record BackupReadinessItem(string Key, string Name, BackupReadinessStatus Status, string Detail);

/// <summary>
/// The one PostgreSQL tool resolution result for Backup &amp; Transfer: which provider runs pg_dump/pg_restore (configured directory, the Tester
/// Package's bundled tools, PATH, or a container verified to be the database server BirkNext is connected to), the versions that actually
/// answered, and whether backup and isolated restore can run. No connection string, password or secret is included.
/// </summary>
public sealed record BackupReadiness
{
    public BackupReadinessStatus Backup { get; init; }
    public BackupReadinessStatus Restore { get; init; }
    /// <summary>The first reason backup cannot run (null when Ready).</summary>
    public string? BackupBlocker { get; init; }
    public string? RestoreBlocker { get; init; }
    /// <summary>Runtime profile of this installation (Developer (containers) / Tester (native Windows)).</summary>
    public string RuntimeProfile { get; init; } = "";
    /// <summary>How the tools are reached: "Configured directory", "Tester Package (bundled)", "PATH" or "Container exec".</summary>
    public string ToolProvider { get; init; } = "";
    /// <summary>Where the tools were looked for (directory names or "PATH" or the container name); never a secret.</summary>
    public List<string> SearchLocations { get; init; } = [];
    public string? DumpToolVersion { get; init; }
    public string? RestoreToolVersion { get; init; }
    public string? ServerVersion { get; init; }
    public List<BackupReadinessItem> Items { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransferCheckStatus { Pass, Warning, Fail, NotRun }
public sealed record TransferCheck(string Name, TransferCheckStatus Status, string Detail);

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

// ───────────────────────────── Portable workspace packages (.birknext) ─────────────────────────────

/// <summary>Dependency status in an export closure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyStatus { Included, ExternalReference, Missing, Optional, Unauthorized, Unsupported }

/// <summary>How an object travels: plain configuration, configuration that must be revalidated in the destination, or history.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Portability { Portable, EnvironmentDependent, HistoricalEvidence, HistoricalDecision, CapabilityReference }

/// <summary>Import classification of one object against the destination (before anything is written).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportStatus { New, Identical, NewerRevision, OlderRevision, ConflictingContent, MissingDependency, Unauthorized, Unsupported, EnvironmentRebindRequired }

/// <summary>A person's decision for one object.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportAction { CreateNew, ReuseIdentical, ImportAsNewRevision, KeepExisting, SkipOptional, ResolveManually }

/// <summary>What happens to the integration's environment in the destination. Never chosen by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentMappingAction { LeaveUnresolved, ReuseVerified, CreateDisabled, SkipOptional }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReconciliationStatus { Resolved, NeedsRevalidation, Unresolved, Unsupported }

public sealed record PortableObjectRef(string Type, string LogicalId);

/// <summary>One object in a package: type + logical identity (never a local database key as the identity), revision, owner, fingerprint of its
/// canonical payload, the entry holding it, and what it needs.</summary>
public sealed record PortableObject
{
    public string Type { get; init; } = "";
    public string LogicalId { get; init; } = "";
    public string Label { get; init; } = "";
    public int? Revision { get; init; }
    public string? OwningProject { get; init; }
    public string? EnvironmentId { get; init; }
    public Portability Portability { get; init; }
    public string Fingerprint { get; init; } = "";
    public string Path { get; init; } = "";
    public IReadOnlyList<PortableObjectRef> Dependencies { get; init; } = [];
}

public sealed record DependencyEntry(PortableObjectRef From, PortableObjectRef To, DependencyStatus Status, bool Required, string Detail);

public sealed record PackageManifest
{
    public string Format { get; init; } = "birknext-workspace-package";
    public int PackageFormatVersion { get; init; } = 1;
    public TransferKind ExportType { get; init; }
    public string BirkNextApplicationVersion { get; init; } = "";
    public string SourceCommit { get; init; } = "unknown";
    public string ExporterInstanceId { get; init; } = "";
    public DateTimeOffset ExportTimestamp { get; init; }
    public string Label { get; init; } = "";
    public IReadOnlyList<string> IncludedProjects { get; init; } = [];
    public IReadOnlyList<string> IncludedIntegrationScopes { get; init; } = [];
    /// <summary>Main schema migration head of the exporting installation.</summary>
    public string SchemaVersion { get; init; } = "";
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    /// <summary>Entry path → SHA-256 (the same values as integrity/hashes.json). Integrity only: an unsigned package proves nothing about who made it.</summary>
    public IReadOnlyDictionary<string, string> ContentFingerprints { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<DependencyEntry> DependencyManifest { get; init; } = [];
    public string RedactionProfile { get; init; } = "none";
    public string OptionalEvidencePolicy { get; init; } = "excluded";
    /// <summary>COMPLETE (every required dependency resolved) or PARTIAL (explicitly labelled; never imported as a complete workspace).</summary>
    public string Completeness { get; init; } = "COMPLETE";
    public IReadOnlyList<string> Exclusions { get; init; } = [];
    public string Authenticity { get; init; } = "UNSIGNED: content hashes prove integrity of the transfer, not its origin.";
}

public sealed record EnvironmentCandidate(string Id, string Name, string? EnvironmentType = null);

public sealed record ExportRequest
{
    public TransferKind Kind { get; init; }
    /// <summary>Project packages: the Source Analysis repository key.</summary>
    public string? ProjectKey { get; init; }
    /// <summary>Integration packages: the Target Environment id whose integration setup is exported.</summary>
    public string? EnvironmentId { get; init; }
    public bool IncludeEvidence { get; init; }
    /// <summary>The browser's Target Environment profile (JSON) to carry as a disabled draft; sanitised again on the server.</summary>
    public string? TargetProfileJson { get; init; }
    /// <summary>Diagnostic: limit to one integration suite (SuiteKey) and/or one Critical E2E flow.</summary>
    public string? SuiteKey { get; init; }
    public string? FlowId { get; init; }
    /// <summary>Acknowledge identifier-shaped values (11 digits) found in exported content; secrets are never exportable.</summary>
    public bool AcknowledgeSensitive { get; init; }
    /// <summary>Allow an explicitly PARTIAL package when required dependencies are missing.</summary>
    public bool AllowPartial { get; init; }
    public string? Label { get; init; }
}

public sealed record SecurityFinding(string Severity, string Location, string Finding);

/// <summary>An integration stage as stored (composite source context stage → source project, systems). Only what exists is shown.</summary>
public sealed record StageSummary(string Stage, string? SourceProject, int Systems, int Contracts, int Tests, string Status);

public sealed record ExportPreview
{
    public TransferKind Kind { get; init; }
    public string Scope { get; init; } = "";
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<PortableObject> Objects { get; init; } = [];
    public IReadOnlyList<DependencyEntry> Dependencies { get; init; } = [];
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public IReadOnlyList<SecurityFinding> Security { get; init; } = [];
    public IReadOnlyList<string> Excluded { get; init; } = [];
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public IReadOnlyList<StageSummary> Stages { get; init; } = [];
    public string Completeness { get; init; } = "COMPLETE";
}

public sealed record ExportResult(Guid OperationId, string FileName, string Sha256, long Bytes, PackageManifest Manifest, IReadOnlyList<SecurityFinding> Security);

public sealed record ImportItem
{
    public string Key { get; init; } = "";
    public string Type { get; init; } = "";
    public string LogicalId { get; init; } = "";
    public string Label { get; init; } = "";
    public Portability Portability { get; init; }
    public ImportStatus Status { get; init; }
    public IReadOnlyList<ImportAction> AllowedActions { get; init; } = [];
    public ImportAction DefaultAction { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record ImportEnvironmentMapping
{
    public string? ImportedEnvironmentId { get; init; }
    public string? ImportedName { get; init; }
    public IReadOnlyList<EnvironmentCandidate> Candidates { get; init; } = [];
    public string Note { get; init; } = "";
}

public sealed record ImportPreview
{
    public Guid OperationId { get; init; }
    public PackageManifest? Manifest { get; init; }
    public IReadOnlyList<TransferCheck> Checks { get; init; } = [];
    public IReadOnlyList<ImportItem> Items { get; init; } = [];
    public ImportEnvironmentMapping? Environment { get; init; }
    public IReadOnlyList<SecurityFinding> Security { get; init; } = [];
    public IReadOnlyList<string> UnsupportedCapabilities { get; init; } = [];
    public string PlanFingerprint { get; init; } = "";
    public bool CanApply { get; init; }
    public string Verdict { get; init; } = "";
}

public sealed record ImportApplyRequest
{
    public Guid OperationId { get; init; }
    public string PlanFingerprint { get; init; } = "";
    public Dictionary<string, ImportAction> Decisions { get; init; } = [];
    public EnvironmentMappingAction EnvironmentAction { get; init; } = EnvironmentMappingAction.LeaveUnresolved;
    public string? TargetEnvironmentId { get; init; }
    public bool ImportEvidence { get; init; }
}

public sealed record ReconciliationItem(string Item, ReconciliationStatus Status, string Detail);
public sealed record ReadinessDimension(string Dimension, string Status, string Detail);
public sealed record IdentityMapping(string Type, string ImportedId, string DestinationId);

public sealed record ImportReport
{
    public Guid OperationId { get; init; }
    public string State { get; init; } = "";
    public IReadOnlyList<string> Applied { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public IReadOnlyList<ReconciliationItem> Reconciliation { get; init; } = [];
    public IReadOnlyList<ReadinessDimension> Readiness { get; init; } = [];
    public IReadOnlyList<IdentityMapping> IdentityMap { get; init; } = [];
    public int HistoricalRecords { get; init; }
    public string? DestinationEnvironmentId { get; init; }
    /// <summary>A sanitised Target Environment profile for the browser to save as a NEW, disabled draft (never merged into an existing profile).</summary>
    public string? ProfileDraftJson { get; init; }
    public string? Error { get; init; }
}

public sealed record TransferOperationSummary(Guid Id, string Kind, string State, string Scope, string Summary, string Actor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? PackageSha256, string? Error);
