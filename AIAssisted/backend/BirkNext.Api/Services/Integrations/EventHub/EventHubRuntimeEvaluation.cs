using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.EventHub;

/// <summary>
/// Pure comparison of the configured Event Hub expectation with what the read-only sources observed. Every comparison keeps both sides and a
/// typed state: an observed match is not a Pass, a difference is not a failure, an unreadable source is "Not authorized"/"Not assessed"
/// (never "missing"), and an observed consumer group never confirms which application reads with it.
/// </summary>
public static class EventHubRuntimeEvaluation
{
    /// <summary>
    /// Configured hubs of the platform against the Azure Resource Manager hub list. Enabled business hubs: matched, difference observed or missing
    /// in Azure. Observed hubs nobody configured: technical/support (known Debezium/Kafka Connect hubs, the platform's technical topics) or
    /// additional observed — neither is an error. Without a readable list every configured hub is Not authorized / Not assessed.
    /// </summary>
    public static List<EventHubHubComparison> CompareHubs(IntegrationPlatform platform, IEnumerable<IntegrationDefinition> integrations, EventHubNamespaceObservation observation)
    {
        var configured = integrations.Where(i => i.PlatformId == platform.Id && i.Kind == IntegrationKind.EventHub && !string.IsNullOrWhiteSpace(i.EndpointOrTopic)).ToList();
        var listed = observation is { State: IntegrationEvidenceState.Available, HubListState: IntegrationEvidenceState.Available };
        var byName = observation.Hubs.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var comparisons = new List<EventHubHubComparison>();
        foreach (var integration in configured)
        {
            var hub = integration.EndpointOrTopic!.Trim();
            var expectedPartitions = integration.PartitionCount ?? platform.DefaultPartitionCount;
            var expectedRetention = (integration.RetentionDays ?? platform.DefaultRetentionDays) is { } days ? days * 24L : (long?)null;
            if (!integration.Enabled)
            {
                comparisons.Add(new() { Hub = hub, IntegrationId = integration.Id, State = EventHubComparisonState.ConfiguredDisabled, ConfiguredPartitions = expectedPartitions, ConfiguredRetentionHours = expectedRetention,
                    ObservedPartitions = byName.GetValueOrDefault(hub)?.PartitionCount, ObservedRetentionHours = byName.GetValueOrDefault(hub)?.RetentionHours, ObservedStatus = byName.GetValueOrDefault(hub)?.Status,
                    Detail = "The integration is disabled, so it is not reviewed." });
                continue;
            }
            if (!listed)
            {
                var state = (observation.State == IntegrationEvidenceState.Available ? observation.HubListState : observation.State) == IntegrationEvidenceState.NotAuthorized
                    ? EventHubComparisonState.NotAuthorized : EventHubComparisonState.NotAssessed;
                comparisons.Add(new() { Hub = hub, IntegrationId = integration.Id, State = state, ConfiguredPartitions = expectedPartitions, ConfiguredRetentionHours = expectedRetention,
                    Detail = observation.State == IntegrationEvidenceState.Available ? observation.HubListReason : observation.Reason });
                continue;
            }
            if (!byName.TryGetValue(hub, out var observed))
            {
                comparisons.Add(new() { Hub = hub, IntegrationId = integration.Id, State = EventHubComparisonState.MissingInAzure, ConfiguredPartitions = expectedPartitions, ConfiguredRetentionHours = expectedRetention,
                    Detail = "Configured but not found in the namespace's hub list (Azure Resource Manager)." });
                continue;
            }
            var differences = new List<string>();
            if (expectedPartitions is { } ep && observed.PartitionCount is { } op && ep != op) differences.Add($"partitions configured {ep}, observed {op}");
            if (expectedRetention is { } er && observed.RetentionHours is { } orh && er != orh) differences.Add($"retention configured {er} h, observed {orh} h");
            if (observed.Status is { Length: > 0 } status && !string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)) differences.Add($"status {status}");
            comparisons.Add(new()
            {
                Hub = hub, IntegrationId = integration.Id, State = differences.Count == 0 ? EventHubComparisonState.ObservedMatch : EventHubComparisonState.DifferenceObserved,
                ConfiguredPartitions = expectedPartitions, ObservedPartitions = observed.PartitionCount, ConfiguredRetentionHours = expectedRetention, ObservedRetentionHours = observed.RetentionHours,
                ObservedStatus = observed.Status,
                Detail = differences.Count == 0 ? Observed(observed) : $"Difference observed: {string.Join("; ", differences)}.",
            });
        }
        if (listed)
        {
            var known = configured.Select(i => i.EndpointOrTopic!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in observation.Hubs.Where(h => !known.Contains(h.Name)).OrderBy(h => h.Name, StringComparer.Ordinal))
            {
                var technical = EventHubRuntimeSources.IsTechnical(platform, extra.Name);
                comparisons.Add(new()
                {
                    Hub = extra.Name, Technical = technical, State = technical ? EventHubComparisonState.TechnicalObserved : EventHubComparisonState.AdditionalObserved,
                    ObservedPartitions = extra.PartitionCount, ObservedRetentionHours = extra.RetentionHours, ObservedStatus = extra.Status,
                    Detail = technical ? "Platform-support hub (Debezium / Kafka Connect); never reviewed as a business integration." : "Observed in Azure but not configured in Integrations; not an error by itself.",
                });
            }
        }
        return comparisons;
    }

    private static string Observed(EventHubObservedHub hub) =>
        string.Join(" · ", new[] { hub.Status, hub.PartitionCount is { } p ? $"{p} partition{(p == 1 ? "" : "s")}" : null, hub.RetentionHours is { } h ? $"{h} h retention" : null }.OfType<string>());

    /// <summary>
    /// The consumer group checkpoints are read for, against the groups Azure lists for the hub. "Observed match" only means the group exists;
    /// the application mapping stays "Needs confirmation" unless a person confirmed the consumer — an observed group never confirms it.
    /// </summary>
    public static EventHubConsumerGroupComparison CompareGroup(IntegrationDefinition topic, IntegrationPlatform? platform, EvidenceResult<ConsumerGroupList>? groups)
    {
        var (expected, assumed) = IntegrationConfigurationRules.EffectiveConsumerGroup(topic, platform);
        var provenance = expected is null ? "Not configured"
            : assumed ? IntegrationRuntimeEvidenceSettings.ProvenanceLabel(platform?.RuntimeEvidence?.ExpectedConsumerGroupProvenance is IntegrationValueProvenance.NotSpecified or null
                ? IntegrationValueProvenance.ConfiguredAssumption : platform.RuntimeEvidence.ExpectedConsumerGroupProvenance)
            : IntegrationRuntimeEvidenceSettings.ProvenanceLabel(IntegrationValueProvenance.ConfiguredOnIntegration);
        // Only a person's confirmation of the consumer, with the group on the integration itself, is a confirmed mapping.
        var mapping = !assumed && expected is not null && topic.Consumer.MappingState == ConsumerMappingState.Confirmed ? "Confirmed" : "Needs confirmation";
        var comparison = new EventHubConsumerGroupComparison { IntegrationId = topic.Id, Hub = topic.EndpointOrTopic ?? "", Expected = expected, ExpectedProvenance = provenance, Mapping = mapping };
        if (groups is not { IsAvailable: true, Value: { } list })
            return comparison with
            {
                State = groups?.State == IntegrationEvidenceState.NotAuthorized ? EventHubComparisonState.NotAuthorized : EventHubComparisonState.NotAssessed,
                Reason = groups is null ? "The consumer-group list was not read." : $"{IntegrationReviewLabels.EvidenceState(groups.State)}: {groups.Reason}",
            };
        var observed = list.Names.ToList();
        if (expected is null)
            return comparison with { Observed = observed, State = EventHubComparisonState.NotAssessed, Reason = "No expected consumer group is configured to compare." };
        var found = observed.Any(n => string.Equals(n, expected, StringComparison.OrdinalIgnoreCase));
        return comparison with
        {
            Observed = observed, State = found ? EventHubComparisonState.ObservedMatch : EventHubComparisonState.DifferenceObserved,
            Reason = found ? $"{expected} exists on the hub; that does not show which application reads with it." : $"{expected} is not among the groups Azure lists for the hub.",
        };
    }

    /// <summary>Checkpoint configuration (where the store is, and how that was verified) apart from checkpoint runtime evidence (what was read).</summary>
    public static EventHubCheckpointSummary Checkpoint(IntegrationDefinition topic, IntegrationPlatform? platform, EvidenceResult<CheckpointEvidence>? checkpoint)
    {
        var (group, assumed) = IntegrationConfigurationRules.EffectiveConsumerGroup(topic, platform);
        var settings = platform?.RuntimeEvidence;
        var configuration = settings?.ResolvedCheckpointContainerUrl() is null ? "Not configured"
            : settings.CheckpointProvenance == IntegrationValueProvenance.SourceConfigurationVerified ? "From source configuration" : "Configured";
        return new EventHubCheckpointSummary
        {
            IntegrationId = topic.Id, Hub = topic.EndpointOrTopic ?? "", ConsumerGroup = group, GroupAssumed = assumed, Configuration = configuration,
            Runtime = checkpoint?.State, RuntimeReason = checkpoint is null ? group is null ? "No consumer group to look up checkpoints for." : "Not read." : checkpoint.Reason,
            Partitions = checkpoint?.Value?.Partitions.Count ?? 0, LastUpdated = checkpoint?.Value?.LastUpdated,
        };
    }
}
