using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// Requirement identity (identity/1): a source-only analysis holds requirement ids without a module or feature (<c>Requirement|FR-012</c>). When the
/// same analysis includes a Delivery snapshot, every feature pack that defines the same normalized id is a proposed scoped identity
/// (<c>Requirement|payments/001-payments/FR-012</c>): one pack = candidate, several = ambiguous (no default), none = unresolved. Nothing is matched by
/// name, text or module guessing, and nothing resolves until a person confirms one specific target in the shared decision registry
/// (<see cref="ReviewTargetKind.Identity"/>, append-only with history). Decisions are never touched by a reanalysis; a confirmation applies only
/// while its target is still a proposal of the latest analysis.
/// </summary>
public sealed partial class CanonicalInterpretationService
{
    sealed record IdentityInputs(InterpretationSnapshotRecord Latest, IReadOnlyList<SnapshotReference> Refs, bool DeliveryIncluded,
        IReadOnlyList<CanonicalEntityRecord> Unscoped, IReadOnlyDictionary<string, List<CanonicalEntityRecord>> ScopedById,
        IReadOnlyDictionary<Guid, List<CanonicalOccurrenceRecord>> Occurrences, IReadOnlySet<string> DeliveryRefs, IReadOnlySet<Guid> Members);

    /// <summary>
    /// The project's unscoped requirement ids — those of the latest analysis and those an earlier (source-only) analysis held, which scopes may still
    /// reference: once a Delivery snapshot is analyzed, explicit test links move to the feature pack and the unscoped id leaves the latest analysis —
    /// and the scoped requirements the latest analysis' Delivery snapshot(s) define, by id.
    /// </summary>
    async Task<IdentityInputs?> IdentityInputsAsync(string scope, CancellationToken ct)
    {
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        var refs = JsonSerializer.Deserialize<List<SnapshotReference>>(latest.SourceSnapshotsJson, Json) ?? [];
        var deliveryRefs = refs.Where(r => r.Type == SnapshotType.Delivery).Select(r => $"delivery:{r.Id}").ToHashSet(StringComparer.Ordinal);
        var requirements = await (from m in db.SnapshotEntities.AsNoTracking()
                                  join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                                  where m.SnapshotId == latest.Id && e.Scope == scope && e.EntityType == "Requirement"
                                  select e).ToListAsync(ct);
        var members = requirements.Select(e => e.Id).ToHashSet();
        var earlier = await db.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == "Requirement" && !e.IdentityKey.Contains("/")).ToListAsync(ct);
        requirements.AddRange(earlier.Where(e => !members.Contains(e.Id)));
        var ids = requirements.Select(e => e.Id).ToList();
        var occurrences = (await db.Occurrences.AsNoTracking().Where(o => o.Scope == scope && ids.Contains(o.EntityId)).ToListAsync(ct))
            .GroupBy(o => o.EntityId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.SourceSnapshotId, StringComparer.Ordinal).ThenBy(o => o.OccurrenceKey, StringComparer.Ordinal).ToList());
        var unscoped = requirements.Where(e => RequirementIdentity.IsUnscoped(e.EntityType, e.IdentityKey)).OrderBy(e => e.IdentityKey, StringComparer.Ordinal).ToList();
        // Only requirements a Delivery snapshot of THIS analysis defines are proposals (an older Delivery import never is).
        var scopedById = requirements.Where(e => members.Contains(e.Id) && !RequirementIdentity.IsUnscoped(e.EntityType, e.IdentityKey)
                && occurrences.TryGetValue(e.Id, out var occ) && occ.Any(o => deliveryRefs.Contains(o.SourceSnapshotId)))
            .Select(e => (Id: Attr(e.AttributesJson, "id"), Entity: e)).Where(x => x.Id is not null)
            .GroupBy(x => x.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Entity).OrderBy(e => e.IdentityKey, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return new(latest, refs, deliveryRefs.Count > 0, unscoped, scopedById, occurrences, deliveryRefs, members);
    }

    async Task<Dictionary<string, ReviewDecisionRecord>> CurrentIdentityDecisionsAsync(string scope, CancellationToken ct) =>
        (await db.Decisions.AsNoTracking().Where(d => d.Scope == scope && d.TargetKind == nameof(ReviewTargetKind.Identity) && d.SupersededById == null).ToListAsync(ct))
            .GroupBy(d => d.TargetKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Sequence).First(), StringComparer.Ordinal);

    static IdentityTargetState TargetState(ReviewDecisionRecord? decision) => decision?.Outcome switch
    {
        nameof(ReviewDecisionOutcome.Confirmed) => IdentityTargetState.Confirmed,
        nameof(ReviewDecisionOutcome.Rejected) => IdentityTargetState.Rejected,
        _ => IdentityTargetState.Candidate,
    };

    /// <summary>The unscoped requirements of the latest analysis with their proposed scoped identities, evidence and current decisions.</summary>
    public async Task<RequirementIdentityView> RequirementIdentitiesAsync(string scope, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var inputs = await IdentityInputsAsync(scope, ct);
        if (inputs is null)
            return new(scope, null, [], false, [], ["No relationship analysis exists for this project yet. Analyze relationships first."]);
        var decisions = await CurrentIdentityDecisionsAsync(scope, ct);
        var labels = inputs.Refs.DistinctBy(r => (r.Type, r.Id)).ToDictionary(r => r.Type == SnapshotType.Delivery ? $"delivery:{r.Id}" : r.Id, r => r.Label ?? r.Id, StringComparer.Ordinal);
        string SnapshotLabel(string snapshot) => labels.GetValueOrDefault(snapshot) ?? snapshot;

        // Where the source names each unscoped id: the active relations into it (symbol/test references), with their evidence.
        var unscopedIds = inputs.Unscoped.Select(e => e.Id).ToList();
        var incoming = await (from r in db.Relations.AsNoTracking()
                              join s in db.Entities.AsNoTracking() on r.SourceEntityId equals s.Id
                              where r.Scope == scope && unscopedIds.Contains(r.TargetEntityId)
                              select new { r.TargetEntityId, r.RelationType, r.RuleId, r.EvidenceJson, r.Active, s.EntityType, s.DisplayName }).ToListAsync(ct);
        // The current references first; an id the latest analysis no longer holds shows where it was named before.
        var foundBy = incoming.OrderByDescending(r => r.Active).ToLookup(r => r.TargetEntityId);

        var rows = new List<RequirementIdentityRow>();
        var notices = new List<string>();
        foreach (var u in inputs.Unscoped)
        {
            var unscopedKey = CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, u.IdentityKey);
            var inLatest = inputs.Members.Contains(u.Id);
            var found = foundBy[u.Id].Where(r => r.Active || !inLatest).DistinctBy(r => (r.DisplayName, r.RelationType)).Take(8).Select(r =>
            {
                var ev = JsonSerializer.Deserialize<List<CorrelationEvidence>>(r.EvidenceJson, Json)?.FirstOrDefault();
                return new CorrelationEvidence($"{r.EntityType} {r.RelationType}", ev?.Reference ?? r.DisplayName, r.DisplayName,
                    ev?.SourceSnapshotId is { } sid ? SnapshotLabel(sid) : null);
            }).ToList();
            if (found.Count == 0)
                found = [.. inputs.Occurrences.GetValueOrDefault(u.Id, []).Take(8).Select(o => new CorrelationEvidence(o.Kind, o.OccurrenceKey, o.Detail, SnapshotLabel(o.SourceSnapshotId)))];

            var targets = inputs.ScopedById.GetValueOrDefault(u.IdentityKey, []).Select(t =>
            {
                var targetKey = CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, t.IdentityKey);
                var decisionKey = RequirementIdentity.DecisionKey(unscopedKey, targetKey);
                var decision = decisions.GetValueOrDefault(decisionKey);
                var evidence = inputs.Occurrences.GetValueOrDefault(t.Id, []).Where(o => inputs.DeliveryRefs.Contains(o.SourceSnapshotId))
                    .Select(o => new CorrelationEvidence("delivery-requirement", o.OccurrenceKey, $"defined in Delivery snapshot {SnapshotLabel(o.SourceSnapshotId)}", o.SourceSnapshotId)).ToList();
                return new RequirementIdentityTarget(targetKey, Attr(t.AttributesJson, "module") ?? "", Attr(t.AttributesJson, "feature") ?? "", u.IdentityKey, t.DisplayName,
                    evidence, TargetState(decision), decisionKey, decision is null ? null : View(decision));
            }).ToList();
            var decided = decisions.Keys.Any(k => RequirementIdentity.ParseDecisionKey(k)?.Unscoped == unscopedKey);
            // An id an earlier analysis held is asked about only when the latest analysis proposes a feature pack for it, or it was decided before.
            if (!inLatest && targets.Count == 0 && !decided) continue;
            var state = RequirementIdentity.StateOf([.. targets.Select(t => t.State)]);
            var confirmed = state == RequirementIdentityState.Confirmed ? targets.Single(t => t.State == IdentityTargetState.Confirmed) : null;
            var packs = string.Join(", ", targets.Select(t => $"{t.Module}/{t.Feature}"));
            var explanation = state switch
            {
                RequirementIdentityState.Confirmed => $"Confirmed as {confirmed!.Module}/{confirmed.Feature}/{u.IdentityKey} by {confirmed.Decision?.Reviewer}. Scopes and Feature Readiness resolve {u.IdentityKey} to that requirement.",
                RequirementIdentityState.Candidate => $"Only {packs} in the analyzed Delivery snapshot defines {u.IdentityKey}. It is a candidate: nothing resolves it until you confirm it.",
                RequirementIdentityState.Ambiguous => $"{targets.Count} feature packs define {u.IdentityKey} ({packs}). Choose the one this requirement is — there is no default.",
                RequirementIdentityState.Rejected => $"Every proposed feature pack was rejected; {u.IdentityKey} stays unscoped.",
                _ => inputs.DeliveryIncluded
                    ? $"No feature pack of the analyzed Delivery snapshot defines {u.IdentityKey}; it stays unscoped (nothing is matched by the id alone)."
                    : $"The latest analysis has no Delivery snapshot, so no feature pack can be proposed for {u.IdentityKey}. Analyze relationships with a Delivery snapshot.",
            };
            if (!inLatest) explanation += $" (Held by an earlier analysis without a Delivery snapshot; scopes chosen then may still reference {unscopedKey}.)";
            rows.Add(new(unscopedKey, u.IdentityKey, u.DisplayName, state, found, targets, confirmed?.TargetKey, explanation, inLatest));

            // A confirmation whose target the latest analysis no longer proposes is kept in the registry but not applied.
            var proposed = targets.Select(t => t.DecisionKey).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in decisions.Values.Where(d => d.Outcome == nameof(ReviewDecisionOutcome.Confirmed) && !proposed.Contains(d.TargetKey)
                         && RequirementIdentity.ParseDecisionKey(d.TargetKey) is { } k && k.Unscoped == unscopedKey))
                notices.Add($"{u.IdentityKey} was confirmed as {RequirementIdentity.ParseDecisionKey(stale.TargetKey)!.Value.Scoped} by {stale.Reviewer}, but the latest analysis does not propose that feature pack; the decision is kept and not applied.");
        }
        return new(scope, inputs.Latest.Id, inputs.Refs, inputs.DeliveryIncluded,
            [.. rows.OrderBy(r => r.State switch
            {
                RequirementIdentityState.Ambiguous => 0, RequirementIdentityState.Candidate => 1, RequirementIdentityState.Unresolved => 2,
                RequirementIdentityState.Confirmed => 3, _ => 4,
            }).ThenBy(r => r.RequirementId, StringComparer.Ordinal)], notices);
    }

    /// <summary>
    /// Unscoped requirement key → the confirmed scoped key, for the latest analysis. A decision applies only when it is confirmed, it is the only
    /// confirmed target of that id, and its target is still a proposal of the latest analysis (same id, defined by its Delivery snapshot). Candidates,
    /// ambiguous and rejected pairs never resolve.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfirmedRequirementIdentitiesAsync(string scope, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!await db.Decisions.AnyAsync(d => d.Scope == scope && d.TargetKind == nameof(ReviewTargetKind.Identity) && d.Outcome == nameof(ReviewDecisionOutcome.Confirmed)
                && d.SupersededById == null, ct))
            return empty;
        var inputs = await IdentityInputsAsync(scope, ct);
        if (inputs is null) return empty;
        var unscoped = inputs.Unscoped.ToDictionary(e => CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, e.IdentityKey), StringComparer.Ordinal);
        var confirmed = (await CurrentIdentityDecisionsAsync(scope, ct)).Values.Where(d => d.Outcome == nameof(ReviewDecisionOutcome.Confirmed))
            .Select(d => RequirementIdentity.ParseDecisionKey(d.TargetKey)).OfType<(string Unscoped, string Scoped)>()
            .Where(k => unscoped.TryGetValue(k.Unscoped, out var u)
                && inputs.ScopedById.GetValueOrDefault(u.IdentityKey, []).Any(t => CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, t.IdentityKey) == k.Scoped))
            .GroupBy(k => k.Unscoped, StringComparer.Ordinal).Where(g => g.Count() == 1);
        return confirmed.ToDictionary(g => g.Key, g => g.Single().Scoped, StringComparer.Ordinal);
    }

    /// <summary>The identity each key resolves to: the confirmed scoped key for a confirmed unscoped requirement, otherwise the key itself.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveRequirementIdentitiesAsync(string scope, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var confirmed = await ConfirmedRequirementIdentitiesAsync(scope, ct);
        return keys.Distinct(StringComparer.Ordinal).ToDictionary(k => k, k => confirmed.GetValueOrDefault(k) ?? k, StringComparer.Ordinal);
    }

    /// <summary>
    /// Records a decision on one (unscoped → scoped) pair. The target must be a proposal of the latest analysis for that id (a different module,
    /// feature or id is refused, never applied); Confirm and Reject need a rationale; an id can have one confirmed identity at a time.
    /// </summary>
    async Task<ReviewDecisionView> DecideIdentityAsync(string scope, ReviewDecisionRequest request, string reviewer, CancellationToken ct)
    {
        if (RequirementIdentity.ParseDecisionKey(request.TargetKey) is not { } pair)
            throw new CanonicalRequestException("An identity target key is \"identity:Requirement|<id>=>Requirement|<module>/<feature>/<id>\".");
        var inputs = await IdentityInputsAsync(scope, ct) ?? throw new CanonicalRequestException("Analyze relationships first: there is no analysis to decide on.");
        var unscoped = inputs.Unscoped.FirstOrDefault(e => CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, e.IdentityKey) == pair.Unscoped)
            ?? throw new CanonicalRequestException($"{pair.Unscoped} is not an unscoped requirement of this project's latest analysis.");
        var target = inputs.ScopedById.GetValueOrDefault(unscoped.IdentityKey, []).FirstOrDefault(t => CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, t.IdentityKey) == pair.Scoped)
            ?? throw new CanonicalRequestException($"{pair.Scoped} is not a proposed identity for {unscoped.IdentityKey}: only a feature pack of the analyzed Delivery snapshot that defines {unscoped.IdentityKey} can be chosen. Nothing was applied.");
        if (request.Outcome is ReviewDecisionOutcome.Confirmed or ReviewDecisionOutcome.Rejected && string.IsNullOrWhiteSpace(request.Notes))
            throw new CanonicalRequestException("A requirement identity decision needs a rationale: say why this requirement is (or is not) this feature pack's requirement. It changes what every scope resolves.");
        var current = await CurrentIdentityDecisionsAsync(scope, ct);
        if (request.Outcome == ReviewDecisionOutcome.Confirmed && current.Values.FirstOrDefault(d => d.Outcome == nameof(ReviewDecisionOutcome.Confirmed)
                && d.TargetKey != request.TargetKey && RequirementIdentity.ParseDecisionKey(d.TargetKey)?.Unscoped == pair.Unscoped) is { } other)
            throw new CanonicalRequestException($"{unscoped.IdentityKey} is already confirmed as {RequirementIdentity.ParseDecisionKey(other.TargetKey)!.Value.Scoped}. Reject that identity first.");

        var evidence = inputs.Occurrences.GetValueOrDefault(target.Id, []).Where(o => inputs.DeliveryRefs.Contains(o.SourceSnapshotId))
            .Select(o => $"{o.SourceSnapshotId}|{o.OccurrenceKey}").ToList();
        var relational = db.Database.IsRelational();
        await using var tx = relational ? await db.Database.BeginTransactionAsync(ct) : null;
        var now = clock.GetUtcNow();
        var previous = await db.Decisions.Where(d => d.Scope == scope && d.TargetKey == request.TargetKey && d.SupersededById == null).OrderByDescending(d => d.Sequence).FirstOrDefaultAsync(ct);
        var sequence = (await db.Decisions.Where(d => d.Scope == scope).Select(d => (long?)d.Sequence).MaxAsync(ct) ?? 0) + 1;
        var record = new ReviewDecisionRecord
        {
            Id = Guid.NewGuid(), Sequence = sequence, Scope = scope, TargetKind = nameof(ReviewTargetKind.Identity), TargetKey = Trim(request.TargetKey, 2200),
            ProposedJson = JsonSerializer.Serialize(new
            {
                Unscoped = pair.Unscoped, Target = pair.Scoped, Module = Attr(target.AttributesJson, "module"), Feature = Attr(target.AttributesJson, "feature"),
                RequirementId = unscoped.IdentityKey, target.DisplayName, Evidence = evidence,
            }, Json),
            Outcome = request.Outcome.ToString(), Reviewer = reviewer, DecidedAt = now, Notes = request.Notes is null ? null : Trim(request.Notes, 2000),
            EvidenceFingerprint = CanonicalKeys.Fingerprint(evidence), RuleSetVersion = RequirementIdentity.RuleSetVersion, InterpretationId = inputs.Latest.Id, SupersedesId = previous?.Id,
        };
        db.Decisions.Add(record);
        if (previous is not null) previous.SupersededById = record.Id;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return View(record);
    }
}
