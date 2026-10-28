using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceDiscovery;

/// <summary>One stored snapshot in discovery scope, why it is there, and whether it may originate candidates (or only corroborate them).</summary>
public sealed record ScopedSourceSnapshot(IqrSourceSnapshot Snapshot, IReadOnlyList<string> Reasons, bool Originates)
{
    public SourceIntegrationProvenance Provenance => new(Snapshot.Id, Snapshot.Archive.FileName, string.Join("; ", Reasons), !Originates);
}

/// <summary>
/// Which stored snapshots Integration Catalog discovery reads, and how their candidates merge. Deterministic and read-only:
/// <list type="bullet">
/// <item>the current project's snapshot (primary context, first);</item>
/// <item>snapshots bound as IQR evidence in this environment;</item>
/// <item>snapshots a registered Test Pack recognizes as one of its source roles — by content (components, projects, API documents, declared
/// infrastructure), never by archive name. Infrastructure as Code only corroborates: it adds provenance to a candidate an application source
/// found, never a candidate of its own;</item>
/// <item>only the newest snapshot of the same archive among role-recognized ones (a re-import replaces, never doubles).</item>
/// </list>
/// Any other stored snapshot is unrelated history and is never read. Without a current project, binding or recognized source the newest
/// analyzed snapshot is used, as before.
/// </summary>
public static class SourceIntegrationScope
{
    public const string PrimaryReason = "Current project source";
    public const string BoundReason = "Bound as Integration Quality Review evidence";
    public const string FallbackReason = "Newest analyzed source (no current project or recognized source)";

    public static IReadOnlyList<ScopedSourceSnapshot> Select(IReadOnlyList<IqrSourceSnapshot> snapshots, Guid? primarySnapshotId, IReadOnlyCollection<Guid> boundSnapshotIds,
        IEnumerable<IIntegrationJourneyPack> packs)
    {
        var withArchitecture = snapshots.Where(s => s.Architecture is not null).OrderByDescending(s => s.AnalyzedAt).ThenBy(s => s.Id).ToList();
        var reasons = new Dictionary<Guid, (List<string> Reasons, bool Originates, bool Explicit)>();
        void Add(IqrSourceSnapshot snapshot, string reason, bool originates, bool isExplicit)
        {
            if (!reasons.TryGetValue(snapshot.Id, out var entry)) reasons[snapshot.Id] = entry = ([], false, false);
            if (!entry.Reasons.Contains(reason)) entry.Reasons.Add(reason);
            reasons[snapshot.Id] = (entry.Reasons, entry.Originates || originates, entry.Explicit || isExplicit);
        }
        foreach (var snapshot in withArchitecture)
        {
            if (snapshot.Id == primarySnapshotId) Add(snapshot, PrimaryReason, true, true);
            if (boundSnapshotIds.Contains(snapshot.Id)) Add(snapshot, BoundReason, true, true);
        }
        foreach (var pack in packs)
            foreach (var role in pack.SourceRoles.Where(role => role.Provider != TestPackSourceProvider.TestPackReference))
                foreach (var snapshot in withArchitecture.Where(snapshot => TestPackSources.Recognizes(role, snapshot)))
                    Add(snapshot, $"{role.Label} source ({pack.DisplayName})", !role.RequiresInfrastructureAsCode, false);

        // A role-recognized snapshot that is not the newest of its archive is an earlier analysis of the same package: skipped.
        var newestPerArchive = withArchitecture.GroupBy(s => s.Archive.FileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var scoped = withArchitecture
            .Where(s => reasons.TryGetValue(s.Id, out var entry) && (entry.Explicit || newestPerArchive[s.Archive.FileName] == s.Id))
            .Select(s => new ScopedSourceSnapshot(s, reasons[s.Id].Reasons, reasons[s.Id].Originates))
            .OrderByDescending(s => s.Snapshot.Id == primarySnapshotId).ThenByDescending(s => s.Snapshot.AnalyzedAt).ThenBy(s => s.Snapshot.Id).ToList();
        if (scoped.Count == 0 && withArchitecture.Count > 0) scoped.Add(new(withArchitecture[0], [FallbackReason], true));
        return scoped;
    }

    /// <summary>
    /// One discovery over several snapshots. Candidates merge by their existing identity (transport + channel (+ consumer)); provenance keeps every
    /// contributing snapshot. Complementary fields fill in (a producer from one repository, a consumer from another); differing values are never
    /// chosen between — they are listed as <see cref="SourceIntegrationCandidate.SourceDisagreements"/>, which need a decision.
    /// </summary>
    public static SourceIntegrationDiscoveryResult? Merge(IReadOnlyList<(ScopedSourceSnapshot Scope, SourceIntegrationDiscoveryResult Result)> results)
    {
        if (results.Count == 0) return null;
        var merged = new Dictionary<string, SourceIntegrationCandidate>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (scope, result) in results)
        {
            var provenance = scope.Provenance;
            foreach (var candidate in result.Candidates)
            {
                if (merged.TryGetValue(candidate.Id, out var existing)) merged[candidate.Id] = Combine(existing, candidate, provenance, scope.Snapshot.Archive.FileName);
                else if (scope.Originates) { merged[candidate.Id] = candidate with { Sources = [provenance] }; order.Add(candidate.Id); }
            }
        }
        var first = results[0].Result;
        return first with
        {
            Candidates = [.. order.Select(id => merged[id])],
            TechnicalChannels = [.. results.SelectMany(r => r.Result.TechnicalChannels).DistinctBy(c => (c.Name, c.Transport))],
            DetectedTechnologies = [.. results.SelectMany(r => r.Result.DetectedTechnologies).Distinct(StringComparer.Ordinal)],
            Unresolved = [.. results.SelectMany(r => r.Result.Unresolved).Distinct(StringComparer.Ordinal)],
            Diagnostics = [.. results.SelectMany(r => r.Result.Diagnostics).Distinct(StringComparer.Ordinal)],
            Limitations = [.. results.SelectMany(r => r.Result.Limitations).Distinct(StringComparer.Ordinal)],
            Status = results.Any(r => r.Result.Status == SourceDiscoveryStatus.Partial) ? SourceDiscoveryStatus.Partial : first.Status,
        };
    }

