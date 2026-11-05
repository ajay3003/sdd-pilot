using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// The shared review queue and the end-to-end canonical trace. Both read the same scoped relations every consumer reads; nothing here re-derives an
/// interpretation. Decisions are recorded with <see cref="DecideAsync"/> (one registry), so a question answered here is answered for every surface.
/// </summary>
public sealed partial class CanonicalInterpretationService
{
    /// <summary>
    /// One attribute of an entity's stored attributes. Attributes are jsonb: never string-match them (Postgres normalises the text, e.g. "key": "value",
    /// and LIKE does not apply to jsonb); parse and compare values.
    /// </summary>
    internal static string? Attr(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    static string? Relationship(string json) => Attr(json, "relationship");

    // ---------------------------------------------------------------- shared review queue

    /// <summary>
    /// Every open question in the scope, one item per question: relations needing a decision or offered as a suggestion, proposed aliases (conflicting
    /// declarations are asked once, as their relation), and tasks whose story reference names nothing (fixed in the source). Ranked, filtered, paged.
    /// </summary>
    public async Task<ReviewQueueView> ReviewQueueAsync(string scope, ReviewItemKind? kind, ReviewItemType? type, int skip, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id, s.CreatedAt }).FirstOrDefaultAsync(ct);
        // Ambiguous relations are read whatever their review state: an alternative already chosen closes its whole group.
        var relationRows = await db.Relations.AsNoTracking()
            .Where(r => r.Scope == scope && r.Active && (r.Resolution == "Ambiguous" || (r.ReviewState != "Confirmed" && r.ReviewState != "Rejected"
                && (r.ReviewState == "Conflict" || r.ReviewState == "RevalidationRequired" || r.Resolution == "Candidate" || r.Resolution == "Unresolved"))))
            .Select(r => new { r.Id, r.RuleId, r.Resolution, r.ReviewState, r.RelationKey, r.SourceEntityId, r.TargetEntityId }).ToListAsync(ct);
        var aliasRows = await db.Aliases.AsNoTracking().Where(a => a.Scope == scope && a.State == "Proposed" && a.RuleId != CorrelationRules.AliasConflictRule)
            .Select(a => new { a.Id, a.Alias }).ToListAsync(ct);
        var taskRows = latest is null ? [] : (await (from m in db.SnapshotEntities.AsNoTracking()
                                                     join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                                                     where m.SnapshotId == latest.Id && e.EntityType == "Task"
                                                     select new { e.Id, e.IdentityKey, e.AttributesJson }).ToListAsync(ct))
            .Where(t => Relationship(t.AttributesJson) == "UnresolvedReference").ToList();

        var all = new List<(string Id, ReviewItemKind Kind, ReviewItemType Type, int Priority, string Order, Guid Ref, char Source)>();
        var groupMembers = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var closedGroups = relationRows.Where(r => r.Resolution == "Ambiguous" && r.ReviewState == "Confirmed")
            .Select(r => CanonicalReviewQueue.AmbiguityGroup(r.RuleId, r.SourceEntityId, r.TargetEntityId)).ToHashSet(StringComparer.Ordinal);
        foreach (var r in relationRows.OrderBy(r => r.RelationKey, StringComparer.Ordinal))
        {
            var review = Enum.Parse<RelationReviewState>(r.ReviewState);
            if (CanonicalReviewQueue.Classify(r.RuleId, Enum.Parse<RelationResolution>(r.Resolution), review, true) is not { } c) continue;
            if (r.Resolution == "Ambiguous" && review is RelationReviewState.NotReviewed or RelationReviewState.Deferred)
            {
                // One question per ambiguity: its alternatives become the item's candidates.
                var group = CanonicalReviewQueue.AmbiguityGroup(r.RuleId, r.SourceEntityId, r.TargetEntityId);
                if (closedGroups.Contains(group)) continue;
                if (groupMembers.TryGetValue(group, out var members)) { members.Add(r.Id); continue; }
                groupMembers[group] = [r.Id];
                all.Add(($"ambiguity:{group}", c.Kind, c.Type, c.Priority, r.RelationKey, r.Id, 'g'));
                continue;
            }
            all.Add(($"relation:{r.Id}", c.Kind, c.Type, c.Priority, r.RelationKey, r.Id, 'r'));
        }
        foreach (var a in aliasRows) all.Add(($"alias:{a.Id}", ReviewItemKind.Suggestion, ReviewItemType.AliasConfirmation, 9, a.Alias, a.Id, 'a'));
        foreach (var t in taskRows) all.Add(($"task:{t.Id}", ReviewItemKind.Decision, ReviewItemType.StoryReference, 4, t.IdentityKey, t.Id, 't'));

