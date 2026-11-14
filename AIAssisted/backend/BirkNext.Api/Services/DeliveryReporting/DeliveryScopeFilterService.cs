using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.DeliveryReporting;
using BirkNext.Scopes;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>The confirmed scope members a filter uses, with the exact revisions pinned (revision ids from the Scope Registry, never from the request).</summary>
/// <param name="MemberKeys">Canonical keys of confirmed requirement / user story / scenario members, de-duplicated across overlapping scopes.</param>
/// <param name="MembersNotApplicable">Confirmed members of other kinds (systems, contracts, Test Data …): not Delivery items, never counted.</param>
public sealed record DeliveryScopeMembershipResolution(DeliveryScopeProvenance Provenance, IReadOnlySet<string> MemberKeys, int MembersNotApplicable);

/// <summary>
/// "Filter by scope" for Delivery: resolves a requested shared scope selection to its confirmed members in-process (the same Scope Registry rules as
/// api/scopes/selection), pinned to the exact approved revisions that were selected. A draft, in-review or unknown revision is refused — never
/// silently replaced by another revision. Candidates and excluded members are not members. Matching to Delivery items is by canonical key only.
/// </summary>
public sealed class DeliveryScopeFilterService(ScopeRegistryService? registry = null)
{
    /// <summary>Member kinds that identify Delivery items (the canonical key of a requirement, user story or scenario).</summary>
    public static readonly IReadOnlySet<ScopeMemberKind> ItemKinds = new HashSet<ScopeMemberKind> { ScopeMemberKind.Requirement, ScopeMemberKind.UserStory, ScopeMemberKind.Scenario };

    public const string MembershipRule = "confirmed members of the pinned approved revisions; canonical key match type+module+feature+id; acceptance criteria and test cases follow their parent";

    /// <summary>Null resolution and null error = nothing to filter (no selection, or scope context mode).</summary>
    public async Task<(DeliveryScopeMembershipResolution? Resolution, string? Error)> ResolveAsync(DeliveryScopeProvenance? requested, CancellationToken ct)
    {
        if (requested is not { Mode: DeliveryScopeMode.Filter, Scopes.Count: > 0 }) return (null, null);
        if (registry is null) return (null, "Filtering by scope needs the Scope Registry, which is not available. Use Scope context, or try again later.");
        if (requested.ProjectKey is not { Length: > 0 } projectKey) return (null, "Filtering by scope needs the scopes' project (Scope Registry project key); none was sent.");
        var chosen = new List<(BirkNext.Scopes.ScopeSummary, ScopeRevisionView)>();
        var pinned = new List<DeliveryScopeProvenanceEntry>();
        foreach (var entry in requested.Scopes)
        {
            var detail = await registry.GetAsync(projectKey, entry.ScopeId, ct);
            if (detail is null) return (null, $"Scope \"{entry.ScopeName}\" is not part of this project: nothing was filtered.");
            var revision = detail.Revisions.FirstOrDefault(r => r.Number == entry.RevisionNumber);
            if (revision is null) return (null, $"\"{detail.Scope.Name}\" has no revision {entry.RevisionNumber}: nothing was filtered.");
            if (revision.State is not (ScopeRevisionState.Approved or ScopeRevisionState.Superseded) || revision.ApprovedAt is null)
                return (null, $"\"{detail.Scope.Name}\" revision {entry.RevisionNumber} is not approved: only approved revisions can filter Delivery items.");
            if (entry.RevisionId is { } id && id != revision.Id)
                return (null, $"\"{detail.Scope.Name}\" revision {entry.RevisionNumber} does not have the recorded revision id: choose the scope again.");
            chosen.Add((detail.Scope, revision));
            pinned.Add(entry with { ScopeName = detail.Scope.Name, RevisionId = revision.Id });
        }
        var selection = ScopeRegistryRules.Select(projectKey, chosen);
        var keys = selection.Members.Where(m => ItemKinds.Contains(m.Member.Kind)).Select(m => m.Member.TargetId.Trim())
            .Where(k => k.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notApplicable = selection.Members.Count(m => !ItemKinds.Contains(m.Member.Kind));
        var provenance = requested with { Scopes = [.. pinned.OrderBy(s => s.ScopeName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.ScopeId)] };
        return (new(provenance, keys, notApplicable), null);
    }
}

/// <summary>
/// Restricts a built (not yet frozen) report to the items of a scope. Population = the requirements, user stories and scenarios of the report's
/// features (hierarchy rows), matched by canonical key. Metrics fall in four groups:
/// item-level (recomputed from in-scope items only: user stories, scenarios, functional requirements, story/criteria changes, tasks linked to
/// in-scope stories), feature-level (recomputed over the features that contain in-scope items: feature movement, explicit statuses incl. Blocked,
/// attention, scope changes), comparison-level (snapshot direction) and not scope-resolvable (feature-wide or shared task figures, report-level
/// attention) — those are removed and listed by name so a whole-project number never appears under a scope.
/// </summary>
public static class DeliveryReportScopeFilter
{
    public const string FeatureLevelHint = "per feature containing in-scope items";

