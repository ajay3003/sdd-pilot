using System.Text.Json;
using BirkNext.Interpretation;
using BirkNext.Scopes;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// Systems & Scopes reads: the systems and services of the latest interpretation (its frozen membership, never entities an older rule set left
/// behind), the connections the evidence states between them, and a bounded member catalog for scopes. A few set-based queries per call
/// (no query per system). Nothing here is inferred from names: a service is grouped under a system only by a declared or confirmed alias, and
/// a connection between two services exists only when a relation (or an explicit producer/consumer pair of one channel) states it.
/// </summary>
public sealed partial class CanonicalInterpretationService
{
    /// <summary>Upper bound of derived channel connections (producer × consumer pairs) so a hub channel cannot explode the response.</summary>
    public const int MaxChannelConnections = 2000;

    static readonly string[] SystemTypes = [nameof(CanonicalEntityType.System), nameof(CanonicalEntityType.Service)];

    /// <summary>Relations that ask "is this the same thing?" — identity questions, not connections.</summary>
    static readonly HashSet<string> IdentityRelationTypes =
        [nameof(CanonicalRelationType.CandidateSameAs), nameof(CanonicalRelationType.SameAs), nameof(CanonicalRelationType.AliasOf)];

    public async Task<SystemsOverviewView> SystemsOverviewAsync(string scope, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var running = InterpretationRuns.Current(scope);
        var view = await SystemsOverviewCoreAsync(scope, ct);
        return view with { AnalysisRunningSince = running?.Started, AnalysisRunningTrigger = running?.Trigger };
    }

    async Task<SystemsOverviewView> SystemsOverviewCoreAsync(string scope, CancellationToken ct)
    {
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (latest is null)
            return new SystemsOverviewView { Scope = scope, Limitations = ["No interpretation has been run for this Target Environment yet."] };

        var entities = await (from m in db.SnapshotEntities.AsNoTracking()
                              join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                              where m.SnapshotId == latest.Id && (e.EntityType == "System" || e.EntityType == "Service" || e.EntityType == "MessagingChannel")
                              select e).ToListAsync(ct);
        var ids = entities.Select(e => e.Id).ToList();
        var relationIds = await (from m in db.SnapshotRelations.AsNoTracking()
                                 join r in db.Relations.AsNoTracking() on m.RelationId equals r.Id
                                 where m.SnapshotId == latest.Id && ids.Contains(r.SourceEntityId) && ids.Contains(r.TargetEntityId)
                                 select r.Id).ToListAsync(ct);
        var relations = await RelationViewsAsync(scope, db.Relations.AsNoTracking().Where(r => relationIds.Contains(r.Id)), ct);
        var systemIds = entities.Where(e => SystemTypes.Contains(e.EntityType)).Select(e => e.Id).ToList();
        var aliases = await db.Aliases.AsNoTracking().Where(a => a.Scope == scope && systemIds.Contains(a.EntityId)).OrderBy(a => a.Alias).ToListAsync(ct);

        var sources = JsonSerializer.Deserialize<List<BirkNext.Snapshots.SnapshotReference>>(latest.SourceSnapshotsJson, Json) ?? [];
        return SystemsProjection.Build(scope, latest.Id, latest.CreatedAt, [.. sources.Select(s => s.Label ?? s.Id)],
            [.. entities.Select(View)], [.. aliases.Select(View)], relations);
    }

