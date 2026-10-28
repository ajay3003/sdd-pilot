using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceDiscovery;

/// <summary>
/// Reconciles the configured catalog with the latest source discovery. Read-only: it never writes the catalog, never changes a mapping
/// state and never promotes Suggested to Confirmed. A confirmed mapping stays confirmed; new source evidence can only support it, conflict
/// with it, or show that it changed — each visible, none applied.
/// </summary>
public static class SourceIntegrationReconciler
{
    public static SourceIntegrationsReport Reconcile(IntegrationCatalog catalog, SourceIntegrationDiscoveryResult? latest, SourceIntegrationDiscoveryResult? previous, DateTimeOffset now)
    {
        var integrations = catalog.Integrations.Where(i => i.Kind != IntegrationKind.IdentityProvisioning).ToList();
        if (latest is null)
            return new()
            {
                EnvironmentId = catalog.EnvironmentId, SourceAnalysis = SourceDiscoveryStatus.NotAnalyzed, GeneratedAt = now,
                Matches = [.. integrations.Select(i => new SourceIntegrationMatch { IntegrationId = i.Id, State = SourceMatchState.NotAnalyzed, Detail = "No source snapshot has been analyzed for this environment." })],
            };

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<SourceIntegrationMatch>();
        foreach (var integration in integrations)
        {
            var candidates = latest.Candidates.Where(c => c.IsBusinessIntegration && SameChannel(c, integration.EndpointOrTopic)).ToList();
            if (candidates.Count == 0)
            {
                matches.Add(new() { IntegrationId = integration.Id, State = SourceMatchState.NotFoundInSource,
                    Detail = "The configured channel was not found in the analyzed source. This is not evidence that the integration is absent; it may be produced or consumed outside the archive." });
                continue;
            }
            foreach (var c in candidates) matched.Add(c.Id);
            var configured = integration.Consumer;
            var withConsumer = candidates.Where(c => c.ConsumerCandidate is not null).ToList();
            var agreeing = withConsumer.FirstOrDefault(c => SameConsumer(configured, c));
            // Stale: the mapping was confirmed against a snapshot that no longer contributes this candidate.
            var stale = configured.MappingSourceSnapshotId is { } basis && basis != latest.SourceSnapshotId && candidates.All(c => c.Sources.All(s => s.SnapshotId != basis));
            var chosen = agreeing ?? withConsumer.FirstOrDefault() ?? candidates[0];
            var changes = previous is null ? [] : Changes(previous.Candidates.FirstOrDefault(p => p.Id == chosen.Id) ?? previous.Candidates.FirstOrDefault(p => SameChannel(p, integration.EndpointOrTopic)), chosen);
            if (string.IsNullOrWhiteSpace(configured.DisplayName))
                matches.Add(withConsumer.Count > 0
                    ? new() { IntegrationId = integration.Id, State = SourceMatchState.SourceSuggestion, CandidateId = chosen.Id, SourceConsumer = chosen.ConsumerCandidate, SourceConsumerEvidence = chosen.ConsumerEvidence,
                        SourceChanges = changes, Detail = $"Source analysis suggests {chosen.ConsumerCandidate} ({SourceIntegrationLabels.Evidence(chosen.ConsumerEvidence)}). The mapping has not been confirmed." }
                    : new() { IntegrationId = integration.Id, State = SourceMatchState.Supported, CandidateId = chosen.Id, SourceChanges = changes,
                        Detail = "The channel is in the analyzed source; no consumer candidate was found there." });
            else if (agreeing is not null || withConsumer.Count == 0)
                matches.Add(new() { IntegrationId = integration.Id, State = SourceMatchState.Supported, CandidateId = chosen.Id, SourceConsumer = agreeing?.ConsumerCandidate, SourceConsumerEvidence = agreeing?.ConsumerEvidence,
                    StaleEvidence = stale, SourceChanges = changes,
                    Detail = agreeing is not null
                        ? $"The source supports the configured consumer ({agreeing.ConsumerCandidate}, {SourceIntegrationLabels.Evidence(agreeing.ConsumerEvidence)}). Source support is not confirmation."
                        : "The channel is in the analyzed source; the source names no consumer to compare." });
            else
                matches.Add(new() { IntegrationId = integration.Id, State = SourceMatchState.Conflict, CandidateId = chosen.Id, SourceConsumer = chosen.ConsumerCandidate, SourceConsumerEvidence = chosen.ConsumerEvidence,
                    StaleEvidence = stale, SourceChanges = changes,
                    Detail = $"Configured consumer {configured.DisplayName} ({configured.MappingState}) differs from the source's consumer candidate {chosen.ConsumerCandidate}. Needs review — the configured mapping is kept." });
        }
        return new()
        {
            EnvironmentId = catalog.EnvironmentId, SourceAnalysis = latest.Status, Discovery = latest, PreviousSnapshotId = previous?.SourceSnapshotId, Matches = matches, GeneratedAt = now,
            UnconfiguredCandidateIds = [.. latest.Candidates.Where(c => c.IsBusinessIntegration && !matched.Contains(c.Id)).Select(c => c.Id)],
        };
    }