    private static readonly HashSet<string> FeatureLevel = new(StringComparer.Ordinal)
    {
        "features-changed", "moved-forward", "completed", "new-features", "removed-features", "specifications-changed", "inventory-features", "inventory-no-tasks",
        "ready-for-test", "testing", "blocked", "needs-clarification", "quality-scope-review", "quality-traceability", "attention",
    };

    private static readonly HashSet<string> ItemLevel = new(StringComparer.Ordinal)
    {
        "inventory-stories", "inventory-scenarios", "stories-changed", "criteria-changed", "inventory-tasks", "inventory-checked", "inventory-unchecked",
    };

    private const string ComparisonLevel = "quality-direction";

    /// <summary>Whether a metric key can be restricted to a scope's items (item- or feature-level) — false = "Not scope-resolvable".</summary>
    public static bool Resolvable(string key) => FeatureLevel.Contains(key) || ItemLevel.Contains(key) || key == ComparisonLevel;

    private static bool IsScenario(DeliveryUserStoryRow x) => DeliveryScopeMembership.IsScenario(x.Kind, x.Key);

    private static string? KeyOf(DeliveryStoryRow feature, DeliveryUserStoryRow x) =>
        DeliveryScopeMembership.CanonicalKeyOf(IsScenario(x) ? ScopeArtifactType.Scenario : ScopeArtifactType.UserStory, feature.Module, feature.FeatureKey, x.Key);

    private static string? KeyOf(DeliveryStoryRow feature, DeliveryRequirementRow r) =>
        DeliveryScopeMembership.CanonicalKeyOf(ScopeArtifactType.Requirement, feature.Module, feature.FeatureKey, r.Key);

    private static bool LiveStory(DeliveryUserStoryRow x) => x.Change != DeliveryItemChange.Removed && !x.Removed;

    public static (WeeklyDeliveryReport Report, DeliveryScopeFilterCounts Counts) Apply(WeeklyDeliveryReport report, IReadOnlySet<string> memberKeys, int membersNotApplicable)
    {
        var keys = new HashSet<string>(memberKeys, StringComparer.OrdinalIgnoreCase);
        bool In(string? key) => key is not null && keys.Contains(key);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int population = 0, included = 0;
        var currentFeatures = new HashSet<string>(StringComparer.Ordinal);
        var anyFeatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in report.Stories)
        {
            var h = s.Hierarchy;
            if (h is null) continue;
            var storyKeys = h.Stories.Select(x => (Row: x, Key: KeyOf(s, x))).ToList();
            var reqKeys = h.FunctionalRequirements.Select(r => (Row: r, Key: KeyOf(s, r))).ToList();
            if (storyKeys.Any(x => In(x.Key)) || reqKeys.Any(r => In(r.Key))) anyFeatures.Add(s.FeatureKey);
            if (s.RemovedFeature) continue;
            // The current Delivery population: requirement, user story and scenario rows present in the current snapshot.
            foreach (var (row, key) in storyKeys.Where(x => LiveStory(x.Row)))
            {
                population++;
                if (In(key)) { included++; matched.Add(key!); currentFeatures.Add(s.FeatureKey); }
            }
            foreach (var (row, key) in reqKeys.Where(r => r.Row.Change != DeliveryItemChange.Removed))
            {
                population++;
                if (In(key)) { included++; matched.Add(key!); currentFeatures.Add(s.FeatureKey); }
            }
        }

        var projected = DeliveryReportScoping.Project(report, currentFeatures, anyFeatures, report.Scope!);
        // Report-level attention (no feature) cannot be attributed to a scope's items.
        var reportLevelAttention = projected.Attention.Count(DeliveryReportScoping.IsReportLevel);
        projected = projected with
        {
            Scope = report.Scope,
            Attention = [.. projected.Attention.Where(a => !DeliveryReportScoping.IsReportLevel(a))],
            Stories = [.. projected.Stories.Select(s => s with { Hierarchy = s.Hierarchy is null ? null : Prune(s, s.Hierarchy, In) })],
        };