    /// <summary>
    /// A page of one entity type from the latest interpretation (its frozen membership), searched by name or identity. Null when no
    /// interpretation exists (the catalog is unknown, not empty).
    /// </summary>
    public async Task<CanonicalPage<CanonicalEntityView>?> LatestEntitiesAsync(string scope, IReadOnlyList<CanonicalEntityType> types, string? search, int skip, int take,
        CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        var names = types.Select(t => t.ToString()).ToList();
        var query = from m in db.SnapshotEntities.AsNoTracking()
                    join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                    where m.SnapshotId == latest && names.Contains(e.EntityType)
                    select e;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.DisplayName.ToLower().Contains(s) || e.IdentityKey.Contains(s) || e.SourceIdentity.ToLower().Contains(s));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(e => e.DisplayName).ThenBy(e => e.IdentityKey).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        return new([.. rows.Select(View)], total, skip, take);
    }

    /// <summary>
    /// Scenarios that verify these requirements by an established relation of the latest interpretation: Scenario —Verifies→ Requirement that is
    /// explicit, derived or confirmed by a person and not rejected. Candidates and ambiguous links are left out. Null when no interpretation exists.
    /// </summary>
    public async Task<IReadOnlyList<ScopeScenarioLink>?> ScenariosVerifyingAsync(string scope, IReadOnlyCollection<string> requirementKeys, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        var identities = requirementKeys.Where(k => k.StartsWith("Requirement|", StringComparison.Ordinal)).Select(k => k["Requirement|".Length..]).Distinct().ToList();
        if (identities.Count == 0) return [];
        var rows = await (from m in db.SnapshotRelations.AsNoTracking()
                          join r in db.Relations.AsNoTracking() on m.RelationId equals r.Id
                          join s in db.Entities.AsNoTracking() on r.SourceEntityId equals s.Id
                          join t in db.Entities.AsNoTracking() on r.TargetEntityId equals t.Id
                          where m.SnapshotId == latest && r.Active && r.RelationType == "Verifies" && s.EntityType == "Scenario" && t.EntityType == "Requirement"
                                && identities.Contains(t.IdentityKey) && r.ReviewState != "Rejected"
                          select new { Requirement = t.IdentityKey, Scenario = s.IdentityKey, r.Resolution, r.ReviewState }).ToListAsync(ct);
        return [.. rows.Where(r => r.ReviewState == "Confirmed" || r.Resolution is "Explicit" or "Derived" or "Confirmed")
            .Select(r => new ScopeScenarioLink("Requirement|" + r.Requirement, "Scenario|" + r.Scenario,
                r.ReviewState == "Confirmed" ? "confirmed by a person" : r.Resolution == "Explicit" ? "stated by the source" : "derived from the source"))
            .DistinctBy(l => (l.RequirementKey, l.ScenarioKey)).OrderBy(l => l.RequirementKey, StringComparer.Ordinal).ThenBy(l => l.ScenarioKey, StringComparer.Ordinal)];
    }

    /// <summary>Which of these entity keys ("Type|identity") are members of the latest interpretation, with their current names. Null when no
    /// interpretation exists.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> LatestEntityNamesAsync(string scope, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (latest is null) return null;
        var identities = keys.Select(k => k.IndexOf('|') is > 0 and var i ? k[(i + 1)..] : k).Distinct().ToList();
        var rows = await (from m in db.SnapshotEntities.AsNoTracking()
                          join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                          where m.SnapshotId == latest && identities.Contains(e.IdentityKey)
                          select new { e.EntityType, e.IdentityKey, e.DisplayName }).ToListAsync(ct);
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        return rows.Select(r => (Key: $"{r.EntityType}|{r.IdentityKey}", r.DisplayName)).Where(r => wanted.Contains(r.Key))
            .GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);
    }
}

/// <summary>
/// Analyses running in this API instance, per canonical scope (the analysis runs inside one request; there is no shared job store). In memory only:
/// a restart forgets it, and a second instance would not see it.
/// </summary>
public static class InterpretationRuns
{
    public sealed record Run(DateTimeOffset Started, string Trigger);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Run> Running = new(StringComparer.Ordinal);

    public static Run? Current(string scope) => Running.TryGetValue(scope, out var run) ? run : null;

    public static IDisposable Start(string scope, string trigger)
    {
        var run = new Run(DateTimeOffset.UtcNow, trigger);
        Running[scope] = run;
        return new Done(() => Running.TryRemove(new KeyValuePair<string, Run>(scope, run)));
    }

    sealed class Done(Action action) : IDisposable { public void Dispose() => action(); }
}

/// <summary>The pure projection behind <see cref="CanonicalInterpretationService.SystemsOverviewAsync"/> (testable without a database).</summary>
public static class SystemsProjection
{
    public static SystemsOverviewView Build(string scope, Guid interpretationId, DateTimeOffset interpretedAt, List<string> sourceLabels,
        IReadOnlyList<CanonicalEntityView> entities, IReadOnlyList<CanonicalAliasView> aliases, IReadOnlyList<CanonicalRelationView> relations)
    {
        var systems = entities.Where(e => e.Type is CanonicalEntityType.System or CanonicalEntityType.Service).ToList();
        var byId = entities.ToDictionary(e => e.Id);
        var aliasesOf = aliases.GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.ToList());

