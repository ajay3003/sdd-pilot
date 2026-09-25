using System.Diagnostics;
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
    ILogger<IntegrationReviewEngine> logger)
{
    private const string NoSafeEventSource = "No safe runtime event-structure source is configured; events are never consumed to inspect them.";

    // ── Pre-run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<IntegrationEvidenceAdapterStatus> Adapters(IntegrationPlatform platform) =>
    [
        metadata.Describe(platform), consumerGroups.Describe(platform), checkpoints.Describe(platform), telemetry.Describe(platform),
    ];

    public IntegrationReviewReadiness Readiness(IntegrationCatalog catalog, IntegrationContractSet contracts)
    {
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = Systems(catalog, enabled);
        var eventHub = enabled.Where(i => i.Kind == IntegrationKind.EventHub).ToList();
        var platforms = catalog.Platforms.Where(p => eventHub.Any(i => i.PlatformId == p.Id)).ToList();
        var adapters = platforms.SelectMany(p => Adapters(p).Select(a => a with { Adapter = $"{a.Adapter} · {p.Name}" })).ToList();
        bool Configured(IEnumerable<IntegrationEvidenceAdapterStatus> statuses) => statuses.Any(s => s.State == IntegrationEvidenceState.Available);
        var metadataReady = Configured(platforms.Select(metadata.Describe));
        var checkpointReady = Configured(platforms.Select(checkpoints.Describe));
        var telemetryReady = Configured(platforms.Select(telemetry.Describe));
        var unknownGroups = eventHub.Count(i => string.IsNullOrWhiteSpace(i.ConsumerGroup));
        var unknownRoles = eventHub.Count(i => string.IsNullOrWhiteSpace(i.Consumer.ContainerApp));
        var both = enabled.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null && contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null);
        var oneSided = enabled.Count(i => (contracts.Of(i.Id, IntegrationContractRole.Producer) is not null) ^ (contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null));
        var producerContracts = eventHub.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null);
        var thresholds = platforms.Any(p => p.RuntimeEvidence is { MaxConsumerLagEvents: not null } or { MaxCheckpointAgeMinutes: not null });
        var provider = catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.MonitoringProvider));

        IntegrationDomainReadinessRow Row(IntegrationReviewDomain domain, IntegrationDomainReadiness readiness, string explanation) => new(domain, readiness, explanation);
        var domains = new List<IntegrationDomainReadinessRow>
        {
            enabled.Count == 0 ? Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.NotAssessable, "No enabled integration is configured.")
                : Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, $"{enabled.Count} enabled integration{(enabled.Count == 1 ? "" : "s")} will be reviewed against the configured expectation."),
            !catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.NamespaceFqdn)) ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, "No namespace FQDN is configured to probe.")
                : metadataReady ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Ready, "Namespace probe and Event Hub metadata (existence, partitions).")
                : Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Limited, "Namespace reachability can be probed (DNS, TCP, TLS). Event Hub existence needs Event Hub metadata access, which is not configured."),
            both > 0 ? Row(IntegrationReviewDomain.Contract, oneSided > 0 || both < enabled.Count ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.Ready,
                    $"{both} integration(s) have producer and consumer contracts{(both < enabled.Count ? $"; {enabled.Count - both} lack one or both" : "")}.")
                : oneSided > 0 ? Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Limited, $"{oneSided} integration(s) have one contract only; compatibility needs both.")
                : Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, "No producer/consumer contract is uploaded."),
            metadataReady ? Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Available, "Producer activity from Event Hub metadata" + (checkpointReady || telemetryReady ? "; consumer activity from checkpoints/telemetry." : "; consumer activity needs checkpoints or telemetry."))
                : Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.NotAssessable, "Event Hub metadata access is not configured."),
            !checkpointReady && !telemetryReady ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.NotAssessable, "No checkpoint or telemetry evidence source is configured.")
                : unknownGroups > 0 || !checkpointReady ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Limited, !checkpointReady ? "No checkpoint evidence source configured; telemetry indicators only." : $"Consumer group unknown for {unknownGroups} integration(s).")
                : Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Available, "Checkpoint and telemetry evidence."),
            !telemetryReady ? Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.NotAssessable, "No telemetry source configured.")
                : Row(IntegrationReviewDomain.ErrorHandling, unknownRoles > 0 ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.Available,
                    unknownRoles > 0 ? $"Telemetry configured; {unknownRoles} integration(s) have no consumer application to attribute telemetry to." : "Consumer telemetry (exceptions, traces)."),
            Row(IntegrationReviewDomain.Security, enabled.Count == 0 ? IntegrationDomainReadiness.NotAssessable : telemetryReady ? IntegrationDomainReadiness.Available : IntegrationDomainReadiness.Limited,
                telemetryReady ? "Configured authentication, TLS and runtime authorization indicators." : "Configured authentication and TLS; runtime authorization needs telemetry."),
            !provider ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "No monitoring provider is configured.")
                : telemetryReady ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Available, "Monitoring configuration and telemetry access.")
                : Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "Monitoring provider configured; runtime telemetry is not configured."),
            !metadataReady && !checkpointReady && !telemetryReady ? Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable, "No measured timing, lag or backlog evidence source. Configuration values are never performance evidence.")
                : Row(IntegrationReviewDomain.Performance, thresholds ? IntegrationDomainReadiness.Available : IntegrationDomainReadiness.Limited,
                    thresholds ? "Measured event age, lag and processing time; judged against the configured IQR thresholds." : "Measured values are reported as Observed; no IQR lag/checkpoint threshold is configured."),
            producerContracts > 0 ? Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.Limited, $"Envelope structure declared by {producerContracts} producer contract(s). {NoSafeEventSource}")
                : Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, $"No producer contract. {NoSafeEventSource}"),
        };

        var canRun = enabled.Count > 0;
        var limited = domains.Any(d => d.Readiness is IntegrationDomainReadiness.Limited or IntegrationDomainReadiness.NotAssessable);
        var reasons = new List<string>();
        foreach (var system in systems.Where(s => s.Topics > 1 || s.Kind == IntegrationKind.EventHub))
        {
            if (system.ConsumersConfirmed > 0) reasons.Add($"{system.ConsumersConfirmed} confirmed consumer mapping{(system.ConsumersConfirmed == 1 ? "" : "s")} in {system.SystemName}.");
            if (system.ConsumersSuggested > 0) reasons.Add($"{system.ConsumersSuggested} consumer mapping{(system.ConsumersSuggested == 1 ? "" : "s")} suggested by the audited source and not confirmed for this environment.");
            if (system.ConsumersNeedingConfirmation > 0) reasons.Add($"{system.ConsumersNeedingConfirmation} consumer mapping{(system.ConsumersNeedingConfirmation == 1 ? "" : "s")} need confirmation.");
            if (system.ConsumerGroupsUnknown > 0) reasons.Add($"Consumer group not configured for {system.ConsumerGroupsUnknown} topic{(system.ConsumerGroupsUnknown == 1 ? "" : "s")} — only checkpoint/lag checks are affected.");
            if (!system.DomainReviewSupported) reasons.Add($"{system.SystemName}: domain review for {IntegrationConfigurationRules.KindLabel(system.Kind)} is not implemented yet; configuration is still reviewed.");
        }
        foreach (var adapter in adapters.Where(a => a.State != IntegrationEvidenceState.Available).GroupBy(a => a.Reason).Select(g => g.First()))
            reasons.Add($"{adapter.Adapter}: {adapter.Reason}");
        return new IntegrationReviewReadiness
        {
            EnvironmentId = catalog.EnvironmentId, Systems = systems, ConfiguredIntegrations = catalog.Integrations.Count, EnabledIntegrations = enabled.Count,
            Domains = domains, CanRun = canRun, EvidenceAdapters = adapters,
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
                    ConsumerGroupsUnknown = kind == IntegrationKind.EventHub ? g.Count(i => string.IsNullOrWhiteSpace(i.ConsumerGroup)) : 0,
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
        IReadOnlyList<IntegrationContractArtifact> PreviousContracts)
    {
        public string? Hub => Topic.EndpointOrTopic;
        public string? Role => Topic.Consumer.ContainerApp;
        public IntegrationEvidenceItemFreshness Fresh(DateTimeOffset? at) => IntegrationReviewLabels.FreshnessOf(at, CapturedAt, WindowHours);
        public bool InWindow(DateTimeOffset? at) => at is { } t && CapturedAt - t <= TimeSpan.FromHours(WindowHours);
    }

    public async Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, CancellationToken ct) =>
        await RunAsync(catalog, request, IntegrationContractSet.Empty, [], ct);

    public async Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, IntegrationContractSet contracts,
        IReadOnlyList<IntegrationContractArtifact> previousContracts, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = new List<IntegrationSystemResult>();
        var findings = new List<IntegrationReviewFinding>();
        var adapterStatuses = new List<IntegrationEvidenceAdapterStatus>();
        var windows = new List<int>();

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
                    if (string.IsNullOrWhiteSpace(topic.ConsumerGroup)) groupList = await consumerGroups.ListAsync(platform, name, ct);
                    else checkpoint = await checkpoints.GetAsync(platform, name, topic.ConsumerGroup!, ct);
                    if (topic.Consumer.ContainerApp is { Length: > 0 } role)
                    {
                        if (!telemetryByRole.TryGetValue(role, out roleTelemetry)) telemetryByRole[role] = roleTelemetry = await telemetry.GetConsumerAsync(platform, role, window, ct);
                    }
                }
                evidence.Add(new TopicEvidence(topic, platform, window, DateTimeOffset.UtcNow, probe, hub, groupList, checkpoint, roleTelemetry,
                    contracts.Of(topic.Id, IntegrationContractRole.Producer), contracts.Of(topic.Id, IntegrationContractRole.Consumer), previousContracts));
            }

            if (platform is not null && supported)
            {
                var statuses = new[]
                {
                    Rollup(metadata.Describe(platform), evidence.Select(e => e.Metadata)),
                    Rollup(consumerGroups.Describe(platform), evidence.Select(e => e.Groups)),
                    Rollup(checkpoints.Describe(platform), evidence.Select(e => e.Checkpoint)),
                    Rollup(telemetry.Describe(platform), evidence.Select(e => e.Telemetry)),
                };
                adapterStatuses.AddRange(statuses.Select(s => s with { Adapter = $"{s.Adapter} · {platform.Name}" }));
                foreach (var s in statuses)
                    logger.LogInformation("IQR evidence adapter {Adapter} for platform {PlatformId}: {State} ({Reason}).", s.Adapter, platform.Id, s.State, s.Reason);
                platformChecks.AddRange(PlatformChecks(platform, probe, statuses, window));
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
                checks.AddRange(ConfigurationChecks(e));
                if (supported)
                {
                    checks.AddRange(ConnectivityChecks(e, findings));
                    checks.AddRange(ContractChecks(e, findings));
                    checks.AddRange(MessageFlowChecks(e));
                    checks.AddRange(ReliabilityChecks(e, findings));
                    checks.AddRange(ErrorHandlingChecks(e, findings));
                    checks.AddRange(SecurityChecks(e, findings));
                    checks.AddRange(ObservabilityChecks(e));
                    checks.AddRange(await HealthEndpointChecksAsync(e.Topic, ct));
                    checks.AddRange(PerformanceChecks(e, findings));
                    checks.AddRange(DataQualityChecks(e, findings));
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

        var grouped = findings.GroupBy(f => f.Key).Select(g => g.First() with { AffectedIntegrations = g.SelectMany(f => f.AffectedIntegrations).Distinct().ToList() }).ToList();
        var allChecks = systems.SelectMany(s => s.PlatformChecks.Concat(s.Topics.SelectMany(t => t.Checks))).ToList();
        var domains = Enum.GetValues<IntegrationReviewDomain>().Select(domain => DomainResult(domain, allChecks, grouped)).ToList();
        var runtimeAssessed = allChecks.Where(c => c.Provenance is not (IntegrationEvidenceSource.Configuration or IntegrationEvidenceSource.ContractArtifact) && IntegrationReviewLabels.IsAssessed(c.Status)).ToList();
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
                    IntegrationEvidenceState.Available => IntegrationCheckStatus.Pass,
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

    // ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ConfigurationChecks(TopicEvidence e)
    {
        var topic = e.Topic;
        var id = topic.Id;
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
            yield return Check("cfg-consumer-group", IntegrationReviewDomain.Configuration, id, "Consumer group",
                string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
                "The consumer group the consumer reads with is known.",
                topic.ConsumerGroup ?? (suggestion is null ? "Unknown / not configured" : $"Unknown — Azure lists one non-default group: {suggestion} (suggestion, not saved)"),
                string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? "Only checkpoint/lag review depends on it; $Default is never assumed." : "",
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
        yield return Check("conn-hub", IntegrationReviewDomain.Connectivity, id, "Configured Event Hub exists", hub.Exists ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail,
            "The configured hub exists in the namespace.", hub.Exists ? "Found by Event Hub metadata." : "Event Hub metadata reported the hub as not found.", "",
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
            yield return Check("conn-partitions", IntegrationReviewDomain.Connectivity, id, "Partition count", configured == runtime ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning,
                $"{configured} partition(s) as configured.", $"{runtime} partition(s) at runtime.", configured == runtime ? "" : "Configuration and runtime disagree; one of them is out of date.",
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

    private static IEnumerable<IntegrationCheck> MessageFlowChecks(TopicEvidence e)
    {
        var id = e.Topic.Id;
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
                    string.IsNullOrWhiteSpace(e.Topic.ConsumerGroup) ? "Consumer group unknown (no checkpoint lookup)." : e.Checkpoint is { } c1 ? $"Checkpoints: {c1.Reason}" : null,
                    string.IsNullOrWhiteSpace(e.Role) ? "Consumer application not configured (no telemetry attribution)." : e.Telemetry is { } t1 ? $"Telemetry: {t1.Reason}" : null,
                }.OfType<string>()));
    }

    // ── Reliability ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IntegrationCheck> ReliabilityChecks(TopicEvidence e, List<IntegrationReviewFinding> findings)
    {
        var id = e.Topic.Id;
        if (string.IsNullOrWhiteSpace(e.Topic.ConsumerGroup))
        {
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints", IntegrationCheckStatus.NotAssessed, "The consumer records its position.", "",
                "Consumer group is not configured.", "Confirm the consumer group to enable checkpoint/lag review.");
            yield return Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", IntegrationCheckStatus.NotAssessed, "", "", "Consumer group is not configured.");
        }
        else if (e.Checkpoint is { IsAvailable: true, Value: { } cp })
        {
            var hubPartitions = e.Metadata is { IsAvailable: true, Value: { Exists: true } hub } ? hub.Partitions.Count : (int?)null;
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, id, "Consumer checkpoints",
                hubPartitions is { } total && cp.Partitions.Count < total ? IntegrationCheckStatus.Warning : IntegrationCheckStatus.Pass, "The consumer records its position.",
                $"Checkpoints for {cp.Partitions.Count}{(hubPartitions is { } t ? $" of {t}" : "")} partition(s), consumer group {cp.ConsumerGroup}; {cp.OwnershipRecords} ownership record(s).",
                "Checkpoint present is not lag acceptable; see Performance.", hubPartitions is { } t2 && cp.Partitions.Count < t2 ? "Check why some partitions have no checkpoint." : null,
                e.Checkpoint.Source, e.Checkpoint.CapturedAt, cp.LastUpdated, e.Fresh(cp.LastUpdated));
            var threshold = e.Platform?.RuntimeEvidence?.MaxCheckpointAgeMinutes;
            if (cp.LastUpdated is { } updated)
            {
                var ageMinutes = (e.CapturedAt - updated).TotalMinutes;
                yield return threshold is { } limit
                    ? Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", ageMinutes <= limit ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning,
                        $"Last checkpoint within {limit} min (IQR threshold).", $"Last checkpoint {updated:u} ({Ago(updated, e.CapturedAt)}).", "",
                        ageMinutes <= limit ? null : "Check whether the consumer is running and checkpointing.", e.Checkpoint.Source, e.Checkpoint.CapturedAt, updated, e.Fresh(updated))
                    : Check("rel-checkpoint-freshness", IntegrationReviewDomain.Reliability, id, "Checkpoint freshness", IntegrationCheckStatus.Observed, "",
                        $"Last checkpoint {updated:u} ({Ago(updated, e.CapturedAt)}).", "No IQR checkpoint-age threshold is configured, so the age is reported without a verdict.",
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
            var limit = e.Platform?.RuntimeEvidence?.MaxConsumerLagEvents;
            var detail = $"{lag} event(s) behind across {measured.Compared} partition(s){(measured.WithoutCheckpoint > 0 ? $"; {measured.WithoutCheckpoint} partition(s) without checkpoint not counted" : "")}.";
            yield return limit is { } max
                ? Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", lag <= max ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, $"At most {max} event(s) behind (IQR threshold).", detail,
                    "Last enqueued sequence number minus checkpointed sequence number per partition.", lag <= max ? null : "Check consumer throughput and health.", IntegrationEvidenceSource.CheckpointStore, e.Checkpoint.CapturedAt, cp.LastUpdated, e.Fresh(cp.LastUpdated))
                : Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", IntegrationCheckStatus.Observed, "", detail,
                    "Last enqueued sequence number minus checkpointed sequence number per partition. No IQR lag threshold is configured, so no verdict.", provenance: IntegrationEvidenceSource.CheckpointStore,
                    at: e.Checkpoint.CapturedAt, sourceTimestamp: cp.LastUpdated, freshness: e.Fresh(cp.LastUpdated));
            if (limit is { } m2 && lag > m2)
                findings.Add(Finding($"consumer-lag|{id}", "consumer-lag", IntegrationReviewDomain.Performance, IntegrationFindingSeverityV2.Medium, "Consumer lag exceeds the IQR threshold",
                    e.Topic.DisplayName, [detail, $"Threshold {m2} event(s)."], "Check consumer throughput and health.", e.Topic.DisplayName));
        }
        else
            yield return Check("perf-lag", IntegrationReviewDomain.Performance, id, "Consumer lag", IntegrationCheckStatus.NotAssessed, "", "",
                string.IsNullOrWhiteSpace(e.Topic.ConsumerGroup) ? "Consumer group unknown, so no checkpoint to compare."
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
        Add("Confirm consumer group(s)", "Needed for checkpoint and lag review.", enabled.Count(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.ConsumerGroup)) - suggestions);
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
        if (enabled.Any(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.ConsumerGroup))) limitations.Add("Consumer group not configured: checkpoint/lag review was not possible for those topics.");
        if (checks.Any(c => c.CheckId == "contract-compatibility" && c.Status == IntegrationCheckStatus.NotAssessed)) limitations.Add("Contract compatibility needs both producer and consumer contracts; integrations without both were not assessed.");
        if (enabled.Any(i => i.Kind == IntegrationKind.EventHub)) limitations.Add("Data quality uses the structure declared by producer contracts only; no safe runtime event-structure source exists and events are never consumed.");
        limitations.Add("Replay/duplicate handling has no evidence source and needs manual verification.");
        if (enabled.Any(i => i.Kind != IntegrationKind.EventHub)) limitations.Add("Domain review is implemented for Event Hub integrations only; other kinds were reviewed for configuration.");
        return limitations;
    }
}
