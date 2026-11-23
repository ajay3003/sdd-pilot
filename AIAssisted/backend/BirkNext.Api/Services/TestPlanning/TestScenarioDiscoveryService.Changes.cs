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
        if (store is not null) await store.RestoreApplicationAsync(application, ct);
        var previous = cache.CurrentForApplication(application);
        var current = await DiscoverAsync(request with { Inspect = false }, ct);
        if (previous is null || previous.DiscoveryId == current.DiscoveryId)
            return new ScenarioChangeSummary
            {
                ApplicationKey = application, CurrentDiscoveryId = current.DiscoveryId, New = current.Candidates.Count,
                Conflicts = current.Candidates.Count(c => c.Conflicts.Count > 0),
                Reason = "Ingen tidligere oppdagelse av applikasjonen å sammenligne med: alle scenarioer er nye for BirkNext.",
            };
        var before = previous.Candidates.Where(c => c.SourceIdentity.Length > 0).GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = current.Candidates.Where(c => c.SourceIdentity.Length > 0).GroupBy(c => c.SourceIdentity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int added = 0, changed = 0, same = 0, removed = 0, notCompared = 0;
        foreach (var (identity, c) in after)
            if (!before.TryGetValue(identity, out var old)) added++;
            else if (Content(old) == Content(c)) same++;
            else changed++;
        var complete = current.Basis is null || current.Basis.Complete;
        foreach (var (identity, old) in before.Where(x => !after.ContainsKey(x.Key)))
        {
            var capability = current.Capabilities.FirstOrDefault(x => x.Id == old.CapabilityId);
            if (complete && capability is { State: DiscoveryCapabilityState.Available or DiscoveryCapabilityState.NoEvidence, PartialFailure: false }) removed++;
            else notCompared++;
        }
        return new ScenarioChangeSummary
        {
            ApplicationKey = application, Compared = true, CurrentDiscoveryId = current.DiscoveryId, PreviousDiscoveryId = previous.DiscoveryId,
            PreviousArchives = [.. previous.Basis?.Members.Select(m => m.ArchiveName ?? m.DisplayName).OfType<string>() ?? []],
            New = added, Changed = changed, Removed = removed, Unchanged = same, NotCompared = notCompared,
            Conflicts = current.Candidates.Count(c => c.Conflicts.Count > 0),
        };
    }

    /// <summary>What a tester sees of a scenario (title, preconditions, steps, expected result), normalised for whitespace and case.</summary>
    private static string Content(DiscoveredScenario c) => string.Join("\u001f",
        new[] { c.Title, c.ExpectedBehaviour ?? "" }.Concat(c.Preconditions).Concat(c.Steps.Select(s => $"{s.Action} => {s.Expected}"))
            .Select(t => string.Join(' ', (t ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()));
}