        // Grouping: a service belongs under a system only when that system's explicit or confirmed alias is the service's own name. Two systems
        // claiming the same service is a conflict: the service stays on its own.
        var claims = new Dictionary<Guid, List<(CanonicalEntityView System, CanonicalAliasView Alias)>>();
        foreach (var system in systems.Where(s => s.Type == CanonicalEntityType.System))
            foreach (var alias in aliasesOf.GetValueOrDefault(system.Id, []).Where(a => a.State is AliasState.Explicit or AliasState.Confirmed))
                foreach (var service in systems.Where(s => s.Type == CanonicalEntityType.Service && CanonicalKeys.Normalize(s.DisplayName) == CanonicalKeys.Normalize(alias.Alias)))
                    (claims.TryGetValue(service.Id, out var list) ? list : claims[service.Id] = []).Add((system, alias));
        var parentOf = claims.Where(c => c.Value.Select(v => v.System.Id).Distinct().Count() == 1)
            .ToDictionary(c => c.Key, c => c.Value[0]);
        var limitations = new List<string>();
        foreach (var conflict in claims.Where(c => c.Value.Select(v => v.System.Id).Distinct().Count() > 1))
            limitations.Add($"{byId[conflict.Key].DisplayName} is declared as part of {string.Join(" and ", conflict.Value.Select(v => v.System.DisplayName).Distinct())}; it is kept on its own until reviewed.");

        var connections = new List<SystemConnectionView>();
        var questions = new List<SystemIdentityQuestion>();
        bool IsSystem(CanonicalEntityView e) => e.Type is CanonicalEntityType.System or CanonicalEntityType.Service;

        foreach (var r in relations.Where(r => IsSystem(r.Source) && IsSystem(r.Target)))
        {
            if (r.Type is CanonicalRelationType.CandidateSameAs or CanonicalRelationType.SameAs or CanonicalRelationType.AliasOf)
            {
                if (r.NeedsReview)
                    questions.Add(new(r.Source.Key, $"Are {r.Source.DisplayName} and {r.Target.DisplayName} the same system? {r.Explanation}", ReviewTargetKind.Relation, r.RelationKey, r.Target.Key));
                continue;
            }
            connections.Add(new SystemConnectionView
            {
                Id = r.RelationKey, SourceKey = r.Source.Key, SourceName = r.Source.DisplayName, TargetKey = r.Target.Key, TargetName = r.Target.DisplayName,
                Label = RelationLabel(r.Type), Kind = r.Type.ToString(), State = StateOf(r), Origin = "Canonical interpretation",
                Evidence = [.. r.Evidence.Select(Describe)], Explanation = r.Explanation, RelationKeys = [r.RelationKey],
                Unverified = "Stated by source evidence; not observed at runtime.",
            });
        }

        var derived = 0;
        var truncated = false;
        // Messaging: a producer and a consumer of the same channel are connected through it. Both relations must state their end explicitly;
        // a rejected end removes the pair, and the weaker end decides the state.
        foreach (var channel in entities.Where(e => e.Type == CanonicalEntityType.MessagingChannel).OrderBy(e => e.DisplayName, StringComparer.Ordinal))
        {
            var producers = relations.Where(r => r.Type == CanonicalRelationType.Produces && r.Target.Id == channel.Id && IsSystem(r.Source)).ToList();
            var consumers = relations.Where(r => r.Type == CanonicalRelationType.Consumes && r.Target.Id == channel.Id && IsSystem(r.Source)).ToList();
            foreach (var p in producers)
                foreach (var c in consumers)
                {
                    if (p.Source.Id == c.Source.Id) continue;
                    if (++derived > CanonicalInterpretationService.MaxChannelConnections) { truncated = true; continue; }
                    var state = Weaker(StateOf(p), StateOf(c));
                    var kind = channel.Attributes.TryGetValue("kind", out var k) ? k : "channel";
                    connections.Add(new SystemConnectionView
                    {
                        Id = $"{p.RelationKey}+{c.RelationKey}", SourceKey = p.Source.Key, SourceName = p.Source.DisplayName, TargetKey = c.Source.Key, TargetName = c.Source.DisplayName,
                        Label = $"sends messages via {channel.DisplayName}", Kind = "Messaging", Via = $"{channel.DisplayName} ({kind})", State = state, Origin = "Canonical interpretation",
                        Evidence = [.. p.Evidence.Concat(c.Evidence).Select(Describe).Distinct()],
                        Explanation = $"{p.Explanation} {c.Explanation}", RelationKeys = [p.RelationKey, c.RelationKey],
                        Unverified = "Both ends are declared in source; no message has been observed flowing between them.",
                    });
                }
        }