        var notResolvable = new List<string>();
        var metrics = new List<DeliveryReportMetric>();
        var liveInScope = projected.Stories.Where(s => !s.RemovedFeature).SelectMany(s => s.Hierarchy?.Stories ?? []).Where(LiveStory).ToList();
        foreach (var m in report.Metrics)
        {
            if (m.Key is "inventory-tasks" or "inventory-checked" or "inventory-unchecked")
            {
                var (total, done) = (liveInScope.Sum(x => x.TaskTotal), liveInScope.Sum(x => x.TaskCompleted));
                var value = m.Key == "inventory-tasks" ? total : m.Key == "inventory-checked" ? done : total - done;
                metrics.Add(m with { Value = value, Hint = "Tasks linked to the in-scope user stories and scenarios; shared and unlinked tasks are not scope-resolvable" });
                continue;
            }
            if (!Resolvable(m.Key) || DeliveryReportScoping.Recompute(m, projected) is not { } recomputed)
            {
                notResolvable.Add(m.Label);
                continue;
            }
            metrics.Add(FeatureLevel.Contains(m.Key) ? recomputed with { Hint = string.IsNullOrEmpty(m.Hint) ? FeatureLevelHint : $"{m.Hint} · {FeatureLevelHint}" }
                : m.Key == ComparisonLevel ? recomputed with { Hint = $"{m.Hint} · comparison-level (both snapshots), not item-specific" } : recomputed);
        }
        var requirements = projected.Stories.Where(s => !s.RemovedFeature).SelectMany(s => s.Hierarchy?.FunctionalRequirements ?? [])
            .Count(r => r.Change != DeliveryItemChange.Removed);
        metrics.Add(new DeliveryReportMetric("inventory-requirements", "Functional requirements", requirements, "Functional requirements in scope (canonical key match)")
            { Group = DeliveryMetricGroups.Inventory });
        if (reportLevelAttention > 0) notResolvable.Add("Report-level attention");

        var counts = DeliveryScopeMembership.Counts(included, population - included, keys, matched, membersNotApplicable) with
        {
            IncludedFeatures = currentFeatures.Count, NotScopeResolvable = notResolvable,
        };
        var summary = counts.NoApplicableItems
            ? new List<string> { $"{DeliveryScopeProvenanceText.NoApplicableItems}: no requirement, user story or scenario of this comparison is a confirmed member of the selected scope revisions." }
            : new List<string> { $"Filtered by scope: {included} in-scope item{(included == 1 ? "" : "s")} in {currentFeatures.Count} feature{(currentFeatures.Count == 1 ? "" : "s")}; {population - included} outside the scope." };
        if (counts.MembersUnresolved > 0)
            summary.Add($"{counts.MembersUnresolved} scope member{(counts.MembersUnresolved == 1 ? " is" : "s are")} not present in this Delivery comparison (unresolved).");
        return (projected with { Metrics = counts.NoApplicableItems ? [] : metrics, ExecutiveSummary = summary }, counts);
    }

    /// <summary>The feature's hierarchy reduced to in-scope stories/scenarios (with their criteria) and requirements; change counters recomputed from them.
    /// Phases, groups and tasks stay feature-level (shown under the feature, not counted as in-scope figures).</summary>
    private static DeliveryFeatureHierarchy Prune(DeliveryStoryRow feature, DeliveryFeatureHierarchy h, Func<string?, bool> inScope)
    {
        var stories = h.Stories.Where(x => inScope(KeyOf(feature, x))).ToList();
        var requirements = h.FunctionalRequirements.Where(r => inScope(KeyOf(feature, r))).ToList();
        var criteria = stories.SelectMany(x => x.Criteria).ToList();
        return h with
        {
            Stories = stories, FunctionalRequirements = requirements, FeatureCriteria = [],
            StoriesAdded = stories.Count(x => x.Change == DeliveryItemChange.Added), StoriesRemoved = stories.Count(x => x.Change == DeliveryItemChange.Removed),
            StoriesChanged = stories.Count(x => x.Change == DeliveryItemChange.Changed),
            CriteriaAdded = criteria.Count(c => c.Change == DeliveryItemChange.Added), CriteriaRemoved = criteria.Count(c => c.Change == DeliveryItemChange.Removed),
            CriteriaChanged = criteria.Count(c => c.Change == DeliveryItemChange.Changed),
            CriteriaBaseline = criteria.Count(c => c.Change != DeliveryItemChange.Added), CriteriaCurrent = criteria.Count(c => c.Change != DeliveryItemChange.Removed),
        };
    }
}
