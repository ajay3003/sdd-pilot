using System.Diagnostics;
using System.Globalization;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.ServiceBus;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>Trusted event contracts of one environment, loaded for a run (artifact metadata, parsed contract or the reason it no longer parses).</summary>
public sealed record IntegrationContractSet(IReadOnlyList<(IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)> Items)
{
    public static readonly IntegrationContractSet Empty = new([]);
    public (IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)? Of(string integrationId, IntegrationContractRole role) =>
        Items.FirstOrDefault(i => i.Artifact.IntegrationId == integrationId && i.Artifact.Role == role) is { Artifact: not null } found ? found : null;
}

/// <summary>
/// Integration Quality Review over the configured catalog (Target Environment → Integrations) and read-only evidence: the namespace probe,
/// Event Hub metadata, the consumer-group list, the checkpoint store, consumer telemetry and trusted contract artifacts. Configured
/// expectation, runtime evidence, contract evidence and the review conclusion stay four different things: configuration alone never
/// produces a runtime Pass, a missing source is "Not assessed" with its exact reason, and absent evidence is never a zero.
/// </summary>
public sealed class IntegrationReviewEngine(
    IIntegrationNamespaceProbe namespaceProbe,
    IEventHubMetadataSource metadata,
    IEventHubConsumerGroupSource consumerGroups,
    ICheckpointEvidenceSource checkpoints,
    ITelemetryEvidenceSource telemetry,
    HttpClient http,
    ILogger<IntegrationReviewEngine> logger,
    IApplicationMessagingTelemetrySource? messagingTelemetry = null,
    IIntegrationAzureCredential? azure = null,
    ServiceBusEvidenceService? serviceBus = null,
    BirkNext.Api.Services.Integrations.Scim.IScimEvidenceService? scim = null,
    BirkNext.Api.Services.SecurityClassification.IClassificationReviewService? classification = null,
    EventHub.IEventHubNamespaceSource? namespaces = null,
    EventHub.IEventHubMetricsSource? eventHubMetrics = null)
{
    private const string NoSafeEventSource = "No safe runtime event-structure source is configured; events are never consumed to inspect them.";

    // ── Pre-run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<IntegrationEvidenceAdapterStatus> Adapters(IntegrationPlatform platform)
    {
        var adapters = new List<IntegrationEvidenceAdapterStatus> { metadata.Describe(platform), consumerGroups.Describe(platform), checkpoints.Describe(platform), telemetry.Describe(platform) };
        if (namespaces is not null) adapters.Add(namespaces.Describe(platform));
        if (eventHubMetrics is not null) adapters.Add(eventHubMetrics.Describe(platform));
        return adapters;
    }

    /// <summary>Whether this instance calls Azure: its identity when known, else (fakes in tests) whether any source is Available.</summary>
    private bool AzureEnabled(IEnumerable<IntegrationEvidenceAdapterStatus> adapters) =>
        azure is not null ? azure.Credential is not null : adapters.Any(a => a.State == IntegrationEvidenceState.Available);

    private string AzureDisabledReason => azure?.DisabledReason ?? IntegrationAzureCredential.DisabledMessage;

    public IntegrationReviewReadiness Readiness(IntegrationCatalog catalog, IntegrationContractSet contracts) => Readiness(catalog, contracts, null);

    /// <summary>Pre-run readiness. Application messaging evidence is summarized beside it and never changes a domain's readiness.</summary>
    public IntegrationReviewReadiness Readiness(IntegrationCatalog catalog, IntegrationContractSet contracts, ApplicationMessagingEvidenceSet? messaging) =>
        ReadinessCore(catalog, contracts, messaging) with
        {
            ApplicationMessaging = ApplicationMessagingReview.Summaries(catalog, messaging, azure),
            // Service Bus is its own transport: summarized here, never counted among the Event Hub runtime sources.
            ServiceBus = serviceBus is null ? [] : catalog.Platforms.Where(p => p.Enabled && ServiceBusEvidenceService.IsServiceBus(p)).Select(p => serviceBus.Readiness(p, messaging)).ToList(),
        };

    private IntegrationReviewReadiness ReadinessCore(IntegrationCatalog catalog, IntegrationContractSet contracts, ApplicationMessagingEvidenceSet? messaging)
    {
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = Systems(catalog, enabled);
        var eventHub = enabled.Where(i => i.Kind == IntegrationKind.EventHub).ToList();
        var platforms = catalog.Platforms.Where(p => eventHub.Any(i => i.PlatformId == p.Id)).ToList();
        var adapters = platforms.SelectMany(p => Adapters(p).Select(a => a with { Adapter = $"{a.Adapter} · {p.Name}" })).ToList();
        bool Configured(IEnumerable<IntegrationEvidenceAdapterStatus> statuses) => statuses.Any(s => s.State == IntegrationEvidenceState.Available);
        var metadataReady = Configured(platforms.Select(metadata.Describe));
        var groupsReady = Configured(platforms.Select(consumerGroups.Describe));
        var checkpointReady = Configured(platforms.Select(checkpoints.Describe));
        var telemetryReady = Configured(platforms.Select(telemetry.Describe));
        var namespaceReady = namespaces is not null && Configured(platforms.Select(namespaces.Describe));
        var metricsReady = eventHubMetrics is not null && Configured(platforms.Select(eventHubMetrics.Describe));
        var azureOn = AzureEnabled(adapters);
        // Configured sources (settings only), kept apart from whether this instance calls Azure at all.
        var metadataConfigured = platforms.Any(EventHubRuntimeSources.Metadata);
        var armConfigured = platforms.Any(EventHubRuntimeSources.ResourceManager);
        var checkpointConfigured = platforms.Any(EventHubRuntimeSources.Checkpoints);
        var telemetryConfigured = platforms.Any(EventHubRuntimeSources.Telemetry);
        string Gap(bool configured, string what) =>
            !configured ? $"{what}: not configured" : !azureOn ? $"{what}: configured, not read — Azure runtime is not enabled for this instance" : $"{what}: configured, not readable (see Runtime evidence)";
        var appBound = messaging?.Applications.Any(a => a.BoundConsumer is not null) == true;
        var unknownGroups = eventHub.Count(i => EffectiveGroup(i, catalog.Platforms.FirstOrDefault(p => p.Id == i.PlatformId)).Group is null);
        var unknownRoles = eventHub.Count(i => string.IsNullOrWhiteSpace(i.Consumer.ContainerApp));
        var both = enabled.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null && contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null);
        var oneSided = enabled.Count(i => (contracts.Of(i.Id, IntegrationContractRole.Producer) is not null) ^ (contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null));
        var producerContracts = eventHub.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null);
        var thresholds = platforms.Any(p => p.RuntimeEvidence is { MaxConsumerLagEvents: not null } or { MaxCheckpointAgeMinutes: not null });
        var provider = catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.MonitoringProvider));

        IntegrationDomainReadinessRow Row(IntegrationReviewDomain domain, IntegrationDomainReadiness readiness, string explanation, IEnumerable<string?>? available = null, IEnumerable<string?>? missing = null) =>
            new(domain, readiness, explanation, (available ?? []).OfType<string>().ToList(), (missing ?? []).OfType<string>().ToList());
        var azureMetadata = metadataReady || namespaceReady;
        var producer = platforms.Select(p => p.ProducerTechnology).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var expectedGroup = platforms.Select(p => p.RuntimeEvidence?.ExpectedConsumerGroup).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g))?.Trim();
        var providers = catalog.Platforms.Select(p => p.MonitoringProvider).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var appInsights = platforms.Select(p => p.RuntimeEvidence?.ApplicationInsightsResourceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
        var azureMonitorLogs = platforms.Any(p => string.Equals(p.RuntimeEvidence?.ContainerAppsLogDestination?.Trim(), "azure-monitor", StringComparison.OrdinalIgnoreCase));
        var logAnalytics = $"Dedicated Log Analytics workspace: {EventHubRuntimeSources.LogAnalyticsLabel(platforms.FirstOrDefault()?.RuntimeEvidence)}.";
        var hasFqdn = catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.NamespaceFqdn));
        var transport = azureMetadata || checkpointReady || telemetryReady;
        var perfEvidence = metricsReady || metadataReady || checkpointReady || telemetryReady;
        var consumerAuth = eventHub.Select(i => i.ConsumerAuthentication).Where(a => a != IntegrationAuthMechanism.NotConfigured).Distinct().Select(IntegrationConfigurationRules.AuthLabel).ToList();
        var domains = new List<IntegrationDomainReadinessRow>
        {
            enabled.Count == 0 ? Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.NotAssessable, "No enabled integration is configured.")
                : Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready,
                    azureMetadata ? "Configured Event Hubs compared with Azure runtime metadata."
                        : $"{enabled.Count} enabled integration{(enabled.Count == 1 ? "" : "s")} will be reviewed against the configured expectation; the comparison with Azure needs runtime evidence.",
                    [$"{enabled.Count} configured integration{(enabled.Count == 1 ? "" : "s")}: expected Event Hubs, partitions, retention, consumer group, checkpoint store, authentication and monitoring",
                        namespaceReady ? "Configured vs observed hubs (Azure Resource Manager: status, partitions, retention)" : metadataReady ? "Configured vs observed hubs (Event Hub metadata: existence, partitions)" : null],
                    [azureMetadata ? null : Gap(metadataConfigured || armConfigured, "Comparison with Azure runtime metadata")]),
            !hasFqdn ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, "No namespace FQDN is configured to probe.")
                : azureMetadata ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Ready, "DNS/TCP/TLS and Azure metadata access are observed separately; reachable is not authorized.",
                    ["Namespace DNS, TCP 443 and TLS probe", "Azure metadata access with the BirkNext identity (read-only)"])
                : Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Limited,
                    "Namespace reachability can be probed (DNS, TCP, TLS). Event Hub existence and Azure metadata access need Event Hub metadata, which is not read.",
                    ["Namespace DNS, TCP 443 and TLS probe"], [Gap(metadataConfigured || armConfigured, "Azure metadata access")]),
            both > 0 ? Row(IntegrationReviewDomain.Contract, oneSided > 0 || both < enabled.Count ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.Ready,
                    $"{both} integration(s) have producer and consumer contracts{(both < enabled.Count ? $"; {enabled.Count - both} lack one or both" : "")}.",
                    [$"Producer and consumer JSON Schemas for {both} integration(s)"], [both < enabled.Count ? $"Contracts for {enabled.Count - both} integration(s)" : null])
                : oneSided > 0 ? Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Limited, $"{oneSided} integration(s) have one contract only; compatibility needs both.",
                    [$"One-sided contracts for {oneSided} integration(s)"], ["The other side's contract"])
                : Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, "No producer/consumer contract is uploaded. Event Hub runtime metadata does not make contracts assessable.",
                    [], ["Producer and consumer JSON Schemas"]),
            transport ? Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Partial,
                    "Transport progression evidence can be read; application processing is separate evidence and end-to-end flow is never proven by transport evidence.",
                    [producer is null ? null : $"Producer configured ({producer})",
                        azureMetadata ? $"Event Hub observed in Azure ({(metadataReady ? "existence, last enqueued position" : "existence, status")})" : null,
                        groupsReady ? expectedGroup is { } g ? $"Expected consumer group {g} compared with the groups Azure lists (configured assumption — mapping needs confirmation)" : "Consumer groups Azure lists for each hub" : null,
                        checkpointReady ? "Checkpoint progression (blob metadata, read-only)" : null,
                        telemetryReady ? "Consumer processing telemetry (Application Insights)" : null,
                        appBound ? "Application handlers from analyzed source (Wolverine) — configuration, not processing" : null],
                    [azureMetadata ? null : Gap(metadataConfigured || armConfigured, "Event Hub runtime metadata"), groupsReady ? null : Gap(armConfigured, "Consumer-group list"),
                        checkpointReady ? null : Gap(checkpointConfigured, "Checkpoint evidence"), telemetryReady ? null : Gap(telemetryConfigured, "Application-processing telemetry"),
                        "End-to-end processing: never proven by transport evidence"])
                : Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.NotAssessable,
                    !azureOn && (metadataConfigured || armConfigured || checkpointConfigured || telemetryConfigured)
                        ? "Transport configured; runtime evidence sources are configured but not read because Azure runtime is not enabled for this instance."
                        : "Event Hub metadata access is not configured.",
                    [producer is null ? null : $"Producer configured ({producer})", appBound ? "Application handlers from analyzed source (Wolverine) — configuration, not processing" : null],
                    [Gap(metadataConfigured || armConfigured, "Event Hub runtime metadata"), Gap(checkpointConfigured, "Checkpoint evidence"), Gap(telemetryConfigured, "Application-processing telemetry")]),
            !checkpointReady && !telemetryReady ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.NotAssessable,
                    checkpointConfigured && !azureOn ? "Checkpoint store configured; runtime checkpoint evidence is not read because Azure runtime is not enabled for this instance." : "No checkpoint or telemetry evidence source is configured.",
                    [checkpointConfigured ? "Checkpoint store configured" : null], [Gap(checkpointConfigured, "Checkpoint evidence"), Gap(telemetryConfigured, "Retry/failure telemetry"), "Replay / duplicate handling: manual verification"])
                : unknownGroups > 0 || !checkpointReady ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Limited,
                    !checkpointReady ? "No checkpoint evidence source configured; telemetry indicators only." : $"Consumer group unknown for {unknownGroups} integration(s).",
                    [checkpointReady ? "Checkpoint presence and age (read-only)" : null, telemetryReady ? "Retry and dependency-failure indicators (telemetry)" : null],
                    [checkpointReady ? null : Gap(checkpointConfigured, "Checkpoint evidence"), unknownGroups > 0 ? $"Consumer group for {unknownGroups} integration(s)" : null, "Replay / duplicate handling: manual verification"])
                : Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Partial,
                    thresholds ? "Checkpoint/runtime evidence available; judged only against the configured IQR threshold." : "Checkpoint/runtime evidence available. No configured checkpoint-age threshold, so age is Observed only.",
                    ["Checkpoint presence and age (read-only)", telemetryReady ? "Retry and dependency-failure indicators (telemetry)" : null],
                    [telemetryReady ? null : Gap(telemetryConfigured, "Retry/failure telemetry"), thresholds ? null : "Checkpoint-age threshold: none configured", "Replay / duplicate handling: manual verification"]),
            !telemetryReady && !metricsReady ? Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.NotAssessable,
                    telemetryConfigured && !azureOn ? "Telemetry configured; not read because Azure runtime is not enabled for this instance." : "No telemetry source configured.",
                    [appBound ? "Application error handling from analyzed source (separate from transport)" : null],
                    [eventHubMetrics is null ? null : Gap(armConfigured, "Transport error metrics"), Gap(telemetryConfigured, "Consumer exception telemetry")])
                : telemetryReady && !metricsReady && unknownRoles > 0 ? Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.Limited,
                    $"Telemetry configured; {unknownRoles} integration(s) have no consumer application to attribute telemetry to.",
                    ["Consumer exception, deserialization and dead-letter indicators (telemetry)"], [$"Consumer application for {unknownRoles} integration(s)"])
                : Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.Partial, "Azure transport errors can be observed; application error handling evidence remains separate.",
                    [metricsReady ? "Transport errors: server errors, user errors and throttling (namespace metrics)" : null, telemetryReady ? "Consumer exception, deserialization and dead-letter indicators (telemetry)" : null,
                        appBound ? "Application error handling from analyzed source (separate from transport)" : null],
                    [metricsReady || eventHubMetrics is null ? null : Gap(armConfigured, "Transport error metrics"), telemetryReady ? null : Gap(telemetryConfigured, "Consumer exception telemetry")]),
            Row(IntegrationReviewDomain.Security, enabled.Count == 0 ? IntegrationDomainReadiness.NotAssessable : IntegrationDomainReadiness.Limited,
                (consumerAuth.Contains("Managed Identity") ? "Managed Identity configured; " : "Configured authentication; ") + "runtime authorization evidence limited to read-only Azure access.",
                [consumerAuth.Count > 0 ? $"Configured consumer authentication ({string.Join(", ", consumerAuth)})" : null, hasFqdn ? "TLS on the namespace (probe)" : null,
                    namespaceReady ? "Namespace network access and local (SAS) authentication settings (read-only)" : null, telemetryReady ? "Authorization failure indicators (telemetry)" : null],
                ["Consumer identity authorization (RBAC): not evaluated — Managed Identity configured is not authorization proven", namespaceReady ? null : Gap(armConfigured, "Namespace network settings")]),
            providers.Count == 0 ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "No monitoring provider is configured.", [], ["Monitoring provider"])
                : Row(IntegrationReviewDomain.Observability, telemetryReady ? IntegrationDomainReadiness.Partial : IntegrationDomainReadiness.Limited,
                    (telemetryReady ? $"{string.Join(", ", providers)} configured; runtime telemetry can be read." : $"Monitoring provider configured; runtime telemetry is not {(telemetryConfigured && !azureOn ? "read (Azure runtime is not enabled)" : "configured")}.")
                        + (azureMonitorLogs ? " Container App logs go to Azure Monitor." : "") + $" {logAnalytics}",
                    [$"Monitoring provider: {string.Join(", ", providers)}", appInsights is null ? null : $"Application Insights resource {appInsights}", azureMonitorLogs ? "Container App logs: Azure Monitor" : null,
                        telemetryReady ? "Runtime telemetry and its freshness (bounded aggregate queries)" : null],
                    [telemetryReady ? null : Gap(telemetryConfigured, "Runtime telemetry")]),
            !perfEvidence ? Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable,
                    "No measured timing, lag or backlog evidence source. Configuration values are never performance evidence." + (!azureOn && (metadataConfigured || armConfigured || checkpointConfigured || telemetryConfigured) ? " Runtime sources are configured but Azure runtime is not enabled." : ""),
                    [], [Gap(armConfigured, "Throughput metrics"), Gap(metadataConfigured && checkpointConfigured, "Consumer lag (partition positions + checkpoints)"), Gap(telemetryConfigured, "Processing duration")])
                : Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.Partial,
                    thresholds ? "Measured event age, lag and processing time; judged against the configured IQR thresholds, other values Observed." : "Runtime throughput/lag evidence shown as Observed. No thresholds configured.",
                    [metricsReady ? "Event Hub throughput and request metrics (Azure Monitor)" : null, metadataReady ? "Newest event age (Event Hub metadata)" : null,
                        metadataReady && checkpointReady ? "Consumer lag (last enqueued minus checkpointed sequence number)" : null, telemetryReady ? "Event Hubs call duration (telemetry)" : null],
                    [metadataReady && checkpointReady ? null : "Consumer lag: needs partition positions and checkpoints", telemetryReady ? null : "Processing duration: needs telemetry",
                        thresholds ? null : "Thresholds: none configured — measured values are Observed only"]),
            producerContracts > 0 ? Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.Limited, $"Envelope structure declared by {producerContracts} producer contract(s). {NoSafeEventSource}",
                    [$"Debezium envelope declared by {producerContracts} producer contract(s)"], ["Safe event-structure evidence: events are never read"])
                : Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, $"No producer contract. {NoSafeEventSource}",
                    [], ["Producer contract", "Safe event-structure evidence: events are never read"]),
        };

        var canRun = enabled.Count > 0;
        var limited = domains.Any(d => d.Readiness is IntegrationDomainReadiness.Limited or IntegrationDomainReadiness.NotAssessable or IntegrationDomainReadiness.Partial);
        var reasons = new List<string>();
        var (sourcesConfigured, sourcesTotal) = platforms.Select(EventHubRuntimeSources.Count).Aggregate((0, 0), (sum, c) => (sum.Item1 + c.Configured, sum.Item2 + c.Total));
        // Azure runtime off is an instance setting, never an integration misconfiguration: stated once, beside the configured sources.
        if (!azureOn && sourcesConfigured > 0)
            reasons.Add($"Runtime evidence sources: configured ({sourcesConfigured} of {sourcesTotal}). Azure runtime: not configured — {AzureDisabledReason} This is not an integration misconfiguration.");
        foreach (var system in systems.Where(s => s.Topics > 1 || s.Kind == IntegrationKind.EventHub))
        {
            if (system.ConsumersConfirmed > 0) reasons.Add($"{system.ConsumersConfirmed} confirmed consumer mapping{(system.ConsumersConfirmed == 1 ? "" : "s")} in {system.SystemName}.");
            if (system.ConsumersSuggested > 0) reasons.Add($"{system.ConsumersSuggested} consumer mapping{(system.ConsumersSuggested == 1 ? "" : "s")} suggested by the audited source and not confirmed for this environment.");
            if (system.ConsumersNeedingConfirmation > 0) reasons.Add($"{system.ConsumersNeedingConfirmation} consumer mapping{(system.ConsumersNeedingConfirmation == 1 ? "" : "s")} need confirmation.");
            if (system.ConsumerGroupsUnknown > 0) reasons.Add($"Consumer group not configured for {system.ConsumerGroupsUnknown} topic{(system.ConsumerGroupsUnknown == 1 ? "" : "s")} — only checkpoint/lag checks are affected.");
            if (system.ConsumerGroupsAssumed > 0) reasons.Add($"Consumer group {system.ExpectedConsumerGroup} is a configured assumption for {system.ConsumerGroupsAssumed} topic{(system.ConsumerGroupsAssumed == 1 ? "" : "s")} — checkpoints are read for it, but it needs confirmation and yields no verdict.");
            if (!system.DomainReviewSupported) reasons.Add($"{system.SystemName}: domain review for {IntegrationConfigurationRules.KindLabel(system.Kind)} is not implemented yet; configuration is still reviewed.");
        }
        foreach (var adapter in adapters.Where(a => a.State != IntegrationEvidenceState.Available).GroupBy(a => a.Reason).Select(g => g.First()))
            reasons.Add($"{adapter.Adapter}: {adapter.Reason}");
        return new IntegrationReviewReadiness
        {
            EnvironmentId = catalog.EnvironmentId, Systems = systems, ConfiguredIntegrations = catalog.Integrations.Count, EnabledIntegrations = enabled.Count,
            Domains = domains, CanRun = canRun, EvidenceAdapters = adapters,
            AzureRuntimeEnabled = azureOn, AzureRuntimeReason = azureOn ? null : AzureDisabledReason, RuntimeSourcesConfigured = sourcesConfigured, RuntimeSourcesTotal = sourcesTotal,
            Headline = !canRun ? "Cannot run" : limited ? "Can run with limitations" : "Ready",
            Reasons = canRun ? reasons : ["Enable at least one configured integration in Target Environment → Integrations."],
        };
    }

    /// <summary>Topics grouped into integration systems: 16 CDC topics are one system, not 16 external systems.</summary>
    public static List<IntegrationSystemScope> Systems(IntegrationCatalog catalog, IReadOnlyList<IntegrationDefinition> enabled) =>
        enabled.GroupBy(SystemKey)
            .Select(g =>
            {
                var platform = catalog.Platforms.FirstOrDefault(p => p.Id == g.First().PlatformId);
                var kind = g.First().Kind;
                return new IntegrationSystemScope
                {
                    SystemName = g.First().SystemName ?? g.First().DisplayName, PlatformId = platform?.Id, PlatformName = platform?.Name, Kind = kind,
                    Topics = g.Count(), Enabled = g.Count(i => i.Enabled),
                    ConsumersConfirmed = g.Count(i => i.Consumer.MappingState == ConsumerMappingState.Confirmed),
                    ConsumersSuggested = g.Count(i => i.Consumer.MappingState == ConsumerMappingState.Suggested),
                    ConsumersNeedingConfirmation = g.Count(i => i.Consumer.MappingState == ConsumerMappingState.NeedsConfirmation),
                    ConsumerGroupsUnknown = kind == IntegrationKind.EventHub ? g.Count(i => EffectiveGroup(i, platform).Group is null) : 0,
                    ConsumerGroupsAssumed = kind == IntegrationKind.EventHub ? g.Count(i => EffectiveGroup(i, platform).Assumed) : 0,
                    ExpectedConsumerGroup = kind == IntegrationKind.EventHub && g.Any(i => EffectiveGroup(i, platform).Assumed) ? platform?.RuntimeEvidence?.ExpectedConsumerGroup?.Trim() : null,
                    ContractsConfigured = g.Count(i => i.ContractRelationship != ContractRelationshipState.NotConfigured),
                    DomainReviewSupported = kind == IntegrationKind.EventHub,
                };
            }).ToList();

    private static string SystemKey(IntegrationDefinition i) => $"{i.PlatformId}|{i.SystemName ?? i.Id}|{i.Kind}";

    // ── Run ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything the review learned about one topic, gathered once and read by every domain.</summary>
    private sealed record TopicEvidence(
        IntegrationDefinition Topic, IntegrationPlatform? Platform, int WindowHours, DateTimeOffset CapturedAt,
        NamespaceProbeResult? Probe,
        EvidenceResult<EventHubRuntimeMetadata>? Metadata,
        EvidenceResult<ConsumerGroupList>? Groups,
        EvidenceResult<CheckpointEvidence>? Checkpoint,
        EvidenceResult<ConsumerTelemetry>? Telemetry,
        (IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)? Producer,
        (IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)? Consumer,
        IReadOnlyList<IntegrationContractArtifact> PreviousContracts,
        string? Group = null, bool GroupAssumed = false,
        EventHubHubComparison? HubComparison = null, EventHubConsumerGroupComparison? GroupComparison = null)
    {
        public string? Hub => Topic.EndpointOrTopic;
        /// <summary>" (configured assumption)" when the checkpoint group is the platform's expectation rather than the topic's own group.</summary>
        public string AssumptionNote => GroupAssumed ? $" (consumer group {Group} is a configured assumption, not confirmed)" : "";
        public string? Role => Topic.Consumer.ContainerApp;
        public IntegrationEvidenceItemFreshness Fresh(DateTimeOffset? at) => IntegrationReviewLabels.FreshnessOf(at, CapturedAt, WindowHours);
        public bool InWindow(DateTimeOffset? at) => at is { } t && CapturedAt - t <= TimeSpan.FromHours(WindowHours);
    }

    public async Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, CancellationToken ct) =>
        await RunAsync(catalog, request, IntegrationContractSet.Empty, [], ct);

    public Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, IntegrationContractSet contracts,
        IReadOnlyList<IntegrationContractArtifact> previousContracts, CancellationToken ct) => RunAsync(catalog, request, contracts, previousContracts, null, ct);

    public async Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, IntegrationContractSet contracts,
        IReadOnlyList<IntegrationContractArtifact> previousContracts, ApplicationMessagingEvidenceSet? messaging, CancellationToken ct)
    {
        var messagingRuntime = new Dictionary<string, ApplicationMessagingRuntime>(StringComparer.Ordinal);
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = new List<IntegrationSystemResult>();
        var findings = new List<IntegrationReviewFinding>();
        var adapterStatuses = new List<IntegrationEvidenceAdapterStatus>();
        var windows = new List<int>();
        // Namespace-level Event Hub evidence is read once per platform, whatever the number of systems on it.
        var namespaceByPlatform = new Dictionary<string, EventHubNamespaceObservation>(StringComparer.Ordinal);
        var metricsByPlatform = new Dictionary<string, EventHubMetricsEvidence>(StringComparer.Ordinal);
        var hubsByPlatform = new Dictionary<string, List<EventHubHubComparison>>(StringComparer.Ordinal);
        var eventHubSnapshot = new Dictionary<string, EventHubRuntimeSnapshot>(StringComparer.Ordinal);
        var probes = new List<(IntegrationPlatform Platform, NamespaceProbeResult Probe)>();
        var allEvidence = new List<TopicEvidence>();

        foreach (var group in enabled.GroupBy(SystemKey))
        {
            var topics = group.ToList();
            var first = topics[0];
            var platform = catalog.Platforms.FirstOrDefault(p => p.Id == first.PlatformId);
            var supported = first.Kind == IntegrationKind.EventHub;
            var window = platform?.RuntimeEvidence?.ReviewWindowHours is { } w && w > 0 ? w : IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours;
            windows.Add(window);
            var platformChecks = new List<IntegrationCheck>();
            var topicResults = new List<IntegrationTopicResult>();

            NamespaceProbeResult? probe = null;
            if (supported && platform is { NamespaceFqdn: { Length: > 0 } fqdn }) probe = await namespaceProbe.ProbeAsync(fqdn, ct);
            if (probe is not null && platform is not null) probes.Add((platform, probe));

            // Namespace + hub list (Azure Resource Manager, GET only) and namespace metrics (Azure Monitor), each failure isolated from the rest.
            EventHubNamespaceObservation? observation = null;
            EventHubMetricsEvidence? hubMetrics = null;
            List<EventHubHubComparison>? hubComparisons = null;
            if (supported && platform is not null)
            {
                if (namespaces is not null && !namespaceByPlatform.TryGetValue(platform.Id, out observation))
                    namespaceByPlatform[platform.Id] = observation = await namespaces.ReadAsync(platform, ct);
                if (eventHubMetrics is not null && !metricsByPlatform.TryGetValue(platform.Id, out hubMetrics))
                    metricsByPlatform[platform.Id] = hubMetrics = await eventHubMetrics.ReadAsync(platform, window, ct);
                if (observation is not null && !hubsByPlatform.TryGetValue(platform.Id, out hubComparisons))
                    hubsByPlatform[platform.Id] = hubComparisons = EventHub.EventHubRuntimeEvaluation.CompareHubs(platform, catalog.Integrations, observation);
            }

            // Evidence per topic (and telemetry once per consumer role), gathered before any domain reads it.
            var telemetryByRole = new Dictionary<string, EvidenceResult<ConsumerTelemetry>>(StringComparer.Ordinal);
            var evidence = new List<TopicEvidence>();
            foreach (var topic in topics)
            {
                EvidenceResult<EventHubRuntimeMetadata>? hub = null;
                EvidenceResult<ConsumerGroupList>? groupList = null;
                EvidenceResult<CheckpointEvidence>? checkpoint = null;
                EvidenceResult<ConsumerTelemetry>? roleTelemetry = null;
                if (supported && platform is not null && !string.IsNullOrWhiteSpace(topic.EndpointOrTopic))
                {
                    var name = topic.EndpointOrTopic!;
                    hub = await metadata.GetHubAsync(platform, name, ct);
                    // The hub's consumer groups (observed, read-only) are compared with the expected group; when the platform states an expected group,
                    // checkpoints are read for that configured assumption — labelled as such everywhere, never a confirmed mapping.
                    groupList = await consumerGroups.ListAsync(platform, name, ct);
                    if (EffectiveGroup(topic, platform).Group is { } checkpointGroup) checkpoint = await checkpoints.GetAsync(platform, name, checkpointGroup, ct);
                    if (topic.Consumer.ContainerApp is { Length: > 0 } role)
                    {
                        if (!telemetryByRole.TryGetValue(role, out roleTelemetry)) telemetryByRole[role] = roleTelemetry = await telemetry.GetConsumerAsync(platform, role, window, ct);
                    }
                }
                var effective = EffectiveGroup(topic, platform);
                evidence.Add(new TopicEvidence(topic, platform, window, DateTimeOffset.UtcNow, probe, hub, groupList, checkpoint, roleTelemetry,
                    contracts.Of(topic.Id, IntegrationContractRole.Producer), contracts.Of(topic.Id, IntegrationContractRole.Consumer), previousContracts,
                    effective.Group, effective.Assumed, hubComparisons?.FirstOrDefault(h => h.IntegrationId == topic.Id),
                    supported && !string.IsNullOrWhiteSpace(topic.EndpointOrTopic) ? EventHub.EventHubRuntimeEvaluation.CompareGroup(topic, platform, groupList) : null));
            }
            allEvidence.AddRange(evidence);

            if (platform is not null && supported)
            {
                var statuses = new[]
                {
                    Rollup(metadata.Describe(platform), evidence.Select(e => e.Metadata)),
                    Rollup(consumerGroups.Describe(platform), evidence.Select(e => e.Groups)),
                    Rollup(checkpoints.Describe(platform), evidence.Select(e => e.Checkpoint)),
                    Rollup(telemetry.Describe(platform), evidence.Select(e => e.Telemetry)),
                };
                var runtimeStatuses = new List<IntegrationEvidenceAdapterStatus>();
                if (namespaces is not null && observation is not null && !adapterStatuses.Any(a => a.Adapter == $"{EventHub.ArmEventHubNamespaceSource.Adapter} · {platform.Name}"))
                    runtimeStatuses.Add(namespaces.Describe(platform) with { State = observation.State, Reason = observation.State == IntegrationEvidenceState.Available ? $"{observation.Reason} {observation.HubListReason}" : observation.Reason, CapturedAt = observation.CapturedAt });
                if (eventHubMetrics is not null && hubMetrics is not null && !adapterStatuses.Any(a => a.Adapter == $"{EventHub.AzureMonitorEventHubMetricsSource.Adapter} · {platform.Name}"))
                    runtimeStatuses.Add(eventHubMetrics.Describe(platform) with { State = hubMetrics.State, Reason = hubMetrics.Reason, CapturedAt = hubMetrics.CapturedAt });
                adapterStatuses.AddRange(statuses.Concat(runtimeStatuses).Select(s => s with { Adapter = $"{s.Adapter} · {platform.Name}" }));
                foreach (var s in statuses.Concat(runtimeStatuses))
                    logger.LogInformation("IQR evidence adapter {Adapter} for platform {PlatformId}: {State} ({Reason}).", s.Adapter, platform.Id, s.State, s.Reason);
                platformChecks.AddRange(PlatformChecks(platform, probe, statuses, window));
                platformChecks.AddRange(RuntimePlatformChecks(platform, namespaces is null ? null : observation, eventHubMetrics is null ? null : hubMetrics, hubComparisons, findings));
                eventHubSnapshot[platform.Id] = Snapshot(eventHubSnapshot.GetValueOrDefault(platform.Id), platform, observation, hubMetrics, hubComparisons, evidence, statuses[3].State, AzureEnabled(statuses));
            }
            else if (platform is not null) platformChecks.AddRange(PlatformChecks(platform, null, [], window));

            if (probe is { Reachable: false } && platform is not null)
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"namespace-unreachable|{platform.Id}", RuleId = "namespace-unreachable", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Messaging namespace is not reachable from the review", Subject = platform.NamespaceFqdn ?? platform.Name,
                    Evidence = [probe.Detail], Recommendation = "Check DNS, private endpoint and network access to the namespace from where BirkNext runs; the review itself cannot distinguish an outage from a network restriction.",
                    AffectedIntegrations = topics.Select(t => t.DisplayName).ToList(),
                });

            foreach (var e in evidence)
            {
                var checks = new List<IntegrationCheck>();
                checks.AddRange(ConfigurationChecks(e, findings, namespaces is not null));
                if (supported)
                {
                    checks.AddRange(ConnectivityChecks(e, findings));
                    checks.AddRange(ContractChecks(e, findings));
                    checks.AddRange(MessageFlowChecks(e, findings));
                    checks.AddRange(ReliabilityChecks(e, findings));
                    checks.AddRange(ErrorHandlingChecks(e, findings));
                    checks.AddRange(SecurityChecks(e, findings));
                    checks.AddRange(ObservabilityChecks(e));
                    checks.AddRange(await HealthEndpointChecksAsync(e.Topic, ct));
                    checks.AddRange(PerformanceChecks(e, findings));
                    checks.AddRange(DataQualityChecks(e, findings));
                    if (messaging is not null && ApplicationMessagingReview.For(e.Topic, messaging) is { } app)
                    {
                        if (!messagingRuntime.TryGetValue(app.ApplicationId, out var appRuntime))
                            messagingRuntime[app.ApplicationId] = appRuntime = messagingTelemetry is null
                                ? new ApplicationMessagingRuntime { ApplicationId = app.ApplicationId, State = IntegrationEvidenceState.NotConfigured, Reason = "No application-messaging telemetry source in this instance.", CapturedAt = DateTimeOffset.UtcNow, WindowHours = window }
                                : await messagingTelemetry.GetAsync(platform, app, window, ct);
                        checks.AddRange(ApplicationMessagingReview.Checks(e.Topic, app, appRuntime, messaging.AnalyzedAt));
                        logger.LogInformation("IQR application messaging for {IntegrationId}: {Application} Wolverine {Detection}, handlers {Handlers}, retry policy {Retry}, runtime {Runtime}.",
                            e.Topic.Id, app.ApplicationId, app.Detection, app.Handlers.Count, app.RetryPolicy, appRuntime.State);
                        checks.Add(EndToEndCheck(e, app.ApplicationId, appRuntime.State == IntegrationEvidenceState.Available));
                    }
                    else checks.Add(EndToEndCheck(e, null, false));
                }
                else
                {
                    foreach (var domain in Enum.GetValues<IntegrationReviewDomain>().Where(d => d != IntegrationReviewDomain.Configuration))
                        checks.Add(Check($"{domain.ToString().ToLowerInvariant()}-unsupported", domain, e.Topic.Id, $"{IntegrationReviewLabels.Domain(domain)} review", IntegrationCheckStatus.NotAssessed,
                            "", "", $"Domain review for {IntegrationConfigurationRules.KindLabel(e.Topic.Kind)} integrations is not implemented in this version."));
                }
                var (state, _, _) = IntegrationConfigurationRules.Evaluate(e.Topic, platform);
                var suggestion = SuggestedGroup(e);
                topicResults.Add(new IntegrationTopicResult
                {
                    IntegrationId = e.Topic.Id, DisplayName = e.Topic.DisplayName, Topic = e.Topic.EndpointOrTopic, Consumer = e.Topic.Consumer.DisplayName,
                    MappingState = e.Topic.Consumer.MappingState, ConfigurationState = state, Checks = checks,
                    SuggestedConsumerGroup = suggestion, SuggestedConsumerGroupSource = suggestion is null ? null : "Only non-default consumer group Azure Resource Manager lists for this hub (read-only).",
                });
                logger.LogInformation("IQR topic {IntegrationId}: {Assessed} of {Checks} check(s) assessed.", e.Topic.Id, checks.Count(c => IntegrationReviewLabels.IsAssessed(c.Status)), checks.Count);
            }
            systems.Add(new IntegrationSystemResult { SystemName = first.SystemName ?? first.DisplayName, PlatformId = platform?.Id, Kind = first.Kind, DomainReviewSupported = supported, PlatformChecks = platformChecks, Topics = topicResults });
        }

        // Service Bus platforms: topology, runtime metadata and route correlation as platform-scope checks in the existing domains.
        var serviceBusSnapshot = new List<ServiceBusEvidenceCheck>();
        if (serviceBus is not null)
            foreach (var platform in catalog.Platforms.Where(p => p.Enabled && ServiceBusEvidenceService.IsServiceBus(p)))
            {
                var check = await serviceBus.CheckAsync(platform, messaging, ct);
                var (platformChecks, platformFindings) = ServiceBusEvidenceService.ReviewChecks(platform, check);
                systems.Add(new IntegrationSystemResult { SystemName = platform.Name, PlatformId = platform.Id, Kind = IntegrationKind.ServiceBus, DomainReviewSupported = true, PlatformChecks = platformChecks });
                findings.AddRange(platformFindings);
                serviceBusSnapshot.Add(check);
                adapterStatuses.Add(new IntegrationEvidenceAdapterStatus
                {
                    Adapter = $"{ArmServiceBusMetadataSource.Adapter} · {platform.Name}", Source = IntegrationEvidenceSource.AzureResourceManager,
                    State = check.Runtime?.State ?? IntegrationEvidenceState.NotConfigured, Reason = check.Runtime?.Reason ?? "", CapturedAt = check.Runtime?.CapturedAt ?? check.CompletedAt,
                });
            }

        // Identity provisioning (SCIM): source facts, safe GET checks and Service Bus correlation as platform-scope checks in the existing domains.
        var scimSnapshot = new List<ScimEvidenceCheck>();
        if (scim is not null)
            foreach (var platform in catalog.Platforms.Where(p => p.Enabled && BirkNext.Api.Services.Integrations.Scim.ScimEvidenceService.IsScim(p)))
            {
                var bus = serviceBusSnapshot.FirstOrDefault(s => s.PlatformId == platform.ScimProvisioning?.OutboundPlatformId);
                var check = await scim.ReviewAsync(catalog, platform, request.EnvironmentType, bus, ct);
                var (platformChecks, platformFindings) = BirkNext.Api.Services.Integrations.Scim.ScimEvidenceService.ReviewChecks(platform, check);
                systems.Add(new IntegrationSystemResult { SystemName = platform.Name, PlatformId = platform.Id, Kind = IntegrationKind.IdentityProvisioning, DomainReviewSupported = true, PlatformChecks = platformChecks });
                findings.AddRange(platformFindings);
                scimSnapshot.Add(check);
                adapterStatuses.Add(new IntegrationEvidenceAdapterStatus
                {
                    Adapter = $"{BirkNext.Api.Services.Integrations.Scim.HttpScimRuntimeProbe.Adapter} · {platform.Name}", Source = IntegrationEvidenceSource.NetworkProbe,
                    State = check.Runtime.State, Reason = check.Runtime.Reason, CapturedAt = check.Runtime.CapturedAt == default ? check.CompletedAt : check.Runtime.CapturedAt,
                });
            }

        // Security classification: owned by its own review; IQR only takes its source/configuration checks into the existing domains.
        ClassificationReviewResult? classificationSnapshot = null;
        if (classification is not null && await classification.ReviewAsync(catalog.EnvironmentId, request.EnvironmentType, ct) is { } classified)
        {
            var (classificationChecks, classificationFindings) = BirkNext.Api.Services.SecurityClassification.ClassificationEvaluator.ReviewChecks(classified);
            systems.Add(new IntegrationSystemResult { SystemName = "Security classification (BiRK CDC → Person)", Kind = IntegrationKind.Other, DomainReviewSupported = true, PlatformChecks = classificationChecks });
            findings.AddRange(classificationFindings);
            classificationSnapshot = classified;
        }

        var grouped = findings.GroupBy(f => f.Key).Select(g => g.First() with { AffectedIntegrations = g.SelectMany(f => f.AffectedIntegrations).Distinct().ToList() }).ToList();
        var allChecks = systems.SelectMany(s => s.PlatformChecks.Concat(s.Topics.SelectMany(t => t.Checks))).ToList();
        var domains = Enum.GetValues<IntegrationReviewDomain>().Select(domain => DomainResult(domain, allChecks, grouped)).ToList();
        // Source/build facts (application messaging) are configuration evidence, never runtime evidence.
        var runtimeAssessed = allChecks.Where(c => c.Provenance is not (IntegrationEvidenceSource.Configuration or IntegrationEvidenceSource.ContractArtifact
            or IntegrationEvidenceSource.SourceCode or IntegrationEvidenceSource.PackageManifest or IntegrationEvidenceSource.Infrastructure) && IntegrationReviewLabels.IsAssessed(c.Status)).ToList();
        var outcome = allChecks.Count == 0 || !allChecks.Any(c => IntegrationReviewLabels.IsAssessed(c.Status)) ? IntegrationReviewOutcome.NothingAssessed
            : grouped.Count > 0 ? IntegrationReviewOutcome.ManualReviewRequired
            : domains.Any(d => d.ChecksAssessed < d.ChecksTotal) ? IntegrationReviewOutcome.CompletedWithLimitations
            : IntegrationReviewOutcome.Completed;
        var freshness = runtimeAssessed.Count == 0 ? IntegrationEvidenceFreshness.ConfigurationOnly
            : runtimeAssessed.Any(c => c.Freshness == IntegrationEvidenceItemFreshness.Historical) ? IntegrationEvidenceFreshness.Mixed : IntegrationEvidenceFreshness.Current;
        var sources = allChecks.Where(c => IntegrationReviewLabels.IsAssessed(c.Status)).Select(c => c.Provenance).Append(IntegrationEvidenceSource.Configuration).Distinct().OrderBy(s => s).ToList();
        var result = new IntegrationReviewResult
        {
            RunId = Guid.NewGuid(), EnvironmentId = catalog.EnvironmentId, EnvironmentName = request.EnvironmentName, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
            Outcome = outcome, ConfigurationSnapshot = catalog with { Notices = [] }, Systems = systems, Domains = domains, Findings = grouped,
            ManualFollowUp = ManualFollowUp(enabled, catalog, systems, allChecks), Limitations = Limitations(enabled, catalog, adapterStatuses, allChecks),
            Freshness = freshness, EvidenceSources = sources, ReviewWindowHours = windows.Count == 0 ? IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours : windows.Max(),
            EvidenceAdapters = adapterStatuses, ContractSnapshot = contracts.Items.Where(i => enabled.Any(e => e.Id == i.Artifact.IntegrationId)).Select(i => i.Artifact).ToList(),
            // The evidence as used: a later re-analysis or re-binding never changes this result.
            ApplicationMessagingSnapshot = messaging, ApplicationMessagingRuntime = messagingRuntime.Values.ToList(), ServiceBusSnapshot = serviceBusSnapshot, ScimSnapshot = scimSnapshot, SecurityClassificationSnapshot = classificationSnapshot,
            EventHubSnapshot = eventHubSnapshot.Values.ToList(),
            WhatWasTested = Tested(probes, adapterStatuses, eventHubSnapshot.Values.ToList(), allChecks, messagingRuntime.Values.ToList(), serviceBusSnapshot),
            WhatWasNotAssessed = NotAssessed(enabled, catalog, adapterStatuses, allChecks),
        };
        logger.LogInformation(
            "Integration Quality Review for {EnvironmentId}: {Integrations} integration(s) in {Systems} system(s), window {WindowHours} h, sources {Sources}, {Assessed} of {Checks} check(s) assessed, {Findings} finding(s), {DurationMs:0} ms.",
            catalog.EnvironmentId, enabled.Count, systems.Count, result.ReviewWindowHours, string.Join(",", sources), allChecks.Count(c => IntegrationReviewLabels.IsAssessed(c.Status)), allChecks.Count, grouped.Count, stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>An adapter's run outcome: Available as soon as one call delivered; otherwise the first precise failure.</summary>
    private static IntegrationEvidenceAdapterStatus Rollup<T>(IntegrationEvidenceAdapterStatus configured, IEnumerable<EvidenceResult<T>?> results) where T : class
    {
        var calls = results.OfType<EvidenceResult<T>>().ToList();
        if (calls.Count == 0) return configured.State == IntegrationEvidenceState.Available ? configured with { State = IntegrationEvidenceState.NotSupported, Reason = "Not needed for the configured integrations (e.g. every consumer group is known, or no consumer application is configured)." } : configured;
        var available = calls.Where(c => c.State == IntegrationEvidenceState.Available).ToList();
        if (available.Count > 0) return configured with { State = IntegrationEvidenceState.Available, Reason = $"{available.Count} of {calls.Count} request(s) returned evidence.", CapturedAt = available.Max(c => c.CapturedAt) };
        var failure = calls.GroupBy(c => c.State).OrderByDescending(g => g.Count()).First().First();
        return configured with { State = failure.State, Reason = failure.Reason, CapturedAt = failure.CapturedAt };
    }

    /// <summary>The consumer group checkpoints are read for: the topic's own group, else the platform's expected group as a configured
    /// assumption (never silently <c>$Default</c>: only when the platform states it). Null when neither is known.</summary>
    public static (string? Group, bool Assumed) EffectiveGroup(IntegrationDefinition topic, IntegrationPlatform? platform) =>
        IntegrationConfigurationRules.EffectiveConsumerGroup(topic, platform);

    private static string? SuggestedGroup(TopicEvidence e) =>
        string.IsNullOrWhiteSpace(e.Topic.ConsumerGroup) && e.Groups is { IsAvailable: true, Value: { } list }
        && list.Names.Where(n => !string.Equals(n, "$Default", StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } only ? only[0] : null;

    // ── Check helper ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IntegrationCheck Check(string id, IntegrationReviewDomain domain, string subject, string title, IntegrationCheckStatus status,
        string expectation, string evidence, string explanation, string? recommendation = null, IntegrationEvidenceSource provenance = IntegrationEvidenceSource.Configuration,
        DateTimeOffset? at = null, DateTimeOffset? sourceTimestamp = null, IntegrationEvidenceItemFreshness freshness = IntegrationEvidenceItemFreshness.Unknown,
        IntegrationCheckScope scope = IntegrationCheckScope.Topic) =>
        new()
        {
            CheckId = id, Domain = domain, Scope = scope, SubjectId = subject, Title = title, Status = status, Expectation = expectation, Evidence = evidence,
            Explanation = explanation, Recommendation = recommendation, Provenance = provenance, CapturedAt = at ?? DateTimeOffset.UtcNow,
            SourceTimestamp = sourceTimestamp, Freshness = freshness,
        };

    private static IntegrationCheck Missing<T>(string id, IntegrationReviewDomain domain, string subject, string title, string expectation, EvidenceResult<T>? source, string fallback) where T : class =>
        Check(id, domain, subject, title, source?.State == IntegrationEvidenceState.NotConfigured ? IntegrationCheckStatus.NotAssessed : IntegrationCheckStatus.NotAssessed,
            expectation, "", source is null ? fallback : $"{IntegrationReviewLabels.EvidenceState(source.State)}: {source.Reason}", provenance: source?.Source ?? IntegrationEvidenceSource.Configuration, at: source?.CapturedAt);

    private static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        return age.TotalMinutes < 1 ? "less than a minute ago" : age.TotalHours < 1 ? $"{age.TotalMinutes:0} min ago" : age.TotalDays < 2 ? $"{age.TotalHours:0.#} h ago" : $"{age.TotalDays:0} days ago";
    }

    // ── Platform ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> PlatformChecks(IntegrationPlatform platform, NamespaceProbeResult? probe, IReadOnlyList<IntegrationEvidenceAdapterStatus> adapters, int window)
    {
        var id = platform.Id;
        const IntegrationCheckScope P = IntegrationCheckScope.Platform;
        yield return Check("cfg-namespace", IntegrationReviewDomain.Configuration, id, "Namespace configured",
            string.IsNullOrWhiteSpace(platform.Namespace) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The platform names its messaging namespace.", platform.Namespace ?? "Not configured", "Configuration only — this says nothing about reachability.", scope: P);
        if (adapters.Count > 0)
        {
            yield return probe is null
                ? Check("conn-namespace", IntegrationReviewDomain.Connectivity, id, "Namespace endpoint reachable", IntegrationCheckStatus.NotAssessed,
                    "DNS resolves and TCP/TLS on 443 succeeds.", "", "No namespace FQDN is configured.", "Configure the namespace FQDN.", scope: P)
                : Check("conn-namespace", IntegrationReviewDomain.Connectivity, id, "Namespace endpoint reachable",
                    probe.Reachable && probe.TlsEstablished ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail,
                    "DNS resolves and TCP/TLS on 443 succeeds from where BirkNext runs.", $"{probe.Detail} ({probe.ElapsedMs:0} ms)",
                    probe.Reachable ? "Reachable does not verify that hubs exist, that messages flow, or that any party is authorized." : "The namespace could not be reached from the review host.",
                    probe.Reachable ? null : "Check DNS, private endpoint and network access.", IntegrationEvidenceSource.NetworkProbe, probe.CapturedAt, probe.CapturedAt, IntegrationEvidenceItemFreshness.Current, P);
            var meta = adapters[0];
            yield return Check("conn-metadata-access", IntegrationReviewDomain.Connectivity, id, "Event Hub metadata readable",
                meta.State switch
                {
                    IntegrationEvidenceState.Available => IntegrationCheckStatus.Observed,
                    IntegrationEvidenceState.NotConfigured => IntegrationCheckStatus.NotConfigured,
                    _ => IntegrationCheckStatus.NotAssessed,
                },
                "The review can read hub metadata with the instance's Azure identity.", IntegrationReviewLabels.EvidenceState(meta.State), meta.Reason,
                meta.State == IntegrationEvidenceState.Available ? null : "Enable Event Hub metadata for the platform and grant the BirkNext identity read access (Data Receiver / Listen).",
                IntegrationEvidenceSource.AzureMetadata, meta.CapturedAt, scope: P);
            yield return probe is { TlsEstablished: true }
                ? Check("sec-tls", IntegrationReviewDomain.Security, id, "Transport encryption (TLS)", IntegrationCheckStatus.Pass, "The namespace accepts TLS.", probe.TlsProtocol ?? "TLS established", "Observed on the review's own connection.",
                    provenance: IntegrationEvidenceSource.NetworkProbe, at: probe.CapturedAt, sourceTimestamp: probe.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current, scope: P)
                : Check("sec-tls", IntegrationReviewDomain.Security, id, "Transport encryption (TLS)", probe is { Reachable: true } ? IntegrationCheckStatus.Fail : IntegrationCheckStatus.NotAssessed,
                    "The namespace accepts TLS.", probe?.Detail ?? "", probe is { Reachable: true } ? "TCP connected but the TLS handshake failed." : "The namespace was not reached.", provenance: IntegrationEvidenceSource.NetworkProbe, scope: P);
            var tel = adapters[3];
            yield return Check("obs-telemetry-access", IntegrationReviewDomain.Observability, id, "Telemetry source accessible",
                tel.State switch
                {
                    IntegrationEvidenceState.Available => IntegrationCheckStatus.Pass,
                    IntegrationEvidenceState.NotConfigured => IntegrationCheckStatus.NotConfigured,
                    _ => IntegrationCheckStatus.NotAssessed,
                },
                "Integration telemetry can be queried.", IntegrationReviewLabels.EvidenceState(tel.State), tel.Reason, provenance: IntegrationEvidenceSource.ApplicationInsights, at: tel.CapturedAt, scope: P);
        }
        yield return Check("sec-producer-auth", IntegrationReviewDomain.Security, id, "Producer authentication mechanism configured",
            platform.ProducerAuthentication == IntegrationAuthMechanism.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The producer's authentication mechanism is named.", IntegrationConfigurationRules.AuthLabel(platform.ProducerAuthentication),
            "Configured, not validated: no credential is read and no send is attempted.", scope: P);
        yield return Check("obs-provider", IntegrationReviewDomain.Observability, id, "Monitoring provider configured",
            string.IsNullOrWhiteSpace(platform.MonitoringProvider) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A monitoring provider is named for the platform.", platform.MonitoringProvider ?? "Not configured", "Configured is not data available: see Telemetry source accessible.", scope: P);
        yield return Check("obs-dashboard", IntegrationReviewDomain.Observability, id, "Monitoring dashboard link",
            string.IsNullOrWhiteSpace(platform.MonitoringUrl) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A concrete dashboard URL lets a reader open integration monitoring.", platform.MonitoringUrl ?? "Not configured", "Configuration only.",
            string.IsNullOrWhiteSpace(platform.MonitoringUrl) ? "Add the Application Insights dashboard/workbook URL." : null, scope: P);
        yield return Check("obs-runbook", IntegrationReviewDomain.Observability, id, "Runbook link",
            string.IsNullOrWhiteSpace(platform.RunbookUrl) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A runbook explains how to act on integration failures.", platform.RunbookUrl ?? "Not configured", "Configuration only.", scope: P);
    }

    // ── Event Hub runtime evidence (namespace, topology, monitoring, transport metrics) ──────────────────────────────

    private static string Number(double value) => value.ToString(Math.Abs(value % 1) < 0.0001 ? "N0" : "N1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Platform-scope checks from the Azure Resource Manager namespace read and the Azure Monitor metrics, plus the configuration facts every
    /// run states (checkpoint store, Application Insights, log destination, consumer RBAC). Observed values carry no verdict without a threshold;
    /// an unreadable source is Not assessed with its reason; a missing Log Analytics workspace is not a gap when logs go to Azure Monitor.
    /// </summary>
    private static IEnumerable<IntegrationCheck> RuntimePlatformChecks(IntegrationPlatform platform, EventHubNamespaceObservation? ns, EventHubMetricsEvidence? metrics,
        List<EventHubHubComparison>? hubs, List<IntegrationReviewFinding> findings)
    {
        var id = platform.Id;
        const IntegrationCheckScope P = IntegrationCheckScope.Platform;
        const IntegrationEvidenceSource Arm = IntegrationEvidenceSource.AzureResourceManager;
        var r = platform.RuntimeEvidence;
        if (ns is not null)
        {
            var available = ns.State == IntegrationEvidenceState.Available;
            var observedDetail = string.Join(" · ", new[] { ns.Status, ns.Sku, ns.Location }.Where(v => !string.IsNullOrWhiteSpace(v)));
            yield return available
                ? Check("cfg-namespace-observed", IntegrationReviewDomain.Configuration, id, "Configured namespace in Azure", IntegrationCheckStatus.Observed,
                    $"Namespace {platform.Namespace} in resource group {platform.ResourceGroup}.", $"Observed match: {platform.Namespace}{(observedDetail.Length == 0 ? "" : $" ({observedDetail})")}.",
                    "Observed in Azure Resource Manager; a namespace that exists is not a healthy integration.", provenance: Arm, at: ns.CapturedAt, sourceTimestamp: ns.CapturedAt,
                    freshness: IntegrationEvidenceItemFreshness.Current, scope: P)
                : ns.State == IntegrationEvidenceState.NotFound
                    ? Check("cfg-namespace-observed", IntegrationReviewDomain.Configuration, id, "Configured namespace in Azure", IntegrationCheckStatus.Warning,
                        $"Namespace {platform.Namespace} in resource group {platform.ResourceGroup}.", "Configured but not found in Azure Resource Manager.",
                        "The subscription, resource group or namespace name may be wrong, or the namespace does not exist.",
                        "Check the configured subscription id, resource group and namespace name.", Arm, ns.CapturedAt, scope: P)
                    : Check("cfg-namespace-observed", IntegrationReviewDomain.Configuration, id, "Configured namespace in Azure", IntegrationCheckStatus.NotAssessed,
                        $"Namespace {platform.Namespace} in resource group {platform.ResourceGroup}.", "", $"{IntegrationReviewLabels.EvidenceState(ns.State)}: {ns.Reason}", provenance: Arm, at: ns.CapturedAt, scope: P);
            if (ns.State == IntegrationEvidenceState.NotFound)
                findings.Add(Finding($"namespace-not-found|{id}", "namespace-not-found", IntegrationReviewDomain.Configuration, IntegrationFindingSeverityV2.Medium,
                    "Configured Event Hubs namespace not found in Azure", platform.Namespace ?? platform.Name, [$"Azure Resource Manager returned HTTP 404 for the configured namespace ({ns.CapturedAt:u})."],
                    "Check the configured subscription id, resource group and namespace name.", platform.Name));

            if (hubs is not null)
            {
                var business = hubs.Where(h => h.State is not (EventHubComparisonState.TechnicalObserved or EventHubComparisonState.AdditionalObserved or EventHubComparisonState.ConfiguredDisabled)).ToList();
                int Count(EventHubComparisonState state) => hubs.Count(h => h.State == state);
                var unread = business.Count(h => h.State is EventHubComparisonState.NotAuthorized or EventHubComparisonState.NotAssessed);
                yield return business.Count > 0 && unread == business.Count
                    ? Check("cfg-topology", IntegrationReviewDomain.Configuration, id, "Configured vs observed Event Hubs", IntegrationCheckStatus.NotAssessed,
                        $"{business.Count} configured business hub(s) exist in the namespace.", "", $"{EventHubComparisonLabels.State(business[0].State)}: {business[0].Detail}", provenance: Arm, at: ns.CapturedAt, scope: P)
                    : Check("cfg-topology", IntegrationReviewDomain.Configuration, id, "Configured vs observed Event Hubs",
                        Count(EventHubComparisonState.MissingInAzure) + Count(EventHubComparisonState.DifferenceObserved) > 0 ? IntegrationCheckStatus.Warning : IntegrationCheckStatus.Observed,
                        $"{business.Count} configured business hub(s) exist in the namespace as configured.",
                        $"{Count(EventHubComparisonState.ObservedMatch)} observed match, {Count(EventHubComparisonState.DifferenceObserved)} difference observed, {Count(EventHubComparisonState.MissingInAzure)} configured but not found in Azure"
                        + $" · {Count(EventHubComparisonState.AdditionalObserved)} additional observed · {Count(EventHubComparisonState.TechnicalObserved)} technical/support hub(s) · {ns.Hubs.Count} hub(s) listed in the namespace.",
                        "Additional and technical hubs are listed, never errors. A match is configuration agreeing with Azure, not message flow.",
                        Count(EventHubComparisonState.MissingInAzure) > 0 ? "Correct the configured hub names or provision the missing hubs through the platform's normal process." : null,
                        Arm, ns.CapturedAt, ns.CapturedAt, IntegrationEvidenceItemFreshness.Current, P);
            }

            yield return Check("conn-arm-access", IntegrationReviewDomain.Connectivity, id, "Azure management metadata readable", available ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NotAssessed,
                "The review can read the namespace through Azure Resource Manager with the instance's identity.",
                available ? "Observed (read-only GET)" : IntegrationReviewLabels.EvidenceState(ns.State),
                available ? "Separate from reachability: this is BirkNext's read access, not the consumer's authorization." : ns.Reason,
                available ? null : "Grant the BirkNext identity Reader on the namespace, or enable Azure runtime evidence for this instance.", Arm, ns.CapturedAt,
                available ? ns.CapturedAt : null, available ? IntegrationEvidenceItemFreshness.Current : IntegrationEvidenceItemFreshness.Unknown, P);

            if (available)
            {
                yield return Check("sec-network-access", IntegrationReviewDomain.Security, id, "Namespace network access", IntegrationCheckStatus.Observed, "",
                    $"Public network access: {ns.PublicNetworkAccess ?? "not reported"} · private endpoint connection(s): {(ns.PrivateEndpointConnections is { } pe ? pe.ToString(CultureInfo.InvariantCulture) : "not reported")} · minimum TLS: {ns.MinimumTlsVersion ?? "not reported"}.",
                    "Observed configuration; no network policy is configured to judge it.", provenance: Arm, at: ns.CapturedAt, sourceTimestamp: ns.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current, scope: P);
                yield return Check("sec-local-auth", IntegrationReviewDomain.Security, id, "Local (SAS) authentication", IntegrationCheckStatus.Observed, "",
                    ns.DisableLocalAuth switch { true => "Disabled on the namespace (Entra ID only).", false => "Enabled on the namespace.", _ => "Not reported." },
                    platform.ProducerAuthentication == IntegrationAuthMechanism.Sas ? "Observed without a verdict; the producer is configured with SAS, which needs local authentication." : "Observed without a verdict.",
                    provenance: Arm, at: ns.CapturedAt, sourceTimestamp: ns.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current, scope: P);
            }
            else
                yield return Check("sec-network-access", IntegrationReviewDomain.Security, id, "Namespace network access", IntegrationCheckStatus.NotAssessed, "", "",
                    $"{IntegrationReviewLabels.EvidenceState(ns.State)}: {ns.Reason}", provenance: Arm, at: ns.CapturedAt, scope: P);
        }

        yield return Check("sec-consumer-rbac", IntegrationReviewDomain.Security, id, "Consumer identity authorization (RBAC)", IntegrationCheckStatus.NotAssessed,
            "The consumer identity holds Azure Event Hubs Data Receiver on its hubs.", "",
            "Not evaluated: the review reads with BirkNext's own identity; Managed Identity configured is not authorization proven.", "Verify the consumer identity's role assignment on the hubs.", scope: P);

        yield return r?.ResolvedCheckpointContainerUrl() is { } container
            ? Check("rel-checkpoint-config", IntegrationReviewDomain.Reliability, id, "Checkpoint configuration", IntegrationCheckStatus.Configured, "The consumer's checkpoint store is known.",
                $"{container} — {(r.CheckpointProvenance == IntegrationValueProvenance.SourceConfigurationVerified ? "Verified" : "Configured")} ({IntegrationRuntimeEvidenceSettings.ProvenanceLabel(r.CheckpointProvenance)}){(string.IsNullOrWhiteSpace(r.CheckpointSourceNote) ? "" : $": {r.CheckpointSourceNote.Trim()}")}",
                "Where checkpoints are stored — configuration, not checkpoint runtime evidence (see Consumer checkpoints).", scope: P)
            : Check("rel-checkpoint-config", IntegrationReviewDomain.Reliability, id, "Checkpoint configuration", IntegrationCheckStatus.NotConfigured, "The consumer's checkpoint store is known.",
                "Not configured", "Without the checkpoint store, checkpoint progression cannot be read.", "Configure the consumer's checkpoint Blob endpoint and container.", scope: P);

        yield return r?.ApplicationInsightsResourceName is { Length: > 0 } ai
            ? Check("obs-appinsights", IntegrationReviewDomain.Observability, id, "Application Insights resource", IntegrationCheckStatus.Configured, "The consumer's Application Insights resource is known.",
                $"{ai.Trim()}{(string.IsNullOrWhiteSpace(r.ApplicationInsightsResourceGroup) ? "" : $" ({r.ApplicationInsightsResourceGroup.Trim()})")}{(r.ApplicationInsightsConfigured == true ? " · configured on the consumer" : "")}",
                "Configured is not telemetry observed: see Telemetry source accessible.", scope: P)
            : Check("obs-appinsights", IntegrationReviewDomain.Observability, id, "Application Insights resource", IntegrationCheckStatus.NotConfigured, "The consumer's Application Insights resource is known.",
                "Not configured", "", "Add the Application Insights resource name and resource group to the platform's runtime evidence.", scope: P);

        if (!string.IsNullOrWhiteSpace(r?.ContainerAppsLogDestination) || Guid.TryParse(r?.TelemetryWorkspaceId?.Trim(), out _))
        {
            var destination = r!.ContainerAppsLogDestination?.Trim();
            yield return Check("obs-log-destination", IntegrationReviewDomain.Observability, id, "Container App log destination", IntegrationCheckStatus.Configured, "Where the consumer's container logs go.",
                $"{(string.Equals(destination, "azure-monitor", StringComparison.OrdinalIgnoreCase) ? "Azure Monitor" : destination ?? "Not stated")} · Dedicated Log Analytics workspace: {EventHubRuntimeSources.LogAnalyticsLabel(r)}",
                "A dedicated Log Analytics workspace is not required: telemetry is read through Application Insights.", scope: P);
        }

        if (metrics is null) yield break;
        var window = metrics.WindowHours;
        if (metrics.State == IntegrationEvidenceState.Available)
        {
            IntegrationCheck Transport(string checkId, string title, string metric) => metrics.Total(metric) is { } value
                ? Check(checkId, IntegrationReviewDomain.ErrorHandling, id, title, value == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Observed, "",
                    $"{Number(value)} in the last {window} h (namespace-wide).", "Transport evidence (Azure Monitor), observed without a threshold; application error handling is assessed separately.",
                    provenance: IntegrationEvidenceSource.AzureMonitor, at: metrics.CapturedAt, sourceTimestamp: metrics.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current, scope: P)
                : Check(checkId, IntegrationReviewDomain.ErrorHandling, id, title, IntegrationCheckStatus.NotAssessed, "", "", $"Azure Monitor returned no {metric} value.", provenance: IntegrationEvidenceSource.AzureMonitor, at: metrics.CapturedAt, scope: P);
            yield return Transport("err-transport-server", "Event Hubs server errors", "ServerErrors");
            yield return Transport("err-transport-user", "Event Hubs user errors", "UserErrors");
            yield return Transport("err-transport-throttling", "Throttled requests", "ThrottledRequests");
            var incoming = metrics.Total("IncomingMessages");
            var outgoing = metrics.Total("OutgoingMessages");
            string Rate(double? value) => value is { } v ? $"{Number(v)} ({Number(v / window)}/h)" : "not returned";
            yield return incoming is null && outgoing is null
                ? Check("perf-throughput", IntegrationReviewDomain.Performance, id, "Namespace throughput", IntegrationCheckStatus.NotAssessed, "", "", "Azure Monitor returned no message counts.",
                    provenance: IntegrationEvidenceSource.AzureMonitor, at: metrics.CapturedAt, scope: P)
                : Check("perf-throughput", IntegrationReviewDomain.Performance, id, "Namespace throughput", IntegrationCheckStatus.Observed, "",
                    $"Incoming {Rate(incoming)} · outgoing {Rate(outgoing)} events over {window} h{(metrics.Total("IncomingRequests") is { } requests ? $" · {Number(requests)} incoming request(s)" : "")}.",
                    "Observed only: no throughput threshold is configured, and throughput is not health.", provenance: IntegrationEvidenceSource.AzureMonitor, at: metrics.CapturedAt,
                    sourceTimestamp: metrics.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current, scope: P);
        }
        else
            foreach (var (checkId, title, domain) in new[]
                     {
                         ("err-transport-server", "Event Hubs server errors", IntegrationReviewDomain.ErrorHandling), ("err-transport-user", "Event Hubs user errors", IntegrationReviewDomain.ErrorHandling),
                         ("err-transport-throttling", "Throttled requests", IntegrationReviewDomain.ErrorHandling), ("perf-throughput", "Namespace throughput", IntegrationReviewDomain.Performance),
                     })
                yield return Check(checkId, domain, id, title, IntegrationCheckStatus.NotAssessed, "", "", $"{IntegrationReviewLabels.EvidenceState(metrics.State)}: {metrics.Reason}",
                    provenance: IntegrationEvidenceSource.AzureMonitor, at: metrics.CapturedAt, scope: P);
    }

    /// <summary>The run's Event Hub evidence for one platform, kept on the result so an old review renders exactly what it read.</summary>
    private static EventHubRuntimeSnapshot Snapshot(EventHubRuntimeSnapshot? existing, IntegrationPlatform platform, EventHubNamespaceObservation? ns, EventHubMetricsEvidence? metrics,
        List<EventHubHubComparison>? hubs, List<TopicEvidence> evidence, IntegrationEvidenceState telemetryState, bool azureOn)
    {
        var groups = evidence.Select(e => e.GroupComparison).OfType<EventHubConsumerGroupComparison>().ToList();
        var checkpointSummaries = evidence.Where(e => !string.IsNullOrWhiteSpace(e.Hub)).Select(e => EventHub.EventHubRuntimeEvaluation.Checkpoint(e.Topic, platform, e.Checkpoint)).ToList();
        if (existing is not null) return existing with { ConsumerGroups = [.. existing.ConsumerGroups, .. groups], Checkpoints = [.. existing.Checkpoints, .. checkpointSummaries] };
        var r = platform.RuntimeEvidence;
        return new EventHubRuntimeSnapshot
        {
            PlatformId = platform.Id, PlatformName = platform.Name, Namespace = platform.Namespace, AzureRuntimeEnabled = azureOn, CapturedAt = DateTimeOffset.UtcNow,
            NamespaceObservation = ns, Metrics = metrics, Hubs = hubs ?? [], ConsumerGroups = groups, Checkpoints = checkpointSummaries,
            ApplicationInsights = r?.ApplicationInsightsResourceName, TelemetryState = telemetryState, ContainerAppsLogDestination = r?.ContainerAppsLogDestination,
            LogAnalytics = EventHubRuntimeSources.LogAnalyticsLabel(r),
        };
    }

    /// <summary>
    /// The message-flow evidence chain for one topic: producer → hub → consumer group → checkpoint → application handler. Always "Not assessed":
    /// transport evidence never proves end-to-end processing, and application telemetry does not show that every event was processed.
    /// </summary>
    private static IntegrationCheck EndToEndCheck(TopicEvidence e, string? application, bool applicationObserved)
    {
        var hubObserved = e.HubComparison is { State: EventHubComparisonState.ObservedMatch or EventHubComparisonState.DifferenceObserved } || e.Metadata is { IsAvailable: true, Value.Exists: true };
        var hub = hubObserved ? "Observed"
            : e.HubComparison?.State == EventHubComparisonState.MissingInAzure || e.Metadata is { IsAvailable: true, Value.Exists: false } ? "Not found" : "Not observed";
        var group = e.Group is null ? "Unknown"
            : $"{e.Group} {(e.GroupComparison is { } gc ? EventHubComparisonLabels.State(gc.State) : "Not assessed")}{(e.GroupAssumed ? " (configured assumption)" : "")}";
        var checkpoint = e.Checkpoint is { IsAvailable: true } ? "Observed" : e.Checkpoint is { } c ? IntegrationReviewLabels.EvidenceState(c.State)
            : e.Platform?.RuntimeEvidence?.ResolvedCheckpointContainerUrl() is not null ? "Configured" : "Not configured";
        var handler = applicationObserved ? $"Observed ({application})" : e.Telemetry is { IsAvailable: true, Value.ProcessingTraces: > 0 } ? "Processing traces observed" : "Not observed";
        var transport = hubObserved && (e.Checkpoint is { IsAvailable: true } || e.GroupComparison?.State == EventHubComparisonState.ObservedMatch);
        return Check("flow-end-to-end", IntegrationReviewDomain.MessageFlow, e.Topic.Id, "End-to-end message flow", IntegrationCheckStatus.NotAssessed, "Events are processed end to end.",
            $"Producer {(string.IsNullOrWhiteSpace(e.Topic.Producer) ? "Not configured" : "Configured")} → Event Hub {hub} → Consumer group {group} → Checkpoint {checkpoint} → Application handler {handler}",
            applicationObserved || handler != "Not observed" ? "End-to-end not proven: application activity does not show that each event was processed."
            : transport ? "Transport progression evidence exists, but application processing has not been observed; end-to-end flow is not proven."
            : "End-to-end not proven: transport and application evidence are incomplete.");
    }

    /// <summary>Only what this run executed with evidence; a source that could not be read belongs to "What was not assessed".</summary>
    private static List<string> Tested(List<(IntegrationPlatform Platform, NamespaceProbeResult Probe)> probes, List<IntegrationEvidenceAdapterStatus> adapters, List<EventHubRuntimeSnapshot> snapshots,
        List<IntegrationCheck> checks, List<ApplicationMessagingRuntime> messagingRuntime, List<ServiceBusEvidenceCheck> serviceBus)
    {
        var tested = new List<string>();
        foreach (var (platform, probe) in probes.DistinctBy(p => p.Platform.Id))
            tested.Add($"Namespace DNS/TCP/TLS — {platform.NamespaceFqdn}: {(probe.Reachable && probe.TlsEstablished ? $"reachable ({probe.TlsProtocol ?? "TLS"})" : probe.Detail)}");
        foreach (var s in snapshots)
        {
            if (s.NamespaceObservation is { State: IntegrationEvidenceState.Available } ns)
            {
                tested.Add($"Event Hub namespace metadata — {s.Namespace} (Azure Resource Manager, GET only)");
                if (ns.HubListState == IntegrationEvidenceState.Available)
                    tested.Add($"Configured vs observed Event Hub topology — {s.Hubs.Count(h => h.IntegrationId is not null && h.State != EventHubComparisonState.ConfiguredDisabled)} configured, {ns.Hubs.Count} observed");
            }
            if (s.ConsumerGroups.Count(g => g.State is EventHubComparisonState.ObservedMatch or EventHubComparisonState.DifferenceObserved || g.Observed.Count > 0) is > 0 and var groups)
                tested.Add($"Consumer group existence — {groups} hub(s) (Azure Resource Manager)");
            if (s.Checkpoints.Count(c => c.Runtime == IntegrationEvidenceState.Available) is > 0 and var read)
                tested.Add($"Checkpoint metadata — {read} hub(s) (blob listing, read-only)");
            if (s.Metrics is { State: IntegrationEvidenceState.Available } m) tested.Add($"Event Hubs namespace metrics — last {m.WindowHours} h (Azure Monitor)");
        }
        bool Read(IntegrationEvidenceSource source) => adapters.Any(a => a.Source == source && a.State == IntegrationEvidenceState.Available);
        if (Read(IntegrationEvidenceSource.AzureMetadata)) tested.Add("Event Hub partition metadata — existence and last enqueued position");
        if (Read(IntegrationEvidenceSource.ApplicationInsights)) tested.Add("Application Insights telemetry — bounded aggregate queries");
        if (checks.Any(c => c.Provenance == IntegrationEvidenceSource.HealthEndpoint && c.Status != IntegrationCheckStatus.NotConfigured)) tested.Add("Consumer health endpoints (HTTP GET)");
        if (checks.Any(c => c.CheckId == "contract-compatibility" && IntegrationReviewLabels.IsAssessed(c.Status))) tested.Add("Producer/consumer contract compatibility (trusted JSON Schemas)");
        if (messagingRuntime.Count > 0) tested.Add("Code-route comparison — analyzed application messaging source bound to the consumer");
        if (serviceBus.Any(sb => sb.Runtime?.State == IntegrationEvidenceState.Available)) tested.Add("Service Bus metadata (separate transport)");
        return tested;
    }

    /// <summary>What the run did not assess — stated explicitly, with the reason for each unread source.</summary>
    private static List<string> NotAssessed(List<IntegrationDefinition> enabled, IntegrationCatalog catalog, List<IntegrationEvidenceAdapterStatus> adapters, List<IntegrationCheck> checks)
    {
        var items = new List<string>();
        var eventHub = enabled.Where(i => i.Kind == IntegrationKind.EventHub).ToList();
        if (eventHub.Count > 0)
        {
            items.Add("Business payload correctness — events are never read or consumed.");
            items.Add("End-to-end message processing — transport evidence never proves that the application processed each event.");
            var unconfirmed = eventHub.Count(i => i.Consumer.MappingState != ConsumerMappingState.Confirmed);
            var assumed = eventHub.Select(i => EffectiveGroup(i, catalog.Platforms.FirstOrDefault(p => p.Id == i.PlatformId))).Where(g => g.Assumed).ToList();
            if (unconfirmed > 0 || assumed.Count > 0)
                items.Add($"Consumer application mapping — {unconfirmed} mapping(s) not confirmed{(assumed.Count > 0 ? $"; consumer group {assumed[0].Group} is a configured assumption for {assumed.Count} topic(s), and observing it does not confirm the mapping" : "")}.");
            items.Add("Message loss and delivery guarantees.");
            items.Add("Processing correctness and business rules.");
            items.Add("Consumer identity authorization (RBAC) — BirkNext reads with its own identity.");
        }
        var noContracts = checks.Count(c => c.CheckId == "contract-compatibility" && !IntegrationReviewLabels.IsAssessed(c.Status));
        if (noContracts > 0) items.Add($"Contract compatibility — no producer and consumer schemas for {noContracts} integration(s).");
        var lag = checks.Count(c => c.CheckId == "perf-lag" && !IntegrationReviewLabels.IsAssessed(c.Status));
        if (lag > 0) items.Add($"Consumer lag — partition positions and checkpoints were not both available for {lag} integration(s).");
        if (checks.Any(c => c.CheckId == "dq-runtime-structure")) items.Add("Runtime event structure / data quality — no safe event-structure evidence.");
        foreach (var adapter in adapters.Where(a => a.State is not (IntegrationEvidenceState.Available or IntegrationEvidenceState.NotSupported)).DistinctBy(a => a.Adapter))
            items.Add($"{adapter.Adapter}: {IntegrationReviewLabels.EvidenceState(adapter.State)} — {adapter.Reason}");
        return items;
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ConfigurationChecks(TopicEvidence e, List<IntegrationReviewFinding> findings, bool compareWithAzure)
    {
        var topic = e.Topic;
        var id = topic.Id;
        if (compareWithAzure && topic.Kind == IntegrationKind.EventHub && !string.IsNullOrWhiteSpace(topic.EndpointOrTopic))
            yield return HubObservedCheck(e, findings);
        yield return Check("cfg-topic", IntegrationReviewDomain.Configuration, id, "Topic configured",
            string.IsNullOrWhiteSpace(topic.EndpointOrTopic) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The integration names its Event Hub / endpoint.", topic.EndpointOrTopic ?? "Not configured", "Configuration only — existence is a Connectivity question.");
        yield return Check("cfg-producer", IntegrationReviewDomain.Configuration, id, "Producer configured",
            string.IsNullOrWhiteSpace(topic.Producer) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass, "The producing system is named.", topic.Producer ?? "Not configured", "");
        yield return Check("cfg-consumer", IntegrationReviewDomain.Configuration, id, "Consumer mapping",
            topic.Consumer.MappingState == ConsumerMappingState.Confirmed ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.NeedsConfirmation,
            "The consuming application is confirmed for this environment.",
            topic.Consumer.DisplayName is { } consumer ? $"{consumer} — {IntegrationConfigurationRules.MappingLabel(topic.Consumer.MappingState)}{(topic.Consumer.MappingSource is { } src ? $" ({src})" : "")}" : "No consumer configured",
            topic.Consumer.MappingState == ConsumerMappingState.Suggested ? "Suggested by the audited source; receiver rights on the namespace never confirm a consumer." : "",
            topic.Consumer.MappingState == ConsumerMappingState.Confirmed ? null : "Confirm the consuming application for this environment.");
        if (topic.Kind == IntegrationKind.EventHub)
        {
            var suggestion = SuggestedGroup(e);
            var observed = e.Groups is { IsAvailable: true, Value: { } listed } ? $" · Observed on the hub: {(listed.Names.Count == 0 ? "none" : string.Join(", ", listed.Names))}" : "";
            yield return e.GroupAssumed
                ? Check("cfg-consumer-group", IntegrationReviewDomain.Configuration, id, "Consumer group", IntegrationCheckStatus.NeedsConfirmation,
                    "The consumer group the consumer reads with is known.",
                    $"Expected: {e.Group} ({IntegrationRuntimeEvidenceSettings.ProvenanceLabel(e.Platform?.RuntimeEvidence?.ExpectedConsumerGroupProvenance ?? IntegrationValueProvenance.ConfiguredAssumption)}){observed} · Mapping: Needs confirmation",
                    "The platform's expected group is used for checkpoint lookups; it is an assumption, not the confirmed group of this consumer, so no checkpoint or lag verdict is given.",
                    "Confirm the consumer group the consumer reads with, and save it on the integration.",
                    e.Groups is { IsAvailable: true } ? e.Groups.Source : IntegrationEvidenceSource.Configuration)
                : Check("cfg-consumer-group", IntegrationReviewDomain.Configuration, id, "Consumer group",
                    string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
                    "The consumer group the consumer reads with is known.",
                    topic.ConsumerGroup ?? (suggestion is null ? "Unknown / not configured" : $"Unknown — Azure lists one non-default group: {suggestion} (suggestion, not saved)"),
                    string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? "Only checkpoint/lag review depends on it; $Default is never assumed unless the platform states it as an expectation." : "",
                    string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? "Confirm the consumer group to enable checkpoint/lag review." : null);
            if (IsDebezium(e))
                yield return Check("cfg-delete-expectation", IntegrationReviewDomain.Configuration, id, "Delete/tombstone expectation",
                    topic.DeleteExpectation == CdcDeleteExpectation.NotSpecified ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
                    "The consumer's expectation for delete events and tombstones is stated.", DeleteLabel(topic.DeleteExpectation), "Tombstone handling is only judged against a stated expectation.");
        }
        yield return Check("cfg-contract", IntegrationReviewDomain.Configuration, id, "Contract relationship",
            topic.ContractRelationship == ContractRelationshipState.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "Producer and consumer contracts are referenced.", IntegrationConfigurationRules.ContractLabel(topic.ContractRelationship), "Configured references are not contract compatibility.");
    }

    /// <summary>The configured hub against the hub list Azure Resource Manager returned: Observed match, Difference observed or Configured but not found.</summary>
    private static IntegrationCheck HubObservedCheck(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        var hc = e.HubComparison;
        var expectation = hc is null ? "The configured hub exists in the namespace." :
            $"Configured: {string.Join(", ", new[] { hc.ConfiguredPartitions is { } p ? $"{p} partition(s)" : null, hc.ConfiguredRetentionHours is { } h ? $"{h} h retention" : null }.OfType<string>().DefaultIfEmpty("exists in the namespace"))}.";
        const IntegrationEvidenceSource Arm = IntegrationEvidenceSource.AzureResourceManager;
        switch (hc?.State)
        {
            case EventHubComparisonState.ObservedMatch:
                return Check("cfg-hub-observed", IntegrationReviewDomain.Configuration, id, "Configured Event Hub in Azure", IntegrationCheckStatus.Observed, expectation, $"Observed match: {hc.Detail}.",
                    "Configured and observed agree; a hub that exists does not show that messages are processed.", provenance: Arm, freshness: IntegrationEvidenceItemFreshness.Current);
            case EventHubComparisonState.DifferenceObserved:
                if (hc.ConfiguredPartitions is { } cp && hc.ObservedPartitions is { } op && cp != op)
                    findings.Add(Finding($"partition-drift|{id}", "partition-drift", IntegrationReviewDomain.Connectivity, IntegrationFindingSeverityV2.Low, "Configured partition count differs from the runtime hub",
                        e.Topic.EndpointOrTopic ?? e.Topic.DisplayName, [$"Configured {cp}, observed {op} (Azure Resource Manager)."], "Align the configuration with the provisioned hub.", e.Topic.DisplayName));
                if (hc.ConfiguredRetentionHours is { } cr && hc.ObservedRetentionHours is { } or2 && cr != or2)
                    findings.Add(Finding($"retention-drift|{id}", "retention-drift", IntegrationReviewDomain.Configuration, IntegrationFindingSeverityV2.Low, "Configured retention differs from the runtime hub",
                        e.Topic.EndpointOrTopic ?? e.Topic.DisplayName, [$"Configured {cr} h, observed {or2} h (Azure Resource Manager)."], "Align the configured retention with the provisioned hub.", e.Topic.DisplayName));
                return Check("cfg-hub-observed", IntegrationReviewDomain.Configuration, id, "Configured Event Hub in Azure", IntegrationCheckStatus.Warning, expectation, hc.Detail,
                    "Configuration and Azure disagree; one of them is out of date. A difference is not a failure of the integration.",
                    "Align the configuration with the provisioned hub, or check whether the hub was re-provisioned.", Arm, freshness: IntegrationEvidenceItemFreshness.Current);
            case EventHubComparisonState.MissingInAzure:
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"hub-missing|{e.Topic.EndpointOrTopic}", RuleId = "hub-missing", Domain = IntegrationReviewDomain.Configuration, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Configured Event Hub not found in Azure", Subject = e.Topic.EndpointOrTopic ?? e.Topic.DisplayName,
                    Evidence = ["Configured, but absent from the namespace's hub list (Azure Resource Manager)."],
                    Recommendation = "Correct the configured Event Hub name or provision the hub through the platform's normal process.", AffectedIntegrations = [e.Topic.DisplayName],
                });
                return Check("cfg-hub-observed", IntegrationReviewDomain.Configuration, id, "Configured Event Hub in Azure", IntegrationCheckStatus.Warning, expectation, "Configured but not found in Azure.",
                    hc.Detail, "Correct the hub name or provision the hub through the platform's normal process.", Arm, freshness: IntegrationEvidenceItemFreshness.Current);
            default:
                return Check("cfg-hub-observed", IntegrationReviewDomain.Configuration, id, "Configured Event Hub in Azure", IntegrationCheckStatus.NotAssessed, expectation, "",
                    hc is null ? "The namespace hub list was not read." : $"{EventHubComparisonLabels.State(hc.State)}: {hc.Detail}", provenance: Arm);
        }
    }

    private static bool IsDebezium(TopicEvidence e) => (e.Platform?.ProducerTechnology ?? e.Topic.Producer ?? "").Contains("Debezium", StringComparison.OrdinalIgnoreCase);

    private static string DeleteLabel(CdcDeleteExpectation expectation) => expectation switch
    {
        CdcDeleteExpectation.DeleteEventOnly => "Delete event only (no tombstone)",
        CdcDeleteExpectation.DeleteEventAndTombstone => "Delete event followed by a tombstone",
        CdcDeleteExpectation.TombstoneNotExpected => "Tombstones not expected",
        _ => "Not specified",
    };

    // ── Connectivity ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ConnectivityChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        if (e.Metadata is not { IsAvailable: true, Value: { } hub })
        {
            yield return Missing("conn-hub", IntegrationReviewDomain.Connectivity, id, "Configured Event Hub exists", "The configured hub exists in the namespace.", e.Metadata,
                "No hub name is configured; existence is never inferred from saved configuration.");
            yield return Missing("conn-partitions", IntegrationReviewDomain.Connectivity, id, "Partition count", "Runtime partitions match the configured count.", e.Metadata, "No Event Hub metadata.");
            yield break;
        }
        yield return Check("conn-hub", IntegrationReviewDomain.Connectivity, id, "Configured Event Hub exists", hub.Exists ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.Fail,
            "The configured hub exists in the namespace.", hub.Exists ? "Observed match: found by Event Hub metadata." : "Event Hub metadata reported the hub as not found.",
            hub.Exists ? "Existence is not message flow." : "",
            hub.Exists ? null : "Correct the hub name or provision the hub through the platform's normal process.", e.Metadata.Source, e.Metadata.CapturedAt, e.Metadata.CapturedAt, IntegrationEvidenceItemFreshness.Current);
        if (!hub.Exists)
        {
            findings.Add(new IntegrationReviewFinding
            {
                Key = $"hub-missing|{e.Topic.EndpointOrTopic}", RuleId = "hub-missing", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                Title = "Configured Event Hub does not exist", Subject = e.Topic.EndpointOrTopic ?? e.Topic.DisplayName,
                Evidence = [$"Event Hub metadata reported the hub as not found ({e.Metadata.CapturedAt:u})."],
                Recommendation = "Correct the configured Event Hub name or provision the hub through the platform's normal process.", AffectedIntegrations = [e.Topic.DisplayName],
            });
            yield return Check("conn-partitions", IntegrationReviewDomain.Connectivity, id, "Partition count", IntegrationCheckStatus.NotAssessed, "", "", "The hub does not exist.");
            yield break;
        }
        var configured = e.Topic.PartitionCount ?? e.Platform?.DefaultPartitionCount;
        var runtime = hub.Partitions.Count;
        if (configured is null)
            yield return Check("conn-partitions", IntegrationReviewDomain.Connectivity, id, "Partition count", IntegrationCheckStatus.Observed, "", $"{runtime} partition(s) at runtime.", "No partition count is configured to compare.",
                provenance: e.Metadata.Source, at: e.Metadata.CapturedAt, sourceTimestamp: e.Metadata.CapturedAt, freshness: IntegrationEvidenceItemFreshness.Current);
        else
        {
            yield return Check("conn-partitions", IntegrationReviewDomain.Connectivity, id, "Partition count", configured == runtime ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.Warning,
                $"{configured} partition(s) as configured.", $"{runtime} partition(s) at runtime.", configured == runtime ? "Observed match." : "Configuration and runtime disagree; one of them is out of date.",
                configured == runtime ? null : "Update the configured partition count, or check whether the hub was re-provisioned.", e.Metadata.Source, e.Metadata.CapturedAt, e.Metadata.CapturedAt, IntegrationEvidenceItemFreshness.Current);
            if (configured != runtime)
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"partition-drift|{e.Topic.Id}", RuleId = "partition-drift", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.Low,
                    Title = "Configured partition count differs from the runtime hub", Subject = e.Topic.EndpointOrTopic ?? e.Topic.DisplayName,
                    Evidence = [$"Configured {configured}, runtime {runtime} (Event Hub metadata)."], Recommendation = "Align the configuration with the provisioned hub.", AffectedIntegrations = [e.Topic.DisplayName],
                });
        }
    }

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ContractChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        var producer = e.Producer;
        var consumer = e.Consumer;
        string Describe((IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem) c) => $"{c.Artifact.FileName} (sha256 {c.Artifact.ShortHash}{(c.Artifact.Version is { } v ? $", {v}" : "")})";
        yield return (producer, consumer) switch
        {
            (not null, not null) => Check("contract-availability", IntegrationReviewDomain.Contract, id, "Contract availability", IntegrationCheckStatus.Pass, "Producer and consumer contracts are available.",
                $"Producer {Describe(producer.Value)}; consumer {Describe(consumer.Value)}.", "Availability is not compatibility.", provenance: IntegrationEvidenceSource.ContractArtifact),
            (not null, null) => Check("contract-availability", IntegrationReviewDomain.Contract, id, "Contract availability", IntegrationCheckStatus.Observed, "Producer and consumer contracts are available.",
                $"Producer contract only: {Describe(producer.Value)}.", "Compatibility needs the consumer contract too.", "Upload the consumer contract.", IntegrationEvidenceSource.ContractArtifact),
            (null, not null) => Check("contract-availability", IntegrationReviewDomain.Contract, id, "Contract availability", IntegrationCheckStatus.Observed, "Producer and consumer contracts are available.",
                $"Consumer contract only: {Describe(consumer.Value)}.", "Compatibility needs the producer contract too.", "Upload the producer contract.", IntegrationEvidenceSource.ContractArtifact),
            _ => Check("contract-availability", IntegrationReviewDomain.Contract, id, "Contract availability", IntegrationCheckStatus.NotConfigured, "Producer and consumer contracts are available.",
                e.Topic.ContractRelationship == ContractRelationshipState.NotConfigured ? "No contract uploaded." : $"References configured ({IntegrationConfigurationRules.ContractLabel(e.Topic.ContractRelationship)}); no contract artifact uploaded.",
                "No contract is not the same as no compatibility issue.", "Upload the producer and consumer JSON Schemas."),
        };

        if (producer is { Contract: { } p } && consumer is { Contract: { } c })
        {
            var differences = EventContractComparer.Compare(p, c);
            var breaking = differences.Where(d => d.Breaking).ToList();
            yield return Check("contract-compatibility", IntegrationReviewDomain.Contract, id, "Producer/consumer compatibility",
                breaking.Count == 0 ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail, "Everything the consumer requires is provided by the producer with an accepted type.",
                breaking.Count == 0 ? $"Compatible{(differences.Count > 0 ? $"; {differences.Count} informational difference(s): {string.Join(" ", differences.Select(d => d.Detail))}" : ".")}" : string.Join(" ", breaking.Select(d => d.Detail)),
                "Structural comparison of the two trusted contracts; no payload is read.", breaking.Count == 0 ? null : "Align the producer and consumer contracts, or version the change.", IntegrationEvidenceSource.ContractArtifact);
            if (breaking.Count > 0)
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"contract-incompatible|{id}", RuleId = "contract-incompatible", Domain = IntegrationReviewDomain.Contract, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Consumer contract is not satisfied by the producer contract", Subject = e.Topic.DisplayName,
                    Evidence = breaking.Select(d => $"{d.Code}: {d.Detail}").Take(20).ToList(),
                    Recommendation = "Add the missing/changed fields to the producer contract or relax the consumer's requirement; version the contract if the change is intentional.",
                    AffectedIntegrations = [e.Topic.DisplayName],
                });
        }
        else
            yield return Check("contract-compatibility", IntegrationReviewDomain.Contract, id, "Producer/consumer compatibility", IntegrationCheckStatus.NotAssessed,
                "Everything the consumer requires is provided by the producer with an accepted type.", "",
                producer is { Problem: { } pp } ? $"The stored producer contract no longer parses: {pp}" : consumer is { Problem: { } cp } ? $"The stored consumer contract no longer parses: {cp}"
                : "Compatibility needs both a producer and a consumer contract; one-sided evidence is never called compatible.");

        foreach (var (role, current) in new[] { (IntegrationContractRole.Producer, producer), (IntegrationContractRole.Consumer, consumer) })
        {
            if (current is null) continue;
            var previous = e.PreviousContracts.FirstOrDefault(a => a.IntegrationId == id && a.Role == role);
            yield return previous is null
                ? Check($"contract-drift-{role.ToString().ToLowerInvariant()}", IntegrationReviewDomain.Contract, id, $"{role} contract drift", IntegrationCheckStatus.NotAssessed, "", "",
                    "No previous review recorded this contract, so there is no baseline to compare.", provenance: IntegrationEvidenceSource.ContractArtifact)
                : previous.ContentHash == current.Value.Artifact.ContentHash
                    ? Check($"contract-drift-{role.ToString().ToLowerInvariant()}", IntegrationReviewDomain.Contract, id, $"{role} contract drift", IntegrationCheckStatus.Pass, "Unchanged since the previous review.",
                        $"sha256 {current.Value.Artifact.ShortHash} (as in the previous review).", "Drift is separate from compatibility.", provenance: IntegrationEvidenceSource.ContractArtifact)
                    : Check($"contract-drift-{role.ToString().ToLowerInvariant()}", IntegrationReviewDomain.Contract, id, $"{role} contract drift", IntegrationCheckStatus.Observed, "Unchanged since the previous review.",
                        $"Changed: sha256 {previous.ShortHash} → {current.Value.Artifact.ShortHash}.", "A change is drift, not an incompatibility; compatibility is judged separately.", provenance: IntegrationEvidenceSource.ContractArtifact);
        }
    }

    // ── Message flow ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> MessageFlowChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        if (e.GroupComparison is { } gc)
        {
            var observed = gc.State is EventHubComparisonState.NotAuthorized || (gc.State == EventHubComparisonState.NotAssessed && gc.Observed.Count == 0) ? "not read" : gc.Observed.Count == 0 ? "none" : string.Join(", ", gc.Observed);
            var read = e.Groups is { IsAvailable: true };
            yield return Check("flow-consumer-group", IntegrationReviewDomain.MessageFlow, id, "Expected consumer group observed",
                gc.State switch
                {
                    EventHubComparisonState.ObservedMatch => IntegrationCheckStatus.Observed,
                    EventHubComparisonState.DifferenceObserved => e.GroupAssumed ? IntegrationCheckStatus.NeedsConfirmation : IntegrationCheckStatus.Warning,
                    _ when read => IntegrationCheckStatus.Observed,
                    _ => IntegrationCheckStatus.NotAssessed,
                },
                "The group the consumer reads with exists on the hub.",
                (gc.Expected is null ? "Expected: not configured" : $"Expected: {gc.Expected} ({gc.ExpectedProvenance})") + $" · Observed: {observed} · Result: {(gc.Expected is null && read ? "Observed only" : EventHubComparisonLabels.State(gc.State))} · Application mapping: {gc.Mapping}",
                gc.Reason, gc.State == EventHubComparisonState.DifferenceObserved ? "Confirm the consumer group the consumer reads with." : null,
                read ? e.Groups!.Source : IntegrationEvidenceSource.AzureResourceManager, e.Groups?.CapturedAt, read ? e.Groups!.CapturedAt : null,
                read ? IntegrationEvidenceItemFreshness.Current : IntegrationEvidenceItemFreshness.Unknown);
            if (gc.State == EventHubComparisonState.DifferenceObserved && !e.GroupAssumed)
                findings.Add(Finding($"consumer-group-missing|{id}", "consumer-group-missing", IntegrationReviewDomain.MessageFlow, IntegrationFindingSeverityV2.Medium,
                    "Configured consumer group not found on the hub", e.Topic.EndpointOrTopic ?? e.Topic.DisplayName, [$"Configured {gc.Expected}; Azure lists {observed}."],
                    "Correct the configured consumer group, or check how the consumer reads the hub.", e.Topic.DisplayName));
        }
        if (e.Metadata is { IsAvailable: true, Value: { Exists: true } hub })
        {
            var last = hub.LastEnqueuedTime;
            yield return last is null
                ? Check("flow-producer", IntegrationReviewDomain.MessageFlow, id, "Producer activity", IntegrationCheckStatus.NoRecentEvidence, "Events are being published.",
                    "Every partition is empty.", "No activity is not a failure; the source may be idle.", provenance: e.Metadata.Source, at: e.Metadata.CapturedAt)
                : e.InWindow(last)
                    ? Check("flow-producer", IntegrationReviewDomain.MessageFlow, id, "Producer activity", IntegrationCheckStatus.Observed, "Events are being published.",
                        $"Last enqueued {last:u} ({Ago(last.Value, e.CapturedAt)}).", "Producer activity alone never proves consumer success.", provenance: e.Metadata.Source, at: e.Metadata.CapturedAt, sourceTimestamp: last, freshness: e.Fresh(last))
                    : Check("flow-producer", IntegrationReviewDomain.MessageFlow, id, "Producer activity", IntegrationCheckStatus.NoRecentEvidence, "Events are being published.",
                        $"Last enqueued {last:u} ({Ago(last.Value, e.CapturedAt)}), outside the {e.WindowHours} h review window.", "No recent activity is not a failure without an activity expectation.",
                        provenance: e.Metadata.Source, at: e.Metadata.CapturedAt, sourceTimestamp: last, freshness: e.Fresh(last));
        }
        else
            yield return Missing("flow-producer", IntegrationReviewDomain.MessageFlow, id, "Producer activity", "Events are being published.", e.Metadata, "No Event Hub metadata.");

        var checkpointAt = e.Checkpoint is { IsAvailable: true, Value: { } cp } ? cp.LastUpdated : null;
        var telemetryAt = e.Telemetry is { IsAvailable: true, Value: { } tel } && (tel.ProcessingTraces > 0 || tel.DependencyCalls > 0) ? tel.LastActivity : null;
        var consumerAt = new[] { checkpointAt, telemetryAt }.Max();
        var source = checkpointAt >= (telemetryAt ?? DateTimeOffset.MinValue) && checkpointAt is not null ? IntegrationEvidenceSource.CheckpointStore : IntegrationEvidenceSource.ApplicationInsights;
        if (consumerAt is { } seen)
            yield return Check("flow-consumer", IntegrationReviewDomain.MessageFlow, id, "Consumer activity", e.InWindow(seen) ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NoRecentEvidence,
                "The consumer makes progress.", $"Last consumer activity {seen:u} ({Ago(seen, e.CapturedAt)}) from {IntegrationReviewLabels.Source(source)}.",
                "Consumer activity is not correct processing; see Error handling.", provenance: source, sourceTimestamp: seen, freshness: e.Fresh(seen));
        else if (e.Checkpoint?.State == IntegrationEvidenceState.NotFound || e.Telemetry is { IsAvailable: true })
            yield return Check("flow-consumer", IntegrationReviewDomain.MessageFlow, id, "Consumer activity", IntegrationCheckStatus.NoRecentEvidence, "The consumer makes progress.",
                e.Checkpoint?.State == IntegrationEvidenceState.NotFound ? e.Checkpoint.Reason : $"No consumer processing telemetry in the last {e.WindowHours} h.", "No recent activity is not a failure.",
                provenance: e.Telemetry is { IsAvailable: true } ? IntegrationEvidenceSource.ApplicationInsights : IntegrationEvidenceSource.CheckpointStore);
        else
            yield return Check("flow-consumer", IntegrationReviewDomain.MessageFlow, id, "Consumer activity", IntegrationCheckStatus.NotAssessed, "The consumer makes progress.", "",
                string.Join(" ", new[]
                {
                    e.Group is null ? "Consumer group unknown (no checkpoint lookup)." : e.Checkpoint is { } c1 ? $"Checkpoints: {c1.Reason}" : null,
                    string.IsNullOrWhiteSpace(e.Role) ? "Consumer application not configured (no telemetry attribution)." : e.Telemetry is { } t1 ? $"Telemetry: {t1.Reason}" : null,
                }.OfType<string>()));
    }

    // ── Reliability ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ReliabilityChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        if (e.Group is null)
        {
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints", IntegrationCheckStatus.NotAssessed, "The consumer records its position.", "",
                "Consumer group is not configured.", "Confirm the consumer group to enable checkpoint/lag review.");
            yield return Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", IntegrationCheckStatus.NotAssessed, "", "", "Consumer group is not configured.");
        }
        else if (e.Checkpoint is { IsAvailable: true, Value: { } cp })
        {
            var hubPartitions = e.Metadata is { IsAvailable: true, Value: { Exists: true } hub } ? hub.Partitions.Count : (int?)null;
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints",
                e.GroupAssumed ? IntegrationCheckStatus.Observed : hubPartitions is { } total && cp.Partitions.Count < total ? IntegrationCheckStatus.Warning : IntegrationCheckStatus.Pass, "The consumer records its position.",
                $"Checkpoints for {cp.Partitions.Count}{(hubPartitions is { } t ? $" of {t}" : "")} partition(s), consumer group {cp.ConsumerGroup}{e.AssumptionNote}; {cp.OwnershipRecords} ownership record(s).",
                e.GroupAssumed ? "Observed for the assumed group only; it does not show which application owns the group, so no verdict is given." : "Checkpoint present is not lag acceptable; see Performance.", hubPartitions is { } t2 && cp.Partitions.Count < t2 ? "Check why some partitions have no checkpoint." : null,
                e.Checkpoint.Source, e.Checkpoint.CapturedAt, cp.LastUpdated, e.Fresh(cp.LastUpdated));
            // An assumed group is never judged: a threshold verdict would read as confirmation of the group.
            var threshold = e.GroupAssumed ? null : e.Platform?.RuntimeEvidence?.MaxCheckpointAgeMinutes;
            if (cp.LastUpdated is { } updated)
            {
                var ageMinutes = (e.CapturedAt - updated).TotalMinutes;
                yield return threshold is { } limit
                    ? Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", ageMinutes <= limit ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning,
                        $"Last checkpoint within {limit} min (IQR threshold).", $"Last checkpoint {updated:u} ({Ago(updated, e.CapturedAt)}).", "",
                        ageMinutes <= limit ? null : "Check whether the consumer is running and checkpointing.", e.Checkpoint.Source, e.Checkpoint.CapturedAt, updated, e.Fresh(updated))
                    : Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", IntegrationCheckStatus.Observed, "",
                        $"Last checkpoint {updated:u} ({Ago(updated, e.CapturedAt)}){e.AssumptionNote}.",
                        e.GroupAssumed ? "The consumer group is a configured assumption, so the age is reported without a verdict." : "No IQR checkpoint-age threshold is configured, so the age is reported without a verdict.",
                        provenance: e.Checkpoint.Source, at: e.Checkpoint.CapturedAt, sourceTimestamp: updated, freshness: e.Fresh(updated));
                if (threshold is { } l2 && ageMinutes > l2)
                    findings.Add(new IntegrationReviewFinding
                    {
                        Key = $"consumer-progress-stale|{id}", RuleId = "consumer-progress-stale", Domain = IntegrationReviewDomain.Reliability, Severity = IntegrationFindingSeverityV2.Medium,
                        Title = "Consumer checkpoint is older than the IQR threshold", Subject = e.Topic.DisplayName,
                        Evidence = [$"Last checkpoint {updated:u}; threshold {l2} min (checkpoint store)."], Recommendation = "Check the consumer's health and checkpointing.", AffectedIntegrations = [e.Topic.DisplayName],
                    });
            }
        }
        else if (e.Checkpoint?.State == IntegrationEvidenceState.NotFound)
        {
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints", IntegrationCheckStatus.NoRecentEvidence, "The consumer records its position.",
                e.Checkpoint.Reason, "The consumer may not have run yet, or checkpoints elsewhere.", "Confirm the consumer group and checkpoint store.", e.Checkpoint.Source, e.Checkpoint.CapturedAt);
            yield return Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", IntegrationCheckStatus.NotAssessed, "", "", "No checkpoint is recorded.");
        }
        else
        {
            yield return Missing("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints", "The consumer records its position.", e.Checkpoint, "No checkpoint evidence source configured.");
            yield return Missing("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", "", e.Checkpoint, "No checkpoint evidence source configured.");
        }

        if (e.Telemetry is { IsAvailable: true, Value: { } tel })
        {
            yield return Check("rel-retry", IntegrationReviewDomain.Reliability, id, "Retry indicators", tel.RetryIndicators == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Observed,
                "", $"{tel.RetryIndicators} retry indicator(s) in {tel.WindowHours} h.", tel.RetryIndicators == 0 ? "None in the window; this does not prove retry behaviour." : "Retries happen; see Error handling for failures.",
                provenance: e.Telemetry.Source, at: e.Telemetry.CapturedAt, sourceTimestamp: tel.LastActivity, freshness: e.Fresh(tel.LastActivity));
            yield return Check("rel-dependency-failures", IntegrationReviewDomain.Reliability, id, "Event Hubs dependency failures",
                tel.DependencyCalls == 0 ? IntegrationCheckStatus.NoRecentEvidence : tel.DependencyFailures == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Observed,
                "", tel.DependencyCalls == 0 ? $"No Event Hubs dependency calls recorded in {tel.WindowHours} h." : $"{tel.DependencyFailures} failed of {tel.DependencyCalls} call(s).", "",
                provenance: e.Telemetry.Source, at: e.Telemetry.CapturedAt, sourceTimestamp: tel.LastActivity, freshness: e.Fresh(tel.LastActivity));
        }
        else
            yield return Missing("rel-retry", IntegrationReviewDomain.Reliability, id, "Retry indicators", "", e.Telemetry,
                string.IsNullOrWhiteSpace(e.Role) ? "Consumer application not configured, so telemetry cannot be attributed." : "No telemetry source configured.");
        yield return Check("rel-replay-duplicates", IntegrationReviewDomain.Reliability, id, "Replay / duplicate handling", IntegrationCheckStatus.NotAssessed, "Replays and duplicates are handled idempotently.", "",
            "No evidence source shows replay or duplicate handling; verify idempotency manually.");
    }

    // ── Error handling ──────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ErrorHandlingChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        var checks = new[] { ("err-deserialization", "Deserialization error indicators"), ("err-authorization", "Authorization error indicators"),
            ("err-consumer-exceptions", "Consumer exception indicators"), ("err-dead-letter", "Poison / dead-letter indicators") };
        if (e.Telemetry is not { IsAvailable: true, Value: { } tel })
        {
            foreach (var (cid, title) in checks)
                yield return Missing(cid, IntegrationReviewDomain.ErrorHandling, id, title, "", e.Telemetry,
                    string.IsNullOrWhiteSpace(e.Role) ? "Consumer application (container app / cloud role) is not configured, so telemetry cannot be attributed." : "No telemetry source configured.");
            yield break;
        }
        var role = e.Role!;
        IntegrationCheck Indicator(string cid, string title, long count, IntegrationCheckStatus whenObserved, string recommendation) =>
            Check(cid, IntegrationReviewDomain.ErrorHandling, id, title, count == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : whenObserved, "",
                $"{count} in the last {tel.WindowHours} h (consumer {role}).", count == 0 ? "None observed; this does not establish correct error handling." : "",
                count == 0 ? null : recommendation, e.Telemetry.Source, e.Telemetry.CapturedAt, tel.LastException ?? tel.LastActivity, e.Fresh(tel.LastException ?? tel.LastActivity));
        yield return Indicator("err-deserialization", "Deserialization error indicators", tel.DeserializationErrors, IntegrationCheckStatus.Warning, "Compare failing payload shapes with the consumer contract; route undeserializable events to a poison path.");
        yield return Indicator("err-authorization", "Authorization error indicators", tel.AuthorizationErrors, IntegrationCheckStatus.Warning, "See Security: runtime authorization.");
        yield return Indicator("err-consumer-exceptions", "Consumer exception indicators", tel.Exceptions, IntegrationCheckStatus.Warning, "Investigate the consumer's exceptions in Application Insights.");
        yield return Indicator("err-dead-letter", "Poison / dead-letter indicators", tel.DeadLetterIndicators, IntegrationCheckStatus.Warning, "Inspect the poison/dead-letter path and replay once fixed.");
        // Consumer-level findings: one logical issue per consumer application, listing every topic it serves.
        if (tel.DeserializationErrors > 0)
            findings.Add(Finding($"deserialization-errors|{role}", "deserialization-errors", IntegrationReviewDomain.ErrorHandling, IntegrationFindingSeverityV2.Medium,
                "Consumer reports deserialization errors", role, [$"{tel.DeserializationErrors} deserialization error(s) in {tel.WindowHours} h (Application Insights)."],
                "Compare the failing payload shape with the consumer contract; route undeserializable events to a poison/dead-letter path.", e.Topic.DisplayName));
        if (tel.DeadLetterIndicators > 0)
            findings.Add(Finding($"dead-letter|{role}", "dead-letter", IntegrationReviewDomain.ErrorHandling, IntegrationFindingSeverityV2.Medium,
                "Consumer routes events to a poison/dead-letter path", role, [$"{tel.DeadLetterIndicators} indicator(s) in {tel.WindowHours} h."], "Inspect and replay the affected events once fixed.", e.Topic.DisplayName));
        if (tel.Exceptions > 0 && tel.DeserializationErrors == 0 && tel.AuthorizationErrors == 0)
            findings.Add(Finding($"consumer-exceptions|{role}", "consumer-exceptions", IntegrationReviewDomain.ErrorHandling, IntegrationFindingSeverityV2.Low,
                "Consumer exceptions observed", role, [$"{tel.Exceptions} exception(s) in {tel.WindowHours} h."], "Investigate the exceptions in Application Insights.", e.Topic.DisplayName));
    }

    private static IntegrationReviewFinding Finding(string key, string rule, IntegrationReviewDomain domain, IntegrationFindingSeverityV2 severity, string title, string subject, List<string> evidence, string recommendation, string affected) =>
        new() { Key = key, RuleId = rule, Domain = domain, Severity = severity, Title = title, Subject = subject, Evidence = evidence, Recommendation = recommendation, AffectedIntegrations = [affected] };

    // ── Security ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> SecurityChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var topic = e.Topic;
        yield return Check("sec-consumer-auth", IntegrationReviewDomain.Security, topic.Id, "Consumer authentication mechanism configured",
            topic.ConsumerAuthentication == IntegrationAuthMechanism.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The consumer's authentication mechanism is named.", IntegrationConfigurationRules.AuthLabel(topic.ConsumerAuthentication) + (topic.Consumer.ManagedIdentity is { } mi ? $" ({mi})" : ""),
            "Configured, not validated: Managed Identity configured is not runtime authorization verified.");
        if (e.Telemetry is { IsAvailable: true, Value: { } tel })
        {
            yield return Check("sec-runtime-auth", IntegrationReviewDomain.Security, topic.Id, "Runtime consumer authorization",
                tel.AuthorizationErrors == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Fail, "The consumer is authorized to receive.",
                $"{tel.AuthorizationErrors} authorization failure indicator(s) in {tel.WindowHours} h.", tel.AuthorizationErrors == 0 ? "No failures observed; this is not proof of authorization." : "",
                tel.AuthorizationErrors == 0 ? null : "Grant the consumer identity the Azure Event Hubs Data Receiver role on the hub.", e.Telemetry.Source, e.Telemetry.CapturedAt, tel.LastException, e.Fresh(tel.LastException));
            if (tel.AuthorizationErrors > 0)
                findings.Add(Finding($"consumer-unauthorized|{e.Role}", "consumer-unauthorized", IntegrationReviewDomain.Security, IntegrationFindingSeverityV2.High,
                    "Consumer reports authorization failures", e.Role!, [$"{tel.AuthorizationErrors} authorization failure indicator(s) in {tel.WindowHours} h (Application Insights)."],
                    "Grant the consumer identity the Azure Event Hubs Data Receiver role on the hub.", topic.DisplayName));
        }
        else
            yield return Missing("sec-runtime-auth", IntegrationReviewDomain.Security, topic.Id, "Runtime consumer authorization", "The consumer is authorized to receive.", e.Telemetry,
                "No runtime authorization evidence (telemetry) is available.");
    }

    // ── Observability ───────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ObservabilityChecks(TopicEvidence e)
    {
        var id = e.Topic.Id;
        if (e.Telemetry is { IsAvailable: true, Value: { } tel })
        {
            yield return Check("obs-traces", IntegrationReviewDomain.Observability, id, "Integration-specific traces", tel.ProcessingTraces > 0 ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NoRecentEvidence,
                "Processing is visible in telemetry.", $"{tel.ProcessingTraces} processing trace(s) in {tel.WindowHours} h.", "", provenance: e.Telemetry.Source, at: e.Telemetry.CapturedAt, sourceTimestamp: tel.LastActivity, freshness: e.Fresh(tel.LastActivity));
            yield return Check("obs-correlation", IntegrationReviewDomain.Observability, id, "Correlation identifiers", tel.CorrelatedRows > 0 ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NoRecentEvidence,
                "Telemetry carries operation/correlation ids.", $"{tel.CorrelatedRows} correlated trace(s) in {tel.WindowHours} h.", "", provenance: e.Telemetry.Source, at: e.Telemetry.CapturedAt, sourceTimestamp: tel.LastActivity, freshness: e.Fresh(tel.LastActivity));
        }
        else
        {
            yield return Missing("obs-traces", IntegrationReviewDomain.Observability, id, "Integration-specific traces", "Processing is visible in telemetry.", e.Telemetry,
                string.IsNullOrWhiteSpace(e.Role) ? "Consumer application not configured, so telemetry cannot be attributed." : "No telemetry source configured.");
            yield return Missing("obs-correlation", IntegrationReviewDomain.Observability, id, "Correlation identifiers", "Telemetry carries operation/correlation ids.", e.Telemetry, "No telemetry source configured.");
        }
    }

    private async Task<IEnumerable<IntegrationCheck>> HealthEndpointChecksAsync(IntegrationDefinition topic, CancellationToken ct)
    {
        var checks = new List<IntegrationCheck>();
        foreach (var (id, title, url) in new[] { ("obs-health", "Consumer health endpoint", topic.HealthUrl), ("obs-worker", "Worker endpoint", topic.WorkerUrl) })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                checks.Add(Check(id, IntegrationReviewDomain.Observability, topic.Id, title, IntegrationCheckStatus.NotConfigured, "A health endpoint shows the consumer's own state.", "Not configured", ""));
                continue;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                checks.Add(Check(id, IntegrationReviewDomain.Observability, topic.Id, title, response.IsSuccessStatusCode ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning,
                    "The endpoint answers with a success status.", $"HTTP {(int)response.StatusCode}", "A healthy endpoint does not prove message processing.",
                    response.IsSuccessStatusCode ? null : "Check the consumer's health.", IntegrationEvidenceSource.HealthEndpoint, freshness: IntegrationEvidenceItemFreshness.Current, sourceTimestamp: DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                checks.Add(Check(id, IntegrationReviewDomain.Observability, topic.Id, title, IntegrationCheckStatus.Unavailable, "The endpoint answers with a success status.",
                    $"Not reachable ({ex.GetType().Name}).", "", "Check the configured URL and network access.", IntegrationEvidenceSource.HealthEndpoint));
            }
        }
        return checks;
    }

    // ── Performance ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lag per partition = last enqueued sequence number − checkpointed sequence number. Both are Event Hubs sequence numbers of the same partition, so they are comparable; partitions without a checkpoint are left out and counted.</summary>
    public static (long? Lag, int Compared, int WithoutCheckpoint) Lag(EventHubRuntimeMetadata hub, CheckpointEvidence checkpoint)
    {
        long total = 0;
        int compared = 0, missing = 0;
        foreach (var partition in hub.Partitions)
        {
            var cp = checkpoint.Partitions.FirstOrDefault(c => c.PartitionId == partition.PartitionId);
            if (partition.IsEmpty) { compared++; continue; }
            if (cp?.SequenceNumber is not { } seq) { missing++; continue; }
            total += Math.Max(0, partition.LastEnqueuedSequenceNumber - seq);
            compared++;
        }
        return (compared == 0 ? null : total, compared, missing);
    }

    private static IEnumerable<IntegrationCheck> PerformanceChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        if (e.Metadata is { IsAvailable: true, Value: { Exists: true } hub } && hub.LastEnqueuedTime is { } newest)
            yield return Check("perf-event-age", IntegrationReviewDomain.Performance, id, "Newest event age", IntegrationCheckStatus.Observed, "",
                $"Newest event enqueued {Ago(newest, e.CapturedAt)} ({newest:u}).", "Measured; no event-age threshold is configured.", provenance: e.Metadata.Source, at: e.Metadata.CapturedAt, sourceTimestamp: newest, freshness: e.Fresh(newest));
        else
            yield return Missing("perf-event-age", IntegrationReviewDomain.Performance, id, "Newest event age", "", e.Metadata is { IsAvailable: true } ? null : e.Metadata,
                e.Metadata is { IsAvailable: true } ? "No event has been enqueued (all partitions empty)." : "No Event Hub metadata.");

        if (e.Metadata is { IsAvailable: true, Value: { Exists: true } h2 } && e.Checkpoint is { IsAvailable: true, Value: { } cp } && Lag(h2, cp) is { Lag: { } lag } measured)
        {
            var limit = e.GroupAssumed ? null : e.Platform?.RuntimeEvidence?.MaxConsumerLagEvents;
            var detail = $"{lag} event(s) behind across {measured.Compared} partition(s){(measured.WithoutCheckpoint > 0 ? $"; {measured.WithoutCheckpoint} partition(s) without checkpoint not counted" : "")}{e.AssumptionNote}.";
            yield return limit is { } max
                ? Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", lag <= max ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, $"At most {max} event(s) behind (IQR threshold).", detail,
                    "Last enqueued sequence number minus checkpointed sequence number per partition.", lag <= max ? null : "Check consumer throughput and health.", IntegrationEvidenceSource.CheckpointStore, e.Checkpoint.CapturedAt, cp.LastUpdated, e.Fresh(cp.LastUpdated))
                : Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", IntegrationCheckStatus.Observed, "", detail,
                    e.GroupAssumed ? "Last enqueued sequence number minus checkpointed sequence number per partition. The consumer group is a configured assumption, so no verdict."
                        : "Last enqueued sequence number minus checkpointed sequence number per partition. No IQR lag threshold is configured, so no verdict.", provenance: IntegrationEvidenceSource.CheckpointStore,
                    at: e.Checkpoint.CapturedAt, sourceTimestamp: cp.LastUpdated, freshness: e.Fresh(cp.LastUpdated));
            if (limit is { } m2 && lag > m2)
                findings.Add(Finding($"consumer-lag|{id}", "consumer-lag", IntegrationReviewDomain.Performance, IntegrationFindingSeverityV2.Medium, "Consumer lag exceeds the IQR threshold",
                    e.Topic.DisplayName, [detail, $"Threshold {m2} event(s)."], "Check consumer throughput and health.", e.Topic.DisplayName));
        }
        else
            yield return Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", IntegrationCheckStatus.NotAssessed, "", "",
                e.Group is null ? "Consumer group unknown, so no checkpoint to compare."
                : "Lag needs both the latest enqueued position (Event Hub metadata) and the consumer checkpoint; it is never shown as 0 without them.");

        if (e.Telemetry is { IsAvailable: true, Value: { DependencyMedianMs: { } median } tel } && tel.DependencyCalls > 0)
            yield return Check("perf-processing", IntegrationReviewDomain.Performance, id, "Event Hubs call duration (median)", IntegrationCheckStatus.Observed, "",
                $"{median:0} ms over {tel.DependencyCalls} call(s) in {tel.WindowHours} h.", "Measured from telemetry; no processing-time threshold is configured.",
                provenance: e.Telemetry.Source, at: e.Telemetry.CapturedAt, sourceTimestamp: tel.LastActivity, freshness: e.Fresh(tel.LastActivity));
        else
            yield return Missing("perf-processing", IntegrationReviewDomain.Performance, id, "Event Hubs call duration (median)", "", e.Telemetry is { IsAvailable: true } ? null : e.Telemetry,
                e.Telemetry is { IsAvailable: true } ? "No Event Hubs dependency calls in the window." : "No telemetry source configured.");
    }

    // ── Data quality ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> DataQualityChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        const string Declared = "Declared by the trusted producer contract; no event was read.";
        yield return Check("dq-runtime-structure", IntegrationReviewDomain.DataQuality, id, "Observed event structure", IntegrationCheckStatus.NotAssessed, "", "", NoSafeEventSource);
        if (!IsDebezium(e))
        {
            yield return Check("dq-envelope", IntegrationReviewDomain.DataQuality, id, "CDC envelope", IntegrationCheckStatus.NotAssessed, "", "", "The producer is not a Debezium CDC source.");
            yield break;
        }
        if (e.Producer is not { Contract: { } producer })
        {
            yield return Check("dq-envelope", IntegrationReviewDomain.DataQuality, id, "CDC envelope (before/after/source/op)", IntegrationCheckStatus.NotAssessed, "Events carry the Debezium envelope.", "",
                "No producer contract is uploaded.", "Upload the producer JSON Schema.");
            yield return TombstoneCheck(e);
            yield break;
        }
        var envelope = DebeziumEnvelope.From(producer);
        var missing = new[] { ("before", envelope.HasBefore), ("after", envelope.HasAfter), ("source", envelope.HasSource), ("op", envelope.HasOp) }.Where(x => !x.Item2).Select(x => x.Item1).ToList();
        yield return Check("dq-envelope", IntegrationReviewDomain.DataQuality, id, "CDC envelope (before/after/source/op)", envelope.IsEnvelope ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning,
            "Events carry the Debezium envelope.", envelope.IsEnvelope ? "before, after, source and op are declared." : $"Not declared: {string.Join(", ", missing)}.", Declared,
            envelope.IsEnvelope ? null : "Update the producer contract to the Debezium envelope the consumer reads.", IntegrationEvidenceSource.ContractArtifact);
        if (!envelope.IsEnvelope)
            findings.Add(Finding($"envelope-incomplete|{id}", "envelope-incomplete", IntegrationReviewDomain.DataQuality, IntegrationFindingSeverityV2.Low, "Producer contract does not declare the full Debezium envelope",
                e.Topic.DisplayName, [$"Missing: {string.Join(", ", missing)} (producer contract {e.Producer.Value.Artifact.FileName})."], "Update the producer contract to the Debezium envelope.", e.Topic.DisplayName));
        if (envelope.HasOp)
        {
            var unknown = envelope.Operations?.Where(o => !DebeziumEnvelope.KnownOperations.Contains(o)).ToList() ?? [];
            yield return envelope.Operations is null
                ? Check("dq-operations", IntegrationReviewDomain.DataQuality, id, "Operation types", IntegrationCheckStatus.Observed, "op is one of c, u, d, r (t, m).", "op is not constrained by an enum.", Declared, provenance: IntegrationEvidenceSource.ContractArtifact)
                : Check("dq-operations", IntegrationReviewDomain.DataQuality, id, "Operation types", unknown.Count == 0 ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, "op is one of c, u, d, r (t, m).",
                    $"Declared: {string.Join(", ", envelope.Operations)}.", Declared, unknown.Count == 0 ? null : $"Unknown operation value(s): {string.Join(", ", unknown)}.", IntegrationEvidenceSource.ContractArtifact);
            var deletes = envelope.Operations?.Contains("d");
            yield return deletes switch
            {
                true when envelope.BeforeIsObject => Check("dq-delete", IntegrationReviewDomain.DataQuality, id, "Delete event structure", IntegrationCheckStatus.Pass, "Delete events carry the before image.",
                    "op \"d\" declared and before is an object.", Declared, provenance: IntegrationEvidenceSource.ContractArtifact),
                true => Check("dq-delete", IntegrationReviewDomain.DataQuality, id, "Delete event structure", IntegrationCheckStatus.Warning, "Delete events carry the before image.",
                    "op \"d\" declared but before is not an object.", Declared, "Declare the before image as an object for delete events.", IntegrationEvidenceSource.ContractArtifact),
                false => Check("dq-delete", IntegrationReviewDomain.DataQuality, id, "Delete event structure", IntegrationCheckStatus.Observed, "", "The contract declares no delete operation.", Declared, provenance: IntegrationEvidenceSource.ContractArtifact),
                _ => Check("dq-delete", IntegrationReviewDomain.DataQuality, id, "Delete event structure", IntegrationCheckStatus.NotAssessed, "", "", "op is not constrained, so delete support is not declared.", provenance: IntegrationEvidenceSource.ContractArtifact),
            };
        }
        yield return Check("dq-source-metadata", IntegrationReviewDomain.DataQuality, id, "Source metadata", envelope.SourceFields.Any(f => f is "table" or "db" or "schema") ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Observed,
            "source identifies database and table.", envelope.SourceFields.Count == 0 ? "No source fields declared." : $"source: {string.Join(", ", envelope.SourceFields.Take(10))}.", Declared, provenance: IntegrationEvidenceSource.ContractArtifact);
        yield return TombstoneCheck(e);
    }

    private static IntegrationCheck TombstoneCheck(TopicEvidence e) =>
        e.Topic.DeleteExpectation == CdcDeleteExpectation.NotSpecified
            ? Check("dq-tombstone", IntegrationReviewDomain.DataQuality, e.Topic.Id, "Tombstone handling", IntegrationCheckStatus.NotAssessed, "", "",
                "No delete/tombstone expectation is configured; tombstones are never judged without one.", "State the consumer's delete/tombstone expectation.")
            : Check("dq-tombstone", IntegrationReviewDomain.DataQuality, e.Topic.Id, "Tombstone handling", IntegrationCheckStatus.NotAssessed, DeleteLabel(e.Topic.DeleteExpectation), "",
                "Tombstones are null-value events that only runtime event evidence shows. " + NoSafeEventSource);

    // ── Rollups ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IntegrationDomainResult DomainResult(IntegrationReviewDomain domain, List<IntegrationCheck> checks, List<IntegrationReviewFinding> findings)
    {
        var own = checks.Where(c => c.Domain == domain).ToList();
        var assessed = own.Count(c => IntegrationReviewLabels.IsAssessed(c.Status));
        var limitation = own.Where(c => !IntegrationReviewLabels.IsAssessed(c.Status)).GroupBy(c => c.Explanation).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return new IntegrationDomainResult
        {
            Domain = domain, ChecksAssessed = assessed, ChecksTotal = own.Count, Findings = findings.Count(f => f.Domain == domain),
            StateLabel = own.Count == 0 || assessed == 0 ? "Not assessed" : assessed == own.Count ? "Assessed" : "Partially assessed",
            KeyLimitation = string.IsNullOrWhiteSpace(limitation) ? null : limitation,
            Observed = own.Where(c => IntegrationReviewLabels.IsAssessed(c.Status)).GroupBy(c => c.Title)
                .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(c => IntegrationReviewLabels.Status(c.Status)).Distinct())}").Take(8).ToList(),
            Missing = own.Where(c => !IntegrationReviewLabels.IsAssessed(c.Status)).GroupBy(c => c.Title)
                .Select(g => g.Select(c => c.Explanation).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } why ? $"{g.Key} — {why}" : g.Key).Take(8).ToList(),
        };
    }

    private static List<IntegrationManualFollowUp> ManualFollowUp(List<IntegrationDefinition> enabled, IntegrationCatalog catalog, List<IntegrationSystemResult> systems, List<IntegrationCheck> checks)
    {
        var items = new List<IntegrationManualFollowUp>();
        void Add(string title, string detail, int count) { if (count > 0) items.Add(new(title, detail, count)); }
        Add("Confirm suggested consumer mappings", "The audited source names a consumer for these topics; confirm each for this environment (Integrations → Confirm).", enabled.Count(i => i.Consumer.MappingState == ConsumerMappingState.Suggested));
        Add("Identify consumers", "No consumer is known for these topics.", enabled.Count(i => i.Consumer.MappingState == ConsumerMappingState.NeedsConfirmation));
        var suggestions = systems.SelectMany(s => s.Topics).Count(t => t.SuggestedConsumerGroup is not null);
        Add("Confirm suggested consumer groups", "Azure lists one non-default consumer group for these hubs; confirm it before saving it.", suggestions);
        int Assumed(IntegrationDefinition i) => EffectiveGroup(i, catalog.Platforms.FirstOrDefault(p => p.Id == i.PlatformId)).Assumed ? 1 : 0;
        Add("Confirm consumer group(s)", "Needed for checkpoint and lag review.", enabled.Count(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.ConsumerGroup) && Assumed(i) == 0) - suggestions);
        Add("Confirm the assumed consumer group", "The platform's expected group (a configured assumption) was used for checkpoints; confirm the group each consumer reads with.", enabled.Sum(Assumed));
        Add("Upload event contracts", "Producer and consumer JSON Schemas enable compatibility review.", enabled.Count(i => i.ContractRelationship is ContractRelationshipState.NotConfigured or ContractRelationshipState.ProducerContractAvailable or ContractRelationshipState.ConsumerContractAvailable));
        Add("Confirm delete/tombstone expectation", "State what the consumer expects for delete events.", checks.Count(c => c.CheckId == "cfg-delete-expectation" && c.Status == IntegrationCheckStatus.NotConfigured));
        Add("Configure the consumer application", "The container app / cloud role lets telemetry be attributed to the consumer.", enabled.Count(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.Consumer.ContainerApp)));
        Add("Approve runtime evidence access", "Grant the BirkNext identity read access, or configure the evidence sources, for the adapters that were not available.",
            checks.Count(c => c.CheckId is "conn-metadata-access" or "obs-telemetry-access" && c.Status is IntegrationCheckStatus.NotAssessed or IntegrationCheckStatus.NotConfigured));
        Add("Define IQR lag/checkpoint thresholds", "Lag and checkpoint age were measured but no threshold exists, so they carry no verdict.",
            checks.Count(c => c.CheckId is "perf-lag" or "rel-checkpoint-freshness" && c.Status == IntegrationCheckStatus.Observed));
        Add("Confirm monitoring dashboard and runbook", "Add the monitoring dashboard and runbook links to the platform.", catalog.Platforms.Count(p => string.IsNullOrWhiteSpace(p.MonitoringUrl) || string.IsNullOrWhiteSpace(p.RunbookUrl)));
        Add("Verify checkpoint and replay behaviour", "Restart/replay, duplicate handling and ordering need a person to verify.", enabled.Count(i => i.Kind == IntegrationKind.EventHub));
        return items;
    }

    private static List<string> Limitations(List<IntegrationDefinition> enabled, IntegrationCatalog catalog, List<IntegrationEvidenceAdapterStatus> adapters, List<IntegrationCheck> checks)
    {
        var limitations = adapters.Where(a => a.State is not (IntegrationEvidenceState.Available or IntegrationEvidenceState.NotSupported))
            .Select(a => $"{a.Adapter}: {IntegrationReviewLabels.EvidenceState(a.State)} — {a.Reason}").Distinct().ToList();
        var groups = enabled.Where(i => i.Kind == IntegrationKind.EventHub).Select(i => EffectiveGroup(i, catalog.Platforms.FirstOrDefault(p => p.Id == i.PlatformId))).ToList();
        if (groups.Any(g => g.Group is null)) limitations.Add("Consumer group not configured: checkpoint/lag review was not possible for those topics.");
        if (groups.FirstOrDefault(g => g.Assumed) is { Group: { } assumed })
            limitations.Add($"Consumer group {assumed} is a configured assumption for {groups.Count(g => g.Assumed)} topic(s): checkpoint and lag values for it are observed without a verdict and do not confirm the mapping.");
        if (checks.Any(c => c.CheckId == "contract-compatibility" && c.Status == IntegrationCheckStatus.NotAssessed)) limitations.Add("Contract compatibility needs both producer and consumer contracts; integrations without both were not assessed.");
        if (enabled.Any(i => i.Kind == IntegrationKind.EventHub)) limitations.Add("Data quality uses the structure declared by producer contracts only; no safe runtime event-structure source exists and events are never consumed.");
        limitations.Add("Replay/duplicate handling has no evidence source and needs manual verification.");
        if (enabled.Any(i => i.Kind != IntegrationKind.EventHub)) limitations.Add("Domain review is implemented for Event Hub integrations only; other kinds were reviewed for configuration.");
        return limitations;
    }
}
