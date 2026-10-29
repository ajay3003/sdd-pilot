using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Capability-aware review profiles. Which checks a domain needs depends on what the integration is: an event stream is reviewed with
/// hubs, consumer groups and checkpoints; a message broker with queues/topics, subscriptions and dead-letter; an HTTP/API integration with
/// its endpoint, authentication, contract and the consumer's source — never with Event Hub metadata, checkpoints, Managed Identity/RBAC or
/// producer/consumer JSON Schemas it does not have. Generic: no product, pack or integration is named here; a pack contributes only
/// evidence facts (reference metadata, canonical source) through <see cref="IntegrationReviewEvidenceFacts"/>.
/// </summary>
public static class IntegrationReviewProfiles
{
    /// <summary>The shared kind → profile mapping (the Integrations pane's per-integration readiness uses the same one).</summary>
    public static IntegrationReviewCapability Capability(IntegrationKind kind) => IntegrationReviewCapabilities.Of(kind);

    public static string Label(IntegrationReviewCapability capability) => capability switch
    {
        IntegrationReviewCapability.EventStream => "Event Hub",
        IntegrationReviewCapability.MessageBroker => "Service Bus",
        IntegrationReviewCapability.HttpApi => "HTTP/API",
        _ => "Other",
    };

    private static IntegrationDomainReadinessRow Row(IntegrationReviewDomain domain, IntegrationDomainReadiness readiness, string explanation,
        IEnumerable<string?>? available = null, IEnumerable<string?>? missing = null) =>
        new(domain, readiness, explanation, (available ?? []).OfType<string>().ToList(), (missing ?? []).OfType<string>().ToList());

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    // ── HTTP/API ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// HTTP/API integrations (e.g. a webhook): endpoint shape and HTTPS, reference authentication metadata, the reference submission contract
    /// and the consumer's source evidence. Nothing is sent before or during the review, so reachability and runtime contract validation stay
    /// "not observed"; no messaging requirement is added.
    /// </summary>
    public static IReadOnlyList<IntegrationDomainReadinessRow> HttpApi(IReadOnlyList<IntegrationDefinition> http, IntegrationContractSet contracts,
        IReadOnlyDictionary<string, IntegrationReviewEvidenceFacts> facts)
    {
        IntegrationReviewEvidenceFacts? Facts(IntegrationDefinition i) => facts.TryGetValue(i.Id, out var f) ? f : null;
        var withEndpoint = http.Where(i => Uri.TryCreate(i.EndpointOrTopic, UriKind.Absolute, out _)).ToList();
        var https = withEndpoint.Count(i => Uri.TryCreate(i.EndpointOrTopic, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps);
        var unconfirmed = http.Count(i => i.Consumer.MappingState != ConsumerMappingState.Confirmed);
        var authReference = http.Select(Facts).Where(f => f?.AuthenticationReference.Count > 0).ToList();
        var contractReference = http.Select(Facts).Where(f => f?.ContractReference.Count > 0).ToList();
        var bound = http.Select(Facts).Where(f => f?.BoundArchive is not null).ToList();
        var sourceAvailable = http.Select(Facts).Where(f => f?.BoundArchive is null && f?.SourceMatches > 0).ToList();
        var schemas = http.Select(Facts).Where(f => f?.SchemaFiles.Count == 1).ToList();
        var uploaded = http.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null || contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null);
        var configuredAuth = http.Select(i => i.ProducerAuthentication).Concat(http.Select(i => i.ConsumerAuthentication))
            .Where(a => a != IntegrationAuthMechanism.NotConfigured).Distinct().Select(IntegrationConfigurationRules.AuthLabel).ToList();
        var monitoring = http.Count(i => !string.IsNullOrWhiteSpace(i.MonitoringUrl) || !string.IsNullOrWhiteSpace(i.HealthUrl));
        string? Facts_(IReadOnlyList<IntegrationReviewEvidenceFacts?> list, Func<IntegrationReviewEvidenceFacts, string> text) => list.Count == 0 ? null : string.Join("; ", list.OfType<IntegrationReviewEvidenceFacts>().Select(text).Distinct());
        string Joined(IReadOnlyList<TestPackReferenceFact> f) => string.Join(", ", f.Select(x => x.Value));
        var sourceLine = bound.Count > 0 ? Facts_(bound, f => $"{f.SourceRole ?? "Source"} source evidence bound ({f.BoundArchive})")
            : sourceAvailable.Count > 0 ? Facts_(sourceAvailable, f => $"{f.SourceRole ?? "Source"} source evidence available · needs binding") : null;
        var noSourceGap = bound.Count == 0 ? (sourceAvailable.Count > 0 ? "Canonical source binding (bind it in Setup & Evidence)" : "Source evidence for the consumer") : null;

        return
        [
            Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready,
                $"{Plural(http.Count, "HTTP/API integration")} reviewed against the configured expectation: endpoint, producer, consumer and authentication mechanism.",
                [$"Endpoint configured for {withEndpoint.Count} of {http.Count}", unconfirmed == 0 ? "Consumer mappings confirmed" : null],
                [unconfirmed > 0 ? $"Consumer mapping confirmation for {Plural(unconfirmed, "integration")}" : null]),
            withEndpoint.Count == 0 ? Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, "No absolute HTTP endpoint is configured.", [], ["HTTP endpoint"])
                : Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.Partial,
                    "Endpoint shape and the HTTPS expectation are reviewed from configuration; the review sends no request, so reachability is not observed.",
                    [$"HTTP endpoint configured ({withEndpoint.Count})", https == withEndpoint.Count ? "HTTPS endpoint" : null,
                        authReference.Count > 0 ? "Authentication metadata available (Test Pack reference)" : null],
                    [https < withEndpoint.Count ? $"HTTPS for {Plural(withEndpoint.Count - https, "endpoint")}" : null, "Target reachability: not observed (read-only review)"]),
            contractReference.Count == 0 && bound.Count == 0 && sourceAvailable.Count == 0 && uploaded == 0
                ? Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, "No reference contract, source evidence or uploaded contract is known for the HTTP/API integrations.",
                    [], ["Reference or source contract (OpenAPI, application metadata or schema)"])
                : Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Partial,
                    "Reference and source contract evidence are reviewed separately; no request or payload is validated at runtime.",
                    [Facts_(contractReference, f => $"Reference: {f.ReferenceTitle ?? "Test Pack reference"} ({Joined(f.ContractReference)})"), sourceLine,
                        Facts_(schemas, f => $"Schema candidate in source: {f.SchemaFiles[0]}"), uploaded > 0 ? $"Uploaded contract for {Plural(uploaded, "integration")}" : null],
                    [noSourceGap, "Runtime contract validation: not verified"]),
            Row(IntegrationReviewDomain.MessageFlow, bound.Count > 0 ? IntegrationDomainReadiness.Partial : IntegrationDomainReadiness.Limited,
                "The configured hop (producer → consumer endpoint) and the consumer's source are reviewed; downstream hops only where they are configured or evidenced.",
                [.. http.Select(i => $"Configured flow: {i.Producer ?? "producer"} → {i.Consumer.DisplayName ?? "consumer"}").Distinct(), sourceLine],
                [noSourceGap, "Delivery at runtime: not observed"]),
            Row(IntegrationReviewDomain.Reliability, bound.Count > 0 ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.NotAssessable,
                bound.Count > 0 ? "Retry, duplicate/idempotency and timeout handling are visible only in the consumer's source." : "Retry and idempotency handling need the consumer's source evidence.",
                [bound.Count > 0 ? "Webhook retry/idempotency handling in the bound source (source-visible only)" : null],
                [noSourceGap, "Retry and duplicate behaviour at runtime: not observed"]),
            Row(IntegrationReviewDomain.ErrorHandling, bound.Count > 0 ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.NotAssessable,
                bound.Count > 0 ? "Error responses and timeout handling are reviewed from the consumer's source only." : "Error handling needs the consumer's source evidence.",
                [bound.Count > 0 ? "Error-response handling in the bound source" : null], [noSourceGap, "Error responses at runtime: not observed"]),
            Row(IntegrationReviewDomain.Security, IntegrationDomainReadiness.Limited,
                "Authentication metadata and the configured mechanism are reviewed; runtime identity and authorization are never verified by this review.",
                // The Test Pack reference describes how users sign in to the producer application — context, never this integration's authentication.
                [Facts_(authReference, f => $"Producer application sign-in metadata (not this integration's authentication): {Joined(f.AuthenticationReference)} (Test Pack reference)"),
                    configuredAuth.Count > 0 ? $"Configured mechanism: {string.Join(", ", configuredAuth)}" : null, https == withEndpoint.Count && https > 0 ? "HTTPS endpoint" : null],
                [configuredAuth.Count == 0 ? "Integration authentication mechanism (Unknown until configured or reviewed)" : null, "Runtime identity and authorization: not verified"]),
            Row(IntegrationReviewDomain.Observability, bound.Count > 0 || monitoring > 0 ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.NotAssessable,
                "HTTP receipt logging and correlation are reviewed from the consumer's source and configured links; no telemetry is read for HTTP/API.",
                [bound.Count > 0 ? "Receipt logging and correlation in the bound source (source-visible only)" : null, monitoring > 0 ? $"Monitoring or health link for {Plural(monitoring, "integration")}" : null],
                [noSourceGap, "HTTP receipt telemetry: not observed"]),
            Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable,
                "No measured HTTP timing evidence. Configuration values are never performance evidence.", [], ["Measured response time (only a run or test observes it)"]),
            contractReference.Count > 0 || schemas.Count > 0
                ? Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.Partial, "Payload structure is declared by the reference and source schema; payloads are never read.",
                    [Facts_(contractReference, f => $"Declared data type and content type ({Joined(f.ContractReference.Take(2).ToList())})"), Facts_(schemas, f => $"Schema candidate: {f.SchemaFiles[0]}")],
                    ["Payload content: never read"])
                : Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, "No declared payload structure. Payloads are never read.", [], ["Declared payload structure"]),
        ];
    }

    // ── Service Bus ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Message-broker integrations: namespace, queue/topic and subscription, code routes, dead-letter/retry and runtime metadata as the
    /// platform summary reports them. No HTTP or Event Hub requirement is added.
    /// </summary>
    public static IReadOnlyList<IntegrationDomainReadinessRow> ServiceBus(IReadOnlyList<IntegrationDefinition> brokers, IReadOnlyList<IntegrationPlatform> platforms,
        IReadOnlyList<ServiceBusReadiness> summaries, IntegrationContractSet contracts)
    {
        var used = summaries.Where(s => brokers.Any(i => i.PlatformId == s.PlatformId) || brokers.All(i => i.PlatformId is null)).ToList();
        var runtime = used.Any(s => s.RuntimeState == IntegrationEvidenceState.Available);
        var metrics = used.Any(s => s.MetricsState == IntegrationEvidenceState.Available);
        var entities = used.Sum(s => s.Queues + s.Topics + s.Subscriptions);
        var routes = used.Sum(s => s.RoutesTotal);
        var matched = used.Sum(s => s.RoutesMatched);
        var fqdn = platforms.Any(p => brokers.Any(i => i.PlatformId == p.Id) && !string.IsNullOrWhiteSpace(p.NamespaceFqdn));
        var contracted = brokers.Count(i => contracts.Of(i.Id, IntegrationContractRole.Producer) is not null || contracts.Of(i.Id, IntegrationContractRole.Consumer) is not null);
        var auth = brokers.Select(i => i.ConsumerAuthentication).Where(a => a != IntegrationAuthMechanism.NotConfigured).Distinct().Select(IntegrationConfigurationRules.AuthLabel).ToList();
        var runtimeGap = runtime ? null : $"Runtime metadata: {used.Select(s => s.RuntimeReason).FirstOrDefault(r => r.Length > 0) ?? "not configured"}";
        var providers = platforms.Where(p => brokers.Any(i => i.PlatformId == p.Id)).Select(p => p.MonitoringProvider).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        return
        [
            Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready,
                $"{Plural(brokers.Count, "Service Bus integration")} reviewed against the configured namespace, queues/topics and subscriptions.",
                [$"Configured entities: {entities} (queues, topics, subscriptions)"], [runtime ? null : "Comparison with the observed namespace (runtime metadata)"]),
            fqdn ? Row(IntegrationReviewDomain.Connectivity, runtime ? IntegrationDomainReadiness.Ready : IntegrationDomainReadiness.Limited,
                    "Namespace reachability can be probed; entity existence needs runtime metadata.", ["Namespace DNS, TCP 443 and TLS probe"], [runtimeGap])
                : Row(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, "No namespace FQDN is configured to probe.", [], ["Namespace FQDN"]),
            contracted > 0 ? Row(IntegrationReviewDomain.Contract, contracted < brokers.Count ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.Ready,
                    $"Message contracts for {Plural(contracted, "integration")}.", [$"Message contracts ({contracted})"], [contracted < brokers.Count ? $"Message contract for {Plural(brokers.Count - contracted, "integration")}" : null])
                : Row(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, "No message contract is referenced.", [], ["Message contract"]),
            Row(IntegrationReviewDomain.MessageFlow, routes > 0 || runtime ? IntegrationDomainReadiness.Partial : IntegrationDomainReadiness.Limited,
                "Code routes from source and broker metadata are reviewed; processing is never proven by transport evidence.",
                [routes > 0 ? $"Code routes matched: {matched} of {routes}" : null, runtime ? "Entity runtime metadata" : null], [routes > 0 ? null : "Code routes (Source Analysis)", runtimeGap]),
            Row(IntegrationReviewDomain.Reliability, runtime ? IntegrationDomainReadiness.Partial : IntegrationDomainReadiness.NotAssessable,
                runtime ? "Dead-letter and scheduled counts are observed; retry policy is source evidence." : "Dead-letter and retry evidence need runtime metadata.",
                [runtime ? "Dead-letter and scheduled message counts (read-only)" : null], [runtimeGap, "Retry/dead-letter policy at runtime: manual verification"]),
            Row(IntegrationReviewDomain.ErrorHandling, runtime ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.NotAssessable,
                "Dead-lettered messages indicate handling failures; application error handling is separate evidence.", [runtime ? "Dead-letter indicators" : null], [runtimeGap]),
            Row(IntegrationReviewDomain.Security, IntegrationDomainReadiness.Limited, "Configured identity and authorization are reviewed; runtime authorization is not evaluated.",
                [auth.Count > 0 ? $"Configured consumer authentication ({string.Join(", ", auth)})" : null, fqdn ? "TLS on the namespace (probe)" : null],
                [auth.Count > 0 ? null : "Consumer authentication mechanism", "Identity authorization on the entities: not evaluated"]),
            providers.Count > 0 ? Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, $"Monitoring provider configured ({string.Join(", ", providers)}).", [$"Monitoring provider: {string.Join(", ", providers)}"], ["Runtime telemetry"])
                : Row(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited, "No monitoring provider is configured.", [], ["Monitoring provider"]),
            metrics ? Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.Partial, "Namespace metrics are observed; no threshold makes them a verdict.", ["Namespace metrics (Azure Monitor)"], [])
                : Row(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable, "No measured broker metrics. Configuration values are never performance evidence.", [], ["Namespace metrics"]),
            contracted > 0 ? Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.Limited, "Message structure is declared by the contracts; messages are never read.", [$"Declared message structure ({contracted})"], ["Message content: never read"])
                : Row(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, "No declared message structure. Messages are never read.", [], ["Message contract"]),
        ];
    }

    // ── Configuration only ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A type without a domain profile yet (file, other): its configuration is reviewed; no other domain claims anything.</summary>
    public static IReadOnlyList<IntegrationDomainReadinessRow> ConfigurationOnly(IReadOnlyList<IntegrationDefinition> others) =>
    [
        Row(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, $"{Plural(others.Count, "integration")} of a type without a domain profile: configuration is reviewed.",
            [$"Configured expectation for {others.Count}"]),
        .. Enum.GetValues<IntegrationReviewDomain>().Where(d => d != IntegrationReviewDomain.Configuration)
            .Select(d => Row(d, IntegrationDomainReadiness.NotAssessable, "No review profile for this integration type yet.", [], [])),
    ];

    // ── Several capabilities in one environment ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row per domain from the profiles present. A single profile is returned as is. Several are combined: the same state stays; any
    /// reviewable part makes the domain partially reviewable; evidence and gaps keep their capability as a prefix so nothing leaks between them.
    /// </summary>
    public static List<IntegrationDomainReadinessRow> Combine(IReadOnlyList<(IntegrationReviewCapability Capability, IReadOnlyList<IntegrationDomainReadinessRow> Rows)> profiles)
    {
        if (profiles.Count == 1) return [.. profiles[0].Rows];
        return [.. Enum.GetValues<IntegrationReviewDomain>().Select(domain =>
        {
            var parts = profiles.Select(p => (p.Capability, Row: p.Rows.FirstOrDefault(r => r.Domain == domain))).Where(p => p.Row is not null).ToList();
            var states = parts.Select(p => p.Row!.Readiness).Distinct().ToList();
            var readiness = states.Count == 1 ? states[0]
                : states.Any(s => s is IntegrationDomainReadiness.Ready or IntegrationDomainReadiness.Available or IntegrationDomainReadiness.Partial) ? IntegrationDomainReadiness.Partial
                : states.Contains(IntegrationDomainReadiness.Limited) ? IntegrationDomainReadiness.Limited : IntegrationDomainReadiness.NotAssessable;
            return new IntegrationDomainReadinessRow(domain, readiness, string.Join(" ", parts.Select(p => $"{Label(p.Capability)}: {p.Row!.Explanation}")),
                [.. parts.SelectMany(p => (p.Row!.Available ?? []).Select(a => $"{Label(p.Capability)} · {a}"))],
                [.. parts.SelectMany(p => (p.Row!.Missing ?? []).Select(m => $"{Label(p.Capability)} · {m}"))]);
        })];
    }
}