        var decisions = all.Where(i => i.Kind == ReviewItemKind.Decision).ToList();
        var suggestions = all.Where(i => i.Kind == ReviewItemKind.Suggestion).ToList();
        var filtered = all.Where(i => (kind is null || i.Kind == kind) && (type is null || i.Type == type))
            .OrderBy(i => i.Priority).ThenBy(i => i.Order, StringComparer.Ordinal).ToList();
        take = Math.Clamp(take, 1, 100);
        var page = filtered.Skip(Math.Max(0, skip)).Take(take).ToList();

        var relationIds = page.Where(i => i.Source == 'r').Select(i => i.Ref)
            .Concat(page.Where(i => i.Source == 'g').SelectMany(i => groupMembers[i.Id["ambiguity:".Length..]])).ToList();
        var relationViews = (await RelationViewsAsync(scope, db.Relations.AsNoTracking().Where(r => relationIds.Contains(r.Id)), ct)).ToDictionary(r => r.Id);
        var aliasIds = page.Where(i => i.Source == 'a').Select(i => i.Ref).ToList();
        var aliases = await db.Aliases.AsNoTracking().Where(a => aliasIds.Contains(a.Id)).ToListAsync(ct);
        var subjectIds = page.Where(i => i.Source == 't').Select(i => i.Ref).Concat(aliases.Select(a => a.EntityId)).ToList();
        var subjects = (await db.Entities.AsNoTracking().Where(e => subjectIds.Contains(e.Id)).ToListAsync(ct)).ToDictionary(e => e.Id);
        var evaluated = latest?.CreatedAt ?? clock.GetUtcNow();

