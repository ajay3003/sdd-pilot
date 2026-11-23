using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>Scenario changes of a new import against the application's previous current discovery (2026-11-23). Reuses discovery and the stable
/// scenario identity (<see cref="DiscoveredScenario.SourceIdentity"/>); computes nothing it cannot read.</summary>
public sealed partial class TestScenarioDiscoveryService
{
    public async Task<ScenarioChangeSummary> ChangesAsync(ScenarioDiscoveryRequest request, CancellationToken ct)
    {
        if (request.SnapshotId is not { } snapshotId || await sources.FindSourceAnalysisAsync(snapshotId, ct) is not { } snapshot)
            throw new TestPlanException(404, "snapshot-not-found", "Kildeversjonen finnes ikke.");
        var application = SourceBaselineService.KeyOf(snapshot);
        var current = await DiscoverAsync(request with { Inspect = false }, ct);
        // The previous version is the newest earlier discovery of the application read from different source content — not merely the last
        // discovery (after a re-import of the same content that would compare a version with itself).
        var previous = store is null || current.Basis is null ? null : await store.PreviousVersionAsync(application, ScenarioDiscoveryStore.Content(current.Basis), ct);
        if (previous is null)
            return new ScenarioChangeSummary
            {
                ApplicationKey = application, CurrentDiscoveryId = current.DiscoveryId, New = current.Candidates.Count,
                Conflicts = current.Candidates.Count(c => c.Conflicts.Count > 0),
                Reason = "Ingen tidligere versjon av applikasjonen (med annet innhold) er oppdaget: alle scenarioer er nye for BirkNext.",
            };
        var before = previous.Candidates.Where(c => c.SourceIdentity.Length > 0).GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = current.Candidates.Where(c => c.SourceIdentity.Length > 0).GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // A scenario whose identity changed but whose text did not (renumbered, or its file moved in the new version) is the unique candidate with
        // exactly the same content on both sides — the rule plans follow too (Renumbered). Anything ambiguous stays new / removed.
        var goneBefore = before.Where(x => !after.ContainsKey(x.Key)).Select(x => x.Value).ToList();
        var newAfter = after.Where(x => !before.ContainsKey(x.Key)).Select(x => x.Value).ToList();
        var uniqueGone = goneBefore.GroupBy(Content).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var moved = newAfter.GroupBy(Content).Where(g => g.Count() == 1 && uniqueGone.ContainsKey(g.Key)).Select(g => g.Key).ToHashSet();
        int added = 0, changed = 0, same = 0, removed = 0, notCompared = 0;
        foreach (var (identity, c) in after)
            if (!before.TryGetValue(identity, out var old)) { if (!moved.Contains(Content(c))) added++; }
            else if (Content(old) == Content(c)) same++;
            else changed++;
        var complete = current.Basis is null || current.Basis.Complete;
        foreach (var (identity, old) in before.Where(x => !after.ContainsKey(x.Key) && !moved.Contains(Content(x.Value))))
        {
            var capability = current.Capabilities.FirstOrDefault(x => x.Id == old.CapabilityId);
            if (complete && capability is { State: DiscoveryCapabilityState.Available or DiscoveryCapabilityState.NoEvidence, PartialFailure: false }) removed++;
            else notCompared++;
        }
        return new ScenarioChangeSummary
        {
            ApplicationKey = application, Compared = true, CurrentDiscoveryId = current.DiscoveryId, PreviousDiscoveryId = previous.DiscoveryId,
            PreviousArchives = [.. previous.Basis?.Members.Select(m => m.ArchiveName ?? m.DisplayName).OfType<string>() ?? []],
            New = added, Changed = changed, Removed = removed, Unchanged = same, Moved = moved.Count, NotCompared = notCompared,
            Conflicts = current.Candidates.Count(c => c.Conflicts.Count > 0),
        };
    }

    /// <summary>What a tester sees of a scenario (title, preconditions, steps, expected result), normalised for whitespace and case.</summary>
    private static string Content(DiscoveredScenario c) => string.Join("\u001f",
        new[] { c.Title, c.ExpectedBehaviour ?? "" }.Concat(c.Preconditions).Concat(c.Steps.Select(s => $"{s.Action} => {s.Expected}"))
            .Select(t => string.Join(' ', (t ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()));
}