    private static SourceIntegrationCandidate Combine(SourceIntegrationCandidate existing, SourceIntegrationCandidate other, SourceIntegrationProvenance provenance, string otherArchive)
    {
        var firstArchive = existing.Sources.FirstOrDefault()?.ArchiveName ?? "first source";
        var disagreements = new List<string>(existing.SourceDisagreements);
        string? Pick(string label, string? a, string? b)
        {
            if (a is null) return b;
            if (b is not null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) disagreements.Add($"{label}: {a} ({firstArchive}) ≠ {b} ({otherArchive})");
            return a;
        }
        if (existing.Pattern != other.Pattern && other.Pattern != IntegrationPattern.Unknown && existing.Pattern != IntegrationPattern.Unknown)
            disagreements.Add($"Integration pattern: {SourceIntegrationLabels.Pattern(existing.Pattern)} ({firstArchive}) ≠ {SourceIntegrationLabels.Pattern(other.Pattern)} ({otherArchive})");
        var consumerFromOther = existing.ConsumerCandidate is null && other.ConsumerCandidate is not null;
        return existing with
        {
            CaptureTechnology = Pick("Capture technology", existing.CaptureTechnology, other.CaptureTechnology),
            ProducerComponent = Pick("Producer", existing.ProducerComponent, other.ProducerComponent),
            ConsumerCandidate = Pick("Consumer", existing.ConsumerCandidate, other.ConsumerCandidate),
            ConsumerComponentId = existing.ConsumerComponentId ?? other.ConsumerComponentId,
            ConsumerEvidence = consumerFromOther ? other.ConsumerEvidence : existing.ConsumerEvidence,
            SourceEntity = existing.SourceEntity ?? other.SourceEntity,
            SourceSystem = existing.SourceSystem ?? other.SourceSystem,
            NamespaceName = existing.NamespaceName ?? other.NamespaceName,
            ChannelVariants = [.. existing.ChannelVariants.Concat(other.ChannelVariants).Distinct(StringComparer.OrdinalIgnoreCase)],
            Evidence = [.. existing.Evidence.Concat(other.Evidence).Distinct()],
            Sources = [.. existing.Sources, provenance],
            SourceDisagreements = [.. disagreements.Distinct(StringComparer.Ordinal)],
        };
    }
}
