using System.Text.Json;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

public sealed partial class TestPlanSuggestionService
{
    /// <summary>
    /// Finn og forstå's work queues for one discovery result (<see cref="ScenarioWorkQueues"/>), with each scenario's saved test cases counted over
    /// every plan of the application (this import and earlier imports of it; active and archived, never deleted). A case is matched to its scenario
    /// by the rule plan status, impact and refresh share (<see cref="Locate"/>: identity, former identities, merged scenarios, then an unambiguous
    /// move), so a case keeps its association across imports; its source state is the plan's read-time flag against the current source. Computed
    /// once here so the browser only filters (1,752 M2LB scenarios); nothing is stored or changed.
    /// </summary>
    public async Task<ScenarioWorkQueueView> WorkQueuesAsync(string projectKey, string? discoveryId, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        if (discoveryStore is not null) await discoveryStore.RestoreAsync(key, null, ct);
        var discovery = (discoveryId is { Length: > 0 } id ? discoveries?.Find(key, id) : null) ?? discoveries?.Latest(key)
            ?? throw new TestPlanException(404, "discovery-missing", "Discover test scenarios for this project first.");
        var application = discovery.ApplicationKey;
        if (discoveryStore is not null) await discoveryStore.RestoreApplicationAsync(application, ct);
        var newest = discoveries?.CurrentForApplication(application);
        // Outdated only by a newer discovery on OTHER source versions, or a newer one for the SAME selection (e.g. a changed specification).
        // Discoveries are made per selection (module + specification): a newer one for another selection on the same source versions does not
        // make this one older (2026-11-27, found in the V9 browser journey: after switching module, the first module's queue said «eldre
        // kildeversjon» and recommended nothing although no source had changed).
        static string Sources(ScenarioDiscoveryResult r) => string.Join("|", (r.Basis?.Members ?? []).Select(m => $"{m.RepositoryKey}:{m.SnapshotId}").Order(StringComparer.Ordinal));
        var outdated = newest is not null && newest.DiscoveryId != discovery.DiscoveryId && newest.DiscoveredAt > discovery.DiscoveredAt
            && (Sources(newest) != Sources(discovery) || string.Equals(newest.SelectionScope, discovery.SelectionScope, StringComparison.Ordinal));
        var discoveryCurrent = discovery.IsCurrent && !outdated;

        // Every non-deleted project plan of this project or another import of the application. An unreadable plan is counted, never guessed.
        var owner = ProjectOwner(key);
        var plans = new List<(TestPlanDraft Plan, bool Active)>();
        var unreadable = 0;
        foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner || d.GuideKey.StartsWith("project:")).ToListAsync(ct))
        {
            TestPlanDraft plan;
            try { plan = Read(row); }
            catch (JsonException) { if (row.GuideKey == owner) unreadable++; continue; }
            if (plan.ProjectKey is null) continue;
            if (plan.ProjectKey != key && plan.ApplicationKey != application) continue;
            plans.Add((plan, row.Lifecycle == ActiveState));
        }

        var index = new CandidateIndex(discovery.Candidates);
        var byId = discovery.Candidates.GroupBy(c => c.CandidateId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var counts = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var translations = new Dictionary<string, ScenarioTranslationView>(StringComparer.Ordinal);
        var storyTranslations = new Dictionary<string, StoryTranslation>(StringComparer.Ordinal);
        var unmatched = 0;
        foreach (var (stored, active) in plans)
        {
            var plan = FlagProject(stored);
            var moves = Renumbered(plan, discovery.Candidates);
            foreach (var st in plan.StoryTranslations ?? [])
                if (string.Equals(st.Language, ScenarioPlanning.Norwegian, StringComparison.OrdinalIgnoreCase) && (!storyTranslations.TryGetValue(st.StoryKey, out var held) || held.At < st.At))
                    storyTranslations[st.StoryKey] = st;
            foreach (var item in plan.PlannedScenarios ?? [])
            {
                // A scenario-only item is membership when no current case of the plan represents the same scenario (then the case is shown).
                if (plan.Cases.Any(c => c.ParentScenario == item.ScenarioId && c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded))) continue;
                var target = byId.GetValueOrDefault(item.ScenarioId)
                    ?? (item.SourceIdentity is { } si ? discovery.Candidates.FirstOrDefault(x => x.SourceIdentity == si || x.FormerIdentities.Contains(si)) : null);
                if (target is null) continue;
                if (!counts.TryGetValue(target.CandidateId, out var ti)) counts[target.CandidateId] = ti = new Tally();
                ti.Memberships.Add(new ScenarioPlanMembership(plan.Id, plan.PlanName, active, "", item.Title,
                    plan.Suites.Any(s => s.Id == item.SuiteId) ? PlanStructure.PathOf(plan.Suites, item.SuiteId) : null, TestCaseReviewState.Generated) { ScenarioOnly = true });
            }
            foreach (var c in plan.Cases)
            {
                var target = c.Source.Kind switch
                {
                    ScenarioSourceKind.ProjectDiscovery => Locate(c, index, moves),
                    ScenarioSourceKind.Manual when c.ScenarioKey is { } under =>
                        byId.GetValueOrDefault(under) ?? (moves.TryGetValue(under, out var moved) ? moved : null),
                    _ => null,
                };
                var counted = c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded);
                if (target is null)
                {
                    if (active && counted && c.Source.Kind is ScenarioSourceKind.ProjectDiscovery) unmatched++;
                    continue;
                }
                if (!counts.TryGetValue(target.CandidateId, out var t)) counts[target.CandidateId] = t = new Tally();
                // The scenario's stored Norwegian text: only from a case of the scenario itself (not a hand-written one) whose title a person kept.
                if (counted && c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.Edits.Title is null && c.Translation(ScenarioPlanning.Norwegian) is { } nb
                    && (!translations.TryGetValue(target.CandidateId, out var had) || had.At < nb.At))
                    translations[target.CandidateId] = new ScenarioTranslationView(nb.Title, nb.Given, nb.When, nb.Then, nb.ExpectedResult, nb.Origin,
                        nb.SourceFingerprint == ScenarioPlanning.SourceFingerprint(c), nb.At);
                if (counted)
                    t.Memberships.Add(new ScenarioPlanMembership(plan.Id, plan.PlanName, active, c.Source.ScenarioId, c.EffectiveTitle,
                        c.SuiteId is { } suite && plan.Suites.Any(s => s.Id == suite) ? PlanStructure.PathOf(plan.Suites, suite) : null, c.Review));
                if (!active) { if (counted) t.Archived++; continue; }
                switch (c.Review)
                {
                    case TestCaseReviewState.Superseded: t.Historical++; continue;
                    case TestCaseReviewState.Excluded: t.Excluded++; continue;
                    case TestCaseReviewState.Accepted: t.Approved++; break;
                    case TestCaseReviewState.Generated: t.Draft++; break;
                    default: t.InProgress++; break;
                }
                if (c.SourceState == CaseSourceState.Changed) t.SourceChanged++;
                if (c.HasUnresolvedConflict) t.Conflicts++;
                t.Plans.Add(plan.Id);
            }
        }
        var coverage = counts.ToDictionary(kv => kv.Key, kv => kv.Value.ToCoverage(), StringComparer.Ordinal);
        return new ScenarioWorkQueueView
        {
            DiscoveryId = discovery.DiscoveryId, DiscoveryCurrent = discoveryCurrent, CoverageComplete = unreadable == 0,
            PlansRead = plans.Count, PlansUnreadable = unreadable, UnmatchedCases = unmatched,
            Items = [.. ScenarioWorkQueues.Classify(discovery.Candidates, coverage, unreadable == 0, discoveryCurrent)
                .Select(i => translations.TryGetValue(i.CandidateId, out var tr) ? i with { Translation = tr } : i)],
            StoryTranslations = [.. storyTranslations.Values.OrderBy(s => s.StoryKey, StringComparer.Ordinal)],
        };
    }

    private sealed class Tally
    {
        public int Draft, InProgress, Approved, Excluded, Historical, Archived, SourceChanged, Conflicts;
        public HashSet<Guid> Plans { get; } = [];
        public List<ScenarioPlanMembership> Memberships { get; } = [];

        public ScenarioCaseCoverage ToCoverage() => new()
        {
            Draft = Draft, InProgress = InProgress, Approved = Approved, Excluded = Excluded, Historical = Historical, Archived = Archived,
            SourceChanged = SourceChanged, Conflicts = Conflicts, Plans = [.. Plans.Order()],
            Memberships = [.. Memberships.OrderBy(m => m.Active ? 0 : 1).ThenBy(m => m.PlanName, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.CaseId, StringComparer.Ordinal)],
        };
    }
}