        var items = new List<ReviewItemView>();
        foreach (var i in page)
        {
            switch (i.Source)
            {
                case 'g' when relationViews.TryGetValue(i.Ref, out var first):
                    var candidates = groupMembers[i.Id["ambiguity:".Length..]].Select(id => relationViews.GetValueOrDefault(id)).OfType<CanonicalRelationView>().ToList();
                    items.Add(new(i.Id, i.Kind, i.Type, i.Priority, candidates.Count > 1 ? $"{first.Explanation} ({candidates.Count} alternatives)" : first.Explanation,
                        ReviewTargetKind.Relation, first.RelationKey, first, null, null,
                        CanonicalReviewQueue.AffectedConsumers([.. candidates.SelectMany(x => new[] { x.Source.Type, x.Target.Type })]), first.RuleId, first.RuleSetVersion,
                        "Ambiguous", latest?.Id, evaluated, null, candidates));
                    break;
                case 'r' when relationViews.TryGetValue(i.Ref, out var r):
                    items.Add(new(i.Id, i.Kind, i.Type, i.Priority, r.Explanation, ReviewTargetKind.Relation, r.RelationKey, r, null, null,
                        CanonicalReviewQueue.AffectedConsumers(r.Source.Type, r.Target.Type), r.RuleId, r.RuleSetVersion, r.ReviewState == RelationReviewState.NotReviewed ? r.Resolution.ToString() : r.ReviewState.ToString(),
                        latest?.Id, evaluated, null));
                    break;
                case 'a':
                    var alias = aliases.First(a => a.Id == i.Ref);
                    if (!subjects.TryGetValue(alias.EntityId, out var owner)) break;
                    var ownerView = View(owner);
                    items.Add(new(i.Id, i.Kind, i.Type, i.Priority, $"\"{alias.Alias}\" may be another name for {owner.DisplayName} ({alias.Source}). A matching abbreviation is not proof.",
                        ReviewTargetKind.Alias, CorrelationPrecedence.AliasTargetKey(ownerView.Key, alias.Alias), null, View(alias), ownerView,
                        CanonicalReviewQueue.AffectedConsumers(ownerView.Type), alias.RuleId, null, "Proposed", latest?.Id, evaluated, null));
                    break;
                case 't' when subjects.TryGetValue(i.Ref, out var task):
                    var view = View(task);
                    items.Add(new(i.Id, i.Kind, i.Type, i.Priority,
                        $"Task {view.Attributes.GetValueOrDefault("id")} names {view.Attributes.GetValueOrDefault("storyReference") ?? "a story"}, which the feature's specification does not define.",
                        null, null, null, null, view, CanonicalReviewQueue.AffectedConsumers(CanonicalEntityType.Task), view.Attributes.GetValueOrDefault("workItemRule") ?? "WI-UNRESOLVED",
                        WorkItemInterpreter.RuleSetVersion, "Unresolved", latest?.Id, evaluated, CanonicalReviewQueue.StoryReferenceResolution));
                    break;
            }
        }
        return new(decisions.Count, suggestions.Count,
            decisions.GroupBy(i => i.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            suggestions.GroupBy(i => i.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            new(items, filtered.Count, skip, take), latest?.Id);
    }

    // ---------------------------------------------------------------- end-to-end trace

    static readonly string[] WorkItemTypes = ["Requirement", "UserStory", "Scenario", "FunctionalGroup", "ScenarioGroup", "SharedFoundationGroup", "Task"];

    /// <summary>Scope-wide trace counts from active relations: what is written, what is implemented, what is tested — separately.</summary>
    public async Task<TraceSummaryView> TraceSummaryAsync(string scope, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id }).FirstOrDefaultAsync(ct);
        var entities = await db.Entities.AsNoTracking().Where(e => e.Scope == scope && (WorkItemTypes.Contains(e.EntityType) || e.EntityType == "Feature"))
            .Select(e => new { e.Id, e.EntityType, e.AttributesJson }).ToListAsync(ct);
        // Only work items some active relation still holds (an entity left by an older interpretation is not counted).
        var ids = entities.Select(e => e.Id).ToList();
        var relations = await db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.Active && (ids.Contains(r.SourceEntityId) || ids.Contains(r.TargetEntityId)))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId, r.RelationType, r.Resolution, r.ReviewState, r.RuleId }).ToListAsync(ct);
        var live = relations.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).ToHashSet();
        var current = entities.Where(e => live.Contains(e.Id)).ToList();
        var byId = current.ToDictionary(e => e.Id);
        int Count(string type) => current.Count(e => e.EntityType == type);
        bool Established(string resolution, string review) => review == "Confirmed" || resolution is "Explicit" or "Derived" or "Confirmed";

        var requirements = current.Where(e => e.EntityType == "Requirement" && Attr(e.AttributesJson, "module") is not null).Select(e => e.Id).ToHashSet();
        var verifies = relations.Where(r => r.RelationType == "Verifies" && requirements.Contains(r.TargetEntityId) && r.ReviewState != "Rejected").ToList();
        var explicitTested = verifies.Where(r => Established(r.Resolution, r.ReviewState)).Select(r => r.TargetEntityId).ToHashSet();
        var candidateTested = verifies.Select(r => r.TargetEntityId).Where(id => !explicitTested.Contains(id)).ToHashSet();
        var tasks = current.Where(e => e.EntityType == "Task").Select(e => (e.Id, Relationship: Relationship(e.AttributesJson))).Where(t => t.Relationship is not null).ToList();
        var implemented = relations.Where(r => r.RelationType == "References" && r.RuleId == CorrelationRules.TaskSymbolRule && Established(r.Resolution, r.ReviewState))
            .Select(r => r.SourceEntityId).ToHashSet();
        var limitations = new List<string>();
        if (current.Count == 0) limitations.Add("No feature packs are interpreted yet: interpret a Delivery snapshot together with the source snapshots (Relationship Review).");
        if (tasks.Count > 0 && implemented.Count == 0)
            limitations.Add("No task names a source type of its module yet (or the source was analyzed before declared types were recorded): implementation evidence is not linked.");
        // A feature pack counts when it contains work items (the same rule the feature list uses), so the two numbers always agree.
        var workItemIds = current.Where(e => e.EntityType != "Feature").Select(e => e.Id).ToHashSet();
        var features = relations.Where(r => r.RelationType == "Contains" && byId.TryGetValue(r.SourceEntityId, out var f) && f.EntityType == "Feature" && workItemIds.Contains(r.TargetEntityId))
            .Select(r => r.SourceEntityId).Distinct().Count();
        return new(latest?.Id, features, requirements.Count, Count("UserStory"), Count("Scenario"), Count("FunctionalGroup"), Count("ScenarioGroup"), tasks.Count,
            tasks.Count(t => t.Relationship == "SharedFoundational"), tasks.Count(t => implemented.Contains(t.Id)),
            explicitTested.Count, candidateTested.Count, requirements.Count - explicitTested.Count - candidateTested.Count,
            tasks.Count(t => t.Relationship == "UnresolvedReference"), tasks.Count(t => t.Relationship == "RemovedStoryReference"), limitations);
    }

    /// <summary>Feature packs with their trace counts (one row per feature; detail is loaded per feature).</summary>
    public async Task<IReadOnlyList<TraceFeatureRow>> TraceFeaturesAsync(string scope, string? module, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var features = await db.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == "Feature").ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(module)) features = [.. features.Where(f => f.IdentityKey.StartsWith(CanonicalKeys.Normalize(module) + "/", StringComparison.Ordinal))];
        var featureIds = features.Select(f => f.Id).ToList();
        var contains = await db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.Active && r.RelationType == "Contains" && featureIds.Contains(r.SourceEntityId))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId }).ToListAsync(ct);
        var childIds = contains.Select(c => c.TargetEntityId).ToList();
        var children = (await db.Entities.AsNoTracking().Where(e => childIds.Contains(e.Id)).Select(e => new { e.Id, e.EntityType, e.AttributesJson }).ToListAsync(ct)).ToDictionary(e => e.Id);
        var links = await db.Relations.AsNoTracking().Where(r => r.Scope == scope && r.Active && r.ReviewState != "Rejected"
                && ((r.RelationType == "Verifies" && childIds.Contains(r.TargetEntityId)) || (r.RuleId == CorrelationRules.TaskSymbolRule && childIds.Contains(r.SourceEntityId))))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId, r.RelationType, r.Resolution, r.ReviewState }).ToListAsync(ct);
        static bool Established(string resolution, string review) => review == "Confirmed" || resolution is "Explicit" or "Derived" or "Confirmed";
        var tested = links.Where(l => l.RelationType == "Verifies" && Established(l.Resolution, l.ReviewState)).Select(l => l.TargetEntityId).ToHashSet();
        var anyTest = links.Where(l => l.RelationType == "Verifies").Select(l => l.TargetEntityId).ToHashSet();
        var implemented = links.Where(l => l.RelationType == "References" && Established(l.Resolution, l.ReviewState)).Select(l => l.SourceEntityId).ToHashSet();
        var rows = new List<TraceFeatureRow>();
        foreach (var f in features.Where(f => contains.Any(c => c.SourceEntityId == f.Id && children.TryGetValue(c.TargetEntityId, out var ch) && WorkItemTypes.Contains(ch.EntityType)))
                     .OrderBy(f => f.IdentityKey, StringComparer.Ordinal))
        {
            var reqs = contains.Where(c => c.SourceEntityId == f.Id && children.TryGetValue(c.TargetEntityId, out var e) && e.EntityType == "Requirement").Select(c => c.TargetEntityId).ToList();
            var tasks = contains.Where(c => c.SourceEntityId == f.Id && children.TryGetValue(c.TargetEntityId, out var e) && e.EntityType == "Task").Select(c => children[c.TargetEntityId]).ToList();
            var workItems = contains.Count(c => c.SourceEntityId == f.Id && children.TryGetValue(c.TargetEntityId, out var e)
                && e.EntityType is "UserStory" or "Scenario" or "FunctionalGroup" or "ScenarioGroup");
            rows.Add(new(View(f), reqs.Count, workItems, tasks.Count, tasks.Count(t => implemented.Contains(t.Id)), reqs.Count(tested.Contains), reqs.Count(r => !anyTest.Contains(r)),
                tasks.Count(t => Relationship(t.AttributesJson) is "UnresolvedReference" or "RemovedStoryReference")));
        }
        return rows;
    }

    /// <summary>
    /// One feature's chain: requirements with their tests; each work item (user story, scenario, functional/scenario group, shared work, unresolved) with
    /// its tasks; each task with the source types it references, the API operations those types' members implement, contracts they reference, and the
    /// tests of those operations. Every link carries its relation (state, rule, evidence). Missing links are reported as gaps, never filled in.
    /// </summary>
    public async Task<TraceFeatureDetail?> TraceFeatureAsync(string scope, Guid featureId, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var feature = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Scope == scope && e.Id == featureId && e.EntityType == "Feature", ct);
        if (feature is null) return null;
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

        async Task<List<CanonicalRelationView>> Rel(IQueryable<CanonicalRelationRecord> q) =>
            [.. (await RelationViewsAsync(scope, q.Where(r => r.Scope == scope && r.Active && r.ReviewState != "Rejected").Take(2000), ct))];

        var contains = await Rel(db.Relations.AsNoTracking().Where(r => r.SourceEntityId == featureId && r.RelationType == "Contains"));
        var childIds = contains.Select(c => c.Target.Id).ToList();
        var requirements = contains.Where(c => c.Target.Type == CanonicalEntityType.Requirement).Select(c => c.Target).OrderBy(r => r.IdentityKey, StringComparer.Ordinal).ToList();
        var tasks = contains.Where(c => c.Target.Type == CanonicalEntityType.Task).Select(c => c.Target).ToList();
        var taskIds = tasks.Select(t => t.Id).ToList();
        var reqIds = requirements.Select(r => r.Id).ToList();

        var reqTests = await Rel(db.Relations.AsNoTracking().Where(r => r.RelationType == "Verifies" && reqIds.Contains(r.TargetEntityId)));
        var taskLinks = await Rel(db.Relations.AsNoTracking().Where(r => taskIds.Contains(r.SourceEntityId) && (r.RelationType == "BelongsTo" || r.RelationType == "References")));
        var symbolIds = taskLinks.Where(l => l.Target.Type == CanonicalEntityType.SourceSymbol).Select(l => l.Target.Id).Distinct().ToList();
        var symbolLinks = await Rel(db.Relations.AsNoTracking().Where(r => symbolIds.Contains(r.SourceEntityId) && (r.RelationType == "Contains" || r.RelationType == "References" || r.RelationType == "Implements")));
        var memberIds = symbolLinks.Where(l => l.Type == CanonicalRelationType.Contains && l.Target.Type == CanonicalEntityType.SourceSymbol).Select(l => l.Target.Id).ToList();
        var memberLinks = await Rel(db.Relations.AsNoTracking().Where(r => memberIds.Contains(r.SourceEntityId) && r.RelationType == "Implements"));
        var operationIds = symbolLinks.Concat(memberLinks).Where(l => l.Target.Type == CanonicalEntityType.ApiOperation).Select(l => l.Target.Id).Distinct().ToList();
        var operationTests = await Rel(db.Relations.AsNoTracking().Where(r => r.RelationType == "Verifies" && operationIds.Contains(r.TargetEntityId)));

        TraceTask TaskChain(CanonicalEntityView task)
        {
            var implementation = taskLinks.Where(l => l.Source.Id == task.Id && l.Target.Type == CanonicalEntityType.SourceSymbol).Select(l => new TraceLink(l.Target, l)).ToList();
            var symbols = implementation.Select(i => i.Entity.Id).ToHashSet();
            var members = symbolLinks.Where(l => symbols.Contains(l.Source.Id) && l.Type == CanonicalRelationType.Contains).Select(l => l.Target.Id).Concat(symbols).ToHashSet();
            var operations = symbolLinks.Concat(memberLinks).Where(l => members.Contains(l.Source.Id) && l.Target.Type == CanonicalEntityType.ApiOperation)
                .DistinctBy(l => l.Target.Id).Select(l => new TraceLink(l.Target, l)).ToList();
            var contracts = symbolLinks.Where(l => symbols.Contains(l.Source.Id) && l.Target.Type is CanonicalEntityType.ContractVersion or CanonicalEntityType.Contract)
                .Select(l => new TraceLink(l.Target, l)).ToList();
            var ops = operations.Select(o => o.Entity.Id).ToHashSet();
            var tests = operationTests.Where(l => ops.Contains(l.Target.Id)).Select(l => new TraceLink(l.Source, l)).ToList();
            return new(task, implementation, operations, contracts, tests);
        }

        var workItems = new List<TraceWorkItem>();
        var itemTypes = new[] { CanonicalEntityType.UserStory, CanonicalEntityType.Scenario, CanonicalEntityType.FunctionalGroup, CanonicalEntityType.ScenarioGroup };
        var grouped = new HashSet<Guid>();
        foreach (var item in contains.Where(c => itemTypes.Contains(c.Target.Type)).Select(c => c.Target).OrderBy(i => i.Type).ThenBy(i => i.IdentityKey, StringComparer.Ordinal))
        {
            // Tasks that belong to it, and tasks that still name it after the specification removed it (shown under the removed story, needing review).
            var mine = taskLinks.Where(l => l.Target.Id == item.Id && (l.Type == CanonicalRelationType.BelongsTo || l.Resolution == RelationResolution.Unresolved))
                .Select(l => l.Source).DistinctBy(t => t.Id).OrderBy(t => t.IdentityKey, StringComparer.Ordinal).ToList();
            grouped.UnionWith(mine.Select(t => t.Id));
            var kind = item.Type switch
            {
                CanonicalEntityType.UserStory => item.Attributes.ContainsKey("removed") ? "User story (removed)" : "User story",
                CanonicalEntityType.Scenario => "Acceptance scenario",
                CanonicalEntityType.FunctionalGroup => "Functional group",
                _ => "Scenario group",
            };
            workItems.Add(new(item, kind, [.. mine.Select(TaskChain)]));
        }
        var shared = taskLinks.Where(l => l.Type == CanonicalRelationType.BelongsTo && l.Target.Type == CanonicalEntityType.SharedFoundationGroup).Select(l => l.Source).ToList();
        grouped.UnionWith(shared.Select(t => t.Id));
        if (shared.Count > 0) workItems.Add(new(null, "Shared & foundational", [.. shared.OrderBy(t => t.IdentityKey, StringComparer.Ordinal).Select(TaskChain)]));
        var rest = tasks.Where(t => !grouped.Contains(t.Id)).OrderBy(t => t.IdentityKey, StringComparer.Ordinal).ToList();
        if (rest.Count > 0) workItems.Add(new(null, "Unresolved story references", [.. rest.Select(TaskChain)]));

        var traceRequirements = requirements.Select(r => new TraceRequirement(r, [.. reqTests.Where(l => l.Target.Id == r.Id).Select(l => new TraceLink(l.Source, l))
            .OrderBy(l => l.Relation.Counts ? 0 : 1).ThenBy(l => l.Entity.DisplayName, StringComparer.Ordinal)])).ToList();
        var gaps = new List<string>();
        var untested = traceRequirements.Count(r => r.Tests.Count == 0);
        if (untested > 0) gaps.Add($"{untested} requirement(s) have no linked test.");
        var candidateOnly = traceRequirements.Count(r => r.Tests.Count > 0 && r.Tests.All(t => !t.Relation.Counts));
        if (candidateOnly > 0) gaps.Add($"{candidateOnly} requirement(s) have only suggested or ambiguous test links (not counted as tested).");
        var allTasks = workItems.SelectMany(w => w.Tasks).ToList();
        var unimplemented = allTasks.Count(t => t.Implementation.Count == 0);
        if (unimplemented > 0) gaps.Add($"{unimplemented} of {allTasks.Count} task(s) have no linked implementation evidence (a checked box is not implementation evidence).");
        return new(View(feature), traceRequirements, workItems, gaps, latest);
    }
}