    public static bool SameChannel(SourceIntegrationCandidate candidate, string? channel) =>
        !string.IsNullOrWhiteSpace(channel)
        && (string.Equals(candidate.ChannelName, channel.Trim(), StringComparison.OrdinalIgnoreCase) || candidate.ChannelVariants.Any(v => string.Equals(v, channel.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static readonly Regex Token = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", RegexOptions.Compiled);

    /// <summary>Lower-case name tokens: "M2LB.PersonBiRKAdapter.Worker" → m2lb, person, bi, rk, adapter, worker.</summary>
    public static HashSet<string> Tokens(string? name) =>
        name is null ? [] : Token.Matches(name).Select(m => m.Value.ToLowerInvariant()).Where(t => t.Length > 1 || char.IsDigit(t[0])).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether a configured consumer and a source consumer candidate name the same thing: every word of the configured display name, logical
    /// name or container app (one of them) appears in the candidate component's name. A name match is support — never confirmation.
    /// </summary>
    public static bool SameConsumer(IntegrationConsumer configured, SourceIntegrationCandidate candidate)
    {
        var target = Tokens(candidate.ConsumerCandidate);
        target.UnionWith(Tokens(candidate.ConsumerComponentId?.Split(':').LastOrDefault()));
        if (target.Count == 0) return false;
        foreach (var name in new[] { configured.DisplayName, configured.LogicalName })
            if (Tokens(name) is { Count: > 0 } words && words.IsSubsetOf(target)) return true;
        return false;
    }

    /// <summary>Field changes of the same candidate between two snapshots — source evidence change, never deployment drift.</summary>
    public static List<string> Changes(SourceIntegrationCandidate? before, SourceIntegrationCandidate after)
    {
        if (before is null) return [];
        var changes = new List<string>();
        void Diff(string label, string? a, string? b) { if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add($"{label}: {a ?? "not detected"} → {b ?? "not detected"}"); }
        Diff("Capture technology", before.CaptureTechnology, after.CaptureTechnology);
        Diff("Consumer candidate", before.ConsumerCandidate, after.ConsumerCandidate);
        Diff("Channel", before.ChannelName, after.ChannelName);
        Diff("Integration pattern", SourceIntegrationLabels.Pattern(before.Pattern), SourceIntegrationLabels.Pattern(after.Pattern));
        Diff("Transport", SourceIntegrationLabels.Transport(before.Transport), SourceIntegrationLabels.Transport(after.Transport));
        return changes;
    }
}

/// <summary>
/// Builds the Source integrations report of an environment from the stored snapshots in discovery scope (<see cref="SourceIntegrationScope"/>:
/// the current project's snapshot first, IQR-bound snapshots, Test Pack-recognized sources) and the configured catalog. Read-only: nothing is
/// created, bound or confirmed.
/// </summary>
public sealed class SourceIntegrationService(IqrSourceStore sources, IIntegrationCatalogService catalog,
    BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore? bindings = null,
    IEnumerable<BirkNext.Api.Services.IntegrationJourneys.IIntegrationJourneyPack>? packs = null)
{
    public async Task<SourceIntegrationsReport> ReportAsync(string environmentId, CancellationToken ct = default) => await ReportAsync(environmentId, null, ct);

    public async Task<SourceIntegrationsReport> ReportAsync(string environmentId, Guid? primarySnapshotId, CancellationToken ct = default)
    {
        // No environment type/URL: a read must never attach seed records.
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        var stored = await sources.ListAsync(environmentId, ct);
        var bound = bindings is null ? [] : (await bindings.ListAsync(environmentId, ct)).Select(b => b.SnapshotId).ToHashSet();
        var scope = SourceIntegrationScope.Select(stored, primarySnapshotId, bound, packs ?? []);
        var latest = SourceIntegrationScope.Merge([.. scope.Select(s => (s, SourceIntegrationDiscoveryEngine.Discover(s.Snapshot)))]);
        // "Changes since previous": the earlier analysis of the primary source's own archive, never another repository.
        var previousSnapshot = scope.Count == 0 ? null : stored.Where(s => s.Architecture is not null && s.Id != scope[0].Snapshot.Id && s.AnalyzedAt <= scope[0].Snapshot.AnalyzedAt
                && string.Equals(s.Archive.FileName, scope[0].Snapshot.Archive.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
        var previous = previousSnapshot is null ? null : SourceIntegrationDiscoveryEngine.Discover(previousSnapshot);
        return SourceIntegrationReconciler.Reconcile(configured, latest, previous, DateTimeOffset.UtcNow) with { ContributingSources = [.. scope.Select(s => s.Provenance)] };
    }
}
