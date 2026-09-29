using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>Targeted read-only orchestration of the existing IQR providers. No catalog writer, network probe,
/// Azure client or history writer is used. Role telemetry and group checkpoints cannot prove app/topic ownership.</summary>
public sealed class IntegrationMappingEvidenceService(
    IIntegrationAzureCredential azure, IEventHubMetadataSource metadata, IEventHubConsumerGroupSource groups,
    ICheckpointEvidenceSource checkpoints, ITelemetryEvidenceSource telemetry)
{
    public async Task<IntegrationMappingEvidenceCheck> CheckAsync(IntegrationDefinition definition, IntegrationPlatform? platform,
        IntegrationContractSet contracts, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var checks = new List<IntegrationMappingEvidenceItem>();
        var window = platform?.RuntimeEvidence?.ReviewWindowHours ?? IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours;
        void Add(string id, string label, IntegrationEvidenceState state, string summary,
            IntegrationEvidenceSource source = IntegrationEvidenceSource.Configuration, DateTimeOffset? captured = null,
            IntegrationEvidenceItemFreshness freshness = IntegrationEvidenceItemFreshness.Unknown, bool supports = false)
            => checks.Add(new() { CheckId = id, Label = label, State = state, Summary = summary, Provenance = source,
                CapturedAt = captured ?? started, Freshness = freshness, MissingReason = state == IntegrationEvidenceState.Available ? null : summary,
                SupportsMapping = supports });

        var consumerKnown = !string.IsNullOrWhiteSpace(definition.Consumer.DisplayName);
        Add("consumer", "Consumer configuration", consumerKnown ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotConfigured,
            consumerKnown ? $"{definition.Consumer.DisplayName} is configured ({definition.Consumer.MappingState}). Configuration is not observed processing."
                : "No consumer is configured or suggested. Assign a consumer before checking its relationship.");
        Add("provenance", "Stored mapping provenance", IntegrationEvidenceState.Available,
            $"{definition.Consumer.MappingState}: {definition.Consumer.MappingSource ?? "No source recorded"}. {definition.Consumer.MappingEvidence}");
        Add("identity", "Consumer authentication", IntegrationEvidenceState.Available,
            $"Configured mechanism: {IntegrationConfigurationRules.AuthLabel(definition.ConsumerAuthentication)}. Identity: {definition.Consumer.ManagedIdentity ?? "not configured"}.");
        Add("rbac", "Application existence and receiver capability", IntegrationEvidenceState.NotSupported,
            "Existing IQR providers do not inspect application existence or RBAC assignments. Namespace receiver rights would establish capability only, not a topic subscription.");
        var (group, assumed) = IntegrationReviewEngine.EffectiveGroup(definition, platform);
        var groupProvenance = assumed ? platform!.RuntimeEvidence!.ExpectedConsumerGroupProvenance : group is null ? IntegrationValueProvenance.NotSpecified : IntegrationValueProvenance.ConfiguredOnIntegration;
        // A platform expectation is never a confirmed group, whatever provenance it was saved with.
        if (assumed && groupProvenance is IntegrationValueProvenance.ConfirmedByPerson or IntegrationValueProvenance.ConfiguredOnIntegration or IntegrationValueProvenance.NotSpecified) groupProvenance = IntegrationValueProvenance.ConfiguredAssumption;
        Add("configured-group", assumed ? "Expected consumer group" : "Configured consumer group", group is null ? IntegrationEvidenceState.NotConfigured : IntegrationEvidenceState.Available,
            assumed ? $"Expected: {group} ({IntegrationRuntimeEvidenceSettings.ProvenanceLabel(groupProvenance)}, platform setting). Used for the checkpoint lookup only; it is not proof of the consumer relationship. Mapping: Needs confirmation."
            : group is { } configuredGroup ? $"Configured: {configuredGroup}. This is not proof of the consumer relationship."
            : "Consumer group not configured and the platform states no expected group; no default is assumed.");

        var blocked = !consumerKnown ? "No consumer is configured or suggested; no runtime calls were attempted."
            : !definition.Enabled ? "Integration is disabled; no runtime calls were attempted."
            : definition.Kind != IntegrationKind.EventHub ? "Mapping evidence for this integration kind is not supported."
            : platform is null || string.IsNullOrWhiteSpace(definition.EndpointOrTopic) ? "Platform or topic is not configured."
            : azure.Credential is null ? azure.DisabledReason : null;

        async Task<EvidenceResult<T>> Read<T>(string id, string label, IntegrationEvidenceSource source,
            IntegrationEvidenceAdapterStatus? status, string? missing, Func<CancellationToken, Task<EvidenceResult<T>>> read) where T : class
        {
            EvidenceResult<T> result;
            if (blocked is not null || status?.State != IntegrationEvidenceState.Available || missing is not null)
                result = EvidenceResult<T>.Missing(blocked is not null ? IntegrationEvidenceState.NotConfigured : status?.State != IntegrationEvidenceState.Available ? status?.State ?? IntegrationEvidenceState.NotConfigured : IntegrationEvidenceState.NotConfigured,
                    source, blocked ?? (status?.State != IntegrationEvidenceState.Available ? status?.Reason : missing) ?? "Source not configured.");
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { result = await read(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = EvidenceResult<T>.Missing(IntegrationEvidenceState.Unavailable, source, "Evidence source timed out; the mapping has not failed."); }
                catch (Exception ex) when (ex is not OperationCanceledException) { result = EvidenceResult<T>.Missing(IntegrationEvidenceState.Error, source, $"Evidence source error ({ex.GetType().Name}); no mapping conclusion was made."); }
            }
            if (!result.IsAvailable) Add(id, label, result.State, result.Reason, result.Source, result.CapturedAt);
            return result;
        }

        var hub = await Read("topic", "Event Hub metadata", IntegrationEvidenceSource.AzureMetadata,
            platform is null ? null : metadata.Describe(platform), null, token => metadata.GetHubAsync(platform!, definition.EndpointOrTopic!, token));
        if (hub.IsAvailable) Add("topic", "Event Hub metadata", hub.Value!.Exists ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotFound,
            hub.Value.Exists ? $"Topic exists; {hub.Value.Partitions.Count} partitions observed. Existence does not establish the consumer relationship." : "Topic was not found.", hub.Source, hub.CapturedAt,
            IntegrationReviewLabels.FreshnessOf(hub.CapturedAt, DateTimeOffset.UtcNow, window), hub.Value.Exists);

        var groupList = await Read("groups", "Consumer-group evidence", IntegrationEvidenceSource.AzureResourceManager,
            platform is null ? null : groups.Describe(platform), null, token => groups.ListAsync(platform!, definition.EndpointOrTopic!, token));
        if (groupList.IsAvailable) Add("groups", "Consumer-group evidence", IntegrationEvidenceState.Available,
            groupList.Value!.Names.Count == 0 ? "No consumer groups found." : $"Observed candidates: {string.Join(", ", groupList.Value.Names)}. None was selected or saved; group existence does not establish application ownership.",
            groupList.Source, groupList.CapturedAt, IntegrationReviewLabels.FreshnessOf(groupList.CapturedAt, DateTimeOffset.UtcNow, window), groupList.Value.Names.Count > 0);

        var checkpoint = await Read("checkpoint", "Checkpoint evidence", IntegrationEvidenceSource.CheckpointStore,
            platform is null ? null : checkpoints.Describe(platform), group is null ? "Consumer group not configured; checkpoints cannot be attributed." : null,
            token => checkpoints.GetAsync(platform!, definition.EndpointOrTopic!, group!, token));
        if (checkpoint.IsAvailable) Add("checkpoint", "Checkpoint evidence", checkpoint.Value!.Partitions.Count > 0 ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotFound,
            $"{checkpoint.Value.Partitions.Count} checkpoint partition(s) for group {checkpoint.Value.ConsumerGroup}{(assumed ? " (configured assumption)" : "")}. This does not identify the application owning the group.", checkpoint.Source, checkpoint.CapturedAt,
            IntegrationReviewLabels.FreshnessOf(checkpoint.Value.LastUpdated, DateTimeOffset.UtcNow, window), checkpoint.Value.Partitions.Count > 0);

        var freshCheckpoint = checkpoint.IsAvailable && checkpoint.Value!.ConsumerGroup == group
            && IntegrationReviewLabels.FreshnessOf(checkpoint.Value.LastUpdated, DateTimeOffset.UtcNow, window) is IntegrationEvidenceItemFreshness.Current or IntegrationEvidenceItemFreshness.Recent;
        var lag = hub is { IsAvailable: true, Value.Exists: true } && freshCheckpoint ? IntegrationReviewEngine.Lag(hub.Value, checkpoint.Value!).Lag : null;
        Add("lag", "Lag", lag is null ? IntegrationEvidenceState.Unavailable : IntegrationEvidenceState.Available,
            lag is null ? "Comparable topic positions and current checkpoints are unavailable; lag is unknown, not zero." : $"Observed lag: {lag} events across comparable partitions; not proof of application ownership.", IntegrationEvidenceSource.CheckpointStore);

        var tel = await Read("telemetry", "Application Insights evidence", IntegrationEvidenceSource.ApplicationInsights,
            platform is null ? null : telemetry.Describe(platform), string.IsNullOrWhiteSpace(definition.Consumer.ContainerApp) ? "Consumer application identifier is not configured for telemetry attribution." : null,
            token => telemetry.GetConsumerAsync(platform!, definition.Consumer.ContainerApp!, window, token));
        if (tel.IsAvailable) Add("telemetry", "Application Insights evidence", IntegrationEvidenceState.Available,
            $"{tel.Value!.ProcessingTraces} processing trace(s) for the configured application in {window} hours. Existing telemetry is application-scoped, not an exact topic correlation.", tel.Source, tel.CapturedAt,
            IntegrationReviewLabels.FreshnessOf(tel.Value.LastActivity, DateTimeOffset.UtcNow, window), tel.Value.ProcessingTraces > 0);

        foreach (var role in Enum.GetValues<IntegrationContractRole>())
        {
            var contract = contracts.Of(definition.Id, role);
            Add("contract-" + role, role + " contract", contract?.Contract is not null ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotConfigured,
                contract?.Contract is not null ? $"{contract.Value.Artifact.FileName} ({contract.Value.Artifact.ShortHash}); contract presence does not prove subscription."
                    : contract?.Problem ?? $"{role} contract not configured.", IntegrationEvidenceSource.ContractArtifact);
        }
        if (contracts.Of(definition.Id, IntegrationContractRole.Producer)?.Contract is { } producer && contracts.Of(definition.Id, IntegrationContractRole.Consumer)?.Contract is { } consumer)
            Add("compatibility", "Contract compatibility", IntegrationEvidenceState.Available,
                $"{EventContractComparer.Compare(producer, consumer).Count} declared contract difference(s). Compatibility is not mapping proof.", IntegrationEvidenceSource.ContractArtifact);
        Add("relationship", "Direct topic → consumer relationship", IntegrationEvidenceState.NotSupported,
            "No direct relationship evidence: current providers cannot associate this exact topic with this exact consumer. Group checkpoints and application-level telemetry are supporting evidence only.");

        var state = Classify(checks, blocked is not null);
        return new() { IntegrationId = definition.Id, Topic = definition.EndpointOrTopic, SuggestedConsumer = definition.Consumer.DisplayName,
            StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, OverallState = state, Checks = checks,
            ExpectedConsumerGroup = group, ExpectedConsumerGroupProvenance = groupProvenance,
            ObservedConsumerGroups = groupList.IsAvailable ? groupList.Value!.Names.ToList() : null, MappingState = definition.Consumer.MappingState,
            ManualFollowUp = ["Confirm the mapping only if the consumer relationship is known from a trusted source. Running this check never confirms it.",
                "Configure missing runtime evidence sources and consumer identifiers, then run the check again. No supporting evidence does not mean the mapping is wrong."] };
    }

    private static readonly string[] RuntimeChecks = ["topic", "groups", "checkpoint", "telemetry"];

    /// <summary>
    /// Evidence strength from the gathered items — never from configuration or provenance. Strong needs a CURRENT item that directly ties this
    /// exact topic to this exact consumer (<see cref="IntegrationMappingEvidenceItem.DirectRelationship"/>); existence, reachability, receiver
    /// capability, group checkpoints and application-scoped telemetry are supporting only (Partial). Stale or unknown-age items never count.
    /// No current provider emits a direct item, so Strong is not reachable from runtime reads in this build.
    /// </summary>
    public static IntegrationMappingEvidenceState Classify(IReadOnlyList<IntegrationMappingEvidenceItem> checks, bool blocked)
    {
        if (blocked) return IntegrationMappingEvidenceState.NotTestable;
        static bool Current(IntegrationMappingEvidenceItem c) => c.State == IntegrationEvidenceState.Available
            && c.Freshness is IntegrationEvidenceItemFreshness.Current or IntegrationEvidenceItemFreshness.Recent;
        if (checks.Any(c => c.DirectRelationship && Current(c))) return IntegrationMappingEvidenceState.StrongEvidence;
        if (checks.Any(c => c.SupportsMapping && Current(c))) return IntegrationMappingEvidenceState.PartialEvidence;
        var runtime = checks.Where(c => RuntimeChecks.Contains(c.CheckId)).ToList();
        if (runtime.Any(c => c.State is IntegrationEvidenceState.Available or IntegrationEvidenceState.NotFound)) return IntegrationMappingEvidenceState.NoSupportingEvidence;
        return runtime.Any(c => c.State == IntegrationEvidenceState.Error) ? IntegrationMappingEvidenceState.Error : IntegrationMappingEvidenceState.NotTestable;
    }
}
