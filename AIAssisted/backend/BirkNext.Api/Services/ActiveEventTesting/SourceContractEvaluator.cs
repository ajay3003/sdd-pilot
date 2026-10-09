using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.Integrations.SourceDiscovery;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

public enum SourceContractStatus { Compatible, Incompatible, Outdated, NoSourceSnapshot, NotApplicable }

/// <summary>
/// What a provider's fixture needs from the consuming adapter's source: the CDC resource, the adapter record (if the provider knows its
/// name; otherwise the best-matching adapter record is used) and the fields the fixture sends.
/// </summary>
public sealed record SourceContractRequirement(
    string ScenarioId,
    string ScenarioVersion,
    int FixtureSchemaVersion,
    string Resource,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> OptionalFields,
    ActiveEventOperation Operation,
    IReadOnlyList<string> RecordTypeCandidates);

public sealed record SourceContractEvaluation
{
    public SourceContractStatus Status { get; init; } = SourceContractStatus.NoSourceSnapshot;
    public Guid? SourceSnapshotId { get; init; }
    public string ArchiveSha256 { get; init; } = "";
    public string SourceCommit { get; init; } = "";
    public int AnalyzerVersion { get; init; }
    public string RecordType { get; init; } = "";
    public IReadOnlyList<string> ConfirmedFields { get; init; } = [];
    public IReadOnlyList<string> MissingFields { get; init; } = [];
    public string Detail { get; init; } = "";
    public string Fingerprint { get; init; } = "";

    public string Label => Status switch
    {
        SourceContractStatus.Compatible => "Compatible",
        SourceContractStatus.Outdated => "Outdated — needs review",
        SourceContractStatus.Incompatible => "Incompatible",
        SourceContractStatus.NotApplicable => "Not applicable",
        _ => "Source evidence unavailable",
    };
}

