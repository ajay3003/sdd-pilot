using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>Canonical functional scopes for scoped views (Delivery first): one bounded query, never the whole graph.</summary>
public sealed partial class CanonicalInterpretationService
{
    /// <summary>The canonical scope (project key) that interpreted one of these Delivery snapshots, preferring the earliest id given; null when
    /// none was interpreted.</summary>
    public async Task<string?> ScopeOfDeliveryAsync(IReadOnlyList<Guid> deliverySnapshotIds, CancellationToken ct)
    {
        var refs = deliverySnapshotIds.Select(i => $"delivery:{i}").ToList();
        if (refs.Count == 0) return null;
        var rows = await db.Occurrences.AsNoTracking().Where(o => refs.Contains(o.SourceSnapshotId)).Select(o => new { o.Scope, o.SourceSnapshotId }).Distinct().ToListAsync(ct);
        return refs.Select(r => rows.Where(x => x.SourceSnapshotId == r).Select(x => x.Scope).Order(StringComparer.Ordinal).FirstOrDefault()).FirstOrDefault(s => s is not null);
    }

    /// <summary>
    /// Capabilities and change intents the given features implement. Strict whitelist: Feature —Implements→ Capability only (no dependency
    /// relation ever adds a member). Established relations (explicit, derived, confirmed) are members; other non-rejected relations are candidates.
    /// </summary>
    public async Task<IReadOnlyList<CanonicalFunctionalScope>> FunctionalScopesAsync(string scope, IReadOnlyCollection<string> featureIdentities, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var wanted = featureIdentities.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return [];
        var rows = await (from r in db.Relations.AsNoTracking()
                          join f in db.Entities.AsNoTracking() on r.SourceEntityId equals f.Id
                          join c in db.Entities.AsNoTracking() on r.TargetEntityId equals c.Id
                          where r.Scope == scope && r.Active && r.RelationType == nameof(CanonicalRelationType.Implements)
                                && f.EntityType == nameof(CanonicalEntityType.Feature) && c.EntityType == nameof(CanonicalEntityType.Capability)
                                && r.ReviewState != nameof(RelationReviewState.Rejected)
                          select new { Feature = f.IdentityKey, Capability = c.IdentityKey, c.DisplayName, c.AttributesJson, r.Resolution, r.ReviewState, r.RuleId, r.Explanation })
            .ToListAsync(ct);
        return [.. rows.Where(r => wanted.Contains(r.Feature)).GroupBy(r => r.Capability, StringComparer.Ordinal).Select(g =>
        {
            var first = g.First();
            var kind = Attr(first.AttributesJson, "kind") == "change-intent" ? FunctionalScopeKind.ChangeIntent : FunctionalScopeKind.Capability;
            var members = new List<CanonicalScopeMember>();
            var candidates = new List<CanonicalScopeMember>();
            foreach (var r in g.OrderBy(r => r.Feature, StringComparer.Ordinal))
            {
                // A person's confirmation is recorded as the review state (the rule's own resolution stays as evidence of how it was proposed).
                var confirmed = r.ReviewState == nameof(RelationReviewState.Confirmed) || r.Resolution == nameof(RelationResolution.Confirmed);
                var established = confirmed || r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived);
                var how = confirmed ? "confirmed by a person" : r.Resolution switch
                {
                    nameof(RelationResolution.Derived) => "declared in the feature",
                    nameof(RelationResolution.Explicit) => "explicit",
                    _ => "candidate — awaiting review",
                };
                (established ? members : candidates).Add(new(r.Feature, r.Resolution, r.RuleId, $"Implements → {first.DisplayName} ({how})", r.Explanation));
            }
            var detail = kind == FunctionalScopeKind.ChangeIntent ? $"Change intent from {Attr(first.AttributesJson, "decision") ?? "a shared decision"}" : "Canonical capability";
            return new CanonicalFunctionalScope($"capability:{g.Key}", first.DisplayName, kind, detail, members, candidates);
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
