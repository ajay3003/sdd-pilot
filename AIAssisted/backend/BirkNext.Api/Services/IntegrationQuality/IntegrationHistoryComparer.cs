using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 5: compares a previous snapshot's integration set against the current one
/// and reports typed historical changes.
///
/// This owns set-level and relationship-level history only. Contract difference semantics stay
/// in ContractComparer.CompareForDrift; nothing here re-implements them. Changes are reported
/// as facts, without inventing severity: an integration appearing or disappearing is not
/// automatically breaking.
/// </summary>
public sealed class IntegrationHistoryComparer
{
    public IReadOnlyList<IntegrationHistoricalChange> Compare(
        IntegrationQualitySnapshot? previous,
        IReadOnlyList<IntegrationSnapshotEntry> current)
    {
        if (previous is null)
            return [];

        var changes = new List<IntegrationHistoricalChange>();

        var previousByKey = previous.Integrations
            .GroupBy(e => e.BaselineKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var currentByKey = current
            .GroupBy(e => e.BaselineKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var entry in current.Where(e => !previousByKey.ContainsKey(e.BaselineKey)))
        {
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.IntegrationAdded,
                BaselineKey = entry.BaselineKey,
                IntegrationName = entry.DisplayName,
                NewValue = entry.DisplayName,
                Description = $"Integration '{entry.DisplayName}' is present in this review but not in the previous one"
            });
        }

        foreach (var entry in previous.Integrations.Where(e => !currentByKey.ContainsKey(e.BaselineKey)))
        {
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.IntegrationRemoved,
                BaselineKey = entry.BaselineKey,
                IntegrationName = entry.DisplayName,
                OldValue = entry.DisplayName,
                Description = $"Integration '{entry.DisplayName}' was present in the previous review but not in this one"
            });
        }

        foreach (var (key, currentEntry) in currentByKey)
        {
            if (!previousByKey.TryGetValue(key, out var previousEntry))
                continue;

            AddRelationshipChanges(previousEntry, currentEntry, changes);
            AddAuthenticationChanges(previousEntry, currentEntry, changes);
            AddRuntimeEvidenceChanges(previousEntry, currentEntry, changes);
        }

        return changes
            .OrderBy(c => c.IntegrationName, StringComparer.Ordinal)
            .ThenBy(c => c.Type)
            .ToList();
    }

    private static void AddRelationshipChanges(
        IntegrationSnapshotEntry previous,
        IntegrationSnapshotEntry current,
        List<IntegrationHistoricalChange> changes)
    {
        if (!string.Equals(previous.Producer, current.Producer, StringComparison.Ordinal))
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.ProducerChanged,
                BaselineKey = current.BaselineKey,
                IntegrationName = current.DisplayName,
                OldValue = previous.Producer,
                NewValue = current.Producer,
                Description = $"Producer changed from '{Describe(previous.Producer)}' to '{Describe(current.Producer)}'"
            });

        if (!string.Equals(previous.Consumer, current.Consumer, StringComparison.Ordinal))
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.ConsumerChanged,
                BaselineKey = current.BaselineKey,
                IntegrationName = current.DisplayName,
                OldValue = previous.Consumer,
                NewValue = current.Consumer,
                Description = $"Consumer changed from '{Describe(previous.Consumer)}' to '{Describe(current.Consumer)}'"
            });

        if (previous.RelationshipSource != current.RelationshipSource)
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.RelationshipSourceChanged,
                BaselineKey = current.BaselineKey,
                IntegrationName = current.DisplayName,
                OldValue = previous.RelationshipSource.ToString(),
                NewValue = current.RelationshipSource.ToString(),
                Description = $"Relationship source changed from {previous.RelationshipSource} to {current.RelationshipSource}"
            });
    }

    private static void AddAuthenticationChanges(
        IntegrationSnapshotEntry previous,
        IntegrationSnapshotEntry current,
        List<IntegrationHistoricalChange> changes)
    {
        if (previous.AuthenticationRequired != current.AuthenticationRequired)
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.AuthenticationRequiredChanged,
                BaselineKey = current.BaselineKey,
                IntegrationName = current.DisplayName,
                OldValue = Describe(previous.AuthenticationRequired),
                NewValue = Describe(current.AuthenticationRequired),
                Description = "Authentication requirement changed since the previous review"
            });

        // Capability availability is not the same fact as authenticated execution, so it is
        // reported separately rather than merged.
        if (previous.AuthenticatedCapabilityAvailable != current.AuthenticatedCapabilityAvailable)
            changes.Add(new IntegrationHistoricalChange
            {
                Type = IntegrationHistoricalChangeType.AuthenticatedCapabilityChanged,
                BaselineKey = current.BaselineKey,
                IntegrationName = current.DisplayName,
                OldValue = Describe(previous.AuthenticatedCapabilityAvailable),
                NewValue = Describe(current.AuthenticatedCapabilityAvailable),
                Description = "Authenticated review capability changed since the previous review"
            });
    }

    private static void AddRuntimeEvidenceChanges(
        IntegrationSnapshotEntry previous,
        IntegrationSnapshotEntry current,
        List<IntegrationHistoricalChange> changes)
    {
        if (previous.RuntimeEvidenceState == current.RuntimeEvidenceState)
            return;

        // An evidence state change is a change in what was observed, not proof that the
        // integration failed. Absence of current evidence is reported as exactly that.
        changes.Add(new IntegrationHistoricalChange
        {
            Type = IntegrationHistoricalChangeType.RuntimeEvidenceStateChanged,
            BaselineKey = current.BaselineKey,
            IntegrationName = current.DisplayName,
            OldValue = previous.RuntimeEvidenceState.ToString(),
            NewValue = current.RuntimeEvidenceState.ToString(),
            Description = previous.RuntimeEvidenceState == RuntimeEvidenceState.Observed
                          && current.RuntimeEvidenceState != RuntimeEvidenceState.Observed
                ? "Runtime traffic was observed in the previous review but not in this one"
                : $"Runtime evidence state changed from {previous.RuntimeEvidenceState} to {current.RuntimeEvidenceState}"
        });
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not recorded" : value;

    private static string Describe(bool? value) =>
        value switch { true => "yes", false => "no", null => "not recorded" };
}

public enum IntegrationHistoricalChangeType
{
    IntegrationAdded = 0,
    IntegrationRemoved = 1,
    ProducerChanged = 2,
    ConsumerChanged = 3,
    RelationshipSourceChanged = 4,
    AuthenticationRequiredChanged = 5,
    AuthenticatedCapabilityChanged = 6,
    RuntimeEvidenceStateChanged = 7
}

/// <summary>
/// A typed historical fact. Severity is deliberately absent: these are observations, and policy
/// about what they mean belongs elsewhere.
/// </summary>
public sealed class IntegrationHistoricalChange
{
    [JsonPropertyName("type")]
    public IntegrationHistoricalChangeType Type { get; init; }

    [JsonPropertyName("baselineKey")]
    public string BaselineKey { get; init; } = "";

    [JsonPropertyName("integrationName")]
    public string IntegrationName { get; init; } = "";

    [JsonPropertyName("oldValue")]
    public string? OldValue { get; init; }

    [JsonPropertyName("newValue")]
    public string? NewValue { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
}
