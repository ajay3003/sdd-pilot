using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.WorkspaceTransfer;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.WorkspaceTransfer;

public sealed partial class WorkspacePackageService
{
    private static readonly IReadOnlySet<string> DatabaseTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        PackageTypes.Snapshot, PackageTypes.Association, PackageTypes.Contract, PackageTypes.ParquetRules, PackageTypes.Suite, PackageTypes.Composite, PackageTypes.Platform,
        PackageTypes.Integration, PackageTypes.EvidenceBinding, PackageTypes.ContractArtifact, PackageTypes.GraphQlSchema, PackageTypes.MessageFlows, PackageTypes.History,
        PackageTypes.SourceDecision,
    };

    private sealed class ApplyContext(Guid operationId, string originInstance, string? importedEnvironment, string? destinationEnvironment, DateTimeOffset now)
    {
        public Guid OperationId { get; } = operationId;
        public string OriginInstance { get; } = originInstance;
        public string? ImportedEnvironment { get; } = importedEnvironment;
        public string? Destination { get; } = destinationEnvironment;
        public DateTimeOffset Now { get; } = now;
        public List<string> Applied { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<ReconciliationItem> Reconciliation { get; } = [];
        public List<IdentityMapping> Identity { get; } = [];
        public int History { get; set; }
        public string? ProfileDraft { get; set; }
        public string? MapEnvironment(string? env) => env is null ? null : env == ImportedEnvironment ? Destination : env;
    }

    public async Task<ImportReport> ApplyImportAsync(ImportApplyRequest request, string actor, CancellationToken ct)
    {
        var op = await _db.WorkspaceTransferOperations.FirstOrDefaultAsync(o => o.Id == request.OperationId && o.Kind == "Import", ct)
            ?? throw new TransferRequestException("NOT_FOUND", "No import with that id.");
        var retry = op.State == FailedRecoverable;
        if (op.State is not (Previewed or FailedRecoverable)) throw new TransferRequestException("STATE", $"This import is {op.State}; preview it first.");
        if (!string.Equals(op.PlanFingerprint, request.PlanFingerprint, StringComparison.Ordinal))
            throw new TransferRequestException("PLAN_CHANGED", "The plan you approved is not the current plan of this import; preview it again.");
        var plan = JsonSerializer.Deserialize<StoredPlan>(op.PlanJson!, TransferJson.Options)!;
        var path = Path.Combine(Staging, op.StagedPackage ?? "");
        if (!File.Exists(path)) throw new TransferRequestException("EXPIRED", "The staged package has expired; upload and preview it again.");
        var checks = new List<TransferCheck>();
        var (manifest, entries, security) = await ReadPackageAsync(path, checks, ct);
        if (manifest is null) throw new TransferRequestException("PACKAGE_CHANGED", "The staged package no longer validates.");

        // Destination environment: chosen by a person from the candidates shown at preview, or a new disabled one. Never by name.
        string? destination = null;
        var envItems = plan.Items.Any(i => PackageTypes.EnvironmentScoped.Contains(i.Type));
        switch (request.EnvironmentAction)
        {
            case EnvironmentMappingAction.ReuseVerified:
                if (request.TargetEnvironmentId is not { Length: > 0 } target || !plan.Candidates.Any(c => c.Id == target))
                    throw new TransferRequestException("ENVIRONMENT", "Choose one of the environments offered at preview.");
                destination = target;
                break;
            case EnvironmentMappingAction.CreateDisabled:
                destination = op.ResultJson is { } previous && JsonSerializer.Deserialize<ImportReport>(previous, TransferJson.Options)?.DestinationEnvironmentId is { } earlier
                    ? earlier : $"imported-{op.Id.ToString("N")[..8]}";
                break;
        }

        var replanned = new List<PlannedItem>();
        foreach (var entry in entries.OrderBy(e => PackageTypes.Order(e.Object.Type)))
            replanned.Add(await ClassifyAsync(entry, manifest, entries, security, destination, ct));
        if (!retry)
        {
            // Concurrent destination change since preview: environment-independent objects must be exactly as previewed.
            var changed = replanned.Where(p => !PackageTypes.EnvironmentScoped.Contains(p.Item.Type) && plan.Tokens.TryGetValue(p.Item.Key, out var t) && t != p.DestinationToken).ToList();
            if (changed.Count > 0)
                throw new TransferRequestException("DESTINATION_CHANGED", $"This installation changed since the preview ({string.Join(", ", changed.Take(3).Select(c => c.Item.Label))}); preview again.");
        }

        var decisions = new Dictionary<string, ImportAction>(StringComparer.Ordinal);
        foreach (var p in replanned)
        {
            var action = request.Decisions.TryGetValue(p.Item.Key, out var chosen) ? chosen : p.Item.DefaultAction;
            if (retry && p.Item.Status == ImportStatus.Identical) action = ImportAction.ReuseIdentical;
            if (!p.Item.AllowedActions.Contains(action))
                throw new TransferRequestException("DECISION", $"{action} is not allowed for {p.Item.Label} ({p.Item.Status}).");
            decisions[p.Item.Key] = action;
        }

        op.State = Approved;
        op.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        op.State = Applying;
        await _db.SaveChangesAsync(ct);

        var context = new ApplyContext(op.Id, manifest.ExporterInstanceId, plan.ImportedEnvironmentId, destination, _clock.GetUtcNow());
        var toApply = replanned.Where(p => decisions[p.Item.Key] is ImportAction.CreateNew or ImportAction.ImportAsNewRevision).ToList();
        foreach (var p in replanned.Except(toApply))
        {
            context.Skipped.Add($"{p.Item.Label}: {decisions[p.Item.Key]}");
            context.Reconciliation.Add(new(p.Item.Label, decisions[p.Item.Key] switch
            {
                ImportAction.ReuseIdentical => ReconciliationStatus.Resolved,
                ImportAction.ResolveManually => ReconciliationStatus.Unresolved,
                ImportAction.SkipOptional when p.Item.Status == ImportStatus.Unsupported => ReconciliationStatus.Unsupported,
                ImportAction.SkipOptional when PackageTypes.EnvironmentScoped.Contains(p.Item.Type) && destination is null => ReconciliationStatus.Unresolved,
                _ => ReconciliationStatus.Resolved,
            }, p.Item.Detail));
        }
        var payloads = entries.ToDictionary(e => PackageIds.Key(e.Object.Type, e.Object.LogicalId), e => e, StringComparer.Ordinal);

        // Phase 1: database objects in one transaction (all or nothing).
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var count = 0;
                foreach (var p in toApply.Where(p => DatabaseTypes.Contains(p.Item.Type)))
                {
                    if (PackageTypes.EnvironmentScoped.Contains(p.Item.Type) && destination is null) { Unmapped(context, p); continue; }
                    await ApplyDatabaseObjectAsync(payloads[p.Item.Key], p, decisions[p.Item.Key], context, ct);
                    count++;
                    if (FailDatabasePhaseAfter?.Invoke(count) == true) throw new InvalidOperationException("Injected database failure.");
                }
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            var failed = await _db.WorkspaceTransferOperations.FirstAsync(o => o.Id == op.Id, CancellationToken.None);
            var report = Report(context, FailedRolledBack, $"Nothing was imported: {ex.Message}", manifest, replanned, decisions);
            failed.State = FailedRolledBack;
            failed.Error = Truncate(ex.Message);
            failed.ResultJson = JsonSerializer.Serialize(report, TransferJson.Options);
            failed.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(CancellationToken.None);
            return report;
        }
        _db.ChangeTracker.Clear();

        // Phase 2: owner stores outside the database. Each step is idempotent, so a retry after a failure completes without duplicates.
        try
        {
            foreach (var p in toApply.Where(p => !DatabaseTypes.Contains(p.Item.Type)).OrderBy(p => PackageTypes.Order(p.Item.Type)))
            {
                if (FailFilePhaseAt?.Invoke(p.Item.Type) == true) throw new IOException($"Injected failure while applying {p.Item.Type}.");
                if (PackageTypes.EnvironmentScoped.Contains(p.Item.Type) && destination is null) { Unmapped(context, p); continue; }
                await ApplyStoreObjectAsync(payloads[p.Item.Key], p, decisions[p.Item.Key], context, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var current = await _db.WorkspaceTransferOperations.FirstAsync(o => o.Id == op.Id, CancellationToken.None);
            var report = Report(context, FailedRecoverable, $"Database objects were imported; a later step failed and can be retried: {ex.Message}", manifest, replanned, decisions);
            current.State = FailedRecoverable;
            current.Error = Truncate(ex.Message);
            current.ResultJson = JsonSerializer.Serialize(report, TransferJson.Options);
            current.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(CancellationToken.None);
            return report;
        }

        var final = Report(context, Completed, null, manifest, replanned, decisions);
        var done = await _db.WorkspaceTransferOperations.FirstAsync(o => o.Id == op.Id, ct);
        done.State = Completed;
        done.Error = null;
        done.Summary = Truncate($"{manifest.Label}: {context.Applied.Count} applied, {context.Skipped.Count} kept or skipped", 500);
        done.ResultJson = JsonSerializer.Serialize(final, TransferJson.Options);
        done.UpdatedAt = _clock.GetUtcNow();
        done.StagedPackage = null;
        await _db.SaveChangesAsync(ct);
        TryDelete(path);
        return final;
    }

    public async Task<ImportReport?> ImportReportAsync(Guid operationId, CancellationToken ct) =>
        await _db.WorkspaceTransferOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, ct) is { ResultJson: { } json }
            ? JsonSerializer.Deserialize<ImportReport>(json, TransferJson.Options) : null;

    private static void Unmapped(ApplyContext context, PlannedItem p)
    {
        context.Skipped.Add($"{p.Item.Label}: no destination environment");
        context.Reconciliation.Add(new(p.Item.Label, ReconciliationStatus.Unresolved, "Bound to the exported environment and no destination environment was chosen: not imported."));
    }

    private static string Truncate(string text, int max = 2000) => text.Length > max ? text[..max] : text;

    // ───────────────────────────── Database objects ─────────────────────────────

    private async Task<Guid> FreeId<T>(DbSet<T> set, Guid wanted, Func<T, Guid> id, CancellationToken ct) where T : class =>
        await set.FindAsync([wanted], ct) is null && set.Local.All(x => id(x) != wanted) ? wanted : Guid.NewGuid();

    private async Task ApplyDatabaseObjectAsync(PackageEntry entry, PlannedItem planned, ImportAction action, ApplyContext ctx, CancellationToken ct)
    {
        var p = entry.Payload;
        var label = planned.Item.Label;
        switch (planned.Item.Type)
        {
            case PackageTypes.Snapshot:
            {
                var s = p.Deserialize<SnapshotPayload>(Web)!;
                _db.IqrSourceSnapshots.Add(s.Record);
                if (s.Schema is not null && !await _db.SourceSchemaArtifacts.AnyAsync(x => x.SnapshotId == s.Record.Id, ct)) _db.SourceSchemaArtifacts.Add(s.Schema);
                foreach (var d in s.Documents)
                {
                    var id = await FreeId(_db.SourceContractDocuments, d.Id, x => x.Id, ct);
                    _db.SourceContractDocuments.Add(Copy(d, id));
                }
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, "Source snapshot imported with its identity and archive fingerprint; the original archive is not part of any package."));
                break;
            }
            case PackageTypes.Contract:
                await ApplyContractAsync(p.Deserialize<ContractPayload>(Web)!, action, ctx, label, ct);
                break;
            case PackageTypes.ParquetRules:
            {
                var r = p.Deserialize<ParquetRuleSetRecord>(Web)!;
                var id = await FreeId(_db.ParquetRuleSets, r.Id, x => x.Id, ct);
                _db.ParquetRuleSets.Add(Copy(r, id));
                Map(ctx, PackageTypes.ParquetRules, r.Id, id);
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, $"Rule set imported with its original approval by {r.ApprovedBy} at {r.ApprovedAt:u} (historical)."));
                break;
            }
            case PackageTypes.Suite:
            {
                var r = p.Deserialize<IntegrationSuiteRecord>(Web)!;
                var id = await FreeId(_db.IntegrationSuites, r.Id, x => x.Id, ct);
                var envId = ctx.MapEnvironment(r.EnvironmentId);
                _db.IntegrationSuites.Add(Copy(r, id, envId));
                Map(ctx, PackageTypes.Suite, r.Id, id);
                ctx.Reconciliation.Add(new(label, await _db.IqrSourceSnapshots.AnyAsync(s => s.Id == r.SnapshotId, ct) || _db.IqrSourceSnapshots.Local.Any(s => s.Id == r.SnapshotId)
                    ? ReconciliationStatus.Resolved : ReconciliationStatus.Unresolved, "Suite definition imported; it has not run here."));
                break;
            }
            case PackageTypes.Composite:
            {
                var r = p.Deserialize<CompositeSourceContextRecord>(Web)!;
                var id = await FreeId(_db.CompositeSourceContexts, r.Id, x => x.Id, ct);
                _db.CompositeSourceContexts.Add(Copy(r, id));
                Map(ctx, PackageTypes.Composite, r.Id, id);
                var referenced = GuidPattern.Matches(r.DefinitionJson).Select(m => Guid.Parse(m.Value)).Distinct().ToList();
                var present = await _db.IqrSourceSnapshots.Where(s => referenced.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
                present.AddRange(_db.IqrSourceSnapshots.Local.Select(s => s.Id));
                var stageSnapshots = entry.Object.Dependencies.Where(d => d.Type == PackageTypes.Snapshot).Select(d => Guid.Parse(d.LogicalId)).ToList();
                var missing = stageSnapshots.Where(s => !present.Contains(s)).ToList();
                ctx.Reconciliation.Add(new(label, missing.Count == 0 ? ReconciliationStatus.Resolved : ReconciliationStatus.Unresolved,
                    missing.Count == 0 ? "Every stage source resolves to its own project's snapshot." : $"{missing.Count} stage source(s) are not in this installation (kept as unresolved references)."));
                break;
            }
            case PackageTypes.Association:
            {
                var r = p.Deserialize<SourceRepositoryAssociationRecord>(Web)!;
                var id = await FreeId(_db.SourceRepositoryAssociations, r.Id, x => x.Id, ct);
                _db.SourceRepositoryAssociations.Add(Copy(r, id, $"Imported from installation {ctx.OriginInstance} (operation {ctx.OperationId:N}). {r.Note}".Trim()));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, $"Association declared by {r.DeclaredBy} at {r.DeclaredAt:u} (original author kept)."));
                break;
            }
            case PackageTypes.SourceDecision:
            {
                // Inserted only (the table is append-only); the origin's ids, actors, times and reasons are kept so the history reads the same here.
                var d = p.Deserialize<SourceDecisionPayload>(Web)!;
                foreach (var r in d.Decisions.OrderBy(x => x.DecidedAt).ThenBy(x => x.Id))
                {
                    var id = await FreeId(_db.SourceDecisions, r.Id, x => x.Id, ct);
                    _db.SourceDecisions.Add(new SourceDecisionRecord
                    {
                        Id = id, Kind = r.Kind, Subject = r.Subject, SnapshotId = r.SnapshotId, Value = r.Value, SnapshotLabel = r.SnapshotLabel, ContentFingerprint = r.ContentFingerprint,
                        DecidedAt = r.DecidedAt, DecidedBy = r.DecidedBy,
                        Reason = $"{r.Reason} [imported from installation {ctx.OriginInstance}, operation {ctx.OperationId:N}]".Trim(),
                    });
                }
                var named = d.Decisions.Where(x => x.SnapshotId is not null).Select(x => x.SnapshotId!.Value).Distinct().ToList();
                var present = await _db.IqrSourceSnapshots.AsNoTracking().Where(s => named.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
                var missing = named.Where(sid => !present.Contains(sid) && !_db.IqrSourceSnapshots.Local.Any(s => s.Id == sid)).ToList();
                ctx.Reconciliation.Add(new(label, missing.Count == 0 ? ReconciliationStatus.Resolved : ReconciliationStatus.NeedsRevalidation, missing.Count == 0
                    ? $"{d.Decisions.Count} decision(s) imported with their original actor and time."
                    : $"{d.Decisions.Count} decision(s) imported; {missing.Count} named snapshot(s) are not in this installation, so that source shows its baseline as unavailable until one is chosen here."));
                break;
            }
            case PackageTypes.Platform:
            {
                var r = p.Deserialize<IntegrationPlatformRecord>(Web)!;
                _db.IntegrationPlatforms.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation, "Platform configuration imported for the destination environment; revalidate endpoints."));
                break;
            }
            case PackageTypes.Integration:
            {
                var r = p.Deserialize<IntegrationDefinitionRecord>(Web)!;
                _db.IntegrationDefinitions.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation, r.Enabled ? "Imported disabled (it was enabled at the origin); revalidate, then enable." : "Imported disabled."));
                break;
            }
            case PackageTypes.EvidenceBinding:
            {
                var r = p.Deserialize<IntegrationEvidenceBindingRecord>(Web)!;
                _db.IntegrationEvidenceBindings.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, $"Bound to source snapshot {r.SnapshotId}."));
                break;
            }
            case PackageTypes.ContractArtifact:
            {
                var r = p.Deserialize<IntegrationContractArtifactRecord>(Web)!;
                _db.IntegrationContractArtifacts.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, "Contract artifact imported (content hash unchanged)."));
                break;
            }
            case PackageTypes.GraphQlSchema:
            {
                var r = p.Deserialize<GraphQlSchemaArtifactRecord>(Web)!;
                _db.GraphQlSchemaArtifacts.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation, "Trusted schema copied; confirm it matches the destination target."));
                break;
            }
            case PackageTypes.MessageFlows:
            {
                var r = p.Deserialize<IntegrationMessageFlowRecord>(Web)!;
                _db.IntegrationMessageFlows.Add(Copy(r, ctx.Destination!));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.Resolved, "Reviewed message flows imported."));
                break;
            }
            case PackageTypes.History:
            {
                var h = p.Deserialize<HistoryPayload>(Web)!;
                if (await _db.ImportedHistoricalRecords.AnyAsync(x => x.OriginInstanceId == ctx.OriginInstance && x.Kind == h.Kind && x.OriginalId == h.OriginalId, ct)) break;
                _db.ImportedHistoricalRecords.Add(new ImportedHistoricalRecord
                {
                    Id = Guid.NewGuid(), OperationId = ctx.OperationId, Kind = h.Kind, OriginalId = h.OriginalId, OriginInstanceId = ctx.OriginInstance, OriginalProject = h.OriginalProject,
                    OriginalEnvironmentId = h.OriginalEnvironmentId, EnvironmentId = ctx.MapEnvironment(h.OriginalEnvironmentId), OriginalTimestamp = h.OriginalTimestamp, ImportedAt = ctx.Now,
                    DocumentJson = h.Document.GetRawText(),
                });
                ctx.History++;
                return;
            }
        }
        ctx.Applied.Add(label);
    }

    private async Task ApplyContractAsync(ContractPayload c, ImportAction action, ApplyContext ctx, string label, CancellationToken ct)
    {
        Guid contractId;
        var existing = await _db.ApiContracts.FirstOrDefaultAsync(x => x.Key == c.Contract.Key, ct);
        if (existing is null)
        {
            contractId = await FreeId(_db.ApiContracts, c.Contract.Id, x => x.Id, ct);
            _db.ApiContracts.Add(new ApiContractRow
            {
                Id = contractId, Key = c.Contract.Key, Name = c.Contract.Name, Protocol = c.Contract.Protocol, CreatedAt = c.Contract.CreatedAt, UpdatedAt = ctx.Now,
                Lifecycle = c.Contract.Lifecycle, ProjectKey = c.Contract.ProjectKey, IdentityState = c.Contract.IdentityState,
            });
        }
        else contractId = existing.Id;
        Map(ctx, PackageTypes.Contract, c.Contract.Id, contractId);
        var haveVersions = await _db.ApiContractVersions.Where(v => v.ContractId == contractId).Select(v => v.Version).ToListAsync(ct);
        foreach (var v in c.Versions.Where(v => !haveVersions.Contains(v.Version)))
        {
            var id = await FreeId(_db.ApiContractVersions, v.Id, x => x.Id, ct);
            _db.ApiContractVersions.Add(new ApiContractVersionRow
            {
                Id = id, ContractId = contractId, Version = v.Version, Sha256 = v.Sha256, Source = v.Source, SourceLabel = v.SourceLabel, Format = v.Format, DocumentText = v.DocumentText,
                ContractJson = v.ContractJson, OperationCount = v.OperationCount, ImportedAt = v.ImportedAt, SourceSnapshotId = v.SourceSnapshotId, SourcePath = v.SourcePath, Hidden = v.Hidden,
                SourceProjectKey = v.SourceProjectKey,
            });
        }
        var testMap = new Dictionary<Guid, Guid>();
        foreach (var t in c.Tests)
        {
            var current = await _db.ContractTests.FirstOrDefaultAsync(x => x.Id == t.Id && x.ContractId == contractId, ct);
            if (current is null)
            {
                var id = await FreeId(_db.ContractTests, t.Id, x => x.Id, ct);
                _db.ContractTests.Add(new ContractTestRow { Id = id, ContractId = contractId, Operation = t.Operation, Name = t.Name, Kind = t.Kind, Revision = t.Revision, DocumentJson = t.DocumentJson, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt });
                testMap[t.Id] = id;
            }
            else
            {
                testMap[t.Id] = current.Id;
                // Only ever forward: a newer revision from the package; never replaces a newer local one.
                if (t.Revision > current.Revision) { current.Revision = t.Revision; current.DocumentJson = t.DocumentJson; current.Name = t.Name; current.Operation = t.Operation; current.UpdatedAt = ctx.Now; }
            }
        }
        foreach (var r in c.TestRevisions)
        {
            if (!testMap.TryGetValue(r.TestId, out var testId)) continue;
            if (await _db.ContractTestRevisions.AnyAsync(x => x.TestId == testId && x.Revision == r.Revision, ct) || _db.ContractTestRevisions.Local.Any(x => x.TestId == testId && x.Revision == r.Revision)) continue;
            var id = await FreeId(_db.ContractTestRevisions, r.Id, x => x.Id, ct);
            _db.ContractTestRevisions.Add(new ContractTestRevisionRow { Id = id, TestId = testId, Revision = r.Revision, DocumentJson = r.DocumentJson, ChangeNote = r.ChangeNote, CreatedAt = r.CreatedAt });
        }
        var bindings = 0;
        foreach (var b in c.Bindings)
        {
            if (ctx.MapEnvironment(b.EnvironmentId) is not { } env) continue;
            if (await _db.ContractEndpointBindings.AnyAsync(x => x.ContractId == contractId && x.EnvironmentId == env, ct)) continue;
            _db.ContractEndpointBindings.Add(new ContractEndpointBindingRow { Id = Guid.NewGuid(), ContractId = contractId, EnvironmentId = env, BasePath = b.BasePath, GraphQlPath = b.GraphQlPath, Authentication = b.Authentication, UpdatedAt = ctx.Now });
            bindings++;
        }
        ctx.Reconciliation.Add(new(label, bindings > 0 ? ReconciliationStatus.NeedsRevalidation : ReconciliationStatus.Resolved,
            $"{(action == ImportAction.ImportAsNewRevision ? "New versions added" : "Contract imported")} with versions {string.Join(", ", c.Versions.Select(v => v.Version))}; "
            + (bindings > 0 ? $"{bindings} endpoint binding(s) for the destination environment need revalidation." : "no environment binding.")));
    }

    private static void Map(ApplyContext ctx, string type, Guid from, Guid to) { if (from != to) ctx.Identity.Add(new(type, from.ToString(), to.ToString())); }

    // Copies with a new key or environment (EF entities are init-only; the originals stay untouched).
    private static SourceContractDocumentRecord Copy(SourceContractDocumentRecord d, Guid id) => JsonClone(d, n => n["id"] = id);
    private static ParquetRuleSetRecord Copy(ParquetRuleSetRecord r, Guid id) => JsonClone(r, n => n["id"] = id);
    private static IntegrationSuiteRecord Copy(IntegrationSuiteRecord r, Guid id, string? env) => JsonClone(r, n => { n["id"] = id; n["environmentId"] = env; });
    private static CompositeSourceContextRecord Copy(CompositeSourceContextRecord r, Guid id) => JsonClone(r, n => n["id"] = id);
    private static SourceRepositoryAssociationRecord Copy(SourceRepositoryAssociationRecord r, Guid id, string note) => JsonClone(r, n => { n["id"] = id; n["note"] = note.Length > 500 ? note[..500] : note; });
    private static IntegrationPlatformRecord Copy(IntegrationPlatformRecord r, string env) => JsonClone(r, n => n["environmentId"] = env);
    private static IntegrationDefinitionRecord Copy(IntegrationDefinitionRecord r, string env) => JsonClone(r, n => { n["environmentId"] = env; n["enabled"] = false; });
    private static IntegrationEvidenceBindingRecord Copy(IntegrationEvidenceBindingRecord r, string env) => JsonClone(r, n => n["environmentId"] = env);
    private static IntegrationContractArtifactRecord Copy(IntegrationContractArtifactRecord r, string env) => JsonClone(r, n => n["environmentId"] = env);
    private static GraphQlSchemaArtifactRecord Copy(GraphQlSchemaArtifactRecord r, string env) => JsonClone(r, n => n["environmentId"] = env);
    private static IntegrationMessageFlowRecord Copy(IntegrationMessageFlowRecord r, string env) => JsonClone(r, n => n["environmentId"] = env);

    private static T JsonClone<T>(T value, Action<JsonObject> change)
    {
        var node = JsonSerializer.SerializeToNode(value, Web)!.AsObject();
        change(node);
        return node.Deserialize<T>(Web)!;
    }

    // ───────────────────────────── Owner stores (files) ─────────────────────────────

    private async Task ApplyStoreObjectAsync(PackageEntry entry, PlannedItem planned, ImportAction action, ApplyContext ctx, CancellationToken ct)
    {
        var p = entry.Payload;
        var label = planned.Item.Label;
        switch (planned.Item.Type)
        {
            case PackageTypes.ConnectedSystems:
            {
                var payload = p.Deserialize<ConnectedSystemsPayload>(Web)!;
                var content = DisableSystems(payload.SetupPackageJson);
                var preflight = await _setup.PreflightAsync(ctx.Destination!, new SetupPreflightRequest(content, "package.setup.json"), ct);
                // Existing destination values are kept (every conflict decided "keep"); new systems and settings are added, disabled.
                var keep = preflight.Changes.Where(c => c.Classification == "Conflict").ToDictionary(c => c.Id, _ => false);
                var result = await _setup.ApplyAsync(ctx.Destination!, new SetupApplyPackageRequest(content, "package.setup.json", ConflictDecisions: keep), $"Backup & Transfer import {ctx.OperationId:N}", ct);
                if (!result.Applied && preflight.Changes.Any(c => c.Classification == "New")) throw new InvalidOperationException($"Connected Systems were not applied: {result.Outcome}");
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation,
                    $"Connected Systems configured disabled ({payload.Systems.Count} system(s)); credentials must be entered here and every target revalidated. Nothing connected."));
                break;
            }
            case PackageTypes.TestDataAsset:
            {
                var asset = p.Deserialize<RegisteredTestDataAsset>(Web)!;
                var mapped = asset with { EnvironmentId = ctx.MapEnvironment(asset.EnvironmentId) };
                if (asset.EnvironmentId is not null && mapped.EnvironmentId is null) { ctx.Skipped.Add($"{label}: environment not mapped"); return; }
                var (registered, existingAlias) = _testData.Register(mapped);
                if (registered is null && existingAlias is null) throw new InvalidOperationException($"Test Data asset {asset.Alias} could not be registered.");
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation, "Registered as metadata; its approval was not transferred (approve it here after review)."));
                break;
            }
            case PackageTypes.TestDataValues:
            {
                var resource = p.Deserialize<AuthoredTestDataResource>(Web)!;
                var existing = _authored.Find(resource.Alias);
                var start = existing?.Versions.Count ?? 0;
                foreach (var v in resource.Versions.Skip(start))
                {
                    var draft = new TestDataResourceDraft
                    {
                        Alias = resource.Alias, Label = v.Label, Description = v.Description, EnvironmentId = ctx.MapEnvironment(v.EnvironmentId), AppliesTo = v.AppliesTo, Content = v.Content,
                        ExpectedVersion = existing?.Current.Version, ChangeNote = $"Imported v{v.Version} ({v.Fingerprint[..Math.Min(12, v.Fingerprint.Length)]}) from installation {ctx.OriginInstance}",
                    };
                    var (outcome, saved) = existing is null ? _authored.Create(draft) : _authored.AddVersion(resource.Alias, draft);
                    if (saved is null) throw new InvalidOperationException($"Test Data values {resource.Alias}: {outcome}");
                    existing = saved;
                }
                var exact = existing!.Versions.Select(v => AuthoredTestDataStore.Fingerprint(v.Content)).SequenceEqual(resource.Versions.Select(v => v.Fingerprint));
                ctx.Reconciliation.Add(new(label, exact ? ReconciliationStatus.Resolved : ReconciliationStatus.Unresolved,
                    exact ? $"{resource.Versions.Count} synthetic version(s) imported with identical fingerprints; nothing regenerated." : "Version fingerprints differ after import."));
                break;
            }
            case PackageTypes.Module:
            {
                var m = p.Deserialize<ModulePayload>(Web)!;
                _flows.RegisterModule(ctx.Destination!, m.Module);
                break;
            }
            case PackageTypes.Flow:
            {
                var flow = p.Deserialize<CriticalE2EFlowDefinition>(Web)!;
                var id = action == ImportAction.CreateNew && _flows.Flow(flow.Id) is not null ? $"{flow.Id}-imported-{ctx.OperationId.ToString("N")[..6]}" : flow.Id;
                if (_flows.Flow(id) is { } already && FlowMaterial(already) == FlowMaterial(flow)) break; // retry: already imported
                _flows.Save(flow with
                {
                    Id = id, EnvironmentId = ctx.Destination!, ProfileId = ctx.Destination!, Enabled = false, RequiredForRelease = false,
                    Tags = [.. flow.Tags.Where(t => !t.StartsWith("imported:", StringComparison.Ordinal)), $"imported:{ctx.OperationId.ToString("N")[..8]}"],
                });
                if (id != flow.Id) ctx.Identity.Add(new(PackageTypes.Flow, flow.Id, id));
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation,
                    $"Imported disabled{(flow.RequiredForRelease ? " and no longer release-gating (it was at the origin)" : "")}: pair the Edge Companion, revalidate the target origin and DOM locators, then enable."));
                break;
            }
            case PackageTypes.Profile:
            {
                var profile = p.Deserialize<ProfilePayload>(Web)!;
                var (json, _) = SanitiseProfile(profile.ProfileJson);
                var node = JsonNode.Parse(json)!.AsObject();
                node["id"] = ctx.Destination;
                node["importedDraft"] = true;
                node["importedFrom"] = ctx.OriginInstance;
                ctx.ProfileDraft = node.ToJsonString();
                ctx.Reconciliation.Add(new(label, ReconciliationStatus.NeedsRevalidation, "A disabled draft profile is ready for your browser to create (NOT_EXECUTABLE until revalidated)."));
                break;
            }
        }
        ctx.Applied.Add(label);
    }

    // ───────────────────────────── Report ─────────────────────────────

    private ImportReport Report(ApplyContext ctx, string state, string? error, PackageManifest manifest, List<PlannedItem> planned, Dictionary<string, ImportAction> decisions)
    {
        var types = planned.Where(p => decisions[p.Item.Key] is not ImportAction.SkipOptional).Select(p => p.Item.Type).ToHashSet(StringComparer.Ordinal);
        var unsupported = manifest.RequiredCapabilities.Where(c => !Supported(c)).ToList();
        var missingDeps = planned.Count(p => p.Item.Status == ImportStatus.MissingDependency);
        var manual = planned.Count(p => decisions[p.Item.Key] == ImportAction.ResolveManually || p.Item.Status == ImportStatus.ConflictingContent);
        var readiness = new List<ReadinessDimension>
        {
            new("SOURCE_READY", missingDeps == 0 ? "Yes" : "No", missingDeps == 0 ? "Every referenced source snapshot is present." : $"{missingDeps} object(s) reference sources that are not here."),
            new("CONFIGURATION_READY", types.Overlaps([PackageTypes.ConnectedSystems, PackageTypes.Integration, PackageTypes.Platform]) ? (ctx.Destination is null ? "No" : "Imported, disabled") : "Not applicable",
                ctx.Destination is null && types.Overlaps(PackageTypes.EnvironmentScoped) ? "No destination environment was chosen; environment configuration stays unresolved." : "Configuration imported disabled; nothing is active."),
            new("TARGET_REVALIDATION_REQUIRED", types.Overlaps(PackageTypes.EnvironmentScoped) ? "Required" : "Not applicable", "Imported endpoints, bindings and profiles are NEEDS_REVALIDATION and NOT_EXECUTABLE."),
            new("CREDENTIALS_REQUIRED", types.Contains(PackageTypes.ConnectedSystems) ? "Required" : "Not applicable", "No credential, token or secret was imported; enter them through Connected Systems / Target Environments."),
            new("TEST_DATA_REQUIRED", types.Overlaps([PackageTypes.TestDataAsset, PackageTypes.TestDataValues]) ? "Review required" : "Not applicable", "Imported Test Data is metadata or synthetic values; approvals were not transferred."),
            new("DOM_REVALIDATION_REQUIRED", types.Contains(PackageTypes.Flow) ? "Required" : "Not applicable", "Imported browser tests are disabled until a new attended pairing re-verifies their locators."),
            new("EXECUTOR_UNAVAILABLE", unsupported.Any(c => c.StartsWith("provider:") || c.StartsWith("critical-e2e:")) ? "Yes" : "No",
                unsupported.Count == 0 ? "Every executor capability exists in this build." : $"Missing: {string.Join(", ", unsupported.Where(c => !c.StartsWith("journey-pack:")))}"),
            new("OBSERVER_UNAVAILABLE", unsupported.Any(c => c.StartsWith("journey-pack:")) ? "Yes" : "No", unsupported.Any(c => c.StartsWith("journey-pack:")) ? $"Missing journey packs: {string.Join(", ", unsupported.Where(c => c.StartsWith("journey-pack:")))}" : "Journey packs referenced by the package are registered."),
            new("DOMAIN_DECISION_REQUIRED", manual > 0 ? "Yes" : "No", manual > 0 ? $"{manual} conflicting or manually resolved object(s)." : "No open domain decision from this import (compatibility findings of the integration itself are unchanged)."),
        };
        return new ImportReport
        {
            OperationId = ctx.OperationId, State = state, Applied = ctx.Applied, Skipped = ctx.Skipped, Reconciliation = ctx.Reconciliation, Readiness = readiness, IdentityMap = ctx.Identity,
            HistoricalRecords = ctx.History, DestinationEnvironmentId = ctx.Destination, ProfileDraftJson = ctx.ProfileDraft, Error = error,
        };
    }

    private async Task UpdateAsync(WorkspaceTransferOperationRecord op, string state, string summary, string? error, CancellationToken ct)
    {
        op.State = state;
        op.Summary = Truncate(summary, 500);
        op.Error = error is null ? null : Truncate(error);
        op.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }
}
