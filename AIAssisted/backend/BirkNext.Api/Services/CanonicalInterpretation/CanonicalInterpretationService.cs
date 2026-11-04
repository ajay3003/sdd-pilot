using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>Reads stored Source Analysis snapshots for interpretation (never re-parses an archive).</summary>
public interface ICanonicalSourceReader
{
    Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct);
}

public sealed class IqrCanonicalSourceReader(Integrations.SourceEvidence.IqrSourceStore store) : ICanonicalSourceReader
{
    public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => store.FindSourceAnalysisAsync(snapshotId, ct);
}

/// <summary>Reads one Delivery snapshot's features (the work items cases G/H compare across modules).</summary>
public interface ICanonicalWorkItemReader
{
    Task<IReadOnlyList<WorkItemIntentInput>?> FeaturesAsync(Guid deliverySnapshotId, CancellationToken ct);
}

public sealed class DeliveryCanonicalWorkItemReader(Data.DeliveryReporting.DeliveryReportingDbContext db) : ICanonicalWorkItemReader
{
    public async Task<IReadOnlyList<WorkItemIntentInput>?> FeaturesAsync(Guid deliverySnapshotId, CancellationToken ct)
    {
        if (!await db.Snapshots.AnyAsync(s => s.Id == deliverySnapshotId, ct)) return null;
        var features = await db.FeatureSnapshots.AsNoTracking().Where(f => f.SnapshotId == deliverySnapshotId)
            .Select(f => new { f.Id, f.Module, f.FeatureKey, f.Title, f.SpecPath }).ToListAsync(ct);
        var stories = await db.UserStorySnapshots.AsNoTracking().Where(s => s.SnapshotId == deliverySnapshotId && !s.Removed)
            .Select(s => new { s.FeatureSnapshotId, s.Title }).ToListAsync(ct);
        var byFeature = stories.ToLookup(s => s.FeatureSnapshotId, s => s.Title);
        return [.. features.Select(f => new WorkItemIntentInput(f.Module, f.FeatureKey, f.Title, [.. byFeature[f.Id]], f.SpecPath, $"delivery:{deliverySnapshotId}"))];
    }
}

/// <summary>Thrown for a request the caller can correct (unknown scope format, unknown target).</summary>
public sealed class CanonicalRequestException(string message) : Exception(message);

