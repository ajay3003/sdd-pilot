using System.Diagnostics;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Integration Quality Review over the configured catalog (Target Environment → Integrations). Read-only by construction:
/// configuration checks read the catalog; connectivity probes DNS/TCP/TLS of the namespace; runtime domains read only what an
/// evidence adapter supplies and report "Not assessed" with the reason otherwise. Configuration checks never stand in for
/// runtime evidence, and absent evidence is never reported as zero, idle or healthy.
/// </summary>
public sealed class IntegrationReviewEngine(
    IIntegrationNamespaceProbe namespaceProbe,
    IIntegrationRuntimeEvidenceSource runtimeEvidence,
    IIntegrationContractSource contracts,
    HttpClient http,
    ILogger<IntegrationReviewEngine> logger)
{
    public const string NoRuntimeAdapterReason = "No runtime evidence adapter is available in this build (no Event Hub metadata, Application Insights or checkpoint access).";

    // ── Pre-run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public IntegrationReviewReadiness Readiness(IntegrationCatalog catalog)
    {
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = Systems(catalog, enabled);
        var eventHub = enabled.Where(i => i.Kind == IntegrationKind.EventHub).ToList();
        var caps = runtimeEvidence.Capabilities;
        var platformsWithFqdn = catalog.Platforms.Count(p => !string.IsNullOrWhiteSpace(p.NamespaceFqdn));
        var unknownGroups = eventHub.Count(i => string.IsNullOrWhiteSpace(i.ConsumerGroup));
        var contractsConfigured = enabled.Count(i => i.ContractRelationship != ContractRelationshipState.NotConfigured);
        var monitoringUrl = catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.MonitoringUrl)) || enabled.Any(i => !string.IsNullOrWhiteSpace(i.MonitoringUrl));
        var provider = catalog.Platforms.Any(p => !string.IsNullOrWhiteSpace(p.MonitoringProvider));
        var runtime = caps == IntegrationEvidenceCapabilities.None ? NoRuntimeAdapterReason : caps.Description;

        IntegrationDomainReadinessRow Row(IntegrationReviewDomain domain, IntegrationDomainReadiness readiness, string explanation) => new(domain, readiness, explanation);
        var domains = new List<IntegrationDomainReadinessRow>
        {
            enabled.Count == 0 ? Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.NotAssessable, "No enabled integration is configured.")
                : Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, $"{enabled.Count} enabled integration{(enabled.Count == 1 ? "" : "s")} will be reviewed against the configured expectation."),
            platformsWithFqdn == 0 ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, "No namespace FQDN is configured to probe.")
                : caps.HubMetadata ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Available, "Namespace reachability and Event Hub existence can be checked.")
                : Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Limited, "Namespace reachability can be probed (DNS, TCP, TLS). Event Hub existence needs Azure metadata access, which is not available."),
            contractsConfigured == 0 ? Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, "No producer/consumer contract relationship is configured.")
                : contracts.CanRetrieve ? Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Available, $"{contractsConfigured} integration(s) have contract references that can be retrieved.")
                : Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Limited, $"{contractsConfigured} integration(s) have contract references; no contract retrieval adapter exists, so compatibility is not assessed."),
            caps.MessageActivity ? Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Available, "Producer/consumer activity evidence is available.")
                : Row(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.NotAssessable, runtime),
            !caps.ConsumerCheckpoints ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.NotAssessable, unknownGroups > 0 ? $"{runtime} Consumer group is not configured for {unknownGroups} integration(s)." : runtime)
                : unknownGroups > 0 ? Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Limited, $"Checkpoint review is not assessable for {unknownGroups} integration(s): consumer group is not configured.")
                : Row(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Available, "Checkpoint/lag evidence is available."),
            caps.ConsumerErrors ? Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.Available, "Consumer error evidence is available.")
                : Row(IntegrationReviewDomain.ErrorHandling, IntegrationDomainReadiness.NotAssessable, runtime),
            Row(IntegrationReviewDomain.Security, enabled.Count == 0 ? IntegrationDomainReadiness.NotAssessable : IntegrationDomainReadiness.Limited,
                "Configured producer/consumer authentication can be reviewed. Runtime authorization is not assessable without runtime evidence."),
            !provider ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "No monitoring provider is configured.")
                : monitoringUrl ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "Monitoring is configured. Integration-specific telemetry is not assessable without a telemetry adapter.")
                : Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "Monitoring provider configured; no dashboard link. Integration telemetry is not assessable without a telemetry adapter."),
            caps.Timing ? Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.Available, "Processing timing evidence is available.")
                : Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable, "No measured timing, lag or backlog evidence is available. Configuration values are never used as performance evidence."),
            caps.PayloadStructure ? Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.Available, "Structural event evidence is available.")
                : Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, "No safe structural event evidence is available (events are never consumed to inspect them)."),
        };

        var canRun = enabled.Count > 0;
        var limited = domains.Any(d => d.Readiness is IntegrationDomainReadiness.Limited or IntegrationDomainReadiness.NotAssessable);
        var reasons = new List<string>();
        foreach (var system in systems.Where(s => s.Topics > 1 || s.Kind == IntegrationKind.EventHub))
        {
            if (system.ConsumersConfirmed > 0) reasons.Add($"{system.ConsumersConfirmed} confirmed consumer mapping{(system.ConsumersConfirmed == 1 ? "" : "s")} in {system.SystemName}.");
            if (system.ConsumersSuggested > 0) reasons.Add($"{system.ConsumersSuggested} consumer mapping{(system.ConsumersSuggested == 1 ? "" : "s")} suggested by the audited source and not confirmed for this environment.");
            if (system.ConsumersNeedingConfirmation > 0) reasons.Add($"{system.ConsumersNeedingConfirmation} consumer mapping{(system.ConsumersNeedingConfirmation == 1 ? "" : "s")} need confirmation.");
            if (system.ConsumerGroupsUnknown > 0) reasons.Add($"Consumer group not configured for {system.ConsumerGroupsUnknown} topic{(system.ConsumerGroupsUnknown == 1 ? "" : "s")} — only checkpoint/consumer-specific checks are affected.");
            if (system.ContractsConfigured == 0) reasons.Add("No contract relationship is configured — only contract assessment is affected.");
            if (!system.DomainReviewSupported) reasons.Add($"{system.SystemName}: domain review for {IntegrationConfigurationRules.KindLabel(system.Kind)} is not implemented yet; configuration is still reviewed.");
        }
        return new IntegrationReviewReadiness
        {
            EnvironmentId = catalog.EnvironmentId, Systems = systems, ConfiguredIntegrations = catalog.Integrations.Count, EnabledIntegrations = enabled.Count,
            Domains = domains, CanRun = canRun,
            Headline = !canRun ? "Cannot run" : limited ? "Can run with limitations" : "Ready",
            Reasons = canRun ? reasons : ["Enable at least one configured integration in Target Environment → Integrations."],
        };
    }

    /// <summary>Topics grouped into integration systems: 16 CDC topics are one system, not 16 external systems.</summary>
    public static List<IntegrationSystemScope> Systems(IntegrationCatalog catalog, IReadOnlyList<IntegrationDefinition> enabled) =>
        enabled.GroupBy(i => SystemKey(i))
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

    public async Task<IntegrationReviewResult> RunAsync(IntegrationCatalog catalog, IntegrationReviewRunRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var enabled = catalog.Integrations.Where(i => i.Enabled).ToList();
        var systems = new List<IntegrationSystemResult>();
        var findings = new List<IntegrationReviewFinding>();
        var sources = new HashSet<IntegrationEvidenceSource> { IntegrationEvidenceSource.Configuration };
        var runtimeCaps = runtimeEvidence.Capabilities;

        foreach (var group in enabled.GroupBy(SystemKey))
        {
            var topics = group.ToList();
            var first = topics[0];
            var platform = catalog.Platforms.FirstOrDefault(p => p.Id == first.PlatformId);
            var supported = first.Kind == IntegrationKind.EventHub;
            var platformChecks = new List<IntegrationCheck>();
            var topicResults = new List<IntegrationTopicResult>();

            NamespaceProbeResult? probe = null;
            if (supported && platform is { NamespaceFqdn: { Length: > 0 } fqdn })
            {
                probe = await namespaceProbe.ProbeAsync(fqdn, ct);
                sources.Add(IntegrationEvidenceSource.NetworkProbe);
            }
            var evidence = supported && platform is not null ? await runtimeEvidence.GetAsync(platform, topics, ct) : new Dictionary<string, IntegrationRuntimeEvidence>();
            if (evidence.Count > 0) foreach (var e in evidence.Values) sources.Add(e.Source);

            if (platform is not null) platformChecks.AddRange(PlatformChecks(platform, probe, supported));
            if (probe is { Reachable: false })
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"namespace-unreachable|{platform!.Id}", RuleId = "namespace-unreachable", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Messaging namespace is not reachable from the review", Subject = platform.NamespaceFqdn ?? platform.Name,
                    Evidence = [probe.Detail], Recommendation = "Check DNS, private endpoint and network access to the namespace from where BirkNext runs; the review itself cannot distinguish an outage from a network restriction.",
                    AffectedIntegrations = topics.Select(t => t.DisplayName).ToList(),
                });

            foreach (var topic in topics)
            {
                evidence.TryGetValue(topic.Id, out var runtime);
                var checks = new List<IntegrationCheck>();
                checks.AddRange(ConfigurationChecks(topic, platform));
                if (supported)
                {
                    checks.AddRange(ConnectivityChecks(topic, runtime, runtimeCaps));
                    checks.AddRange(await ContractChecksAsync(topic, findings, ct));
                    checks.AddRange(MessageFlowChecks(topic, runtime, runtimeCaps));
                    checks.AddRange(ReliabilityChecks(topic, runtime, runtimeCaps));
                    checks.AddRange(ErrorHandlingChecks(topic, runtime, runtimeCaps, findings));
                    checks.AddRange(SecurityChecks(topic, runtime, runtimeCaps, findings));
                    checks.AddRange(PerformanceChecks(topic, runtime, runtimeCaps));
                    checks.AddRange(DataQualityChecks(topic, runtime, runtimeCaps));
                    checks.AddRange(await OperationsChecksAsync(topic, ct));
                    if (runtime?.HubExists == false)
                        findings.Add(new IntegrationReviewFinding
                        {
                            Key = $"hub-missing|{topic.EndpointOrTopic}", RuleId = "hub-missing", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                            Title = "Configured Event Hub does not exist", Subject = topic.EndpointOrTopic ?? topic.DisplayName,
                            Evidence = [$"{IntegrationReviewLabels.Source(runtime.Source)} reported the hub as not found ({runtime.CapturedAt:u})."],
                            Recommendation = "Correct the configured Event Hub name or create the hub through the platform's normal provisioning process.",
                            AffectedIntegrations = [topic.DisplayName],
                        });
                }
                else
                {
                    foreach (var domain in Enum.GetValues<IntegrationReviewDomain>().Where(d => d != IntegrationReviewDomain.Configuration))
                        checks.Add(Check($"{Code(domain)}-unsupported", domain, IntegrationCheckScope.Topic, topic.Id, $"{IntegrationReviewLabels.Domain(domain)} review", IntegrationCheckStatus.NotAssessed,
                            "", "", $"Domain review for {IntegrationConfigurationRules.KindLabel(topic.Kind)} integrations is not implemented in this version."));
                }
                var (state, _, _) = IntegrationConfigurationRules.Evaluate(topic, platform);
                topicResults.Add(new IntegrationTopicResult
                {
                    IntegrationId = topic.Id, DisplayName = topic.DisplayName, Topic = topic.EndpointOrTopic, Consumer = topic.Consumer.DisplayName,
                    MappingState = topic.Consumer.MappingState, ConfigurationState = state, Checks = checks,
                });
            }
            systems.Add(new IntegrationSystemResult { SystemName = first.SystemName ?? first.DisplayName, PlatformId = platform?.Id, Kind = first.Kind, DomainReviewSupported = supported, PlatformChecks = platformChecks, Topics = topicResults });
        }

        var grouped = findings.GroupBy(f => f.Key).Select(g => g.First() with { AffectedIntegrations = g.SelectMany(f => f.AffectedIntegrations).Distinct().ToList() }).ToList();
        var allChecks = systems.SelectMany(s => s.PlatformChecks.Concat(s.Topics.SelectMany(t => t.Checks))).ToList();
        var domains = Enum.GetValues<IntegrationReviewDomain>().Select(domain => DomainResult(domain, allChecks, grouped)).ToList();
        var manual = ManualFollowUp(enabled, catalog);
        var limitations = Limitations(enabled, catalog, runtimeCaps);
        var assessedRuntime = allChecks.Any(c => c.Provenance != IntegrationEvidenceSource.Configuration && IntegrationReviewLabels.IsAssessed(c.Status));
        var outcome = allChecks.Count == 0 || !allChecks.Any(c => IntegrationReviewLabels.IsAssessed(c.Status)) ? IntegrationReviewOutcome.NothingAssessed
            : grouped.Count > 0 ? IntegrationReviewOutcome.ManualReviewRequired
            : domains.Any(d => d.ChecksAssessed < d.ChecksTotal) ? IntegrationReviewOutcome.CompletedWithLimitations
            : IntegrationReviewOutcome.Completed;
        var freshness = !assessedRuntime ? IntegrationEvidenceFreshness.ConfigurationOnly : IntegrationEvidenceFreshness.Current;
        var result = new IntegrationReviewResult
        {
            RunId = Guid.NewGuid(), EnvironmentId = catalog.EnvironmentId, EnvironmentName = request.EnvironmentName, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
            Outcome = outcome, ConfigurationSnapshot = catalog with { Notices = [] }, Systems = systems, Domains = domains, Findings = grouped,
            ManualFollowUp = manual, Limitations = limitations, Freshness = freshness, EvidenceSources = sources.OrderBy(s => s).ToList(),
        };
        logger.LogInformation(
            "Integration Quality Review for {EnvironmentId}: {Integrations} integration(s) in {Systems} system(s), sources {Sources}, {Assessed} of {Checks} check(s) assessed, {Findings} finding(s), {DurationMs:0} ms.",
            catalog.EnvironmentId, enabled.Count, systems.Count, string.Join(",", result.EvidenceSources), allChecks.Count(c => IntegrationReviewLabels.IsAssessed(c.Status)), allChecks.Count, grouped.Count, stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    // ── Checks per domain ───────────────────────────────────────────────────────────────────────────────────────────

    private static IntegrationCheck Check(string id, IntegrationReviewDomain domain, IntegrationCheckScope scope, string subject, string title, IntegrationCheckStatus status,
        string expectation, string evidence, string explanation, string? recommendation = null, IntegrationEvidenceSource provenance = IntegrationEvidenceSource.Configuration, DateTimeOffset? at = null) =>
        new()
        {
            CheckId = id, Domain = domain, Scope = scope, SubjectId = subject, Title = title, Status = status, Expectation = expectation, Evidence = evidence,
            Explanation = explanation, Recommendation = recommendation, Provenance = provenance, CapturedAt = at ?? DateTimeOffset.UtcNow,
        };

    private static string Code(IntegrationReviewDomain domain) => domain.ToString().ToLowerInvariant();

    private static IEnumerable<IntegrationCheck> PlatformChecks(IntegrationPlatform platform, NamespaceProbeResult? probe, bool supported)
    {
        var id = platform.Id;
        yield return Check("cfg-namespace", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Platform, id, "Namespace configured",
            string.IsNullOrWhiteSpace(platform.Namespace) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The platform names its messaging namespace.", platform.Namespace ?? "Not configured", "Configuration only — this says nothing about reachability.");
        if (supported)
        {
            yield return probe is null
                ? Check("conn-namespace", IntegrationReviewDomain.Connectivity, IntegrationCheckScope.Platform, id, "Namespace endpoint reachable", IntegrationCheckStatus.NotAssessed,
                    "DNS resolves and TCP/TLS on 443 succeeds.", "", "No namespace FQDN is configured.", "Configure the namespace FQDN.")
                : Check("conn-namespace", IntegrationReviewDomain.Connectivity, IntegrationCheckScope.Platform, id, "Namespace endpoint reachable",
                    probe.Reachable && probe.TlsEstablished ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail,
                    "DNS resolves and TCP/TLS on 443 succeeds from where BirkNext runs.", $"{probe.Detail} ({probe.ElapsedMs:0} ms)",
                    probe.Reachable ? "Reachable does not verify that messages flow, that hubs exist, or that any party is authorized." : "The namespace could not be reached from the review host.",
                    probe.Reachable ? null : "Check DNS, private endpoint and network access.", IntegrationEvidenceSource.NetworkProbe, probe.CapturedAt);
        }
        yield return Check("sec-producer-auth", IntegrationReviewDomain.Security, IntegrationCheckScope.Platform, id, "Producer authentication mechanism configured",
            platform.ProducerAuthentication == IntegrationAuthMechanism.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The producer's authentication mechanism is named.", IntegrationConfigurationRules.AuthLabel(platform.ProducerAuthentication),
            "Configured, not validated: no credential is read and no send is attempted.");
        yield return Check("obs-provider", IntegrationReviewDomain.Observability, IntegrationCheckScope.Platform, id, "Monitoring provider configured",
            string.IsNullOrWhiteSpace(platform.MonitoringProvider) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A monitoring provider is named for the platform.", platform.MonitoringProvider ?? "Not configured", "Configured is not healthy: telemetry itself is not read.");
        yield return Check("obs-dashboard", IntegrationReviewDomain.Observability, IntegrationCheckScope.Platform, id, "Monitoring dashboard link",
            string.IsNullOrWhiteSpace(platform.MonitoringUrl) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A concrete dashboard URL lets a reader open integration monitoring.", platform.MonitoringUrl ?? "Not configured", "", string.IsNullOrWhiteSpace(platform.MonitoringUrl) ? "Add the Application Insights dashboard/workbook URL." : null);
        yield return Check("obs-runbook", IntegrationReviewDomain.Observability, IntegrationCheckScope.Platform, id, "Runbook link",
            string.IsNullOrWhiteSpace(platform.RunbookUrl) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "A runbook explains how to act on integration failures.", platform.RunbookUrl ?? "Not configured", "");
        yield return Check("obs-telemetry", IntegrationReviewDomain.Observability, IntegrationCheckScope.Platform, id, "Integration-specific telemetry", IntegrationCheckStatus.NotAssessed,
            "Failures, lag and correlation are visible in monitoring.", "", "No telemetry adapter reads monitoring data in this build.");
    }

    private static IEnumerable<IntegrationCheck> ConfigurationChecks(IntegrationDefinition topic, IntegrationPlatform? platform)
    {
        var id = topic.Id;
        yield return Check("cfg-topic", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, id, "Topic configured",
            string.IsNullOrWhiteSpace(topic.EndpointOrTopic) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The integration names its Event Hub / endpoint.", topic.EndpointOrTopic ?? "Not configured", "Configuration only — existence is a Connectivity question.");
        yield return Check("cfg-producer", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, id, "Producer configured",
            string.IsNullOrWhiteSpace(topic.Producer) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The producing system is named.", topic.Producer ?? "Not configured", "");
        yield return Check("cfg-consumer", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, id, "Consumer mapping",
            topic.Consumer.MappingState == ConsumerMappingState.Confirmed ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.NeedsConfirmation,
            "The consuming application is confirmed for this environment.",
            topic.Consumer.DisplayName is { } consumer ? $"{consumer} — {IntegrationConfigurationRules.MappingLabel(topic.Consumer.MappingState)}{(topic.Consumer.MappingSource is { } src ? $" ({src})" : "")}" : "No consumer configured",
            topic.Consumer.MappingState == ConsumerMappingState.Suggested ? "Suggested by the audited source; receiver rights on the namespace do not establish a consumer." : "",
            topic.Consumer.MappingState == ConsumerMappingState.Confirmed ? null : "Confirm the consuming application for this environment.");
        if (topic.Kind == IntegrationKind.EventHub)
            yield return Check("cfg-consumer-group", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, id, "Consumer group",
                string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
                "The consumer group the consumer reads with is known.", topic.ConsumerGroup ?? "Unknown / not configured",
                string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? "Only checkpoint/lag review depends on it; $Default is never assumed." : "",
                string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? "Confirm the consumer group to enable checkpoint/lag review." : null);
        yield return Check("cfg-contract", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, id, "Contract relationship",
            topic.ContractRelationship == ContractRelationshipState.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "Producer and consumer contracts are referenced.", IntegrationConfigurationRules.ContractLabel(topic.ContractRelationship), "Configured references are not contract compatibility.");
    }

    private static IEnumerable<IntegrationCheck> ConnectivityChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps)
    {
        if (runtime?.HubExists is { } exists)
            yield return Check("conn-hub", IntegrationReviewDomain.Connectivity, IntegrationCheckScope.Topic, topic.Id, "Configured Event Hub exists",
                exists ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail, "The configured hub exists in the namespace.",
                exists ? "Found by metadata lookup." : "Metadata lookup reported the hub as not found.", "", exists ? null : "Correct the hub name or provision the hub.",
                runtime.Source, runtime.CapturedAt);
        else
            yield return Check("conn-hub", IntegrationReviewDomain.Connectivity, IntegrationCheckScope.Topic, topic.Id, "Configured Event Hub exists", IntegrationCheckStatus.NotAssessed,
                "The configured hub exists in the namespace.", "", caps.HubMetadata ? "No metadata was returned for this hub." : "Event Hub metadata access is not available; existence is never inferred from saved configuration.");
    }

    private async Task<IEnumerable<IntegrationCheck>> ContractChecksAsync(IntegrationDefinition topic, List<IntegrationReviewFinding> findings, CancellationToken ct)
    {
        var checks = new List<IntegrationCheck>();
        if (topic.ContractRelationship == ContractRelationshipState.NotConfigured)
        {
            checks.Add(Check("contract-availability", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Contract availability", IntegrationCheckStatus.NotConfigured,
                "Producer and consumer contracts are available.", "No contract relationship configured.", "No schema is not the same as no compatibility issue.", "Configure the producer/consumer contract references."));
            checks.Add(Check("contract-compatibility", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Producer/consumer compatibility", IntegrationCheckStatus.NotAssessed,
                "The consumer's required fields and types are provided by the producer.", "", "No contract evidence."));
            checks.Add(Check("contract-drift", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Contract drift", IntegrationCheckStatus.NotAssessed, "", "", "No contract evidence."));
            return checks;
        }
        var evidence = contracts.CanRetrieve ? await contracts.GetAsync(topic, ct) : null;
        checks.Add(Check("contract-availability", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Contract availability",
            evidence is { ProducerSchemaJson: not null, ConsumerSchemaJson: not null } ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Observed,
            "Producer and consumer contracts are available.",
            evidence is null ? $"References configured ({IntegrationConfigurationRules.ContractLabel(topic.ContractRelationship)}); not retrieved." : $"Retrieved from {evidence.Source}.",
            "Availability is not compatibility.", provenance: evidence is null ? IntegrationEvidenceSource.Configuration : IntegrationEvidenceSource.ContractArtifact));
        if (evidence is { ProducerSchemaJson: { } producer, ConsumerSchemaJson: { } consumer })
        {
            var differences = EventContractComparer.Compare(producer, consumer);
            checks.Add(Check("contract-compatibility", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Producer/consumer compatibility",
                differences.Count == 0 ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Fail,
                "The consumer's required fields and types are provided by the producer.",
                differences.Count == 0 ? "All consumer-required fields are provided with matching types." : string.Join(" ", differences.Select(d => d.Detail)),
                "Structural comparison of the two contracts; runtime payloads are not read.", differences.Count == 0 ? null : "Align the producer and consumer contracts.",
                IntegrationEvidenceSource.ContractArtifact));
            if (differences.Count > 0)
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"contract-incompatible|{topic.Id}", RuleId = "contract-incompatible", Domain = IntegrationReviewDomain.Contract, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Consumer contract is not satisfied by the producer contract", Subject = topic.DisplayName,
                    Evidence = differences.Select(d => $"{d.Code}: {d.Detail}").ToList(),
                    Recommendation = "Add the missing fields to the producer contract, or relax the consumer's requirement; version the contract if the change is intentional.",
                    AffectedIntegrations = [topic.DisplayName],
                });
        }
        else
            checks.Add(Check("contract-compatibility", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Producer/consumer compatibility", IntegrationCheckStatus.NotAssessed,
                "The consumer's required fields and types are provided by the producer.", "", "No contract retrieval adapter exists for event contracts in this build."));
        checks.Add(Check("contract-drift", IntegrationReviewDomain.Contract, IntegrationCheckScope.Topic, topic.Id, "Contract drift", IntegrationCheckStatus.NotAssessed, "", "", "No contract baseline for event contracts is recorded yet."));
        return checks;
    }

    private static IEnumerable<IntegrationCheck> MessageFlowChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps)
    {
        if (runtime is null || !caps.MessageActivity)
        {
            yield return Check("flow-producer", IntegrationReviewDomain.MessageFlow, IntegrationCheckScope.Topic, topic.Id, "Producer activity", IntegrationCheckStatus.NotAssessed, "", "", NoRuntimeAdapterReason);
            yield return Check("flow-consumer", IntegrationReviewDomain.MessageFlow, IntegrationCheckScope.Topic, topic.Id, "Consumer activity", IntegrationCheckStatus.NotAssessed, "", "", NoRuntimeAdapterReason);
            yield break;
        }
        // No activity is "no recent evidence", never a failure: the system may legitimately be idle.
        yield return Check("flow-producer", IntegrationReviewDomain.MessageFlow, IntegrationCheckScope.Topic, topic.Id, "Producer activity",
            runtime.LastEnqueuedAt is null ? IntegrationCheckStatus.NoRecentEvidence : IntegrationCheckStatus.Observed, "Events are being published.",
            runtime.LastEnqueuedAt is { } enq ? $"Last enqueued {enq:u}." : "No recent enqueued event evidence.", "Absence of activity can be a legitimately idle system.", provenance: runtime.Source, at: runtime.CapturedAt);
        yield return Check("flow-consumer", IntegrationReviewDomain.MessageFlow, IntegrationCheckScope.Topic, topic.Id, "Consumer activity",
            runtime.LastConsumerProgressAt is null ? IntegrationCheckStatus.NoRecentEvidence : IntegrationCheckStatus.Observed, "The consumer makes progress.",
            runtime.LastConsumerProgressAt is { } prog ? $"Last consumer progress {prog:u}." : "No recent consumer progress evidence.", "Producer activity alone never proves consumer success.", provenance: runtime.Source, at: runtime.CapturedAt);
    }

    private static IEnumerable<IntegrationCheck> ReliabilityChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps)
    {
        if (string.IsNullOrWhiteSpace(topic.ConsumerGroup))
        {
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, IntegrationCheckScope.Topic, topic.Id, "Checkpoint progression", IntegrationCheckStatus.NotAssessed,
                "The consumer checkpoint keeps up with enqueued events.", "", caps.ConsumerCheckpoints ? "Consumer group is not configured." : $"Consumer group is not configured and {NoRuntimeAdapterReason[0..1].ToLowerInvariant()}{NoRuntimeAdapterReason[1..]}",
                "Confirm the consumer group to enable checkpoint/lag review.");
            yield break;
        }
        if (runtime?.CheckpointEventsBehind is not { } behind || !caps.ConsumerCheckpoints)
        {
            yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, IntegrationCheckScope.Topic, topic.Id, "Checkpoint progression", IntegrationCheckStatus.NotAssessed,
                "The consumer checkpoint keeps up with enqueued events.", "", NoRuntimeAdapterReason);
            yield break;
        }
        // No staleness threshold exists in policy, so a lag is reported as observed — never turned into a finding by an invented rule.
        yield return Check("rel-checkpoint", IntegrationReviewDomain.Reliability, IntegrationCheckScope.Topic, topic.Id, "Checkpoint progression",
            behind == 0 ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Observed, "The consumer checkpoint keeps up with enqueued events.",
            behind == 0 ? "Checkpoint is at the latest enqueued event." : $"Checkpoint is {behind} event(s) behind the latest enqueued event.",
            behind == 0 ? "" : "No staleness threshold is configured, so the lag is reported without a verdict.", provenance: runtime.Source, at: runtime.CapturedAt);
    }

    private static IEnumerable<IntegrationCheck> ErrorHandlingChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps, List<IntegrationReviewFinding> findings)
    {
        if (runtime is null || !caps.ConsumerErrors)
        {
            yield return Check("err-deserialization", IntegrationReviewDomain.ErrorHandling, IntegrationCheckScope.Topic, topic.Id, "Deserialization failures", IntegrationCheckStatus.NotAssessed, "", "", NoRuntimeAdapterReason);
            yield return Check("err-consumer-exceptions", IntegrationReviewDomain.ErrorHandling, IntegrationCheckScope.Topic, topic.Id, "Consumer exceptions", IntegrationCheckStatus.NotAssessed, "", "", NoRuntimeAdapterReason);
            yield break;
        }
        foreach (var (id, title, count) in new[] { ("err-deserialization", "Deserialization failures", runtime.DeserializationFailures), ("err-consumer-exceptions", "Consumer exceptions", runtime.ConsumerExceptions) })
        {
            yield return count is null
                ? Check(id, IntegrationReviewDomain.ErrorHandling, IntegrationCheckScope.Topic, topic.Id, title, IntegrationCheckStatus.NotAssessed, "", "", "The evidence source did not report this.")
                : Check(id, IntegrationReviewDomain.ErrorHandling, IntegrationCheckScope.Topic, topic.Id, title, count == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Warning,
                    "No consumer failures.", $"{count} observed.", count == 0 ? "No indicators in the evidence window; this does not establish correct error handling." : "", count == 0 ? null : "Investigate the consumer's failure logs.", runtime.Source, runtime.CapturedAt);
            if (count > 0 && id == "err-deserialization")
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"deserialization-failures|{topic.Id}", RuleId = "deserialization-failures", Domain = IntegrationReviewDomain.ErrorHandling, Severity = IntegrationFindingSeverityV2.Medium,
                    Title = "Consumer reports deserialization failures", Subject = topic.DisplayName, Evidence = [$"{count} failure(s) reported by {IntegrationReviewLabels.Source(runtime.Source)}."],
                    Recommendation = "Compare the failing payload shape with the consumer contract; route undeserializable events to a poison/dead-letter path.", AffectedIntegrations = [topic.DisplayName],
                });
        }
    }

    private static IEnumerable<IntegrationCheck> SecurityChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps, List<IntegrationReviewFinding> findings)
    {
        yield return Check("sec-consumer-auth", IntegrationReviewDomain.Security, IntegrationCheckScope.Topic, topic.Id, "Consumer authentication mechanism configured",
            topic.ConsumerAuthentication == IntegrationAuthMechanism.NotConfigured ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Pass,
            "The consumer's authentication mechanism is named.",
            IntegrationConfigurationRules.AuthLabel(topic.ConsumerAuthentication) + (topic.Consumer.ManagedIdentity is { } mi ? $" ({mi})" : ""),
            "Configured, not validated: Managed Identity configured is not runtime authorization verified.");
        if (runtime?.AuthorizationFailures is { } failures && caps.ConsumerErrors)
        {
            yield return Check("sec-runtime-auth", IntegrationReviewDomain.Security, IntegrationCheckScope.Topic, topic.Id, "Runtime consumer authorization",
                failures == 0 ? IntegrationCheckStatus.NoIndicatorsObserved : IntegrationCheckStatus.Fail, "The consumer is authorized to receive.",
                $"{failures} authorization failure(s) observed.", "", failures == 0 ? null : "Grant the consumer identity the Azure Event Hubs Data Receiver role on the hub.", runtime.Source, runtime.CapturedAt);
            if (failures > 0)
                findings.Add(new IntegrationReviewFinding
                {
                    Key = $"consumer-unauthorized|{topic.Id}", RuleId = "consumer-unauthorized", Domain = IntegrationReviewDomain.Security, Severity = IntegrationFindingSeverityV2.High,
                    Title = "Confirmed consumer lacks expected receiver access", Subject = topic.DisplayName, Evidence = [$"{failures} authorization failure(s)."],
                    Recommendation = "Grant the consumer identity the Azure Event Hubs Data Receiver role.", AffectedIntegrations = [topic.DisplayName],
                });
        }
        else
            yield return Check("sec-runtime-auth", IntegrationReviewDomain.Security, IntegrationCheckScope.Topic, topic.Id, "Runtime consumer authorization", IntegrationCheckStatus.NotAssessed,
                "The consumer is authorized to receive.", "", "No runtime authorization evidence is available.");
    }

    private static IEnumerable<IntegrationCheck> PerformanceChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps)
    {
        // Measured values only; no threshold policy exists for integration timing, so values are observed, never judged.
        yield return runtime?.ProcessingLatencyMs is { } latency && caps.Timing
            ? Check("perf-latency", IntegrationReviewDomain.Performance, IntegrationCheckScope.Topic, topic.Id, "Processing latency", IntegrationCheckStatus.Observed, "",
                $"{latency:0} ms measured.", "No integration latency threshold is configured.", provenance: runtime.Source, at: runtime.CapturedAt)
            : Check("perf-latency", IntegrationReviewDomain.Performance, IntegrationCheckScope.Topic, topic.Id, "Processing latency", IntegrationCheckStatus.NotAssessed, "", "",
                "No measured timing evidence. Partition count, retention and reachability are never used as performance evidence.");
    }

    private static IEnumerable<IntegrationCheck> DataQualityChecks(IntegrationDefinition topic, IntegrationRuntimeEvidence? runtime, IntegrationEvidenceCapabilities caps)
    {
        if (runtime?.EnvelopeObserved is null || !caps.PayloadStructure)
        {
            yield return Check("dq-envelope", IntegrationReviewDomain.DataQuality, IntegrationCheckScope.Topic, topic.Id, "CDC envelope (before/after, source, op)", IntegrationCheckStatus.NotAssessed, "", "",
                "No safe structural event evidence; events are never consumed to inspect them.");
            yield break;
        }
        yield return Check("dq-envelope", IntegrationReviewDomain.DataQuality, IntegrationCheckScope.Topic, topic.Id, "CDC envelope (before/after, source, op)",
            runtime.EnvelopeObserved == true ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, "Events carry the Debezium envelope.",
            runtime.OperationTypesObserved is { Count: > 0 } ops ? $"Operation types observed: {string.Join(", ", ops)}." : "Envelope structure observed.", "Structure only; no payload values are shown.",
            provenance: runtime.Source, at: runtime.CapturedAt);
        if (runtime.RequiredFieldsMissing is { } missing)
            yield return Check("dq-required", IntegrationReviewDomain.DataQuality, IntegrationCheckScope.Topic, topic.Id, "Required fields present",
                missing.Count == 0 ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, "Required fields are present in observed events.",
                missing.Count == 0 ? "All required fields present." : $"Missing: {string.Join(", ", missing)}.", "", provenance: runtime.Source, at: runtime.CapturedAt);
    }

    private async Task<IEnumerable<IntegrationCheck>> OperationsChecksAsync(IntegrationDefinition topic, CancellationToken ct)
    {
        var checks = new List<IntegrationCheck>();
        foreach (var (id, title, url) in new[] { ("obs-health", "Consumer health endpoint", topic.HealthUrl), ("obs-worker", "Worker endpoint", topic.WorkerUrl) })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                checks.Add(Check(id, IntegrationReviewDomain.Observability, IntegrationCheckScope.Topic, topic.Id, title, IntegrationCheckStatus.NotConfigured, "A health endpoint shows the consumer's own state.", "Not configured", ""));
                continue;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                checks.Add(Check(id, IntegrationReviewDomain.Observability, IntegrationCheckScope.Topic, topic.Id, title,
                    response.IsSuccessStatusCode ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.Warning, "The endpoint answers with a success status.", $"HTTP {(int)response.StatusCode}",
                    "A healthy endpoint does not prove message processing.", response.IsSuccessStatusCode ? null : "Check the consumer's health.", IntegrationEvidenceSource.HealthEndpoint));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                checks.Add(Check(id, IntegrationReviewDomain.Observability, IntegrationCheckScope.Topic, topic.Id, title, IntegrationCheckStatus.Unavailable, "The endpoint answers with a success status.",
                    $"Not reachable ({ex.GetType().Name}).", "", "Check the configured URL and network access.", IntegrationEvidenceSource.HealthEndpoint));
            }
        }
        return checks;
    }

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

    private static List<IntegrationManualFollowUp> ManualFollowUp(List<IntegrationDefinition> enabled, IntegrationCatalog catalog)
    {
        var items = new List<IntegrationManualFollowUp>();
        var suggested = enabled.Count(i => i.Consumer.MappingState == ConsumerMappingState.Suggested);
        var unconfirmed = enabled.Count(i => i.Consumer.MappingState == ConsumerMappingState.NeedsConfirmation);
        if (suggested > 0) items.Add(new("Confirm suggested consumer mappings", "The audited source names a consumer for these topics; confirm each for this environment.", suggested));
        if (unconfirmed > 0) items.Add(new("Identify consumers", "No consumer is known for these topics.", unconfirmed));
        var groups = enabled.Count(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.ConsumerGroup));
        if (groups > 0) items.Add(new("Confirm consumer group(s)", "Needed for checkpoint and lag review.", groups));
        var contracts = enabled.Count(i => i.ContractRelationship == ContractRelationshipState.NotConfigured);
        if (contracts > 0) items.Add(new("Configure contract relationships", "Producer/consumer contract references enable contract assessment.", contracts));
        if (catalog.Platforms.Any(p => string.IsNullOrWhiteSpace(p.MonitoringUrl) || string.IsNullOrWhiteSpace(p.RunbookUrl)))
            items.Add(new("Confirm monitoring dashboard and runbook", "Add the monitoring dashboard and runbook links to the platform.", catalog.Platforms.Count(p => string.IsNullOrWhiteSpace(p.MonitoringUrl) || string.IsNullOrWhiteSpace(p.RunbookUrl))));
        if (enabled.Any(i => i.Kind == IntegrationKind.EventHub))
            items.Add(new("Verify checkpoint and replay behaviour", "Restart/replay, duplicate handling and ordering limitations need a person to verify.", enabled.Count(i => i.Kind == IntegrationKind.EventHub)));
        return items;
    }

    private static List<string> Limitations(List<IntegrationDefinition> enabled, IntegrationCatalog catalog, IntegrationEvidenceCapabilities caps)
    {
        var limitations = new List<string>();
        if (caps == IntegrationEvidenceCapabilities.None) limitations.Add("Runtime evidence unavailable: message flow, reliability, error handling, performance and data quality were not assessed.");
        if (!caps.HubMetadata) limitations.Add("Event Hub existence was not checked (no Azure metadata access).");
        if (enabled.Any(i => i.Kind == IntegrationKind.EventHub && string.IsNullOrWhiteSpace(i.ConsumerGroup))) limitations.Add("Consumer group not configured: checkpoint/lag review was not possible.");
        if (enabled.Any(i => i.ContractRelationship == ContractRelationshipState.NotConfigured)) limitations.Add("Contract relationships not configured: compatibility and drift were not assessed.");
        if (enabled.Any(i => i.Consumer.MappingState != ConsumerMappingState.Confirmed)) limitations.Add("Some consumer mappings are not confirmed.");
        if (catalog.Platforms.Any(p => string.IsNullOrWhiteSpace(p.MonitoringUrl))) limitations.Add("Monitoring URL absent.");
        if (enabled.Any(i => i.Kind != IntegrationKind.EventHub)) limitations.Add("Domain review is implemented for Event Hub integrations only; other kinds were reviewed for configuration.");
        return limitations;
    }
}
