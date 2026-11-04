using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>Graph impact analysis (rule set impact/1): bounded, cycle-safe, server-side; every impacted entity has its relation path.</summary>
public sealed partial class CanonicalInterpretationService
{
    /// <summary>
    /// What depends on <paramref name="entityKey"/> ("Type|identity"): breadth-first over active, non-rejected relations in their impact direction
    /// (<see cref="CanonicalImpactRules.Reverse"/> / <see cref="CanonicalImpactRules.Forward"/>), at most <see cref="CanonicalImpactRules.MaxDepth"/>
    /// hops and <see cref="CanonicalImpactRules.MaxEntities"/> entities. A path through a non-established relation is Potential; unresolved
    /// relations are counted as Unknown. Tests are impacted only through VERIFIES. Null when the entity is not in the scope.
    /// </summary>
    public async Task<CanonicalImpactView?> ImpactAsync(string scope, string entityKey, CancellationToken ct, int maxDepth = CanonicalImpactRules.MaxDepth)
    {
        scope = ValidScope(scope);
        var sep = entityKey.IndexOf('|');
        if (sep <= 0 || !Enum.TryParse<CanonicalEntityType>(entityKey[..sep], out var rootType)) return null;
        var identity = entityKey[(sep + 1)..];
        var root = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Scope == scope && e.EntityType == rootType.ToString() && e.IdentityKey == identity, ct);
        if (root is null) return null;

        var relations = await db.Relations.AsNoTracking()
            .Where(r => r.Scope == scope && r.Active && r.ReviewState != nameof(RelationReviewState.Rejected))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId, r.RelationType, r.Resolution, r.ReviewState }).ToListAsync(ct);
        var entities = await db.Entities.AsNoTracking().Where(e => e.Scope == scope)
            .Select(e => new { e.Id, e.EntityType, e.IdentityKey, e.DisplayName }).ToDictionaryAsync(e => e.Id, ct);
        static bool Established(string resolution, string review) =>
            review == nameof(RelationReviewState.Confirmed) || resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived) or nameof(RelationResolution.Confirmed);

        // Impact edges: from the entity that changes to the entity that is impacted.
        var edges = new Dictionary<Guid, List<(Guid To, string Relation, bool Reverse, string Resolution, bool Established)>>();
        void Edge(Guid from, Guid to, string relation, bool reverse, string resolution, bool established)
        {
            if (!edges.TryGetValue(from, out var list)) edges[from] = list = [];
            list.Add((to, relation, reverse, resolution, established));
        }
        foreach (var r in relations)
        {
            if (!Enum.TryParse<CanonicalRelationType>(r.RelationType, out var type)) continue;
            var established = Established(r.Resolution, r.ReviewState);
            if (CanonicalImpactRules.Reverse.Contains(type)) Edge(r.TargetEntityId, r.SourceEntityId, r.RelationType, true, r.Resolution, established);
            else if (CanonicalImpactRules.Forward.Contains(type)) Edge(r.SourceEntityId, r.TargetEntityId, r.RelationType, false, r.Resolution, established);
        }

        var impacted = new Dictionary<Guid, ImpactedEntity>();
        var tests = new Dictionary<Guid, CanonicalImpactedTest>();
        var unknown = 0;
        var truncated = false;
        var frontier = new List<(Guid Id, bool AllEstablished, List<ImpactPathStep> Path)> { (root.Id, true, []) };
        var visited = new HashSet<Guid> { root.Id };
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && !truncated; depth++)
        {
            var next = new List<(Guid, bool, List<ImpactPathStep>)>();
            foreach (var (id, allEstablished, path) in frontier)
                foreach (var e in edges.GetValueOrDefault(id) ?? [])
                {
                    if (!entities.TryGetValue(e.To, out var target)) continue;
                    if (e.Resolution == nameof(RelationResolution.Unresolved)) { unknown++; continue; }
                    var step = new ImpactPathStep(e.Relation, e.Reverse, $"{target.EntityType}|{target.IdentityKey}", target.EntityType, target.DisplayName, e.Resolution);
                    // Containment carries definite impact only down from the changed entity itself: an impacted service does not make all of its
                    // operations impacted, so below the root a Contains / Exposes / Produces step is Potential.
                    var established = allEstablished && e.Established && (e.Reverse || id == root.Id);
                    if (target.EntityType == nameof(CanonicalEntityType.Test))
                    {
                        // A test is impacted only through the relation that verifies the changed (or impacted) entity.
                        if (e.Relation != nameof(CanonicalRelationType.Verifies)) continue;
                        var level = established ? RetestLevel.MustRetest : RetestLevel.ShouldRetest;
                        var why = string.Join(" → ", path.Select(p => p.EntityName).Prepend(root.DisplayName)) + $" ← verified by {target.DisplayName}"
                                  + (e.Established ? "" : " (candidate link)");
                        if (!tests.TryGetValue(e.To, out var existing) || existing.Retest == RetestLevel.ShouldRetest && level == RetestLevel.MustRetest)
                            tests[e.To] = new CanonicalImpactedTest(step.EntityKey, target.DisplayName, level, why);
                        continue;
                    }
                    if (!visited.Add(e.To)) continue;
                    if (impacted.Count >= CanonicalImpactRules.MaxEntities) { truncated = true; break; }
                    var stepPath = new List<ImpactPathStep>(path) { step };
                    impacted[e.To] = new ImpactedEntity(step.EntityKey, target.EntityType, target.DisplayName,
                        !established ? ImpactLevel.Potential : depth == 1 ? ImpactLevel.Direct : ImpactLevel.Downstream, depth, stepPath);
                    next.Add((e.To, established, stepPath));
                }
            frontier = next;
        }

        // Test gaps: impacted (or root) requirements / stories / scenarios that nothing verifies.
        var verified = relations.Where(r => r.RelationType == nameof(CanonicalRelationType.Verifies)).Select(r => r.TargetEntityId).ToHashSet();
        var testable = impacted.Where(kv => CanonicalImpactRules.Testable.Contains(kv.Value.Type) && !verified.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (CanonicalImpactRules.Testable.Contains(root.EntityType) && !verified.Contains(root.Id))
            testable.Insert(0, new ImpactedEntity($"{root.EntityType}|{root.IdentityKey}", root.EntityType, root.DisplayName, ImpactLevel.Direct, 0, []));

        var limitations = new List<string>();
        if (truncated) limitations.Add($"Bounded to {CanonicalImpactRules.MaxEntities} impacted entities; narrow the starting point to see the rest.");
        if (unknown > 0) limitations.Add($"{unknown} unresolved relation(s) were met: their impact is unknown and not guessed.");
        return new CanonicalImpactView
        {
            Scope = scope, RootKey = entityKey, RootName = root.DisplayName, RootType = root.EntityType,
            Entities = [.. impacted.Values.OrderBy(e => e.Level).ThenBy(e => e.Depth).ThenBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)],
            Tests = [.. tests.Values.OrderBy(t => t.Retest).ThenBy(t => t.Name, StringComparer.Ordinal)],
            MissingTests = testable, Unknown = unknown, Truncated = truncated, Limitations = limitations,
        };
    }
}