/// <summary>
/// The canonical interpretation store: idempotent ingestion of correlation results (stable ids found again by identity key, never by display name),
/// the shared review decision registry, immutable interpretation snapshots and scoped queries. Every query is scoped to one project; nothing
/// reads across scopes. Human decisions are applied with <see cref="CorrelationPrecedence"/> on every interpretation and are never overwritten
/// by a heuristic.
/// </summary>
public sealed partial class CanonicalInterpretationService(
    CanonicalInterpretationDbContext db, ICanonicalSourceReader sources, TimeProvider clock, ICanonicalWorkItemReader? workItems = null,
    IEnumerable<IntegrationTopology.ITopologyProfile>? profiles = null, IEnumerable<IntegrationJourneys.IIntegrationJourneyPack>? packs = null)
{
    public const string WorkItemRuleSet = WorkItemInterpreter.RuleSetVersion;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,199}$")]
    private static partial Regex ScopeRe();

    /// <summary>A scope is a project key (the Target Environment profile id the browser works in). Validated so it can never be a pattern or a path.</summary>
    public static string ValidScope(string? scope) =>
        scope is not null && ScopeRe().IsMatch(scope) ? scope : throw new CanonicalRequestException("A project scope (letters, digits, . _ : -) is required.");

    // ---------------------------------------------------------------- interpretation

    public async Task<InterpretationSnapshotView> InterpretAsync(string scope, InterpretRequest request, CancellationToken ct)
    {
        scope = ValidScope(scope);
        if (request.SourceSnapshotIds.Count == 0 && request.DeliverySnapshotId is null)
            throw new CanonicalRequestException("Choose at least one Source Analysis snapshot or a Delivery snapshot to interpret.");

        var inputs = new List<CorrelationInput>();
        var refs = new List<SnapshotReference>();
        var limitations = new List<string>();
        foreach (var id in request.SourceSnapshotIds.Distinct().Order())
        {
            var snapshot = await sources.FindAsync(id, ct) ?? throw new CanonicalRequestException($"Source Analysis snapshot {id} is not stored.");
            inputs.Add(CanonicalEvidenceAdapter.FromSourceSnapshot(scope, snapshot));
            refs.Add(new(SnapshotType.SourceAnalysis, id.ToString(), snapshot.Archive.FileName, snapshot.AnalyzerVersion.ToString()));
            if (snapshot.TestInventory is null) limitations.Add($"{snapshot.Archive.FileName}: no test inventory recorded (analyzed before test evidence).");
            if (snapshot.CodeRiskEvidence is null) limitations.Add($"{snapshot.Archive.FileName}: no endpoint routes recorded (source symbols for API operations unavailable).");
            if (snapshot.EvidenceDomains?.Contracts is null) limitations.Add($"{snapshot.Archive.FileName}: no contract evidence recorded.");
        }
        if (request.DeliverySnapshotId is { } deliveryId)
        {
            var features = workItems is null ? null : await workItems.FeaturesAsync(deliveryId, ct);
            if (features is null) throw new CanonicalRequestException($"Delivery snapshot {deliveryId} is not stored.");
            inputs.Add(CorrelationInput.Empty(scope) with { WorkItems = features });
            refs.Add(new(SnapshotType.Delivery, deliveryId.ToString(), "Delivery snapshot"));
        }
        inputs.Add(CorrelationInput.Empty(scope) with { Systems = CanonicalEvidenceAdapter.DeclaredSystems(profiles ?? [], (packs ?? []).SelectMany(p => p.Aliases)) });

        var result = CorrelationRules.Correlate(CanonicalEvidenceAdapter.Merge(scope, inputs));
        limitations.AddRange(result.Limitations);
        return await PersistAsync(scope, result, refs, limitations, request.Trigger ?? "Interpret", ct);
    }

    /// <summary>Persists one correlation result: upserts identities, occurrences, aliases and relations, applies decisions, freezes a snapshot.</summary>
    public async Task<InterpretationSnapshotView> PersistAsync(string scope, CorrelationResult result, IReadOnlyList<SnapshotReference> sourceRefs,
        IReadOnlyList<string> limitations, string trigger, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync(ct) : null;
        var now = clock.GetUtcNow();

        // Entities: one query for the scope's existing identities; new ones get an id once and keep it.
        var existing = await db.Entities.Where(e => e.Scope == scope).ToDictionaryAsync(e => CanonicalKeys.EntityKey(Enum.Parse<CanonicalEntityType>(e.EntityType), e.IdentityKey), ct);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var fact in result.Entities)
        {
            if (!existing.TryGetValue(fact.Key, out var record))
            {
                record = new CanonicalEntityRecord { Id = Guid.NewGuid(), Scope = scope, EntityType = fact.Type.ToString(), IdentityKey = Trim(fact.IdentityKey, 1000), CreatedAt = now };
                db.Entities.Add(record);
                existing[fact.Key] = record;
            }
            record.DisplayName = Trim(fact.DisplayName, 500);
            record.SourceIdentity = Trim(fact.SourceIdentity, 1000);
            record.ResolutionMethod = Trim(fact.ResolutionMethod, 200);
            record.Version = fact.Version is null ? record.Version : Trim(fact.Version, 100);
            var attributes = JsonSerializer.Serialize(fact.Attributes ?? new Dictionary<string, string>(), Json);
            if (record.AttributesJson != attributes || record.UpdatedAt == default) { record.AttributesJson = attributes; record.UpdatedAt = now; }
            ids[fact.Key] = record.Id;
        }

        // Occurrences: separate from identity; only snapshots read in this pass are touched.
        var snapshotIds = result.Entities.SelectMany(e => e.Evidence).Select(e => e.SourceSnapshotId).OfType<string>().Distinct().ToList();
        var seen = (await db.Occurrences.Where(o => o.Scope == scope && snapshotIds.Contains(o.SourceSnapshotId))
            .Select(o => new { o.EntityId, o.SourceSnapshotId, o.OccurrenceKey }).ToListAsync(ct))
            .Select(o => (o.EntityId, o.SourceSnapshotId, o.OccurrenceKey)).ToHashSet();
        foreach (var fact in result.Entities)
            foreach (var ev in fact.Evidence.Where(e => e.SourceSnapshotId is not null))
            {
                var key = (ids[fact.Key], ev.SourceSnapshotId!, Trim(ev.Reference, 1000));
                if (!seen.Add(key)) continue;
                db.Occurrences.Add(new() { Id = Guid.NewGuid(), EntityId = key.Item1, Scope = scope, SourceSnapshotId = key.Item2, OccurrenceKey = key.Item3,
                    Kind = Trim(ev.Kind, 60), Detail = ev.Detail is null ? null : Trim(ev.Detail, 1000), FirstSeenAt = now });
            }

        var decisions = await CurrentDecisionsAsync(scope, ct);

        // Aliases: ingestion proposes or declares; a human state (Confirmed/Rejected) is never overwritten.
        var aliasRows = await db.Aliases.Where(a => a.Scope == scope).ToListAsync(ct);
        foreach (var alias in result.Aliases)
        {
            if (!ids.TryGetValue(alias.EntityKey, out var entityId)) continue;
            var normalized = CanonicalKeys.Normalize(alias.Alias);
            var row = aliasRows.FirstOrDefault(a => a.EntityId == entityId && a.NormalizedAlias == normalized);
            if (row is null)
            {
                row = new() { Id = Guid.NewGuid(), Scope = scope, EntityId = entityId, Alias = Trim(alias.Alias, 300), NormalizedAlias = Trim(normalized, 300), CreatedAt = now, UpdatedAt = now };
                db.Aliases.Add(row);
                aliasRows.Add(row);
            }
            var decided = decisions.GetValueOrDefault(CorrelationPrecedence.AliasTargetKey(alias.EntityKey, alias.Alias));
            var state = decided?.Outcome switch
            {
                ReviewDecisionOutcome.Confirmed => AliasState.Confirmed,
                ReviewDecisionOutcome.Rejected => AliasState.Rejected,
                _ => row.State is nameof(AliasState.Confirmed) or nameof(AliasState.Rejected) ? Enum.Parse<AliasState>(row.State) : alias.State,
            };
            if (row.State != state.ToString() || row.RuleId != alias.RuleId) { row.State = state.ToString(); row.Source = Trim(alias.Source, 500); row.RuleId = alias.RuleId; row.UpdatedAt = now; }
        }

        // Relations: found again by endpoints + type; decisions applied by relation key and evidence fingerprint.
        var relationRows = await db.Relations.Where(r => r.Scope == scope).ToListAsync(ct);
        var byKey = relationRows.ToDictionary(r => r.RelationKey, StringComparer.Ordinal);
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in result.Relations)
        {
            if (!ids.TryGetValue(fact.SourceKey, out var from) || !ids.TryGetValue(fact.TargetKey, out var to)) continue;
            produced.Add(fact.Key);
            if (!byKey.TryGetValue(fact.Key, out var row))
            {
                row = new() { Id = Guid.NewGuid(), Scope = scope, SourceEntityId = from, TargetEntityId = to, RelationType = fact.Type.ToString(), RelationKey = Trim(fact.Key, 2200), CreatedAt = now };
                db.Relations.Add(row);
                byKey[fact.Key] = row;
            }
            var decision = decisions.GetValueOrDefault(fact.Key);
            var (review, _) = CorrelationPrecedence.Apply(fact.Resolution, fact.EvidenceFingerprint, decision);
            var evidence = JsonSerializer.Serialize(fact.Evidence, Json);
            if (row.Resolution != fact.Resolution.ToString() || row.EvidenceFingerprint != fact.EvidenceFingerprint || row.ReviewState != review.ToString() || !row.Active
                || row.RuleId != fact.RuleId || row.RuleSetVersion != result.RuleSetVersion)
                row.UpdatedAt = now;
            row.Resolution = fact.Resolution.ToString();
            row.Confidence = fact.Confidence.ToString();
            row.RuleId = fact.RuleId;
            row.RuleSetVersion = result.RuleSetVersion;
            row.EvidenceJson = evidence;
            row.EvidenceFingerprint = fact.EvidenceFingerprint;
            row.Explanation = Trim(fact.Explanation, 2000);
            row.ReviewState = review.ToString();
            row.ReviewDecisionId = decision?.Id;
            row.Active = true;
        }
        // A relation the current evidence no longer produces stays for history; a human confirmation of it now needs revalidation.
        foreach (var row in relationRows.Where(r => r.Active && !produced.Contains(r.RelationKey)))
        {
            row.Active = false;
            if (row.ReviewState == nameof(RelationReviewState.Confirmed)) row.ReviewState = nameof(RelationReviewState.RevalidationRequired);
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // Snapshot: membership is frozen, so a later interpretation never changes what this one said.
        var active = relationRows.Concat(db.Relations.Local).Where(r => r.Scope == scope && produced.Contains(r.RelationKey)).DistinctBy(r => r.Id).ToList();
        var memberEntityIds = active.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).Concat(ids.Values).ToHashSet();
        var ruleSets = string.Join(';', new[] { result.RuleSetVersion }.Concat(sourceRefs.Any(r => r.Type == SnapshotType.Delivery) ? [WorkItemRuleSet] : []));
        var sourceSet = CanonicalKeys.Fingerprint(sourceRefs.Select(r => $"{r.Type}:{r.Id}"));
        var decisionSequence = decisions.Count == 0 ? 0 : await db.Decisions.Where(d => d.Scope == scope).MaxAsync(d => d.Sequence, ct);
        var content = CanonicalKeys.Fingerprint(active.Select(r => $"{r.RelationKey}|{r.Resolution}|{r.ReviewState}|{r.EvidenceFingerprint}|{r.RuleId}")
            .Concat(memberEntityIds.Select(id => id.ToString())));
        var latest = await db.Snapshots.Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (latest is not null && latest.SourceSetFingerprint == sourceSet && latest.RuleSetVersions == ruleSets && latest.ContentFingerprint == content
            && latest.DecisionSequence == decisionSequence)
        {
            if (tx is not null) await tx.CommitAsync(ct);
            return View(latest) with { Reused = true };
        }

        var snapshot = new InterpretationSnapshotRecord
        {
            Id = Guid.NewGuid(), Scope = scope, CreatedAt = now, RuleSetVersions = ruleSets, SourceSnapshotsJson = JsonSerializer.Serialize(sourceRefs, Json),
            SourceSetFingerprint = sourceSet, DecisionSequence = decisionSequence, EntityCount = memberEntityIds.Count, RelationCount = active.Count,
            ContentFingerprint = content, LimitationsJson = JsonSerializer.Serialize(limitations.Distinct().ToList(), Json), Trigger = Trim(trigger, 200), PreviousId = latest?.Id,
        };
        db.Snapshots.Add(snapshot);
        var names = existing.Values.ToDictionary(e => e.Id);
        db.SnapshotEntities.AddRange(memberEntityIds.Select(id => new InterpretationSnapshotEntityRecord
            { SnapshotId = snapshot.Id, EntityId = id, DisplayName = names.TryGetValue(id, out var e) ? e.DisplayName : "", Version = names.TryGetValue(id, out var v) ? v.Version : null }));
        db.SnapshotRelations.AddRange(active.Select(r => new InterpretationSnapshotRelationRecord
            { SnapshotId = snapshot.Id, RelationId = r.Id, Resolution = r.Resolution, Confidence = r.Confidence, RuleId = r.RuleId, ReviewState = r.ReviewState, EvidenceFingerprint = r.EvidenceFingerprint }));
        foreach (var e in existing.Values.Where(e => ids.ContainsValue(e.Id))) e.LastInterpretationId = snapshot.Id;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return View(snapshot);
    }

    static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    // ---------------------------------------------------------------- review decisions

    async Task<Dictionary<string, ReviewDecisionFact>> CurrentDecisionsAsync(string scope, CancellationToken ct) =>
        (await db.Decisions.AsNoTracking().Where(d => d.Scope == scope && d.SupersededById == null).ToListAsync(ct))
            .GroupBy(d => d.TargetKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Fact(g.OrderByDescending(d => d.Sequence).First()), StringComparer.Ordinal);

    static ReviewDecisionFact Fact(ReviewDecisionRecord d) => new(d.Id, Enum.Parse<ReviewTargetKind>(d.TargetKind), d.TargetKey, Enum.Parse<ReviewDecisionOutcome>(d.Outcome),
        d.EvidenceFingerprint, d.Reviewer, d.DecidedAt, d.Notes, d.Scope);

    /// <summary>Records a human decision (append-only; a previous decision on the same target is superseded, never edited) and applies it at once.</summary>
    public async Task<ReviewDecisionView> DecideAsync(string scope, ReviewDecisionRequest request, string? authenticatedReviewer, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var reviewer = Trim(!string.IsNullOrWhiteSpace(authenticatedReviewer) ? authenticatedReviewer : string.IsNullOrWhiteSpace(request.Reviewer) ? "local user" : request.Reviewer.Trim(), 200);
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync(ct) : null;
        var now = clock.GetUtcNow();
        string proposed, fingerprint, ruleSet;
        CanonicalRelationRecord? relation = null;
        CanonicalAliasRecord? alias = null;
        if (request.TargetKind == ReviewTargetKind.Relation)
        {
            relation = await db.Relations.FirstOrDefaultAsync(r => r.Scope == scope && r.RelationKey == request.TargetKey, ct)
                ?? throw new CanonicalRequestException("That relation is not part of this project's interpretation.");
            proposed = JsonSerializer.Serialize(new { relation.RelationType, relation.Resolution, relation.Confidence, relation.RuleId, relation.Explanation }, Json);
            (fingerprint, ruleSet) = (relation.EvidenceFingerprint, relation.RuleSetVersion);
        }
        else
        {
            var tilde = request.TargetKey.LastIndexOf('~');
            if (tilde <= 0) throw new CanonicalRequestException("An alias target key is \"<entity key>~<alias>\".");
            var (entityKey, normalized) = (request.TargetKey[..tilde], request.TargetKey[(tilde + 1)..]);
            var sep = entityKey.IndexOf('|');
            if (sep <= 0 || !Enum.TryParse<CanonicalEntityType>(entityKey[..sep], out var type)) throw new CanonicalRequestException("Unknown alias entity.");
            var identity = entityKey[(sep + 1)..];
            var entity = await db.Entities.FirstOrDefaultAsync(e => e.Scope == scope && e.EntityType == type.ToString() && e.IdentityKey == identity, ct)
                ?? throw new CanonicalRequestException("That entity is not part of this project's interpretation.");
            alias = await db.Aliases.FirstOrDefaultAsync(a => a.Scope == scope && a.EntityId == entity.Id && a.NormalizedAlias == normalized, ct)
                ?? throw new CanonicalRequestException("That alias is not proposed for this entity.");
            proposed = JsonSerializer.Serialize(new { alias.Alias, Entity = entity.DisplayName, alias.State, alias.Source, alias.RuleId }, Json);
            (fingerprint, ruleSet) = (CanonicalKeys.Fingerprint([alias.Source, alias.RuleId]), CorrelationRules.RuleSetVersion);
        }

        var previous = await db.Decisions.Where(d => d.Scope == scope && d.TargetKey == request.TargetKey && d.SupersededById == null).OrderByDescending(d => d.Sequence).FirstOrDefaultAsync(ct);
        var sequence = (await db.Decisions.Where(d => d.Scope == scope).Select(d => (long?)d.Sequence).MaxAsync(ct) ?? 0) + 1;
        var latestInterpretation = await db.Snapshots.Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        var record = new ReviewDecisionRecord
        {
            Id = Guid.NewGuid(), Sequence = sequence, Scope = scope, TargetKind = request.TargetKind.ToString(), TargetKey = Trim(request.TargetKey, 2200),
            TargetRelationId = relation?.Id, TargetAliasId = alias?.Id, ProposedJson = proposed, Outcome = request.Outcome.ToString(), Reviewer = reviewer, DecidedAt = now,
            Notes = request.Notes is null ? null : Trim(request.Notes, 2000), EvidenceFingerprint = fingerprint, RuleSetVersion = ruleSet,
            InterpretationId = latestInterpretation, SupersedesId = previous?.Id,
        };
        db.Decisions.Add(record);
        if (previous is not null) previous.SupersededById = record.Id;
        if (relation is not null)
        {
            var (review, _) = CorrelationPrecedence.Apply(Enum.Parse<RelationResolution>(relation.Resolution), relation.EvidenceFingerprint, Fact(record));
            relation.ReviewState = review.ToString();
            relation.ReviewDecisionId = record.Id;
            relation.UpdatedAt = now;
        }
        if (alias is not null && request.Outcome != ReviewDecisionOutcome.Deferred)
        {
            alias.State = (request.Outcome == ReviewDecisionOutcome.Confirmed ? AliasState.Confirmed : AliasState.Rejected).ToString();
            alias.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return View(record);
    }

    public async Task<IReadOnlyList<ReviewDecisionView>> DecisionsAsync(string scope, string? targetKey, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var query = db.Decisions.AsNoTracking().Where(d => d.Scope == scope);
        if (!string.IsNullOrEmpty(targetKey)) query = query.Where(d => d.TargetKey == targetKey);
        return [.. (await query.OrderByDescending(d => d.Sequence).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(View)];
    }

    // ---------------------------------------------------------------- queries

    public async Task<CanonicalPage<CanonicalEntityView>> EntitiesAsync(string scope, CanonicalEntityType? type, string? search, int skip, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var query = db.Entities.AsNoTracking().Where(e => e.Scope == scope);
        if (type is { } t) query = query.Where(e => e.EntityType == t.ToString());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.DisplayName.ToLower().Contains(s) || e.IdentityKey.Contains(s));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(e => e.EntityType).ThenBy(e => e.DisplayName).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return new([.. rows.Select(View)], total, skip, take);
    }

    public async Task<CanonicalEntityDetail?> EntityAsync(string scope, Guid id, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var entity = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Scope == scope && e.Id == id, ct);
        if (entity is null) return null;
        var aliases = await db.Aliases.AsNoTracking().Where(a => a.Scope == scope && a.EntityId == id).OrderBy(a => a.Alias).ToListAsync(ct);
        var occurrences = await db.Occurrences.AsNoTracking().Where(o => o.EntityId == id).OrderBy(o => o.SourceSnapshotId).ThenBy(o => o.OccurrenceKey).Take(200).ToListAsync(ct);
        var outgoing = await RelationViewsAsync(scope, db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.SourceEntityId == id).Take(300), ct);
        var incoming = await RelationViewsAsync(scope, db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.TargetEntityId == id).Take(300), ct);
        return new(View(entity), [.. aliases.Select(View)], [.. occurrences.Select(o => new CanonicalOccurrenceView(o.SourceSnapshotId, o.OccurrenceKey, o.Kind, o.Detail, o.FirstSeenAt))],
            outgoing, incoming);
    }

    /// <summary>The review queue and relation listings: filter by resolution, review state, rule or "needs review".</summary>
    public async Task<CanonicalPage<CanonicalRelationView>> RelationsAsync(string scope, RelationResolution? resolution, RelationReviewState? review, string? ruleId,
        bool needsReview, int skip, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var query = db.Relations.AsNoTracking().Where(r => r.Scope == scope);
        if (resolution is { } res) query = query.Where(r => r.Resolution == res.ToString());
        if (review is { } rv) query = query.Where(r => r.ReviewState == rv.ToString());
        if (!string.IsNullOrWhiteSpace(ruleId)) query = query.Where(r => r.RuleId == ruleId);
        if (needsReview)
            query = query.Where(r => r.Active && (r.ReviewState == "RevalidationRequired" || r.ReviewState == "Conflict"
                || ((r.ReviewState == "NotReviewed" || r.ReviewState == "Deferred") && (r.Resolution == "Candidate" || r.Resolution == "Ambiguous"))));
        var total = await query.CountAsync(ct);
        var page = query.OrderBy(r => r.RuleId).ThenBy(r => r.RelationKey).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200));
        return new(await RelationViewsAsync(scope, page, ct), total, skip, take);
    }

    /// <summary>Case N: walks the default (or a given) cross-domain path from an entity; missing links are reported, never fabricated.</summary>
    public async Task<CorrelationChainView?> ChainAsync(string scope, Guid entityId, IReadOnlyList<CanonicalEntityType>? path, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var start = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Scope == scope && e.Id == entityId, ct);
        if (start is null) return null;
        var startType = Enum.Parse<CanonicalEntityType>(start.EntityType);
        var steps = path is { Count: > 1 } ? path : CorrelationChain.DefaultPath.SkipWhile(t => t != startType).ToArray() is { Length: > 1 } p ? p : [startType, .. CorrelationChain.DefaultPath.Where(t => t != startType)];
        var frontier = new HashSet<Guid> { entityId };
        var result = new List<CorrelationChainStepView>();
        foreach (var next in steps.Skip(1))
        {
            var ids = frontier.ToList();
            var nextType = next.ToString();
            var candidates = db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.Active && r.ReviewState != "Rejected"
                && ((ids.Contains(r.SourceEntityId) && db.Entities.Any(e => e.Id == r.TargetEntityId && e.EntityType == nextType))
                    || (ids.Contains(r.TargetEntityId) && db.Entities.Any(e => e.Id == r.SourceEntityId && e.EntityType == nextType)))).Take(200);
            var links = await RelationViewsAsync(scope, candidates, ct);
            result.Add(new(next, links.Count == 0, links));
            if (links.Count > 0) frontier = [.. links.Select(l => frontier.Contains(l.Source.Id) ? l.Target.Id : l.Source.Id)];
        }
        return new(View(start), steps, result);
    }

    /// <summary>Case K: what a name means in this scope. Only explicit/confirmed aliases (or the entity's own identity) resolve; proposals are candidates.</summary>
    public async Task<AliasResolution> ResolveAliasAsync(string scope, string name, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var normalized = CanonicalKeys.Normalize(name);
        var aliases = await db.Aliases.AsNoTracking().Where(a => a.Scope == scope && a.NormalizedAlias == normalized).ToListAsync(ct);
        var resolvedIds = aliases.Where(a => a.State is "Explicit" or "Confirmed").Select(a => a.EntityId).ToList();
        var own = await db.Entities.AsNoTracking().Where(e => e.Scope == scope && (e.IdentityKey == normalized || resolvedIds.Contains(e.Id))).ToListAsync(ct);
        return new(name, [.. own.Select(View)], [.. aliases.Where(a => a.State == "Proposed").Select(View)], [.. aliases.Where(a => a.State == "Rejected").Select(View)]);
    }

    /// <summary>Alias pairs the shared consumers (Topology, IQR) may apply: explicit or human-confirmed, of systems and services, never proposals.</summary>
    public async Task<IReadOnlyList<(string Canonical, string Alias, AliasState State)>> ApplicableAliasesAsync(string scope, CancellationToken ct)
    {
        if (!ScopeRe().IsMatch(scope)) return [];
        var rows = await (from a in db.Aliases.AsNoTracking()
                          join e in db.Entities.AsNoTracking() on a.EntityId equals e.Id
                          where a.Scope == scope && (a.State == "Explicit" || a.State == "Confirmed") && (e.EntityType == "System" || e.EntityType == "Service")
                          select new { e.DisplayName, a.Alias, a.State }).ToListAsync(ct);
        return [.. rows.Select(r => (r.DisplayName, r.Alias, Enum.Parse<AliasState>(r.State))).OrderBy(r => r.DisplayName, StringComparer.Ordinal).ThenBy(r => r.Alias, StringComparer.Ordinal)];
    }

    /// <summary>AQR: the canonical operation for a method + path (templated the way Endpoint Discovery templates it), with its relations.</summary>
    public async Task<IReadOnlyList<CanonicalEntityDetail>> OperationsAsync(string scope, string method, string path, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var route = CorrelationRules.NormalizeRoute(BirkNext.PerformanceTests.PerformanceScenarioEngine.Template(path));
        var suffix = $"|{method.ToUpperInvariant()} {route}";
        var matches = await db.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == "ApiOperation" && e.IdentityKey.EndsWith(suffix)).Take(10).Select(e => e.Id).ToListAsync(ct);
        var details = new List<CanonicalEntityDetail>();
        foreach (var id in matches) if (await EntityAsync(scope, id, ct) is { } d) details.Add(d);
        return details;
    }

    public async Task<IReadOnlyList<InterpretationSnapshotView>> SnapshotsAsync(string scope, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        return [.. (await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct)).Select(View)];
    }

    /// <summary>Two frozen interpretations compared by their membership rows (what each said when it was made).</summary>
    public async Task<InterpretationComparison?> CompareAsync(string scope, Guid from, Guid to, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var snaps = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope && (s.Id == from || s.Id == to)).ToListAsync(ct);
        if (snaps.Count != (from == to ? 1 : 2)) return null;
        async Task<Dictionary<string, (string Type, string State)>> Members(Guid id) =>
            (await (from m in db.SnapshotRelations.AsNoTracking()
                    join r in db.Relations.AsNoTracking() on m.RelationId equals r.Id
                    where m.SnapshotId == id
                    select new { r.RelationKey, r.RelationType, m.Resolution, m.ReviewState, m.RuleId }).ToListAsync(ct))
            .ToDictionary(x => x.RelationKey, x => (x.RelationType, $"{x.Resolution} · {x.ReviewState} · {x.RuleId}"), StringComparer.Ordinal);
        var a = await Members(from);
        var b = await Members(to);
        static InterpretationRelationChange C(string key, string type, string? before, string? after) => new(key, Enum.Parse<CanonicalRelationType>(type), before, after);
        var ruleChanges = snaps.Select(s => s.RuleSetVersions).Distinct().Count() > 1 ? new[] { $"{snaps.First(s => s.Id == from).RuleSetVersions} → {snaps.First(s => s.Id == to).RuleSetVersions}" } : [];
        return new(from, to,
            [.. b.Where(x => !a.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => C(x.Key, x.Value.Type, null, x.Value.State))],
            [.. a.Where(x => !b.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => C(x.Key, x.Value.Type, x.Value.State, null))],
            [.. a.Where(x => b.TryGetValue(x.Key, out var y) && y.State != x.Value.State).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => C(x.Key, x.Value.Type, x.Value.State, b[x.Key].State))],
            ruleChanges);
    }

    /// <summary>A frozen interpretation's relations exactly as it recorded them (state from the membership row, not the live relation).</summary>
    public async Task<CanonicalPage<CanonicalRelationView>?> SnapshotRelationsAsync(string scope, Guid snapshotId, int skip, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        if (!await db.Snapshots.AnyAsync(s => s.Scope == scope && s.Id == snapshotId, ct)) return null;
        var members = db.SnapshotRelations.AsNoTracking().Where(m => m.SnapshotId == snapshotId);
        var total = await members.CountAsync(ct);
        var page = await members.OrderBy(m => m.RuleId).ThenBy(m => m.RelationId).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var ids = page.Select(m => m.RelationId).ToList();
        var live = await RelationViewsAsync(scope, db.Relations.AsNoTracking().Where(r => ids.Contains(r.Id)), ct);
        var frozen = page.Join(live, m => m.RelationId, r => r.Id, (m, r) =>
        {
            var resolution = Enum.Parse<RelationResolution>(m.Resolution);
            var reviewState = Enum.Parse<RelationReviewState>(m.ReviewState);
            var effective = reviewState switch { RelationReviewState.Confirmed => RelationResolution.Confirmed, RelationReviewState.Rejected => RelationResolution.Rejected, _ => resolution };
            return r with { Resolution = resolution, Effective = effective, ReviewState = reviewState, RuleId = m.RuleId, EvidenceFingerprint = m.EvidenceFingerprint,
                Confidence = Enum.Parse<CorrelationConfidence>(m.Confidence), Active = true };
        }).ToList();
        return new(frozen, total, skip, take);
    }

    // ---------------------------------------------------------------- views

    async Task<IReadOnlyList<CanonicalRelationView>> RelationViewsAsync(string scope, IQueryable<CanonicalRelationRecord> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0) return [];
        var entityIds = rows.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).Distinct().ToList();
        var entities = (await db.Entities.AsNoTracking().Where(e => e.Scope == scope && entityIds.Contains(e.Id)).ToListAsync(ct)).ToDictionary(e => e.Id, View);
        var decisionIds = rows.Select(r => r.ReviewDecisionId).OfType<Guid>().Distinct().ToList();
        var decisions = (await db.Decisions.AsNoTracking().Where(d => decisionIds.Contains(d.Id)).ToListAsync(ct)).ToDictionary(d => d.Id, Fact);
        return [.. rows.Where(r => entities.ContainsKey(r.SourceEntityId) && entities.ContainsKey(r.TargetEntityId)).Select(r =>
        {
            var resolution = Enum.Parse<RelationResolution>(r.Resolution);
            var decision = r.ReviewDecisionId is { } d ? decisions.GetValueOrDefault(d) : null;
            var stored = Enum.Parse<RelationReviewState>(r.ReviewState);
            var effective = decision is null ? resolution : CorrelationPrecedence.Apply(resolution, r.EvidenceFingerprint, decision).Effective;
            if (!r.Active && effective == RelationResolution.Confirmed) effective = resolution; // evidence gone: a confirmation is not counted until revalidated
            return new CanonicalRelationView(r.Id, r.RelationKey, entities[r.SourceEntityId], entities[r.TargetEntityId], Enum.Parse<CanonicalRelationType>(r.RelationType),
                resolution, effective, Enum.Parse<CorrelationConfidence>(r.Confidence), r.RuleId, r.RuleSetVersion,
                JsonSerializer.Deserialize<List<CorrelationEvidence>>(r.EvidenceJson, Json) ?? [], r.EvidenceFingerprint, r.Explanation, stored, r.ReviewDecisionId, r.Active);
        }).OrderBy(r => r.RelationKey, StringComparer.Ordinal)];
    }

    static CanonicalEntityView View(CanonicalEntityRecord e) => new(e.Id, Enum.Parse<CanonicalEntityType>(e.EntityType), e.IdentityKey, e.DisplayName, e.SourceIdentity,
        e.ResolutionMethod, e.Version, JsonSerializer.Deserialize<Dictionary<string, string>>(e.AttributesJson, Json) ?? [], e.CreatedAt, e.UpdatedAt);

    static CanonicalAliasView View(CanonicalAliasRecord a) => new(a.Id, a.EntityId, a.Alias, Enum.Parse<AliasState>(a.State), a.Source, a.RuleId, a.UpdatedAt);

    static ReviewDecisionView View(ReviewDecisionRecord d) => new(d.Id, d.Sequence, Enum.Parse<ReviewTargetKind>(d.TargetKind), d.TargetKey, Enum.Parse<ReviewDecisionOutcome>(d.Outcome),
        d.Reviewer, d.DecidedAt, d.Notes, d.EvidenceFingerprint, d.RuleSetVersion, d.SupersedesId, d.SupersededById, d.ProposedJson);

    static InterpretationSnapshotView View(InterpretationSnapshotRecord s) => new(s.Id, s.Scope, s.CreatedAt, s.RuleSetVersions.Split(';', StringSplitOptions.RemoveEmptyEntries),
        JsonSerializer.Deserialize<List<SnapshotReference>>(s.SourceSnapshotsJson, Json) ?? [], s.DecisionSequence, s.EntityCount, s.RelationCount, s.ContentFingerprint,
        JsonSerializer.Deserialize<List<string>>(s.LimitationsJson, Json) ?? [], s.Trigger, s.PreviousId);
}