/// <summary>
/// Validates a scenario fixture against Source Analysis evidence: every required fixture field must be a CDC field the adapter record reads
/// in the bound snapshot (source compatibility, not deployment correlation). A snapshot that is not the newest one for the integration is
/// Outdated: a fixture is never silently sent against changed source. The fingerprint binds snapshot, archive, status and fields.
/// </summary>
public static class SourceContractEvaluator
{
    public static SourceContractEvaluation Evaluate(SourceContractRequirement requirement, IqrSourceSnapshot? snapshot, Guid? latestSnapshotId)
    {
        if (snapshot is null)
            return Seal(requirement, new SourceContractEvaluation
            {
                Status = SourceContractStatus.NoSourceSnapshot, MissingFields = requirement.RequiredFields,
                Detail = "Source evidence unavailable: no analyzed source snapshot of the consuming adapter is bound to this integration. Upload the adapter source in Source Analysis first.",
            });
        var bound = new SourceContractEvaluation
        {
            SourceSnapshotId = snapshot.Id, ArchiveSha256 = snapshot.Archive.Sha256, SourceCommit = snapshot.Commit, AnalyzerVersion = snapshot.AnalyzerVersion,
        };
        if (snapshot.IntegrationPath is not { } path || snapshot.AnalyzerVersion < 2)
            return Seal(requirement, bound with
            {
                Status = SourceContractStatus.Incompatible, MissingFields = requirement.RequiredFields,
                Detail = "The snapshot has no integration path (analyzer v2 or later is needed to see which CDC fields the adapter reads).",
            });

        var records = path.Fields.Select(f => f.Key).Where(k => k.StartsWith("CDC ", StringComparison.Ordinal) && k.IndexOf('.', 4) > 4)
            .GroupBy(k => k[4..k.IndexOf('.', 4)], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(k => k[(k.IndexOf('.', 4) + 1)..]).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var candidates = requirement.RecordTypeCandidates.Count > 0
            ? records.Where(r => requirement.RecordTypeCandidates.Contains(r.Key, StringComparer.Ordinal))
            : records;
        var best = candidates.OrderByDescending(r => requirement.RequiredFields.Count(r.Value.Contains)).ThenBy(r => r.Key, StringComparer.Ordinal).FirstOrDefault();
        if (best.Key is null || requirement.RequiredFields.Count(best.Value.Contains) == 0)
            return Seal(requirement, bound with
            {
                Status = SourceContractStatus.Incompatible, MissingFields = requirement.RequiredFields,
                Detail = $"The snapshot shows no adapter record reading CDC fields of {requirement.Resource}.",
            });
        var record = best.Key;
        var confirmed = requirement.RequiredFields.Where(best.Value.Contains).ToArray();
        var missing = requirement.RequiredFields.Where(f => !best.Value.Contains(f)).ToArray();
        bound = bound with { RecordType = record, ConfirmedFields = confirmed, MissingFields = missing };
        if (!path.Stages.Any(s => s.Kind == SourceStageKind.AdapterModel && s.TypeName == record))
            return Seal(requirement, bound with { Status = SourceContractStatus.Incompatible, Detail = $"The snapshot has no adapter model {record}." });
        if (missing.Length > 0)
            return Seal(requirement, bound with { Status = SourceContractStatus.Incompatible, Detail = $"The adapter in this snapshot does not read: {string.Join(", ", missing)}." });
        if (latestSnapshotId is { } latest && latest != snapshot.Id)
            return Seal(requirement, bound with { Status = SourceContractStatus.Outdated, Detail = "A newer source snapshot exists for this integration. Select it so the fixture is bound to the current source (needs review)." });
        return Seal(requirement, bound with
        {
            Status = SourceContractStatus.Compatible,
            Detail = $"All {confirmed.Length} fixture fields are read by {record} in this snapshot (source compatibility, not deployment correlation).",
        });
    }

    /// <summary>
    /// Source-declared CDC capture of a table: a change-data-capture candidate for the table in any analyzed snapshot (e.g. a connector table
    /// list in infrastructure source). Declared in source is not deployed — but without it a capture is not proven at all.
    /// </summary>
    public static (bool Captured, string Detail) CdcCapture(IEnumerable<IqrSourceSnapshot> snapshots, string schema, string table)
    {
        foreach (var snapshot in snapshots)
        {
            var discovery = SourceIntegrationDiscoveryEngine.Discover(snapshot);
            var match = discovery.Candidates.FirstOrDefault(c => c.Pattern == IntegrationPattern.ChangeDataCapture &&
                string.Equals(c.SourceEntity, table, StringComparison.OrdinalIgnoreCase) &&
                (c.SourceSchema is null || string.Equals(c.SourceSchema, schema, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
                return (true, $"Source snapshot {snapshot.Archive.FileName} declares change data capture of {schema}.{table}" +
                    $"{(match.CaptureTechnology is { } technology ? $" ({technology})" : "")} on {match.ChannelName ?? "an unnamed channel"} — declared in source, not verified as deployed.");
        }
        return (false, $"CDC capture of {schema}.{table} is not proven: no analyzed source declares a change-data-capture channel for the table. Until the capture configuration includes it, events for this table are not produced by the real pipeline.");
    }

    /// <summary>
    /// The snapshot a fixture binds to: an explicitly chosen Source Analysis snapshot (exact id), else the newest snapshot an earlier
    /// version uploaded for this integration. A standalone Source Analysis snapshot is never bound silently.
    /// </summary>
    public static async Task<(IqrSourceSnapshot? Selected, Guid? LatestId, string? Problem)> SelectAsync(IqrSourceStore sources, string environmentId, string integrationId,
        Guid? requested, CancellationToken ct)
    {
        var snapshots = (await sources.ListAsync(environmentId, ct)).Where(s => s.IntegrationId == integrationId).OrderByDescending(s => s.AnalyzedAt).ToList();
        var selected = requested is { } id ? snapshots.FirstOrDefault(s => s.Id == id) : snapshots.FirstOrDefault();
        if (requested is { } chosen && selected is null && await sources.FindSourceAnalysisAsync(chosen, ct) is { } analysis)
            selected = analysis with { IntegrationId = integrationId };
        var problem = requested is not null && selected is null ? "The selected source snapshot does not exist or does not belong to this integration." : null;
        return (selected, snapshots.FirstOrDefault()?.Id, problem);
    }

    private static SourceContractEvaluation Seal(SourceContractRequirement requirement, SourceContractEvaluation evaluation)
    {
        var text = string.Join("|", requirement.ScenarioId, requirement.ScenarioVersion, requirement.FixtureSchemaVersion, requirement.Resource, requirement.Operation,
            evaluation.SourceSnapshotId, evaluation.ArchiveSha256, evaluation.Status, evaluation.RecordType, string.Join(",", evaluation.ConfirmedFields));
        return evaluation with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16] };
    }
}
