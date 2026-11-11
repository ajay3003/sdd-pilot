using Path = System.IO.Path;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.WorkspaceTransfer;
using Npgsql;

namespace BirkNext.Api.Services.WorkspaceTransfer;

public sealed class FullBackupOptions
{
    /// <summary>Where backups are written and restores are staged. Default: {ContentRoot}/App_Data/backups (owner-only ACL).</summary>
    public string? Directory { get; set; }
    public int KeyDerivationIterations { get; set; } = TransferCrypto.DefaultIterations;
    /// <summary>Upper bound for the managed files held in memory while they are captured at the snapshot (they are small JSON stores).</summary>
    public long MaxManagedFileBytes { get; set; } = 256L << 20;
}

/// <summary>
/// Full application backup and isolated restore (operation A). The database part is PostgreSQL-native: <c>pg_dump --format=custom</c> of the whole
/// database at an exported snapshot. The manifest's row counts and migration state are read in the SAME snapshot, so they describe exactly what
/// the dump contains.
///
/// <para><b>Consistency boundary.</b>
/// <list type="number">
/// <item>Hash the managed files.</item>
/// <item>Open a REPEATABLE READ, READ ONLY transaction and <c>pg_export_snapshot()</c>.</item>
/// <item>Read the managed files into memory and hash them again. If any file changed between 1 and 3, roll back and retry (up to 3 attempts).</item>
/// <item><c>pg_dump --snapshot</c> while the transaction stays open.</item>
/// </list>
/// So the files are the versions that were current when the snapshot was taken. When every attempt saw a change, the backup still completes, but
/// it is labelled FILES_CHANGED_DURING_CAPTURE / COMPLETE_WITH_FILE_DRIFT and is never called point-in-time. No application-wide lock is taken:
/// the database snapshot needs none, and the file window is milliseconds.</para>
///
/// <para><b>Protection.</b> Always encrypted (<see cref="TransferCrypto"/>); the ZIP is streamed into the encryptor, so no plaintext copy exists.
/// The backup directory is owner-only. Secret stores are never included (<see cref="ManagedFileStores.Exclusions"/>).</para>
///
/// <para><b>Restore.</b> Only into a NEW database <c>birknext_restore_{12 hex}</c> on the same server, with the managed files in a separate
/// directory. The active database and files are never written. The report compares the restored state with the manifest. There is no cutover.</para>
/// </summary>
public sealed class FullBackupService
{
    public const string RestorePrefix = "birknext_restore_";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Regex SnapshotReference = new(@"""snapshotId""\s*:\s*""(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RestoreName = new(@"^birknext_restore_[0-9a-f]{12}$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly PostgresTools _tools;
    private readonly ManagedFileStores _files;
    private readonly FullBackupOptions _options;
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _codeMigrations;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    public FullBackupService(string connectionString, PostgresTools tools, ManagedFileStores files, FullBackupOptions options, string backupDirectory,
        Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> codeMigrations, ILogger<FullBackupService> logger, TimeProvider? clock = null)
    {
        _connectionString = connectionString;
        _tools = tools;
        _files = files;
        _options = options;
        BackupDirectory = backupDirectory;
        _codeMigrations = codeMigrations;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public string BackupDirectory { get; }
    private string RestoreRoot => Path.Combine(BackupDirectory, "restores");

    /// <summary>The running build (version without build metadata, and commit) from the one build-information provider.</summary>
    public static (string Version, string Commit) ApplicationIdentity()
    {
        var build = BirkNext.Api.Services.SystemInfo.BuildInfoService.Current;
        return (build.BuildVersion is { Length: > 0 } v ? v : build.InformationalVersion.Split('+')[0], build.SourceCommit ?? "unknown");
    }

    // ───────────────────────────── Create ─────────────────────────────

    public async Task<BackupCreateResult> CreateAsync(string passphrase, string? note, CancellationToken ct)
    {
        if (TransferCrypto.PassphraseProblem(passphrase) is { } problem) throw new TransferRequestException("PASSPHRASE", problem);
        if (_tools.ConfigurationProblem() is { } tool) throw new TransferRequestException("TOOLS_NOT_CONFIGURED", tool);
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct)) throw new TransferRequestException("BUSY", "Another backup or restore is running; try again when it finishes.");
        try
        {
            var dumpVersion = await _tools.VersionAsync("pg_dump", ct)
                ?? throw new TransferRequestException("TOOLS_UNAVAILABLE", "pg_dump could not be started. Configure Backup:PostgresTools (Host with BinDirectory, or ContainerExec with the database container).");
            EnsureDirectory(BackupDirectory);
            var connection = new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false };
            await EnsureDiskSpaceAsync(connection, ct);

            var backupId = Guid.NewGuid();
            var now = _clock.GetUtcNow();
            var fileName = $"birknext-backup-{now:yyyyMMdd-HHmmss}-{backupId.ToString("N")[..8]}.bnbackup";
            var finalPath = Path.Combine(BackupDirectory, fileName);
            var partial = finalPath + ".partial";
            var (version, commit) = ApplicationIdentity();
            var warnings = new List<string>();

            await using var db = new NpgsqlConnection(connection.ConnectionString);
            await db.OpenAsync(ct);
            CapturedFiles? files = null;
            string consistency = "";
            NpgsqlTransaction? snapshotTx = null;
            string snapshotId = "";
            for (var attempt = 1; attempt <= 3 && files is null; attempt++)
            {
                var before = HashStores();
                snapshotTx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", db, snapshotTx)) await ro.ExecuteNonQueryAsync(ct);
                await using (var export = new NpgsqlCommand("SELECT pg_export_snapshot()", db, snapshotTx)) snapshotId = (string)(await export.ExecuteScalarAsync(ct))!;
                AfterSnapshotExported?.Invoke(attempt);
                var captured = CaptureFiles(now);
                if (captured.Files.All(f => before.TryGetValue(f.Key, out var sha) && sha == f.Sha256) && before.Count == captured.Files.Count)
                {
                    files = captured;
                    consistency = "DATABASE_EXPORTED_SNAPSHOT; FILES_UNCHANGED_ACROSS_SNAPSHOT";
                }
                else if (attempt == 3)
                {
                    files = captured;
                    consistency = "DATABASE_EXPORTED_SNAPSHOT; FILES_CHANGED_DURING_CAPTURE";
                    warnings.Add("A managed file changed while the snapshot was taken (3 attempts). The files are the versions read just after the snapshot; this backup is not point-in-time across database and files.");
                }
                else
                {
                    await snapshotTx.RollbackAsync(ct);
                    await snapshotTx.DisposeAsync();
                    snapshotTx = null;
                }
            }

            BackupManifest manifest;
            try
            {
                var inventory = await InventoryAsync(db, snapshotTx!, snapshotId, ct);
                string serverVersion;
                await using (var show = new NpgsqlCommand("SHOW server_version", db, snapshotTx)) serverVersion = (string)(await show.ExecuteScalarAsync(ct))!;
                var snapshotIds = await ExistingSnapshotIdsAsync(db, snapshotTx!, ct);
                var referenced = files!.Files.SelectMany(f => SnapshotReference.Matches(System.Text.Encoding.UTF8.GetString(f.Content)).Select(m => m.Groups["id"].Value.ToLowerInvariant())).Distinct().ToList();
                var dangling = referenced.Where(id => !snapshotIds.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var heads = inventory.Migrations.ToDictionary(kv => kv.Key, kv => kv.Value.LastOrDefault() ?? "");
                var header = new BackupPublicHeader
                {
                    BackupId = backupId, CreatedAt = now, ApplicationVersion = version, ApplicationCommit = commit, DatabaseServerVersion = serverVersion,
                    DumpToolVersion = dumpVersion, MigrationHeads = heads, KeyDerivationIterations = _options.KeyDerivationIterations, Note = (note ?? "").Trim()[..Math.Min((note ?? "").Trim().Length, 200)],
                };
                manifest = new BackupManifest
                {
                    BackupId = backupId, ApplicationVersion = version, ApplicationCommit = commit, DatabaseVersion = serverVersion, DumpToolVersion = dumpVersion,
                    DatabaseName = connection.Database ?? "", BackupTimestamp = now,
                    IncludedComponents = ["database/birknext.dump — pg_dump custom format, every schema", .. _files.Stores.Select(s => $"files/{s.Name} — {s.Owner}")],
                    ExcludedComponents = ManagedFileStores.Exclusions, Database = inventory,
                    ManagedFileInventory = files.Files.Select(f => new BackupManagedFile(f.Store, f.File, f.Content.Length, f.Sha256, f.CapturedAt)).ToList(),
                    ConsistencyMethod = consistency,
                    ConsistencyNotes =
                    [
                        "Row counts, migration state and the dump come from one exported REPEATABLE READ snapshot.",
                        "Managed files were hashed before the snapshot and re-read right after it; unchanged hashes mean they are the versions current at the snapshot.",
                        "No application-wide write lock is held.",
                    ],
                    EncryptionMetadata = $"AES-256-GCM, STREAM construction, 1 MiB chunks; key PBKDF2-HMAC-SHA256 with {_options.KeyDerivationIterations:N0} iterations and a random 16-byte salt. The passphrase is not stored anywhere; losing it loses the backup.",
                    BackupCompleteness = consistency.Contains("FILES_CHANGED_DURING_CAPTURE", StringComparison.Ordinal) ? "COMPLETE_WITH_FILE_DRIFT" : "COMPLETE",
                    RequiredDependencies = [$"PostgreSQL server ≥ {serverVersion.Split(' ')[0]} (same major recommended) and pg_restore ≥ the dump tool version", "This BirkNext build or a newer one (see migration heads)"],
                    FileSnapshotReferences = referenced.Count, DanglingFileReferences = dangling,
                };

                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    TryRestrict(partial, isDirectory: false);
                    await using (var encrypted = TransferCrypto.CreateEncryptingStream(output, passphrase, JsonSerializer.Serialize(header, TransferJson.Options), _options.KeyDerivationIterations))
                    {
                        using var zip = new TransferArchiveWriter(encrypted);
                        foreach (var f in files.Files)
                            await zip.AddAsync($"files/{f.Store}/{f.File}", s => s.WriteAsync(f.Content, ct).AsTask());
                        // The dump is already compressed by pg_dump: store it without recompressing.
                        await zip.AddAsync("database/birknext.dump", s => _tools.DumpAsync(connection, snapshotId, s, ct), System.IO.Compression.CompressionLevel.NoCompression);
                        await zip.AddJsonAsync("database/inventory.json", inventory, TransferJson.Options);
                        await zip.AddJsonAsync("manifest.json", manifest, TransferJson.Options);
                        await zip.SealAsync(TransferJson.Options);
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
            finally
            {
                if (snapshotTx is not null) { await snapshotTx.RollbackAsync(CancellationToken.None); await snapshotTx.DisposeAsync(); }
            }

            File.Move(partial, finalPath);
            var summary = Summary(finalPath) ?? throw new InvalidOperationException("The backup was written but its header cannot be read back.");
            _logger.LogInformation("Full backup {BackupId} written ({Bytes} bytes, {Tables} tables, consistency {Consistency})", backupId, summary.Bytes, manifest.Database.Tables.Count, consistency);
            return new BackupCreateResult(summary, consistency, manifest.BackupCompleteness, manifest.Database.Tables.Count, manifest.Database.Tables.Sum(t => t.Rows),
                manifest.ManagedFileInventory.Count, manifest.ExcludedComponents, warnings);
        }
        finally { Gate.Release(); }
    }

    private sealed record CapturedFile(string Store, string File, string Key, byte[] Content, string Sha256, DateTimeOffset CapturedAt);
    private sealed record CapturedFiles(IReadOnlyList<CapturedFile> Files);

    private Dictionary<string, string> HashStores() =>
        _files.Existing().ToDictionary(x => $"{x.Store.Name}/{x.File}", x => Sha(File.ReadAllBytes(x.FullPath)));

    private CapturedFiles CaptureFiles(DateTimeOffset now)
    {
        var list = new List<CapturedFile>();
        long total = 0;
        foreach (var (store, file, full) in _files.Existing())
        {
            var bytes = File.ReadAllBytes(full);
            total += bytes.Length;
            if (total > _options.MaxManagedFileBytes) throw new TransferRequestException("FILES_TOO_LARGE", $"The managed file stores exceed {_options.MaxManagedFileBytes:N0} bytes.");
            list.Add(new CapturedFile(store.Name, file, $"{store.Name}/{file}", bytes, Sha(bytes), _clock.GetUtcNow()));
        }
        return new CapturedFiles(list);
    }

    private static async Task<BackupDatabaseInventory> InventoryAsync(NpgsqlConnection db, NpgsqlTransaction tx, string snapshotId, CancellationToken ct)
    {
        var tables = new List<(string Schema, string Table)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
            ORDER BY 1, 2
            """, db, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) tables.Add((r.GetString(0), r.GetString(1)));
        var counts = new List<BackupTableCount>();
        foreach (var (schema, table) in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {Quote(schema)}.{Quote(table)}", db, tx);
            counts.Add(new BackupTableCount(schema, table, (long)(await cmd.ExecuteScalarAsync(ct))!));
        }
        var migrations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (schema, table) in tables.Where(t => t.Table.Equals("__EFMigrationsHistory", StringComparison.Ordinal) || t.Table.Equals("__ef_migrations_history", StringComparison.Ordinal)))
        {
            var ids = new List<string>();
            await using var cmd = new NpgsqlCommand($"SELECT \"MigrationId\" FROM {Quote(schema)}.{Quote(table)} ORDER BY 1", db, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
            migrations[$"{schema}.{table}"] = ids;
        }
        int fks;
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE c.contype = 'f' AND n.nspname NOT IN ('pg_catalog','information_schema')", db, tx))
            fks = (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        return new BackupDatabaseInventory { Tables = counts, Migrations = migrations, ForeignKeys = fks, SnapshotId = snapshotId };
    }

    private static async Task<HashSet<string>> ExistingSnapshotIdsAsync(NpgsqlConnection db, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('public.iqr_source_snapshots') IS NOT NULL", db, tx);
        if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return ids;
        await using var cmd = new NpgsqlCommand("SELECT \"Id\"::text FROM public.iqr_source_snapshots", db, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetString(0).ToLowerInvariant());
        return ids;
    }

    private async Task EnsureDiskSpaceAsync(NpgsqlConnectionStringBuilder connection, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection.ConnectionString);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pg_database_size(current_database())", db);
        var size = (long)(await cmd.ExecuteScalarAsync(ct))!;
        var free = FreeBytes(BackupDirectory);
        // A custom-format dump is usually far smaller than the database; requiring the full size plus 256 MB keeps a safe margin.
        var needed = size + (256L << 20);
        if (free is { } f && f < needed)
            throw new TransferRequestException("INSUFFICIENT_DISK_SPACE", $"The backup directory has {f / (1 << 20):N0} MB free; at least {needed / (1 << 20):N0} MB is required.");
    }

    /// <summary>Test hook: runs right after the snapshot is exported on each capture attempt (1-based), before the files are re-read.</summary>
    internal Action<int>? AfterSnapshotExported { get; set; }

    /// <summary>Free bytes on the drive of <paramref name="directory"/>; overridable for tests.</summary>
    internal Func<string, long?> FreeBytes { get; set; } = directory =>
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }
    };

    // ───────────────────────────── List / inspect ─────────────────────────────

    public IReadOnlyList<BackupSummary> List()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, "*.bnbackup").Select(Summary).OfType<BackupSummary>().OrderByDescending(b => b.CreatedAt).ToList();
    }

    private static BackupSummary? Summary(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var preamble = TransferCrypto.ReadPreamble(f);
            var h = JsonSerializer.Deserialize<BackupPublicHeader>(preamble.HeaderJson, TransferJson.Options)!;
            return new BackupSummary(Path.GetFileName(path), h.BackupId, h.CreatedAt, new FileInfo(path).Length, h.ApplicationVersion, h.DatabaseServerVersion, h.MigrationHeads, h.Note);
        }
        catch (Exception ex) when (ex is TransferFormatException or JsonException or IOException) { return null; }
    }

    /// <summary>A backup file inside the backup directory, by its file name only (no paths, no traversal).</summary>
    public string ResolveBackup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".bnbackup", StringComparison.OrdinalIgnoreCase))
            throw new TransferRequestException("BAD_NAME", "Name a backup file from the backup list.");
        var full = Path.Combine(BackupDirectory, fileName);
        if (!File.Exists(full)) throw new TransferRequestException("NOT_FOUND", $"No backup named {fileName}.");
        return full;
    }

    public async Task<BackupInspection> InspectAsync(string fileName, string? passphrase, CancellationToken ct)
    {
        var path = ResolveBackup(fileName);
        BackupPublicHeader header;
        await using (var f = File.OpenRead(path))
            header = JsonSerializer.Deserialize<BackupPublicHeader>(TransferCrypto.ReadPreamble(f).HeaderJson, TransferJson.Options)!;
        if (string.IsNullOrEmpty(passphrase))
            return new BackupInspection(fileName, header, false, null,
                [new("Header", TransferCheckStatus.NotRun, "The header is readable but not authenticated until the backup is decrypted with its passphrase.")], "NOT_VERIFIED");
        var staging = Path.Combine(RestoreRoot, "inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (manifest, checks) = await DecryptAndVerifyAsync(path, passphrase, staging, ct);
            return new BackupInspection(fileName, header, true, manifest, checks, Verdict(checks));
        }
        finally { TryDeleteDirectory(staging); }
    }

    private async Task<(BackupManifest? Manifest, List<TransferCheck> Checks)> DecryptAndVerifyAsync(string path, string passphrase, string staging, CancellationToken ct)
    {
        var checks = new List<TransferCheck>();
        EnsureDirectory(staging);
        var payload = Path.Combine(staging, "payload.zip");
        BackupPublicHeader header;
        try
        {
            await using var input = File.OpenRead(path);
            var preamble = TransferCrypto.ReadPreamble(input);
            header = JsonSerializer.Deserialize<BackupPublicHeader>(preamble.HeaderJson, TransferJson.Options)!;
            if (header.Format != "birknext-backup" || header.FormatVersion != 1)
            {
                checks.Add(new("Format", TransferCheckStatus.Fail, $"Unsupported backup format {header.Format} v{header.FormatVersion}."));
                return (null, checks);
            }
            await using var plain = TransferCrypto.CreateDecryptingStream(input, preamble, passphrase);
            await using var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            TryRestrict(payload, isDirectory: false);
            await plain.CopyToAsync(output, ct);
        }
        catch (TransferAuthenticationException ex)
        {
            checks.Add(new("Decryption and authenticity", TransferCheckStatus.Fail, ex.Message));
            return (null, checks);
        }
        catch (TransferFormatException ex)
        {
            checks.Add(new("Format", TransferCheckStatus.Fail, $"{ex.Code}: {ex.Message}"));
            return (null, checks);
        }
        checks.Add(new("Decryption and authenticity", TransferCheckStatus.Pass, "Every chunk authenticated with the passphrase (AES-256-GCM); the header and chunk order are unchanged."));

        await using var zipFile = File.OpenRead(payload);
        TransferArchiveReader zip;
        try { zip = TransferArchiveReader.Open(zipFile, TransferArchiveLimits.Backup); }
        catch (TransferFormatException ex) { checks.Add(new("Archive", TransferCheckStatus.Fail, $"{ex.Code}: {ex.Message}")); return (null, checks); }
        using (zip)
        {
            var problems = await zip.VerifyHashesAsync(TransferJson.Options, ct);
            checks.Add(problems.Count == 0
                ? new("Content hashes", TransferCheckStatus.Pass, $"{zip.Paths.Count - 1} entries match integrity/hashes.json (SHA-256).")
                : new("Content hashes", TransferCheckStatus.Fail, string.Join(" ", problems.Take(5))));
            if (problems.Count > 0) return (null, checks);
            var manifest = await zip.ReadJsonAsync<BackupManifest>("manifest.json", TransferJson.Options);
            checks.Add(manifest.BackupId == header.BackupId && manifest.BackupFormatVersion == 1
                ? new("Manifest", TransferCheckStatus.Pass, $"Backup {manifest.BackupId:D} from {manifest.BackupTimestamp:u}, BirkNext {manifest.ApplicationVersion} ({manifest.ApplicationCommit}).")
                : new("Manifest", TransferCheckStatus.Fail, "The manifest does not belong to this backup header."));
            checks.Add(zip.Has("database/birknext.dump")
                ? new("Database dump", TransferCheckStatus.Pass, $"pg_dump custom format from {manifest.DumpToolVersion}; {manifest.Database.Tables.Count} tables, {manifest.Database.Tables.Sum(t => t.Rows):N0} rows at snapshot {manifest.Database.SnapshotId}.")
                : new("Database dump", TransferCheckStatus.Fail, "database/birknext.dump is missing."));
            var missingFiles = manifest.ManagedFileInventory.Where(f => !zip.Has($"files/{f.Store}/{f.RelativePath}")).ToList();
            checks.Add(missingFiles.Count == 0
                ? new("Managed files", TransferCheckStatus.Pass, $"{manifest.ManagedFileInventory.Count} managed files present.")
                : new("Managed files", TransferCheckStatus.Fail, $"Missing: {string.Join(", ", missingFiles.Select(f => f.Store + "/" + f.RelativePath))}."));
            checks.Add(Compatibility(manifest));
            checks.Add(manifest.ConsistencyMethod.Contains("FILES_CHANGED_DURING_CAPTURE", StringComparison.Ordinal)
                ? new("Consistency", TransferCheckStatus.Warning, $"{manifest.ConsistencyMethod}: database and files are not from one moment.")
                : new("Consistency", TransferCheckStatus.Pass, manifest.ConsistencyMethod));
            return (manifest, checks);
        }
    }

    private TransferCheck Compatibility(BackupManifest manifest)
    {
        var code = _codeMigrations();
        var unknown = manifest.Database.Migrations
            .SelectMany(kv => kv.Value.Where(id => code.TryGetValue(kv.Key, out var known) && !known.Contains(id, StringComparer.Ordinal)).Select(id => $"{kv.Key}: {id}"))
            .ToList();
        var unknownTables = manifest.Database.Migrations.Keys.Where(k => !code.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            return new("Compatibility", TransferCheckStatus.Fail, $"The backup has migrations this build does not know (it comes from a newer BirkNext): {string.Join(", ", unknown.Take(5))}.");
        var behind = code.Sum(kv => kv.Value.Count - (manifest.Database.Migrations.TryGetValue(kv.Key, out var have) ? have.Count : 0));
        return behind > 0
            ? new("Compatibility", TransferCheckStatus.Warning, $"The backup is {behind} migration(s) behind this build; BirkNext would migrate the restored database forward when it starts against it.")
            : new("Compatibility", TransferCheckStatus.Pass, unknownTables.Count == 0 ? "Schema migration state equals this build." : $"Schema state equals this build (history tables not used by this build: {string.Join(", ", unknownTables)}).");
    }

    // ───────────────────────────── Isolated restore ─────────────────────────────

    public async Task<RestoreReport> RestoreIsolatedAsync(string fileName, string passphrase, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new TransferRequestException("PASSPHRASE", "The backup passphrase is required.");
        if (_tools.ConfigurationProblem() is { } tool) throw new TransferRequestException("TOOLS_NOT_CONFIGURED", tool);
        var path = ResolveBackup(fileName);
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct)) throw new TransferRequestException("BUSY", "Another backup or restore is running; try again when it finishes.");
        var restoreId = Guid.NewGuid();
        var dir = Path.Combine(RestoreRoot, restoreId.ToString("N"));
        var started = _clock.GetUtcNow();
        try
        {
            var staging = Path.Combine(dir, "staging");
            var (manifest, checks) = await DecryptAndVerifyAsync(path, passphrase, staging, ct);
            if (manifest is null || checks.Any(c => c.Status == TransferCheckStatus.Fail))
                return Persist(dir, new RestoreReport { RestoreId = restoreId, BackupFileName = fileName, StartedAt = started, FinishedAt = _clock.GetUtcNow(), Checks = checks, Verdict = "FAILED" });

            var active = new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false };
            var target = RestoreName.IsMatch(RestorePrefix + restoreId.ToString("N")[..12]) ? RestorePrefix + restoreId.ToString("N")[..12] : throw new InvalidOperationException();
            if (string.Equals(target, active.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to restore over the active database.");
            await using (var admin = new NpgsqlConnection(active.ConnectionString))
            {
                await admin.OpenAsync(ct);
                await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", admin);
                exists.Parameters.AddWithValue("n", target);
                if (await exists.ExecuteScalarAsync(ct) is not null) throw new TransferRequestException("TARGET_EXISTS", $"Database {target} already exists; it is never overwritten.");
                await using var create = new NpgsqlCommand($"CREATE DATABASE {Quote(target)}", admin);
                await create.ExecuteNonQueryAsync(ct);
            }
            var targetConnection = new NpgsqlConnectionStringBuilder(active.ConnectionString) { Database = target, Pooling = false };
            var filesDir = Path.Combine(dir, "files");
            await using (var zipFile = File.OpenRead(Path.Combine(staging, "payload.zip")))
            using (var zip = TransferArchiveReader.Open(zipFile, TransferArchiveLimits.Backup))
            {
                try
                {
                    await using var dump = zip.OpenRead("database/birknext.dump");
                    await _tools.RestoreAsync(targetConnection, dump, ct);
                    checks.Add(new("Database restore", TransferCheckStatus.Pass, $"pg_restore into the new database {target} (single transaction)."));
                }
                catch (PostgresToolException ex)
                {
                    checks.Add(new("Database restore", TransferCheckStatus.Fail, ex.Message));
                    // Compensating cleanup of the scratch database this call created a moment ago (nothing else is ever dropped).
                    await DropOwnRestoreDatabaseAsync(active, target);
                    checks.Add(new("Cleanup", TransferCheckStatus.Pass, $"The incomplete database {target}, created by this restore, was dropped."));
                    return Persist(dir, new RestoreReport { RestoreId = restoreId, BackupFileName = fileName, BackupId = manifest.BackupId, RestoredDatabase = target, StartedAt = started, FinishedAt = _clock.GetUtcNow(), Checks = checks, Verdict = "FAILED" });
                }
                foreach (var f in manifest.ManagedFileInventory)
                {
                    var store = _files.Stores.FirstOrDefault(s => s.Name == f.Store);
                    if (store is null || !store.Files.Contains(f.RelativePath)) { checks.Add(new("Managed files", TransferCheckStatus.Fail, $"Unknown managed file {f.Store}/{f.RelativePath}.")); continue; }
                    var destination = Path.Combine(filesDir, f.Store, f.RelativePath);
                    EnsureDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = zip.OpenRead($"files/{f.Store}/{f.RelativePath}");
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) await source.CopyToAsync(output, ct);
                }
            }
            TryDeleteDirectory(staging);
            checks.AddRange(await ValidateRestoredAsync(targetConnection, filesDir, manifest, active.Database ?? "", ct));
            var report = new RestoreReport
            {
                RestoreId = restoreId, BackupFileName = fileName, BackupId = manifest.BackupId, RestoredDatabase = target, RestoredFilesDirectory = filesDir,
                StartedAt = started, FinishedAt = _clock.GetUtcNow(), Checks = checks, Verdict = Verdict(checks) switch { "PASS" => "VERIFIED", "WARNING" => "VERIFIED_WITH_WARNINGS", _ => "FAILED" },
            };
            _logger.LogInformation("Isolated restore {RestoreId} of backup {BackupId} into {Database}: {Verdict}", restoreId, manifest.BackupId, target, report.Verdict);
            return Persist(dir, report);
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(dir, "staging"));
            Gate.Release();
        }
    }

    /// <summary>Independent checks of the restored database and files against the manifest (never against the active installation).</summary>
    private async Task<IReadOnlyList<TransferCheck>> ValidateRestoredAsync(NpgsqlConnectionStringBuilder target, string filesDir, BackupManifest manifest, string activeDatabase, CancellationToken ct)
    {
        var checks = new List<TransferCheck>
        {
            !string.Equals(target.Database, activeDatabase, StringComparison.OrdinalIgnoreCase)
                ? new("Isolation", TransferCheckStatus.Pass, $"Restored into {target.Database}; the active database {activeDatabase} was not written.")
                : new("Isolation", TransferCheckStatus.Fail, "The restore target is the active database."),
        };
        await using var db = new NpgsqlConnection(target.ConnectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var restored = await InventoryAsync(db, tx, "", ct);
        var expected = manifest.Database.Tables.ToDictionary(t => $"{t.Schema}.{t.Table}", t => t.Rows);
        var actual = restored.Tables.ToDictionary(t => $"{t.Schema}.{t.Table}", t => t.Rows);
        var tableDiff = expected.Where(kv => !actual.TryGetValue(kv.Key, out var n) || n != kv.Value).Select(kv => $"{kv.Key}: {kv.Value} → {(actual.TryGetValue(kv.Key, out var n) ? n : -1)}")
            .Concat(actual.Keys.Except(expected.Keys).Select(k => $"{k}: unexpected")).ToList();
        checks.Add(tableDiff.Count == 0
            ? new("Tables and rows", TransferCheckStatus.Pass, $"{actual.Count} tables, {actual.Values.Sum():N0} rows: identical to the backup inventory.")
            : new("Tables and rows", TransferCheckStatus.Fail, string.Join("; ", tableDiff.Take(8))));
        var migrationDiff = manifest.Database.Migrations.Where(kv => !restored.Migrations.TryGetValue(kv.Key, out var ids) || !ids.SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
        checks.Add(migrationDiff.Count == 0
            ? new("EF migration state", TransferCheckStatus.Pass, string.Join(", ", restored.Migrations.Select(kv => $"{kv.Key} {kv.Value.Count}")))
            : new("EF migration state", TransferCheckStatus.Fail, $"Differs in {string.Join(", ", migrationDiff)}."));
        checks.Add(restored.ForeignKeys == manifest.Database.ForeignKeys
            ? new("Referential constraints", TransferCheckStatus.Pass, $"{restored.ForeignKeys} foreign keys restored and validated by PostgreSQL.")
            : new("Referential constraints", TransferCheckStatus.Fail, $"{restored.ForeignKeys} foreign keys, expected {manifest.Database.ForeignKeys}."));
        var fileProblems = new List<string>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in manifest.ManagedFileInventory)
        {
            var full = Path.Combine(filesDir, f.Store, f.RelativePath);
            if (!File.Exists(full)) { fileProblems.Add($"{f.Store}/{f.RelativePath} missing"); continue; }
            var bytes = await File.ReadAllBytesAsync(full, ct);
            if (Sha(bytes) != f.Sha256) fileProblems.Add($"{f.Store}/{f.RelativePath} hash differs");
            foreach (Match m in SnapshotReference.Matches(System.Text.Encoding.UTF8.GetString(bytes))) referenced.Add(m.Groups["id"].Value.ToLowerInvariant());
        }
        checks.Add(fileProblems.Count == 0
            ? new("Managed files", TransferCheckStatus.Pass, $"{manifest.ManagedFileInventory.Count} files restored with identical SHA-256.")
            : new("Managed files", TransferCheckStatus.Fail, string.Join("; ", fileProblems)));
        var snapshotIds = await ExistingSnapshotIdsAsync(db, tx, ct);
        var dangling = referenced.Where(id => !snapshotIds.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        checks.Add(referenced.Count == manifest.FileSnapshotReferences && dangling.SequenceEqual(manifest.DanglingFileReferences)
            ? new("File → database references", dangling.Count == 0 ? TransferCheckStatus.Pass : TransferCheckStatus.Warning,
                dangling.Count == 0 ? $"All {referenced.Count} source-snapshot references from managed files resolve in the restored database."
                : $"{referenced.Count} references; {dangling.Count} were already unresolved in the source at backup time (unchanged): {string.Join(", ", dangling.Take(3))}.")
            : new("File → database references", TransferCheckStatus.Fail, $"{referenced.Count} references with {dangling.Count} unresolved; the backup recorded {manifest.FileSnapshotReferences} with {manifest.DanglingFileReferences.Count}."));
        await tx.RollbackAsync(ct);
        checks.Add(new("Secrets", TransferCheckStatus.Warning, "Credentials, sessions and pairings were not in the backup (by design): re-enter them before using the restored installation."));
        return checks;
    }

    private static async Task DropOwnRestoreDatabaseAsync(NpgsqlConnectionStringBuilder active, string target)
    {
        if (!RestoreName.IsMatch(target) || string.Equals(target, active.Database, StringComparison.OrdinalIgnoreCase)) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(active.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Quote(target)}", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public IReadOnlyList<RestoreReport> Restores()
    {
        if (!Directory.Exists(RestoreRoot)) return [];
        return Directory.EnumerateDirectories(RestoreRoot).Select(d => Path.Combine(d, "report.json")).Where(File.Exists)
            .Select(f => { try { return JsonSerializer.Deserialize<RestoreReport>(File.ReadAllText(f), TransferJson.Options); } catch (JsonException) { return null; } })
            .OfType<RestoreReport>().OrderByDescending(r => r.StartedAt).ToList();
    }

    private static string Verdict(IReadOnlyList<TransferCheck> checks) =>
        checks.Any(c => c.Status == TransferCheckStatus.Fail) ? "FAIL" : checks.Any(c => c.Status == TransferCheckStatus.Warning) ? "WARNING" : "PASS";

    private RestoreReport Persist(string dir, RestoreReport report)
    {
        EnsureDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, TransferJson.Options));
        return report;
    }

    private void EnsureDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        TryRestrict(path, isDirectory: true);
    }

    private void TryRestrict(string path, bool isDirectory)
    {
        try { LocalSessionService.RestrictToCurrentUser(path, isDirectory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or IOException)
        { _logger.LogWarning("Could not restrict {Path} to the current user: {Message}", path, ex.Message); }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>A refused transfer request: <see cref="Code"/> is stable for the UI, the message is safe to show (never contains a secret).</summary>
public sealed class TransferRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