        if (truncated) limitations.Add($"Only the first {CanonicalInterpretationService.MaxChannelConnections} message connections are listed.");
        foreach (var system in systems)
            foreach (var alias in aliasesOf.GetValueOrDefault(system.Id, []).Where(a => a.State == AliasState.Proposed))
                questions.Add(new(system.Key, $"Is \"{alias.Alias}\" another name for {system.DisplayName}? Proposed by {alias.Source}.", ReviewTargetKind.Alias,
                    CorrelationPrecedence.AliasTargetKey(system.Key, alias.Alias), null));

        var items = systems.Select(s =>
        {
            var parent = parentOf.TryGetValue(s.Id, out var p) ? p : default;
            var own = aliasesOf.GetValueOrDefault(s.Id, []);
            return new SystemOverviewItem
            {
                Id = s.Id, Key = s.Key, Name = s.DisplayName, Type = s.Type, Basis = s.ResolutionMethod,
                Declaration = own.Where(a => a.State is AliasState.Explicit).Select(a => a.Source).FirstOrDefault(),
                Aliases = [.. own.Where(a => a.State != AliasState.Historical).Select(a => new SystemAliasInfo(a.Alias, a.State, a.Source))],
                ParentKey = parent.System?.Key,
                ParentBasis = parent.System is null ? null : $"{parent.System.DisplayName} declares \"{parent.Alias.Alias}\" as one of its names ({parent.Alias.Source}).",
                ServiceKeys = [.. parentOf.Where(x => x.Value.System.Id == s.Id).Select(x => byId[x.Key].Key).Order(StringComparer.Ordinal)],
                Outgoing = connections.Count(c => c.SourceKey == s.Key && c.State != SystemConnectionState.Rejected),
                Incoming = connections.Count(c => c.TargetKey == s.Key && c.State != SystemConnectionState.Rejected),
                NeedsReview = questions.Count(q => q.SubjectKey == s.Key || q.OtherKey == s.Key)
                    + connections.Count(c => (c.SourceKey == s.Key || c.TargetKey == s.Key) && c.State is SystemConnectionState.NeedsReview or SystemConnectionState.Candidate),
            };
        }).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();

        return new SystemsOverviewView
        {
            Scope = scope, InterpretationId = interpretationId, InterpretedAt = interpretedAt, SourceLabels = sourceLabels, Systems = items,
            Connections = [.. connections.OrderBy(c => c.SourceName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.TargetName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)],
            IdentityQuestions = questions, Sources = ["Canonical interpretation: used"], Limitations = limitations,
        };
    }

    public static SystemConnectionState StateOf(CanonicalRelationView r) => r.ReviewState switch
    {
        RelationReviewState.Confirmed when r.Counts => SystemConnectionState.ConfirmedByPerson,
        RelationReviewState.Rejected => SystemConnectionState.Rejected,
        RelationReviewState.Conflict or RelationReviewState.RevalidationRequired => SystemConnectionState.NeedsReview,
        _ when r.Counts => SystemConnectionState.FromSourceEvidence,
        _ when r.Resolution == RelationResolution.Ambiguous => SystemConnectionState.NeedsReview,
        _ when r.Effective == RelationResolution.Rejected => SystemConnectionState.Rejected,
        _ => SystemConnectionState.Candidate,
    };

    /// <summary>The less certain of two states (a derived connection is never stronger than its weaker end).</summary>
    public static SystemConnectionState Weaker(SystemConnectionState a, SystemConnectionState b)
    {
        static int Rank(SystemConnectionState s) => s switch
        {
            SystemConnectionState.Rejected => 0, SystemConnectionState.Candidate => 1, SystemConnectionState.NeedsReview => 2,
            SystemConnectionState.FromSourceEvidence => 3, SystemConnectionState.ConfirmedByPerson => 4, _ => 5,
        };
        return Rank(a) <= Rank(b) ? a : b;
    }

    static string RelationLabel(CanonicalRelationType type) => type switch
    {
        CanonicalRelationType.DependsOn => "depends on",
        CanonicalRelationType.Consumes => "consumes",
        CanonicalRelationType.Produces => "produces for",
        CanonicalRelationType.Uses => "uses",
        CanonicalRelationType.Contains => "contains",
        CanonicalRelationType.BelongsTo => "belongs to",
        CanonicalRelationType.References => "references",
        CanonicalRelationType.Implements => "implements",
        CanonicalRelationType.Exposes => "exposes",
        _ => type.ToString().ToLowerInvariant(),
    };

    static string Describe(CorrelationEvidence e) => string.IsNullOrWhiteSpace(e.Detail) ? $"{e.Kind}: {e.Reference}" : $"{e.Kind}: {e.Reference} ({e.Detail})";
}
