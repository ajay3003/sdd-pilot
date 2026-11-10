using System.Collections.Concurrent;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.Parquet.Quality;
using BirkNext.IntegrationSuites;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationSuites.Composite;

/// <summary>
/// Composite Source Contexts: discovery of candidate snapshots per stage, and immutable context revisions that REFERENCE existing Source Analysis
/// snapshots (id, project key, archive fingerprint) — never their content. Associating a snapshot grants nothing: saving needs the write policy,
/// every association records its human decision, and a project's snapshot stays that project's. Snapshot facts are read one snapshot at a time
/// (bounded memory) and cached by id because snapshots are immutable.
/// </summary>
public sealed class CompositeContextService(AppDbContext db, IqrSourceStore sources, ILogger<CompositeContextService> logger, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    public const int MaxSnapshots = 60;
    private const int MaxCachedFacts = 64;
    private static readonly ConcurrentDictionary<Guid, CompositeSourceRules.SnapshotFacts> FactsCache = new();

    // ── Facts ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CompositeSourceRules.SnapshotFacts?> FactsAsync(Guid snapshotId, CancellationToken ct)
    {
        if (FactsCache.TryGetValue(snapshotId, out var cached)) return cached;
        var snapshot = await sources.FindSourceAnalysisAsync(snapshotId, ct);
        if (snapshot is null) return null;
        var docs = await db.SourceContractDocuments.AsNoTracking().Where(d => d.SnapshotId == snapshotId && d.Path.EndsWith(".md"))
            .Select(d => new { d.Path, d.Text, d.Sha256 }).ToListAsync(ct);
        var datasets = new List<(string Path, string Name)>();
        foreach (var d in docs.Where(d => DatasetContractParser.LooksLikeDatasetContract(d.Text)))
            foreach (var c in DatasetContractParser.Parse(d.Path, d.Text, d.Sha256, []))
                datasets.Add((d.Path, $"{c.Key} {c.Version}".Trim()));
        var facts = CompositeSourceRules.Facts(snapshot, ContractRegistryService.ProjectKeyOf(snapshot), datasets);
        if (FactsCache.Count >= MaxCachedFacts) FactsCache.Clear();
        FactsCache[snapshotId] = facts;
        return facts;
    }

    /// <summary>Facts of the newest Source Analysis snapshots (bounded), loaded sequentially so at most one full snapshot is in memory.</summary>
    public async Task<List<CompositeSourceRules.SnapshotFacts>> AllFactsAsync(CancellationToken ct)
    {
        var ids = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == IqrSourceStore.SourceAnalysisOwner)
            .OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id).Select(r => r.Id).Take(MaxSnapshots).ToListAsync(ct);
        var list = new List<CompositeSourceRules.SnapshotFacts>();
        foreach (var id in ids)
            if (await FactsAsync(id, ct) is { } f) list.Add(f);
        return list;
    }

    // ── Discovery ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CompositeDiscovery?> DiscoverAsync(string scopeId, Guid? contextId, CancellationToken ct)
    {
        if (IntegrationSuiteComposer.Scope(scopeId) is not { } scope || scope.Stages.All(s => s.Source is null)) return null;
        var context = contextId is { } id ? await GetAsync(id, ct) : null;
        return CompositeSourceRules.Discover(scope, await AllFactsAsync(ct), context);
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CompositeContextSummary>> ListAsync(CancellationToken ct)
    {
        var rows = await db.CompositeSourceContexts.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        return [.. rows.GroupBy(r => r.ContextKey).Select(g => g.OrderByDescending(r => r.Revision).First())
            .Select(r => (r, c: JsonSerializer.Deserialize<CompositeSourceContext>(r.DefinitionJson, Json)!))
            .Select(x => new CompositeContextSummary(x.r.Id, x.r.ContextKey, x.r.Revision, x.r.ScopeId, x.r.OwningProject, x.c.Snapshots.Count,
                x.c.Snapshots.Count(s => s.Approval == CompositeApproval.Approved), x.c.StageMappings.Count(m => m.Class == CompositeMatchClass.Confirmed), x.c.StageMappings.Count, x.r.CreatedAt))
            .OrderByDescending(s => s.UpdatedAt)];
    }

    public async Task<IReadOnlyList<CompositeContextSummary>> RevisionsAsync(string contextKey, CancellationToken ct)
    {
        var rows = await db.CompositeSourceContexts.AsNoTracking().Where(r => r.ContextKey == contextKey).OrderByDescending(r => r.Revision).ToListAsync(ct);
        return [.. rows.Select(r => (r, c: JsonSerializer.Deserialize<CompositeSourceContext>(r.DefinitionJson, Json)!))
            .Select(x => new CompositeContextSummary(x.r.Id, x.r.ContextKey, x.r.Revision, x.r.ScopeId, x.r.OwningProject, x.c.Snapshots.Count,
                x.c.Snapshots.Count(s => s.Approval == CompositeApproval.Approved), x.c.StageMappings.Count(m => m.Class == CompositeMatchClass.Confirmed), x.c.StageMappings.Count, x.r.CreatedAt))];
    }

    /// <summary>A stored revision, with newer snapshots of its projects (proposed, never applied) and its source-coverage readiness.</summary>
    public async Task<CompositeSourceContext?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.CompositeSourceContexts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        var context = JsonSerializer.Deserialize<CompositeSourceContext>(row.DefinitionJson, Json)!;
        return context with { NewerSources = await NewerAsync(context, ct), SourceCoverage = CompositeSourceRules.SourceCoverage(context.StageMappings, context.Compatibility) };
    }

    private async Task<List<CompositeSourceChange>> NewerAsync(CompositeSourceContext context, CancellationToken ct)
    {
        var result = new List<CompositeSourceChange>();
        var rows = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.IntegrationId == IqrSourceStore.SourceAnalysisOwner)
            .OrderByDescending(r => r.AnalyzedAt).Select(r => new { r.Id, r.AnalyzedAt }).Take(MaxSnapshots).ToListAsync(ct);
        foreach (var association in context.Snapshots.Where(a => a.Approval != CompositeApproval.Rejected))
            foreach (var row in rows.Where(r => r.AnalyzedAt > association.AnalyzedAt && r.Id != association.SnapshotId))
            {
                if (await FactsAsync(row.Id, ct) is not { } f || f.ProjectKey != association.ProjectKey || f.ArchiveSha256 == association.ArchiveSha256) continue;
                if (result.Any(c => c.NewerSnapshotId == f.Id)) continue;
                result.Add(new(association.SnapshotId, f.Id, f.ProjectKey, f.ArchiveSha256, f.AnalyzedAt, [.. association.Stages]));
                break; // the newest one per association
            }
        return result;
    }

    // ── Save (next revision) ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CompositeContextSaveResult> SaveAsync(CompositeContextSaveRequest request, string initiator, CancellationToken ct)
    {
        if (IntegrationSuiteComposer.Scope(request.ScopeId) is not { } scope || scope.Stages.All(s => s.Source is null))
            return new(null, ["This scope has no stage source requirements; a composite context needs a multi-stage scope."]);
        var stageIds = scope.Stages.Where(s => s.Source is not null).Select(s => s.StageId).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        if (request.Associations.GroupBy(a => a.SnapshotId).FirstOrDefault(g => g.Count() > 1) is { } dup) problems.Add($"Snapshot {dup.Key:D} is listed twice.");
        var facts = new Dictionary<Guid, CompositeSourceRules.SnapshotFacts>();
        foreach (var a in request.Associations)
        {
            if (a.Stages.Count == 0) { problems.Add($"Snapshot {a.SnapshotId:D}: choose at least one stage."); continue; }
            if (a.Stages.FirstOrDefault(s => !stageIds.Contains(s)) is { } unknown) { problems.Add($"Unknown stage '{unknown}'."); continue; }
            var f = await FactsAsync(a.SnapshotId, ct);
            if (f is null) { problems.Add($"Snapshot {a.SnapshotId:D} was not found (only Source Analysis snapshots can be associated)."); continue; }
            if (f.Status == "Failed") { problems.Add($"{f.ArchiveFileName}: the snapshot failed analysis and cannot be associated."); continue; }
            var unrecognised = a.Stages.Where(s => Recognised(scope, s, f).Count == 0).ToList();
            if (unrecognised.Count > 0 && string.IsNullOrWhiteSpace(a.Reason))
                problems.Add($"{f.ArchiveFileName} is not recognised as the source of {string.Join(", ", unrecognised)}: a manual association needs a reason.");
            facts[a.SnapshotId] = f;
        }
        if (problems.Count > 0) return new(null, problems);

        var owning = string.IsNullOrWhiteSpace(request.OwningProject)
            ? request.Associations.Where(a => a.Approval != CompositeApproval.Rejected).GroupBy(a => facts[a.SnapshotId].ProjectKey).OrderByDescending(g => g.Sum(a => a.Stages.Count)).Select(g => g.Key).FirstOrDefault() ?? "unassigned"
            : request.OwningProject.Trim();
        var key = $"{scope.ScopeId}:{owning}";
        var latestRow = await db.CompositeSourceContexts.AsNoTracking().Where(r => r.ContextKey == key).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
        if (request.ExpectedRevision is { } expected && (latestRow?.Revision ?? 0) != expected)
            return new(null, [$"The context changed since revision {expected} (now {latestRow?.Revision ?? 0}); reload and review again."], Conflict: true);
        var previous = latestRow is null ? null : JsonSerializer.Deserialize<CompositeSourceContext>(latestRow.DefinitionJson, Json);

        var now = Now;
        var associations = request.Associations.Select(a =>
        {
            var f = facts[a.SnapshotId];
            var modules = a.Stages.ToDictionary(s => s, s => Recognised(scope, s, f), StringComparer.Ordinal);
            var evidence = a.Stages.SelectMany(s => modules[s].Count > 0
                ? CompositeSourceRules.Artifacts(f, modules[s], Requirement(scope, s).DatasetContractEvidence).Select(x => $"{s}: {x}").DefaultIfEmpty($"{s}: folders {string.Join(", ", modules[s])}")
                : [$"{s}: manual association — {a.Reason}"]).Distinct().Take(12).ToList();
            // A decision keeps who made it and when, as long as it is unchanged for the same snapshot and stages.
            var before = previous?.Snapshots.FirstOrDefault(p => p.SnapshotId == a.SnapshotId);
            var same = before is not null && before.Approval == a.Approval && before.Stages.Order().SequenceEqual(a.Stages.Order());
            return new CompositeSnapshotAssociation
            {
                SnapshotId = f.Id, ProjectKey = f.ProjectKey, ArchiveFileName = f.ArchiveFileName, ArchiveSha256 = f.ArchiveSha256, AnalyzedAt = f.AnalyzedAt, AnalysisStatus = f.Status,
                Kind = CompositeSourceKind.Application, Stages = [.. a.Stages.Distinct().OrderBy(s => scope.Stages.FindIndex(t => t.StageId == s))], ModulesByStage = modules, Evidence = evidence,
                Confidence = modules.Values.All(m => m.Count > 0) ? evidence.Any(e => e.Contains(": OpenApi", StringComparison.Ordinal) || e.Contains(": XSD", StringComparison.Ordinal) || e.Contains("Dataset", StringComparison.Ordinal))
                    ? "High (names and stage artifacts)" : "Medium (names only)" : "Low (manual association)",
                Approval = a.Approval,
                DecidedBy = a.Approval == CompositeApproval.Proposed ? null : same ? before!.DecidedBy : initiator,
                DecidedAt = a.Approval == CompositeApproval.Proposed ? null : same ? before!.DecidedAt : now,
                Reason = string.IsNullOrWhiteSpace(a.Reason) ? same ? before!.Reason : null : a.Reason.Trim(),
            };
        }).OrderBy(a => scope.Stages.FindIndex(t => a.Stages.Contains(t.StageId))).ThenBy(a => a.ProjectKey, StringComparer.Ordinal).ToList();

        var mappings = Mappings(scope, associations);
        var sides = Sides(scope, associations, facts);
        var acceptances = request.Acceptances.Where(a => !string.IsNullOrWhiteSpace(a.Reason)).ToList();
        var transitions = CompositeSourceRules.Compatibility(scope, sides, acceptances, initiator);
        var unknownAcceptance = request.Acceptances.Where(a => transitions.All(t => t.Key != a.TransitionKey)).Select(a => a.TransitionKey).ToList();
        if (unknownAcceptance.Count > 0) return new(null, [$"Unknown transition(s): {string.Join(", ", unknownAcceptance)}."]);
        // Keep earlier acceptances' reviewer when the reason is unchanged.
        transitions = [.. transitions.Select(t => previous?.Compatibility.FirstOrDefault(p => p.Key == t.Key && p.Accepted && p.AcceptedReason == t.AcceptedReason) is { } p2 && t.Accepted ? t with { AcceptedBy = p2.AcceptedBy } : t)];
        var fingerprint = IntegrationSuiteComposer.Fingerprint(new
        {
            Scope = scope.ScopeId, Owning = owning,
            A = associations.Select(a => (a.SnapshotId, string.Join(",", a.Stages), a.Approval.ToString(), a.Reason ?? "")),
            T = transitions.Where(t => t.Accepted).Select(t => (t.Key, t.AcceptedReason ?? "")),
        });
        if (latestRow is not null && latestRow.Fingerprint == fingerprint) return new(await GetAsync(latestRow.Id, ct), [], Unchanged: true);

        var context = new CompositeSourceContext
        {
            Id = Guid.NewGuid(), ContextKey = key, Revision = (latestRow?.Revision ?? 0) + 1, OwningProject = owning, ScopeId = scope.ScopeId, ScopeName = scope.DisplayName,
            Snapshots = associations, StageMappings = mappings, Compatibility = transitions, Fingerprint = fingerprint,
            CreatedAt = previous?.CreatedAt ?? now, UpdatedAt = now, CreatedBy = initiator, Changes = previous is null ? ["First revision."] : Changes(previous, associations, transitions),
        };
        db.CompositeSourceContexts.Add(new CompositeSourceContextRecord
        {
            Id = context.Id, ContextKey = key, Revision = context.Revision, ScopeId = scope.ScopeId, OwningProject = owning, Fingerprint = fingerprint, CreatedAt = now,
            CreatedBy = initiator.Length > 120 ? initiator[..120] : initiator, DefinitionJson = JsonSerializer.Serialize(context, Json),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return new(null, ["Another revision of this context was saved at the same time; reload and review again."], Conflict: true);
        }
        logger.LogInformation("Composite source context saved. Key {Key}; revision {Revision}; snapshots {Snapshots}", key, context.Revision, associations.Count);
        return new(await GetAsync(context.Id, ct), []);
    }

    private static StageSourceRequirement Requirement(IntegrationSuiteScope scope, string stageId) => scope.Stages.First(s => s.StageId == stageId).Source!;

    /// <summary>The folders of a snapshot recognised for a stage (with the stage's dataset-contract evidence).</summary>
    public static List<string> Recognised(IntegrationSuiteScope scope, string stageId, CompositeSourceRules.SnapshotFacts f)
    {
        var requirement = Requirement(scope, stageId);
        var modules = CompositeSourceRules.Modules(f, requirement);
        if (requirement.DatasetContractEvidence) modules = [.. modules.Concat(f.DatasetContracts.Select(d => d.Module).Where(m => m.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase)];
        return modules;
    }

    public static List<CompositeStageMapping> Mappings(IntegrationSuiteScope scope, IReadOnlyList<CompositeSnapshotAssociation> associations) =>
        [.. scope.Stages.Where(s => s.Source is not null).Select(stage =>
        {
            var on = associations.Where(a => a.Approval != CompositeApproval.Rejected && a.Stages.Contains(stage.StageId)).ToList();
            var approved = on.Where(a => a.Approval == CompositeApproval.Approved).ToList();
            var projects = on.Select(a => a.ProjectKey).Distinct(StringComparer.Ordinal).Count();
            var cls = approved.Count > 0 && approved.Select(a => a.ProjectKey).Distinct().Count() == 1 ? CompositeMatchClass.Confirmed
                : projects > 1 ? CompositeMatchClass.Ambiguous
                : on.Count == 1 ? CompositeMatchClass.Candidate
                : stage.Source!.ReferenceSource is not null ? CompositeMatchClass.Confirmed : CompositeMatchClass.Missing;
            var missing = new List<string>();
            if (on.Count == 0 && stage.Source!.ReferenceSource is null) missing.Add("No source associated.");
            if (on.Count == 0 && stage.Source!.ReferenceSource is not null) missing.Add($"Only the built-in {stage.Source.ReferenceSource}.");
            if (approved.Select(a => a.ProjectKey).Distinct().Count() > 1) missing.Add("Approved sources of several projects: approve one project per stage.");
            return new CompositeStageMapping
            {
                StageId = stage.StageId, Label = stage.Label, Mandatory = stage.Source!.Mandatory, Class = cls, SnapshotIds = [.. on.Select(a => a.SnapshotId)],
                ReferenceSource = on.Count == 0 ? stage.Source.ReferenceSource : null, Missing = missing,
                Evidence = [.. on.SelectMany(a => a.Evidence.Where(e => e.StartsWith(stage.StageId + ":", StringComparison.Ordinal)).Select(e => $"{a.ProjectKey} · {e[(stage.StageId.Length + 1)..].Trim()}")).Take(8)],
            };
        })];

    public static Dictionary<string, CompositeSourceRules.Side> Sides(IntegrationSuiteScope scope, IReadOnlyList<CompositeSnapshotAssociation> associations,
        IReadOnlyDictionary<Guid, CompositeSourceRules.SnapshotFacts> facts) =>
        scope.Stages.Where(s => s.Source is not null).ToDictionary(s => s.StageId, s => new CompositeSourceRules.Side(s.StageId,
            [.. associations.Where(a => a.Approval != CompositeApproval.Rejected && a.Stages.Contains(s.StageId) && facts.ContainsKey(a.SnapshotId))
                .Select(a => (facts[a.SnapshotId], a.ModulesByStage.TryGetValue(s.StageId, out var m) ? m : [], a.Approval == CompositeApproval.Approved))],
            s.Source!.ReferenceSource), StringComparer.Ordinal);

    private static List<string> Changes(CompositeSourceContext previous, IReadOnlyList<CompositeSnapshotAssociation> now, IReadOnlyList<CompositeTransition> transitions)
    {
        var changes = new List<string>();
        foreach (var a in now)
        {
            var p = previous.Snapshots.FirstOrDefault(x => x.SnapshotId == a.SnapshotId);
            if (p is null)
            {
                var replaced = previous.Snapshots.FirstOrDefault(x => x.ProjectKey == a.ProjectKey && now.All(n => n.SnapshotId != x.SnapshotId));
                changes.Add(replaced is not null
                    ? $"{a.ProjectKey}: snapshot {CompositeSourceRules.Short(replaced.ArchiveSha256)} → {CompositeSourceRules.Short(a.ArchiveSha256)} (stages {string.Join(", ", a.Stages)})"
                    : $"Added {a.ProjectKey} ({a.ArchiveFileName}) for {string.Join(", ", a.Stages)} — {a.Approval}");
            }
            else
            {
                if (p.Approval != a.Approval) changes.Add($"{a.ProjectKey}: {p.Approval} → {a.Approval}");
                if (!p.Stages.Order().SequenceEqual(a.Stages.Order())) changes.Add($"{a.ProjectKey}: stages {string.Join(", ", p.Stages)} → {string.Join(", ", a.Stages)}");
            }
        }
        foreach (var p in previous.Snapshots.Where(p => now.All(n => n.SnapshotId != p.SnapshotId) && now.All(n => n.ProjectKey != p.ProjectKey)))
            changes.Add($"Removed {p.ProjectKey} ({p.ArchiveFileName})");
        foreach (var t in transitions.Where(t => t.Accepted && previous.Compatibility.All(p => p.Key != t.Key || !p.Accepted)))
            changes.Add($"Accepted {t.Key} as {t.Overall}: {t.AcceptedReason}");
        return changes.Count == 0 ? ["Decisions re-recorded without change."] : changes;
    }
}
