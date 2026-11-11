using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.WorkspaceTransfer;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.WorkspaceTransfer;

public sealed partial class WorkspacePackageService
{
    public const string Previewed = "PREVIEWED", Approved = "APPROVED", Applying = "APPLYING", Completed = "COMPLETED",
        FailedRecoverable = "FAILED_RECOVERABLE", FailedRolledBack = "FAILED_ROLLED_BACK";

    /// <summary>Test hooks: fail inside the database transaction after N applied objects, or in the file phase.</summary>
    internal Func<int, bool>? FailDatabasePhaseAfter { get; set; }
    internal Func<string, bool>? FailFilePhaseAt { get; set; }

    private sealed record PlannedItem(ImportItem Item, PortableObject Object, string DestinationToken);
    private sealed record StoredPlan(List<ImportItem> Items, Dictionary<string, string> Tokens, List<EnvironmentCandidate> Candidates, string? ImportedEnvironmentId, string? ImportedName);

    // ───────────────────────────── Stage ─────────────────────────────

    /// <summary>Streams an uploaded package to the staging directory (bounded; never held in memory) and returns its operation id.</summary>
    public async Task<(Guid OperationId, string Sha256, long Bytes)> StageAsync(Stream body, string actor, CancellationToken ct)
    {
        EnsureDirectory(Staging);
        SweepStaging();
        var operationId = Guid.NewGuid();
        var staged = $"{operationId:N}.birknext";
        var path = Path.Combine(Staging, staged);
        long total = 0;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > _options.MaxUploadBytes) throw new TransferRequestException("PACKAGE_TOO_LARGE", $"The package exceeds {_options.MaxUploadBytes / (1 << 20):N0} MB.");
                hash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        catch { TryDelete(path); throw; }
        if (total == 0) { TryDelete(path); throw new TransferRequestException("EMPTY", "The upload is empty."); }
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var now = _clock.GetUtcNow();
        await RecordAsync(new WorkspaceTransferOperationRecord
        {
            Id = operationId, Kind = "Import", State = "STAGED", Actor = actor, CreatedAt = now, UpdatedAt = now, Scope = "Unknown", Summary = "Package uploaded", PackageSha256 = sha,
            StagedPackage = staged,
        }, ct);
        return (operationId, sha, total);
    }

    private void SweepStaging()
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(Staging, "*.birknext"))
            if (File.GetLastWriteTimeUtc(file) < _clock.GetUtcNow().UtcDateTime - _options.StagingLifetime) TryDelete(file);
    }

    // ───────────────────────────── Preview (writes only the operation record) ─────────────────────────────

    public async Task<ImportPreview> PreviewImportAsync(Guid operationId, IReadOnlyList<EnvironmentCandidate> candidates, CancellationToken ct)
    {
        var op = await _db.WorkspaceTransferOperations.FirstOrDefaultAsync(o => o.Id == operationId && o.Kind == "Import", ct)
            ?? throw new TransferRequestException("NOT_FOUND", "No staged import with that id.");
        if (op.State is Applying or Completed) throw new TransferRequestException("STATE", $"This import is {op.State}.");
        var path = Path.Combine(Staging, op.StagedPackage ?? "");
        if (op.StagedPackage is null || !File.Exists(path)) throw new TransferRequestException("EXPIRED", "The staged package has expired; upload it again.");

        var checks = new List<TransferCheck>();
        var (manifest, entries, security) = await ReadPackageAsync(path, checks, ct);
        if (manifest is null)
        {
            await UpdateAsync(op, "FAILED_VALIDATION", "Package refused", string.Join(" ", checks.Where(c => c.Status == TransferCheckStatus.Fail).Select(c => c.Detail)), ct);
            return new ImportPreview { OperationId = operationId, Checks = checks, Security = security, Verdict = "REFUSED" };
        }

        var unsupported = manifest.RequiredCapabilities.Where(c => !Supported(c)).ToList();
        checks.Add(unsupported.Count == 0
            ? new("Capabilities", TransferCheckStatus.Pass, $"{manifest.RequiredCapabilities.Count} capability reference(s) known to this build.")
            : new("Capabilities", TransferCheckStatus.Warning, $"Not available in this build (stay UNSUPPORTED / NOT_EXECUTABLE): {string.Join(", ", unsupported)}."));
        if (manifest.Completeness == "PARTIAL")
            checks.Add(new("Completeness", TransferCheckStatus.Warning, "This is an explicitly PARTIAL package: some required dependencies were missing at export and stay unresolved."));

        var planned = new List<PlannedItem>();
        foreach (var entry in entries.OrderBy(e => PackageTypes.Order(e.Object.Type)))
            planned.Add(await ClassifyAsync(entry, manifest, entries, security, null, ct));
        var importedEnvironment = manifest.IncludedIntegrationScopes.FirstOrDefault();
        var profileName = entries.FirstOrDefault(e => e.Object.Type == PackageTypes.Profile) is { } p && TryParse(p.Payload["profileJson"]?.GetValue<string>() ?? "") is JsonObject profile
            ? profile["name"]?.ToString() ?? profile["Name"]?.ToString() : null;
        var plan = new StoredPlan(planned.Select(p => p.Item).ToList(), planned.ToDictionary(p => p.Item.Key, p => p.DestinationToken), candidates.ToList(), importedEnvironment, profileName);
        var planFingerprint = TransferJson.Sha256(op.PackageSha256 + "\n" + string.Join("\n", plan.Items.Select(i => $"{i.Key}={i.Status}")));
        op.Scope = manifest.ExportType.ToString();
        op.PlanJson = JsonSerializer.Serialize(plan, TransferJson.Options);
        op.PlanFingerprint = planFingerprint;
        op.DestinationFingerprint = TransferJson.Sha256(string.Join("\n", plan.Tokens.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}")));
        await UpdateAsync(op, Previewed, $"{manifest.Label}: {plan.Items.Count} objects", null, ct);

        var blocking = checks.Any(c => c.Status == TransferCheckStatus.Fail);
        return new ImportPreview
        {
            OperationId = operationId, Manifest = manifest, Checks = checks, Items = plan.Items, Security = security, UnsupportedCapabilities = unsupported,
            Environment = importedEnvironment is null ? null : new ImportEnvironmentMapping
            {
                ImportedEnvironmentId = importedEnvironment, ImportedName = profileName, Candidates = candidates,
                Note = "Choose an existing environment you have verified, or create a new disabled one. Names never decide, and nothing in it connects or executes until you revalidate it.",
            },
            PlanFingerprint = planFingerprint, CanApply = !blocking, Verdict = blocking ? "REFUSED" : "READY_FOR_REVIEW",
        };
    }

    private bool Supported(string capability)
    {
        var parts = capability.Split(':');
        return parts[0] switch
        {
            "provider" => parts.Length == 3 && Enum.TryParse<ExternalProviderType>(parts[1], out var type) && ExternalIntegrations.ExternalProviderCatalog.Descriptor(type).Capabilities.Contains(parts[2]),
            "critical-e2e" => parts.Length == 2 && Enum.TryParse<CriticalE2EExecutionMode>(parts[1], out _),
            "journey-pack" => parts.Length == 2 && _packs.Any(p => p.PackId == parts[1]),
            _ => false,
        };
    }

    private async Task<(PackageManifest? Manifest, List<PackageEntry> Entries, List<SecurityFinding> Security)> ReadPackageAsync(string path, List<TransferCheck> checks, CancellationToken ct)
    {
        var entries = new List<PackageEntry>();
        await using var file = File.OpenRead(path);
        TransferArchiveReader zip;
        try { zip = TransferArchiveReader.Open(file, TransferArchiveLimits.Package); }
        catch (TransferFormatException ex) { checks.Add(new("Archive", TransferCheckStatus.Fail, $"{ex.Code}: {ex.Message}")); return (null, entries, []); }
        using (zip)
        {
            checks.Add(new("Archive", TransferCheckStatus.Pass, $"{zip.Paths.Count} entries; paths, sizes and compression within limits."));
            var problems = await zip.VerifyHashesAsync(TransferJson.Options, ct);
            if (problems.Count > 0) { checks.Add(new("Content hashes", TransferCheckStatus.Fail, string.Join(" ", problems.Take(5)))); return (null, entries, []); }
            checks.Add(new("Content hashes", TransferCheckStatus.Pass, "Every entry matches its SHA-256 (integrity only; the package is unsigned, so its origin is not proven)."));
            PackageManifest manifest;
            try { manifest = await zip.ReadJsonAsync<PackageManifest>("manifest.json", TransferJson.Options, 16L << 20); }
            catch (TransferFormatException ex) { checks.Add(new("Manifest", TransferCheckStatus.Fail, $"{ex.Code}: {ex.Message}")); return (null, entries, []); }
            if (manifest.Format != "birknext-workspace-package" || manifest.PackageFormatVersion != 1)
            {
                checks.Add(new("Format version", TransferCheckStatus.Fail, $"{manifest.Format} v{manifest.PackageFormatVersion} is not supported by this build (supports birknext-workspace-package v1)."));
                return (null, entries, []);
            }
            if (manifest.ExportType is not (TransferKind.ProjectWorkspace or TransferKind.IntegrationWorkspace or TransferKind.Diagnostic))
            { checks.Add(new("Format version", TransferCheckStatus.Fail, "A full backup is restored from Backup, never imported as a package.")); return (null, entries, []); }
            checks.Add(new("Manifest", TransferCheckStatus.Pass, $"{manifest.ExportType} \"{manifest.Label}\" exported {manifest.ExportTimestamp:u} by BirkNext {manifest.BirkNextApplicationVersion} ({manifest.SourceCommit})."));
            var objects = await zip.ReadJsonAsync<List<PortableObject>>("inventory/objects.json", TransferJson.Options);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in objects)
            {
                if (!keys.Add(PackageIds.Key(o.Type, o.LogicalId)))
                { checks.Add(new("Identities", TransferCheckStatus.Fail, $"Duplicate logical identity {o.Type} {o.LogicalId}.")); return (null, [], []); }
                if (!zip.Has(o.Path)) { checks.Add(new("Inventory", TransferCheckStatus.Fail, $"{o.Type} {o.LogicalId} has no payload entry.")); return (null, [], []); }
                var payload = await zip.ReadJsonAsync<JsonNode>(o.Path, TransferJson.Options);
                if (PackageIds.Fingerprint(payload) != o.Fingerprint) { checks.Add(new("Inventory", TransferCheckStatus.Fail, $"{o.Type} {o.LogicalId}: payload does not match its fingerprint.")); return (null, [], []); }
                entries.Add(new PackageEntry(o, payload));
            }
            checks.Add(new("Identities", TransferCheckStatus.Pass, $"{objects.Count} objects with unique logical identities."));
            var security = ScanAll(entries);
            checks.Add(security.Any(f => f.Severity == PackageSecretScanner.Blocking)
                ? new("Security", TransferCheckStatus.Warning, $"{security.Count(f => f.Severity == PackageSecretScanner.Blocking)} secret-looking value(s): the objects holding them are refused.")
                : new("Security", TransferCheckStatus.Pass, security.Count == 0 ? "No secret- or identifier-shaped values." : $"{security.Count} identifier-shaped value(s); confirm they are synthetic."));
            return (manifest, entries, security);
        }
    }

    // ───────────────────────────── Classification ─────────────────────────────

    private async Task<PlannedItem> ClassifyAsync(PackageEntry entry, PackageManifest manifest, List<PackageEntry> all, List<SecurityFinding> security, string? destinationEnvironment, CancellationToken ct)
    {
        var o = entry.Object;
        var key = PackageIds.Key(o.Type, o.LogicalId);
        ImportItem Item(ImportStatus status, string detail, ImportAction def, params ImportAction[] allowed) => new()
        {
            Key = key, Type = o.Type, LogicalId = o.LogicalId, Label = o.Label, Portability = o.Portability, Status = status, DefaultAction = def, AllowedActions = allowed, Detail = detail,
        };
        if (!PackageTypes.Known.Contains(o.Type)) return new(Item(ImportStatus.Unsupported, "Unknown object type for this build.", ImportAction.SkipOptional, ImportAction.SkipOptional), o, "unsupported");
        if (security.Any(f => f.Severity == PackageSecretScanner.Blocking && f.Location.StartsWith($"{o.Type}:{o.LogicalId}", StringComparison.Ordinal)))
            return new(Item(ImportStatus.Unsupported, "Holds a secret-looking value; refused.", ImportAction.SkipOptional, ImportAction.SkipOptional), o, "refused");
        // A project package may carry only its own projects' objects (a hand-edited package naming another project is refused).
        if (manifest.ExportType == TransferKind.ProjectWorkspace && o.OwningProject is { } owner && o.Type is not PackageTypes.History && !manifest.IncludedProjects.Contains(owner))
            return new(Item(ImportStatus.Unauthorized, $"Belongs to project {owner}, which this package does not declare.", ImportAction.SkipOptional, ImportAction.SkipOptional), o, "unauthorized");
        if (o.Type == PackageTypes.Flow && Enum.TryParse<CriticalE2EExecutionMode>(entry.Payload["mode"]?.ToString(), out var mode) is false)
            return new(Item(ImportStatus.Unsupported, "Execution mode not supported by this build.", ImportAction.SkipOptional, ImportAction.SkipOptional), o, "unsupported");
        var missing = o.Dependencies.Where(d => !all.Any(e => e.Object.Type == d.Type && e.Object.LogicalId == d.LogicalId)).ToList();
        var (status, detail, token) = await DestinationStateAsync(entry, destinationEnvironment, ct);
        if (missing.Count > 0 && status == ImportStatus.New)
        {
            var resolved = new List<PortableObjectRef>();
            foreach (var d in missing) if (await ExistsInDestinationAsync(d, ct)) resolved.Add(d);
            var unresolved = missing.Except(resolved).ToList();
            if (unresolved.Count > 0)
                return new(Item(ImportStatus.MissingDependency, $"Needs {string.Join(", ", unresolved.Select(d => $"{d.Type} {d.LogicalId}"))}, which is neither in the package nor here; imported with the reference kept as UNRESOLVED.",
                    ImportAction.CreateNew, ImportAction.CreateNew, ImportAction.SkipOptional), o, token);
        }
        if (PackageTypes.EnvironmentScoped.Contains(o.Type) && destinationEnvironment is null && status is ImportStatus.New or ImportStatus.EnvironmentRebindRequired)
            return new(Item(ImportStatus.EnvironmentRebindRequired, "Bound to the exported environment: choose a destination environment first. Imported disabled; revalidate before use.",
                ImportAction.CreateNew, ImportAction.CreateNew, ImportAction.SkipOptional), o, token);
        return status switch
        {
            ImportStatus.New => new(Item(status, detail, ImportAction.CreateNew, ImportAction.CreateNew, ImportAction.SkipOptional), o, token),
            ImportStatus.Identical => new(Item(status, detail, ImportAction.ReuseIdentical, ImportAction.ReuseIdentical), o, token),
            ImportStatus.NewerRevision => new(Item(status, detail, ImportAction.ImportAsNewRevision, ImportAction.ImportAsNewRevision, ImportAction.KeepExisting), o, token),
            ImportStatus.OlderRevision => new(Item(status, detail, ImportAction.KeepExisting, ImportAction.KeepExisting), o, token),
            ImportStatus.ConflictingContent => new(Item(status, detail, ImportAction.KeepExisting,
                o.Type is PackageTypes.Flow ? [ImportAction.KeepExisting, ImportAction.CreateNew, ImportAction.ResolveManually] : [ImportAction.KeepExisting, ImportAction.ResolveManually]), o, token),
            _ => new(Item(status, detail, ImportAction.SkipOptional, ImportAction.SkipOptional), o, token),
        };
    }

    private async Task<bool> ExistsInDestinationAsync(PortableObjectRef d, CancellationToken ct) => d.Type switch
    {
        PackageTypes.Snapshot => Guid.TryParse(d.LogicalId, out var id) && await _db.IqrSourceSnapshots.AnyAsync(s => s.Id == id, ct),
        PackageTypes.Contract => await _db.ApiContracts.AnyAsync(c => c.Key == d.LogicalId, ct),
        _ => false,
    };

    /// <summary>The destination's current state for one object: classification + a token that changes when the destination changes.</summary>
    private async Task<(ImportStatus Status, string Detail, string Token)> DestinationStateAsync(PackageEntry entry, string? env, CancellationToken ct)
    {
        var o = entry.Object;
        var p = entry.Payload;
        switch (o.Type)
        {
            case PackageTypes.Snapshot:
            {
                var payload = p.Deserialize<SnapshotPayload>(Web)!;
                var existing = await _db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payload.Record.Id, ct);
                if (existing is null) return (ImportStatus.New, "New source snapshot (same identity and archive fingerprint as the origin).", "absent");
                var same = TransferJson.Sha256(existing.EvidenceJson) == TransferJson.Sha256(payload.Record.EvidenceJson);
                return same ? (ImportStatus.Identical, "Already here, identical.", TransferJson.Sha256(existing.EvidenceJson))
                    : (ImportStatus.ConflictingContent, "A snapshot with this identity exists with different content; snapshots are never overwritten.", TransferJson.Sha256(existing.EvidenceJson));
            }
            case PackageTypes.Contract:
            {
                var payload = p.Deserialize<ContractPayload>(Web)!;
                var existing = await _db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == payload.Contract.Key, ct);
                if (existing is null) return (ImportStatus.New, $"New contract with {payload.Versions.Count} version(s) and {payload.Tests.Count} test(s).", "absent");
                var versions = await _db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == existing.Id).Select(v => new { v.Version, v.Sha256 }).ToListAsync(ct);
                var tests = await _db.ContractTests.AsNoTracking().Where(t => t.ContractId == existing.Id).Select(t => new { t.Id, t.Revision }).ToListAsync(ct);
                var token = TransferJson.Sha256($"{existing.ProjectKey}|{string.Join(",", versions.OrderBy(v => v.Version).Select(v => $"{v.Version}:{v.Sha256}"))}|{string.Join(",", tests.OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.Revision}"))}");
                if (!string.Equals(existing.ProjectKey, payload.Contract.ProjectKey, StringComparison.Ordinal))
                    return (ImportStatus.ConflictingContent, $"Contract key \"{payload.Contract.Key}\" belongs to project {existing.ProjectKey ?? "(none)"} here; contracts are never merged across projects.", token);
                var mine = payload.Versions.Select(v => (v.Version, v.Sha256)).ToHashSet();
                var theirs = versions.Select(v => (v.Version, v.Sha256)).ToHashSet();
                if (mine.Any(m => theirs.Any(t => t.Version == m.Version && t.Sha256 != m.Sha256)))
                    return (ImportStatus.ConflictingContent, "Same version number with different content: never merged automatically.", token);
                var newerTests = payload.Tests.Any(t => !tests.Any(d => d.Id == t.Id) || tests.Any(d => d.Id == t.Id && d.Revision < t.Revision));
                if (mine.SetEquals(theirs) && !newerTests) return (ImportStatus.Identical, "Already here with the same versions and test revisions.", token);
                if (mine.IsSupersetOf(theirs)) return (ImportStatus.NewerRevision, $"The package adds {mine.Count - theirs.Count} version(s){(newerTests ? " and newer test revisions" : "")}.", token);
                if (mine.IsSubsetOf(theirs) && !newerTests) return (ImportStatus.OlderRevision, "This installation already has newer versions; it is kept.", token);
                return (ImportStatus.ConflictingContent, "Version histories diverge.", token);
            }
            case PackageTypes.ParquetRules:
            {
                var r = p.Deserialize<ParquetRuleSetRecord>(Web)!;
                var existing = await _db.ParquetRuleSets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectKey == r.ProjectKey && x.ContractKey == r.ContractKey && x.ContractVersion == r.ContractVersion && x.Version == r.Version, ct);
                return existing is null ? (ImportStatus.New, "New rule-set version.", "absent")
                    : existing.RulesJson == r.RulesJson ? (ImportStatus.Identical, "Identical rule-set version.", TransferJson.Sha256(existing.RulesJson))
                    : (ImportStatus.ConflictingContent, "Same rule-set version with different rules.", TransferJson.Sha256(existing.RulesJson));
            }
            case PackageTypes.Suite:
            {
                var r = p.Deserialize<IntegrationSuiteRecord>(Web)!;
                var existing = await _db.IntegrationSuites.AsNoTracking().FirstOrDefaultAsync(x => x.SuiteKey == r.SuiteKey && x.Revision == r.Revision, ct);
                return existing is null ? (ImportStatus.New, "New suite revision.", "absent")
                    : existing.Fingerprint == r.Fingerprint ? (ImportStatus.Identical, "Identical suite revision.", existing.Fingerprint)
                    : (ImportStatus.ConflictingContent, "Same suite revision with a different fingerprint.", existing.Fingerprint);
            }
            case PackageTypes.Composite:
            {
                var r = p.Deserialize<CompositeSourceContextRecord>(Web)!;
                var existing = await _db.CompositeSourceContexts.AsNoTracking().FirstOrDefaultAsync(x => x.ContextKey == r.ContextKey && x.Revision == r.Revision, ct);
                return existing is null ? (ImportStatus.New, "New composite source context revision (each stage keeps its own source project).", "absent")
                    : existing.Fingerprint == r.Fingerprint ? (ImportStatus.Identical, "Identical context revision.", existing.Fingerprint)
                    : (ImportStatus.ConflictingContent, "Same context revision with a different fingerprint.", existing.Fingerprint);
            }
            case PackageTypes.Association:
            {
                var r = p.Deserialize<SourceRepositoryAssociationRecord>(Web)!;
                var exists = await _db.SourceRepositoryAssociations.AsNoTracking().AnyAsync(x => x.RevokedAt == null &&
                    (x.RepositoryKeyA == r.RepositoryKeyA && x.RepositoryKeyB == r.RepositoryKeyB || x.RepositoryKeyA == r.RepositoryKeyB && x.RepositoryKeyB == r.RepositoryKeyA), ct);
                return exists ? (ImportStatus.Identical, "These projects are already associated here.", "present") : (ImportStatus.New, "Association declared at the origin (kept with its original author and time).", "absent");
            }
            case PackageTypes.History:
            {
                var h = p.Deserialize<HistoryPayload>(Web)!;
                return (ImportStatus.New, $"Historical {h.Kind} from {h.OriginalTimestamp:u}; never a run or decision of this installation.", "history");
            }
            case PackageTypes.TestDataAsset:
            {
                var asset = p.Deserialize<RegisteredTestDataAsset>(Web)!;
                var existing = _testData.Assets().FirstOrDefault(a => a.Alias == asset.Alias);
                if (existing is null) return (ImportStatus.New, "New Test Data asset (metadata only; its approval is not transferred).", "absent");
                var same = existing.StructureFingerprint == asset.StructureFingerprint && existing.DataType == asset.DataType;
                return (same ? ImportStatus.Identical : ImportStatus.ConflictingContent, same ? "An asset with this alias and structure exists." : "An asset with this alias exists with a different structure.",
                    TransferJson.Sha256(JsonSerializer.Serialize(existing, Web)));
            }
            case PackageTypes.TestDataValues:
            {
                var resource = p.Deserialize<AuthoredTestDataResource>(Web)!;
                var existing = _authored.Find(resource.Alias);
                if (existing is null) return (ImportStatus.New, $"New synthetic values with {resource.Versions.Count} version(s), kept exactly.", "absent");
                var mine = resource.Versions.Select(v => v.Fingerprint).ToList();
                var theirs = existing.Versions.Select(v => v.Fingerprint).ToList();
                var token = TransferJson.Sha256(string.Join(",", theirs));
                if (mine.SequenceEqual(theirs)) return (ImportStatus.Identical, "Same versions.", token);
                if (mine.Count > theirs.Count && mine.Take(theirs.Count).SequenceEqual(theirs)) return (ImportStatus.NewerRevision, $"The package adds {mine.Count - theirs.Count} version(s).", token);
                if (theirs.Count > mine.Count && theirs.Take(mine.Count).SequenceEqual(mine)) return (ImportStatus.OlderRevision, "This installation has newer versions.", token);
                return (ImportStatus.ConflictingContent, "Version histories diverge; never regenerated or merged.", token);
            }
        }
        if (env is null) return (ImportStatus.EnvironmentRebindRequired, "", "unmapped");
        switch (o.Type)
        {
            case PackageTypes.Platform:
            {
                var r = p.Deserialize<IntegrationPlatformRecord>(Web)!;
                var existing = await _db.IntegrationPlatforms.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env && x.Id == r.Id, ct);
                return Doc(existing?.DocumentJson, r.DocumentJson, "platform");
            }
            case PackageTypes.Integration:
            {
                var r = p.Deserialize<IntegrationDefinitionRecord>(Web)!;
                var existing = await _db.IntegrationDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env && x.Id == r.Id, ct);
                return Doc(existing?.DocumentJson, r.DocumentJson, "integration definition");
            }
            case PackageTypes.EvidenceBinding:
            {
                var r = p.Deserialize<IntegrationEvidenceBindingRecord>(Web)!;
                var existing = await _db.IntegrationEvidenceBindings.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env && x.IntegrationId == r.IntegrationId, ct);
                return existing is null ? (ImportStatus.New, "New source binding.", "absent")
                    : existing.SnapshotId == r.SnapshotId ? (ImportStatus.Identical, "Bound to the same snapshot.", existing.SnapshotId.ToString())
                    : (ImportStatus.ConflictingContent, "Bound to a different snapshot here.", existing.SnapshotId.ToString());
            }
            case PackageTypes.ContractArtifact:
            {
                var r = p.Deserialize<IntegrationContractArtifactRecord>(Web)!;
                var existing = await _db.IntegrationContractArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env && x.IntegrationId == r.IntegrationId && x.Role == r.Role, ct);
                return existing is null ? (ImportStatus.New, "New contract artifact.", "absent")
                    : existing.ContentHash == r.ContentHash ? (ImportStatus.Identical, "Same content.", existing.ContentHash) : (ImportStatus.ConflictingContent, "Different content here.", existing.ContentHash);
            }
            case PackageTypes.GraphQlSchema:
            {
                var r = p.Deserialize<GraphQlSchemaArtifactRecord>(Web)!;
                var existing = await _db.GraphQlSchemaArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env && x.TargetId == r.TargetId, ct);
                return existing is null ? (ImportStatus.New, "New GraphQL schema.", "absent")
                    : existing.ContentHash == r.ContentHash ? (ImportStatus.Identical, "Same schema.", existing.ContentHash) : (ImportStatus.ConflictingContent, "Different schema here.", existing.ContentHash);
            }
            case PackageTypes.MessageFlows:
            {
                var r = p.Deserialize<IntegrationMessageFlowRecord>(Web)!;
                var existing = await _db.IntegrationMessageFlows.AsNoTracking().FirstOrDefaultAsync(x => x.EnvironmentId == env, ct);
                return Doc(existing?.DocumentJson, r.DocumentJson, "message-flow review");
            }
            case PackageTypes.ConnectedSystems:
            {
                var payload = p.Deserialize<ConnectedSystemsPayload>(Web)!;
                var preflight = await _setup.PreflightAsync(env, new SetupPreflightRequest(DisableSystems(payload.SetupPackageJson), "package.setup.json"), ct);
                var token = TransferJson.Sha256(JsonSerializer.Serialize(preflight.Changes, Web));
                if (!preflight.Valid) return (ImportStatus.Unsupported, $"The Connected Systems setup is not valid here: {string.Join(" ", preflight.Issues.Take(3).Select(i => i.Message))}", token);
                var conflicts = preflight.Changes.Count(c => c.Classification == "Conflict");
                var news = preflight.Changes.Count(c => c.Classification == "New");
                return conflicts > 0 ? (ImportStatus.ConflictingContent, $"{conflicts} setting(s) differ from this environment; existing values are kept, new ones added disabled.", token)
                    : news > 0 ? (ImportStatus.New, $"{news} setting(s) to add; systems are created disabled.", token)
                    : (ImportStatus.Identical, "The same Connected Systems settings exist.", token);
            }
            case PackageTypes.Flow:
            {
                var flow = p.Deserialize<CriticalE2EFlowDefinition>(Web)!;
                var existing = _flows.Flow(flow.Id);
                if (existing is null) return (ImportStatus.New, "New browser test, imported disabled and not release-gating; DOM locators must be revalidated.", "absent");
                var same = FlowMaterial(existing) == FlowMaterial(flow);
                return (same ? ImportStatus.Identical : ImportStatus.ConflictingContent, same ? "The same flow definition exists." : "A flow with this id exists with a different definition.", FlowMaterial(existing));
            }
            case PackageTypes.Module:
            {
                var m = p.Deserialize<ModulePayload>(Web)!;
                var exists = _flows.RegisteredModules(env).Any(x => x.Name == m.Module.Name);
                return exists ? (ImportStatus.Identical, "Module registered.", "present") : (ImportStatus.New, "Register module.", "absent");
            }
            case PackageTypes.Profile:
                return (ImportStatus.New, "A disabled draft Target Environment profile is handed to your browser to create; NEEDS_REVALIDATION and NOT_EXECUTABLE until you check it.", "profile");
        }
        return (ImportStatus.Unsupported, "Not importable.", "unsupported");
    }

    private static (ImportStatus, string, string) Doc(string? existing, string incoming, string label) =>
        existing is null ? (ImportStatus.New, $"New {label}.", "absent")
        : existing == incoming ? (ImportStatus.Identical, $"Same {label}.", TransferJson.Sha256(existing))
        : (ImportStatus.ConflictingContent, $"This environment has a different {label}; it is kept unless you resolve it.", TransferJson.Sha256(existing));

    private static string FlowMaterial(CriticalE2EFlowDefinition flow) => JsonSerializer.Serialize(flow with
    {
        Version = 0, UpdatedAt = null, EnvironmentId = "", ProfileId = "", Enabled = false, RequiredForRelease = false,
        Tags = [.. flow.Tags.Where(t => !t.StartsWith("imported:", StringComparison.Ordinal))],
    }, Web);

    /// <summary>The setup package with every system disabled: an imported external connection never starts enabled.</summary>
    private static string DisableSystems(string setupJson)
    {
        if (TryParse(setupJson) is not JsonObject root) return setupJson;
        foreach (var system in (root["systems"] as JsonArray ?? []).OfType<JsonObject>()) system["enabled"] = false;
        return root.ToJsonString();
    }
}
