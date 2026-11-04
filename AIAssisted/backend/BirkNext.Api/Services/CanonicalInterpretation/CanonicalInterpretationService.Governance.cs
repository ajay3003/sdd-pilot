using BirkNext.Governance;
using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// Governance findings (governance/1) from the canonical store: the same relations, review states and trace rules the review queue and trace read, so a
/// finding and its review item are one fact. Aggregated per policy and module with a few named examples; the gate itself is the shared
/// <see cref="GovernanceGate.Evaluate"/>. Suggestions are optional and never findings; analyzer limitations are Informational and never review work.
/// </summary>
public sealed partial class CanonicalInterpretationService
{
    const int GovernanceExamples = 5;

    public async Task<GateResult> GovernanceAsync(string scope, GateAction action, IReadOnlyCollection<string>? modules, CancellationToken ct)
    {
        var findings = await GovernanceFindingsAsync(scope, ct);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        return GovernanceGate.Evaluate(action, findings, latest, clock.GetUtcNow(), modules);
    }

    public async Task<IReadOnlyList<GovernanceFinding>> GovernanceFindingsAsync(string scope, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id }).FirstOrDefaultAsync(ct);
        if (latest is null)
            return [Finding(GovernancePolicies.NotInterpreted, 1, [], [], null, [])];

        var findings = new List<GovernanceFinding>();
        var relations = await db.Relations.AsNoTracking()
            .Where(r => r.Scope == scope && r.Active && r.ReviewState != "Confirmed" && r.ReviewState != "Rejected"
                && (r.ReviewState == "Conflict" || r.ReviewState == "RevalidationRequired" || r.Resolution == "Ambiguous" || r.Resolution == "Unresolved"))
            .Select(r => new { r.RuleId, r.Resolution, r.ReviewState, r.SourceEntityId, r.TargetEntityId }).ToListAsync(ct);
        var endpointIds = relations.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).ToHashSet();
        var endpoints = await db.Entities.AsNoTracking().Where(e => endpointIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EntityType, e.DisplayName, e.AttributesJson }).ToDictionaryAsync(e => e.Id, ct);
        // A system, service, package or library named exactly like a declared module belongs to it (SkoleAdapter the service ↔ SkoleAdapter the module), so its
        // findings weigh on that module's scope only; anything else without a module stays global.
        var moduleNames = (await db.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == "Module").Select(e => e.DisplayName).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? ModuleOf(Guid id) => !endpoints.TryGetValue(id, out var e) ? null
            : Attr(e.AttributesJson, "module") ?? (e.EntityType is "System" or "Service" or "Module" or "Package" or "Library" && moduleNames.TryGetValue(e.DisplayName, out var m) ? m : null);
        string Name(Guid id) => endpoints.TryGetValue(id, out var e) ? e.DisplayName : "?";
        CanonicalEntityType[] Types(Guid a, Guid b) =>
            [.. new[] { a, b }.Select(id => endpoints.TryGetValue(id, out var e) && Enum.TryParse<CanonicalEntityType>(e.EntityType, out var t) ? t : CanonicalEntityType.Project)];

        // One finding per policy + module; an ambiguity is one question however many alternatives it has (the review queue's grouping).
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var buckets = new Dictionary<(string Policy, string? Module), (int Count, List<string> Examples, SortedSet<string> Consumers, SortedSet<string> Rules)>();
        void Add(GovernancePolicy policy, string? module, string example, IEnumerable<string> consumers, string rule)
        {
            if (!buckets.TryGetValue((policy.Id, module), out var b)) buckets[(policy.Id, module)] = b = (0, [], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            if (b.Examples.Count < GovernanceExamples) b.Examples.Add(example);
            b.Consumers.UnionWith(consumers);
            b.Rules.Add(rule);
            buckets[(policy.Id, module)] = (b.Count + 1, b.Examples, b.Consumers, b.Rules);
        }
        foreach (var r in relations)
        {
            var review = Enum.Parse<RelationReviewState>(r.ReviewState);
            var resolution = Enum.Parse<RelationResolution>(r.Resolution);
            if (CanonicalReviewQueue.Classify(r.RuleId, resolution, review, true) is not { Kind: ReviewItemKind.Decision } c) continue;
            // A removed-story reference is the task's own finding below (one fact, counted once).
            if (resolution == RelationResolution.Unresolved && r.RuleId == CorrelationRules.TaskGroupRule) continue;
            var module = ModuleOf(r.SourceEntityId) ?? ModuleOf(r.TargetEntityId);
            var consumers = CanonicalReviewQueue.AffectedConsumers(Types(r.SourceEntityId, r.TargetEntityId));
            var example = $"{Name(r.SourceEntityId)} → {Name(r.TargetEntityId)}";
            var policy = c.Type switch
            {
                ReviewItemType.IdentityConflict => GovernancePolicies.IdentityConflict,
                ReviewItemType.StaleDecision => GovernancePolicies.StaleAssociation,
                _ when resolution == RelationResolution.Unresolved => GovernancePolicies.UnknownImpact,
                _ => GovernancePolicies.Ambiguity,
            };
            if (policy == GovernancePolicies.Ambiguity && !groups.Add(CanonicalReviewQueue.AmbiguityGroup(r.RuleId, r.SourceEntityId, r.TargetEntityId))) continue;
            Add(policy, module, example, consumers, r.RuleId);
        }

        // Story references: only the two relationships the Phase 1 engine marks as broken. Shared / foundational and group work never appear here.
        var tasks = await (from m in db.SnapshotEntities.AsNoTracking()
                           join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                           where m.SnapshotId == latest.Id && e.EntityType == "Task"
                           select new { e.DisplayName, e.AttributesJson }).ToListAsync(ct);
        foreach (var t in tasks)
        {
            var policy = Relationship(t.AttributesJson) switch
            {
                "RemovedStoryReference" => GovernancePolicies.RemovedStory,
                "UnresolvedReference" => GovernancePolicies.UnresolvedStory,
                _ => null,
            };
            if (policy is null) continue;
            var story = Attr(t.AttributesJson, "storyReference");
            Add(policy, Attr(t.AttributesJson, "module"), $"{Attr(t.AttributesJson, "id") ?? t.DisplayName}{(story is null ? "" : $" → {story}")}",
                CanonicalReviewQueue.AffectedConsumers(CanonicalEntityType.Task), Attr(t.AttributesJson, "workItemRule") ?? WorkItemInterpreter.RuleSetVersion);
        }

        // Test coverage of requirements: the trace's own rule (established VERIFIES = tested; candidate only = suggested; none = untested).
        var requirements = await (from m in db.SnapshotEntities.AsNoTracking()
                                  join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                                  where m.SnapshotId == latest.Id && e.EntityType == "Requirement"
                                  select new { e.Id, e.DisplayName, e.AttributesJson }).ToListAsync(ct);
        var requirementIds = requirements.Select(r => r.Id).ToList();
        var verifies = await db.Relations.AsNoTracking()
            .Where(r => r.Scope == scope && r.Active && r.RelationType == "Verifies" && r.ReviewState != "Rejected" && requirementIds.Contains(r.TargetEntityId))
            .Select(r => new { r.TargetEntityId, r.Resolution, r.ReviewState }).ToListAsync(ct);
        var established = verifies.Where(v => v.ReviewState == "Confirmed" || v.Resolution is "Explicit" or "Derived" or "Confirmed").Select(v => v.TargetEntityId).ToHashSet();
        var anyLink = verifies.Select(v => v.TargetEntityId).ToHashSet();
        foreach (var r in requirements.Where(r => Attr(r.AttributesJson, "module") is not null).OrderBy(r => r.DisplayName, StringComparer.Ordinal))
        {
            if (established.Contains(r.Id)) continue;
            Add(anyLink.Contains(r.Id) ? GovernancePolicies.CandidateOnly : GovernancePolicies.NoTest, Attr(r.AttributesJson, "module"), r.DisplayName,
                CanonicalReviewQueue.AffectedConsumers(CanonicalEntityType.Requirement), CorrelationRules.TestCandidateRule);
        }

        foreach (var ((policyId, module), b) in buckets.OrderBy(k => k.Key.Policy, StringComparer.Ordinal).ThenBy(k => k.Key.Module, StringComparer.Ordinal))
            findings.Add(Finding(GovernancePolicies.Find(policyId)!, b.Count, b.Examples, [.. b.Consumers], module, [.. b.Rules]));

        // Analyzer limitations: stated, Informational, never review work.
        var trace = await TraceSummaryAsync(scope, ct);
        findings.AddRange(trace.Limitations.Select(l => Finding(GovernancePolicies.Limitation, 1, [l], [], null, [])));
        return findings;
    }

    static GovernanceFinding Finding(GovernancePolicy policy, int count, IReadOnlyList<string> examples, IReadOnlyList<string> consumers, string? module, IReadOnlyList<string> rules) =>
        new(policy.Id, policy.Version, count > 1 ? $"{policy.Title} ({count})" : policy.Title, policy.Purpose, count,
            count > examples.Count ? [.. examples, $"… and {count - examples.Count} more"] : examples, policy.Owner, policy.Resolution, consumers, module, rules);
}
