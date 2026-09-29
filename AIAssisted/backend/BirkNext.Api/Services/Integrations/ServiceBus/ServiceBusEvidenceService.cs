using System.Xml;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ServiceBus;

/// <summary>
/// Service Bus evidence in three separate layers: the CONFIGURED topology (expected entities/properties with provenance), RUNTIME metadata
/// (read-only Azure Resource Manager GETs), and APPLICATION routes from analyzed source (Wolverine and direct Azure SDK use). Existence is
/// Observed, never Pass; Pass is only an exact expected-vs-observed property comparison; an unknown expectation is Not assessed; message
/// counts are Observed with no invented threshold (0 is not a pass, &gt; 0 is not a failure). Route matching is infrastructure — never proof
/// that a message was published or processed; that needs application telemetry (Wolverine evidence), reported separately.
/// </summary>
public sealed class ServiceBusEvidenceService(IServiceBusMetadataSource metadata, ILogger<ServiceBusEvidenceService> logger, IServiceBusMetricsSource? metricsSource = null)
{
    public static string RouteAnalysisOf(ApplicationMessagingEvidenceSet? analyzed) =>
        analyzed is null ? ServiceBusRouteAnalysis.NotAnalyzed : analyzed.AnalyzerVersion < MinimumAnalyzerVersion ? ServiceBusRouteAnalysis.NeedsReanalysis : ServiceBusRouteAnalysis.Current;

    /// <summary>What the test lead can do about unavailable runtime evidence — an action, not only the state.</summary>
    public static string RuntimeAction(IntegrationPlatform platform, IntegrationEvidenceState state, string reason, string what) => state switch
    {
        IntegrationEvidenceState.NotConfigured when reason == IntegrationAzureCredential.DisabledMessage =>
            $"{what}: enable Azure runtime evidence on the BirkNext backend (IntegrationReview:Azure:Enabled=true; read-only, DefaultAzureCredential or IntegrationReview:Azure:ManagedIdentityClientId).",
        IntegrationEvidenceState.NotConfigured => $"{what}: configure {MissingParameters(platform)} (Integrations → Service Bus → Configure runtime evidence).",
        IntegrationEvidenceState.NotAuthorized => $"{what}: grant the BirkNext Azure identity read access (Reader, or Monitoring Reader for metrics) on namespace {platform.Namespace}. {reason}",
        _ => $"{what}: {IntegrationReviewLabels.EvidenceState(state)} — {reason}",
    };

    private static string MissingParameters(IntegrationPlatform platform)
    {
        var missing = new List<string>();
        if (!Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId, out _)) missing.Add("the Azure subscription id");
        if (string.IsNullOrWhiteSpace(platform.ResourceGroup)) missing.Add("the resource group");
        if (string.IsNullOrWhiteSpace(platform.Namespace)) missing.Add("the namespace name");
        return missing.Count == 0 ? "the runtime evidence settings" : string.Join(", ", missing);
    }

    public const string RouteMatchedNote = "Configured infrastructure only; a matched route is not a message published or processed.";
    /// <summary>Application analyses older than this carry no resolved entity names; they are never compared (no false orphans or mismatches).</summary>
    public const int MinimumAnalyzerVersion = 2;

    private static ApplicationMessagingEvidenceSet? Comparable(ApplicationMessagingEvidenceSet? messaging) =>
        messaging is { AnalyzerVersion: >= MinimumAnalyzerVersion } ? messaging : null;

    public static bool IsServiceBus(IntegrationPlatform platform) => platform.Kind == IntegrationKind.ServiceBus;

    // ── Pre-run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public ServiceBusReadiness Readiness(IntegrationPlatform platform, ApplicationMessagingEvidenceSet? messaging)
    {
        var topology = platform.ServiceBusTopology ?? new ServiceBusTopology();
        var routes = Correlate(topology, Comparable(messaging), null);
        var status = metadata.Describe(platform);
        return new ServiceBusReadiness
        {
            PlatformId = platform.Id, PlatformName = platform.Name, Queues = topology.Queues.Count(), Topics = topology.Topics.Count(), Subscriptions = topology.Subscriptions.Count(),
            RoutesTotal = routes.Count(r => r.Direction != nameof(MessagingRouteDirection.Reference)),
            RoutesMatched = routes.Count(r => r.Direction != nameof(MessagingRouteDirection.Reference) && r.Configuration == ServiceBusCheckState.Matched),
            RoutesMismatched = routes.Count(r => r.Direction != nameof(MessagingRouteDirection.Reference) && r.Configuration == ServiceBusCheckState.Mismatch),
            RuntimeState = status.State, RuntimeReason = status.Reason, RouteAnalysis = RouteAnalysisOf(messaging),
            MetricsState = metricsSource?.Describe(platform).State ?? IntegrationEvidenceState.NotConfigured,
        };
    }

    // ── Test / review ───────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ServiceBusEvidenceCheck> CheckAsync(IntegrationPlatform platform, ApplicationMessagingEvidenceSet? messaging, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var topology = platform.ServiceBusTopology ?? new ServiceBusTopology();
        var window = platform.RuntimeEvidence?.ReviewWindowHours is { } w && w > 0 ? w : IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours;
        // No Azure call unless the source is configured (and the instance-level Azure gate is on).
        var runtime = metadata.Describe(platform).State == IntegrationEvidenceState.Available
            ? await metadata.ReadAsync(platform, ct)
            : new ServiceBusRuntimeEvidence { PlatformId = platform.Id, Namespace = platform.Namespace, State = metadata.Describe(platform).State, Reason = metadata.Describe(platform).Reason, CapturedAt = DateTimeOffset.UtcNow };
        // Metrics use the same gate: no Azure call unless the metrics source is configured for this namespace.
        var metrics = metricsSource is null
            ? new ServiceBusMetricsEvidence { State = IntegrationEvidenceState.NotSupported, Reason = "Azure Monitor metrics are not available in this BirkNext instance.", CapturedAt = DateTimeOffset.UtcNow, WindowHours = window }
            : metricsSource.Describe(platform) is { State: not IntegrationEvidenceState.Available } off
                ? new ServiceBusMetricsEvidence { State = off.State, Reason = off.Reason, CapturedAt = DateTimeOffset.UtcNow, WindowHours = window }
                : await metricsSource.ReadAsync(platform, window, ct);
        var result = Evaluate(platform, topology, messaging, runtime, started, window, metrics);
        logger.LogInformation("Service Bus evidence for {PlatformId} ({Namespace}): {State}; topology {Queues}q/{Topics}t/{Subscriptions}s; runtime {Runtime}; {Routes} route(s), {Findings} finding(s).",
            platform.Id, platform.Namespace, result.OverallState, result.Queues, result.Topics, result.Subscriptions, runtime.State, result.Routes.Count, result.Findings.Count);
        return result;
    }

    /// <summary>Pure evaluation of configured topology, runtime metadata and application routes (no I/O).</summary>
    public static ServiceBusEvidenceCheck Evaluate(IntegrationPlatform platform, ServiceBusTopology topology, ApplicationMessagingEvidenceSet? analyzed,
        ServiceBusRuntimeEvidence runtime, DateTimeOffset started, int windowHours, ServiceBusMetricsEvidence? metrics = null)
    {
        var messaging = Comparable(analyzed);
        var configuration = ConfigurationChecks(topology, messaging);
        var runtimeAvailable = runtime.State == IntegrationEvidenceState.Available;
        var runtimeChecks = runtimeAvailable ? RuntimeChecks(topology, runtime) : [];
        var routes = Correlate(topology, messaging, runtimeAvailable ? runtime : null);
        var findings = new List<string>();
        findings.AddRange(routes.Where(r => r.Direction != nameof(MessagingRouteDirection.Reference) && r.Configuration == ServiceBusCheckState.Mismatch)
            .Select(r => $"{r.Application} ({r.Technology}) {r.Direction.ToLowerInvariant()}s {r.Entity}: {r.Detail}"));
        findings.AddRange(runtimeChecks.Where(c => c.State is ServiceBusCheckState.NotFound or ServiceBusCheckState.Mismatch).Select(c => $"{c.Title}: {c.Detail}"));

        var missing = new List<string>();
        if (topology.Entities.Count == 0) missing.Add("No configured Service Bus topology for this platform.");
        if (!runtimeAvailable) missing.Add(RuntimeAction(platform, runtime.State, runtime.Reason, "Service Bus runtime metadata"));
        foreach (var (list, failure) in runtime.ListFailures) missing.Add($"Runtime {list}: {failure}");
        if (analyzed is not null && messaging is null)
            missing.Add($"The application source was analyzed {analyzed.AnalyzedAt:yyyy-MM-dd HH:mm} UTC by an earlier version that did not resolve Service Bus entity names; re-analyze it (Integrations → Application messaging) to compare code routes.");
        else if (messaging is null) missing.Add("No analyzed application source: code routes cannot be compared with the topology (Integrations → Application messaging).");
        if (metrics is not { State: IntegrationEvidenceState.Available })
            missing.Add(metrics is null || metrics.State == IntegrationEvidenceState.NotSupported
                ? "Azure Monitor metrics (throughput, server errors) are not read."
                : RuntimeAction(platform, metrics.State, metrics.Reason, "Azure Monitor metrics"));
        missing.Add("Oldest-message age: not available (not a Service Bus platform metric; never derived from counts).");
        if (messaging is not null)
            foreach (var app in messaging.Applications.Where(a => a.Detection != MessagingDetection.NotDetected && a.Handlers.Count > 0))
                missing.Add($"Application Insights processing evidence for {app.ApplicationId}: read only during an Integration Quality Review run when the application is bound and telemetry is configured.");
        foreach (var unresolved in routes.Where(r => r.Configuration == ServiceBusCheckState.NotAssessed && r.Direction != nameof(MessagingRouteDirection.Reference)))
            missing.Add($"{unresolved.Application}: {unresolved.Direction.ToLowerInvariant()} entity is decided at runtime ({unresolved.EntitySource}).");

        var issue = findings.Count > 0;
        var state = topology.Entities.Count == 0 ? ServiceBusEvidenceState.NotTestable
            : issue ? ServiceBusEvidenceState.IssueDetected
            : runtimeAvailable && runtime.ListFailures.Count == 0 && messaging is not null ? ServiceBusEvidenceState.Consistent
            : ServiceBusEvidenceState.Partial;
        return new ServiceBusEvidenceCheck
        {
            PlatformId = platform.Id, Namespace = platform.Namespace, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, OverallState = state,
            Queues = topology.Queues.Count(), Topics = topology.Topics.Count(), Subscriptions = topology.Subscriptions.Count(),
            Configuration = configuration, Runtime = runtime, RuntimeChecks = runtimeChecks, Routes = routes, Missing = missing.Distinct().ToList(), Findings = findings, WindowHours = windowHours,
            Metrics = metrics, RouteAnalysis = RouteAnalysisOf(analyzed),
        };
    }

    /// <summary>A snapshot older than the review window is stale: its counts and properties are not current evidence.</summary>
    public static bool IsStale(ServiceBusEvidenceCheck check, DateTimeOffset now) =>
        check.Runtime is { State: IntegrationEvidenceState.Available } runtime && now - runtime.CapturedAt > TimeSpan.FromHours(Math.Max(1, check.WindowHours));

    // ── Configuration layer ─────────────────────────────────────────────────────────────────────────────────────────

    private static List<ServiceBusComparison> ConfigurationChecks(ServiceBusTopology topology, ApplicationMessagingEvidenceSet? messaging)
    {
        var checks = new List<ServiceBusComparison>
        {
            new()
            {
                CheckId = "sb-topology", Title = "Configured topology", State = topology.Entities.Count == 0 ? ServiceBusCheckState.NotConfigured : ServiceBusCheckState.Configured,
                Detail = topology.Entities.Count == 0 ? "No expected queues, topics or subscriptions are configured."
                    : $"{topology.Queues.Count()} queue(s), {topology.Topics.Count()} topic(s), {topology.Subscriptions.Count()} subscription(s). Source: {topology.Source}.",
                Provenance = IntegrationEvidenceSource.Configuration,
            },
        };
        foreach (var subscription in topology.Subscriptions.Where(s => !topology.Topics.Any(t => t.Name == s.Topic)))
            checks.Add(new() { CheckId = "sb-topology-subscription-topic", EntityType = ServiceBusEntityType.Subscription, Entity = subscription.Path, Title = "Subscription topic in topology",
                State = ServiceBusCheckState.Mismatch, Detail = $"Subscription {subscription.Path} names topic {subscription.Topic}, which is not in the configured topology.", Provenance = IntegrationEvidenceSource.Configuration });
        if (messaging is null) return checks;

        var referenced = References(messaging);
        foreach (var entity in topology.Entities)
        {
            var users = referenced.Where(r => Matches(entity, r.Name, r.Topic, r.Kind, strictKind: false)).Select(r => r.Application).Distinct().ToList();
            checks.Add(new()
            {
                CheckId = "sb-code-reference", EntityType = entity.EntityType, Entity = entity.Path, Title = $"{entity.EntityType} {entity.Path} referenced by code",
                State = users.Count > 0 ? ServiceBusCheckState.Matched : ServiceBusCheckState.NotReferenced,
                Detail = users.Count > 0 ? $"Referenced by {string.Join(", ", users)}."
                    : "No publisher, listener or entity reference in the analyzed source. This may be intentional (operational, support or future use) — confirm; it is not a defect by itself.",
                Provenance = IntegrationEvidenceSource.SourceCode,
            });
        }
        return checks;
    }

    private sealed record Reference(string Application, string? Name, string? Topic, MessagingEndpointKind Kind);

    private static List<Reference> References(ApplicationMessagingEvidenceSet messaging) =>
        messaging.Applications.SelectMany(a => a.Routes.Concat(a.SdkRoutes).Where(r => r.EntityName is not null)
            .Select(r => new Reference(a.ApplicationId, r.EntityName, r.TopicName, r.EndpointKind))).ToList();

    /// <summary>
    /// Whether an application route names this configured entity. Subscriptions need the exact topic and subscription; queues/topics match by
    /// name, and — with <paramref name="strictKind"/> — by kind as well (a Wolverine queue route must meet a queue, not a topic).
    /// </summary>
    private static bool Matches(ServiceBusEntityExpectation entity, string? name, string? topic, MessagingEndpointKind kind, bool strictKind)
    {
        if (name is null || !string.Equals(entity.Name, name, StringComparison.Ordinal)) return false;
        // A subscription needs its exact topic; a bare name (no topic in source) can only meet a subscription when the kind is not asserted.
        if (entity.EntityType == ServiceBusEntityType.Subscription) return topic is not null ? entity.Topic == topic : !strictKind;
        if (topic is not null) return false;
        if (!strictKind) return true;
        return entity.EntityType == (kind == MessagingEndpointKind.Topic ? ServiceBusEntityType.Topic : ServiceBusEntityType.Queue);
    }

    // ── Route correlation (application messaging ↔ transport) ───────────────────────────────────────────────────────

    public static List<ServiceBusRouteCorrelation> Correlate(ServiceBusTopology topology, ApplicationMessagingEvidenceSet? messaging, ServiceBusRuntimeEvidence? runtime)
    {
        if (messaging is null) return [];
        var result = new List<ServiceBusRouteCorrelation>();
        foreach (var app in messaging.Applications)
        foreach (var route in app.Routes.Concat(app.SdkRoutes))
        {
            var subscription = route.EndpointKind == MessagingEndpointKind.Subscription && route.Direction == MessagingRouteDirection.Listen;
            var strict = route.Technology == "Wolverine";
            // Wolverine routes assert the endpoint kind (queue vs topic); an SDK sender or a reference only names the entity.
            var entity = route.EntityName is null ? null : topology.Entities.FirstOrDefault(e => Matches(e, route.EntityName, route.TopicName, route.EndpointKind, strict));
            var sameName = route.EntityName is null ? null : topology.Entities.FirstOrDefault(e => e.Name == route.EntityName);
            var (configuration, detail) = route.EntityName is null
                ? (ServiceBusCheckState.NotAssessed, "The entity name is decided at runtime; source does not establish it.")
                : entity is not null ? (ServiceBusCheckState.Matched, RouteMatchedNote)
                : subscription && topology.Topics.Any(t => t.Name == route.TopicName)
                    ? (ServiceBusCheckState.Mismatch, $"Subscription {route.EntityName} is not configured on topic {route.TopicName}.")
                : sameName is not null ? (ServiceBusCheckState.Mismatch, $"Source uses a {route.EndpointKind.ToString().ToLowerInvariant()} named {route.EntityName}; the configured topology has it as a {sameName.EntityType.ToString().ToLowerInvariant()}.")
                : (ServiceBusCheckState.Mismatch, $"{route.EntityName} is not in the configured topology.");
            var target = entity;
            var access = target is null || route.Direction == MessagingRouteDirection.Reference || target.Publishers.Count + target.Consumers.Count == 0 ? ServiceBusCheckState.NotAssessed
                : route.Direction == MessagingRouteDirection.Publish ? target.Publishers.Contains(app.ApplicationId) ? ServiceBusCheckState.Matched : ServiceBusCheckState.Mismatch
                : target.Consumers.Contains(app.ApplicationId) || topology.NamespaceReceivers.Contains(app.ApplicationId) ? ServiceBusCheckState.Matched : ServiceBusCheckState.Mismatch;
            var observed = runtime is null || target is null ? ServiceBusCheckState.NotAssessed
                : runtime.Entities.Any(o => o.EntityType == target.EntityType && o.Name == target.Name && o.Topic == target.Topic) ? ServiceBusCheckState.Observed : ServiceBusCheckState.NotFound;
            result.Add(new ServiceBusRouteCorrelation
            {
                Application = app.ApplicationId, Technology = route.Technology, Direction = route.Direction.ToString(), MessageType = route.MessageType,
                Entity = route.EntityName is null ? route.Endpoint : subscription && route.TopicName is not null ? $"{route.TopicName}/{route.EntityName}" : route.EntityName,
                EntityType = target?.EntityType, EntitySource = route.EntityNameSource ?? route.Endpoint, Configuration = configuration, Runtime = observed, Access = access,
                Detail = access == ServiceBusCheckState.Mismatch
                    ? $"{detail} The audited infrastructure grants {(route.Direction == MessagingRouteDirection.Publish ? "send" : "receive")} on {target!.Path} to {Granted(route.Direction == MessagingRouteDirection.Publish ? target.Publishers : target.Consumers)}, not {app.ApplicationId}."
                    : detail,
                Location = route.Location,
            });
        }
        return result;
    }

    private static string Granted(List<string> applications) => applications.Count == 0 ? "no application" : string.Join(", ", applications);

    // ── Runtime layer ───────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly (string Label, string Property, Func<ServiceBusEntityExpectation, string?> Expected, bool Duration)[] Compared =
    [
        ("MaxDeliveryCount", "maxDeliveryCount", e => e.MaxDeliveryCount?.ToString(), false),
        ("LockDuration", "lockDuration", e => e.LockDuration, true),
        ("DefaultMessageTimeToLive", "defaultMessageTimeToLive", e => e.DefaultMessageTimeToLive, true),
        ("RequiresSession", "requiresSession", e => e.RequiresSession?.ToString().ToLowerInvariant(), false),
        ("DeadLetteringOnMessageExpiration", "deadLetteringOnMessageExpiration", e => e.DeadLetteringOnMessageExpiration?.ToString().ToLowerInvariant(), false),
        ("DeadLetteringOnFilterEvaluationExceptions", "deadLetteringOnFilterEvaluationExceptions", e => e.DeadLetteringOnFilterEvaluationExceptions?.ToString().ToLowerInvariant(), false),
        ("RequiresDuplicateDetection", "requiresDuplicateDetection", e => e.RequiresDuplicateDetection?.ToString().ToLowerInvariant(), false),
        ("MaxSizeInMegabytes", "maxSizeInMegabytes", e => e.MaxSizeInMegabytes?.ToString(), false),
    ];

    public static string? NormalizeDuration(string? value)
    {
        if (value is null) return null;
        try { return XmlConvert.ToString(XmlConvert.ToTimeSpan(value)); } catch (FormatException) { return value; }
    }

    public static List<ServiceBusComparison> RuntimeChecks(ServiceBusTopology topology, ServiceBusRuntimeEvidence runtime)
    {
        var checks = new List<ServiceBusComparison>
        {
            new() { CheckId = "sb-namespace", Title = $"Namespace {runtime.Namespace}", State = ServiceBusCheckState.Observed,
                Observed = runtime.NamespaceStatus, Detail = $"Namespace metadata read (status {runtime.NamespaceStatus ?? "not reported"}, SKU {runtime.Sku ?? "not reported"}). Management-plane access is not data-plane reachability.",
                Provenance = IntegrationEvidenceSource.AzureResourceManager },
        };
        foreach (var entity in topology.Entities)
        {
            var listFailure = entity.EntityType switch
            {
                ServiceBusEntityType.Queue => runtime.ListFailures.GetValueOrDefault("Queues"),
                ServiceBusEntityType.Topic => runtime.ListFailures.GetValueOrDefault("Topics"),
                _ => runtime.ListFailures.GetValueOrDefault("Topics") ?? runtime.ListFailures.GetValueOrDefault($"Subscriptions of {entity.Topic}"),
            };
            if (listFailure is not null)
            {
                checks.Add(new() { CheckId = "sb-entity", EntityType = entity.EntityType, Entity = entity.Path, Title = $"{entity.EntityType} {entity.Path}", State = ServiceBusCheckState.NotAssessed,
                    Detail = $"Could not be listed — {listFailure}", Provenance = IntegrationEvidenceSource.AzureResourceManager });
                continue;
            }
            var observed = runtime.Entities.FirstOrDefault(o => o.EntityType == entity.EntityType && o.Name == entity.Name && o.Topic == entity.Topic);
            if (observed is null)
            {
                checks.Add(new() { CheckId = "sb-entity", EntityType = entity.EntityType, Entity = entity.Path, Title = $"Expected {entity.EntityType.ToString().ToLowerInvariant()} {entity.Path}",
                    State = ServiceBusCheckState.NotFound, Expected = "Exists", Observed = "Not listed",
                    Detail = $"The configured {entity.EntityType.ToString().ToLowerInvariant()} {entity.Path} is not in the namespace.", Provenance = IntegrationEvidenceSource.AzureResourceManager });
                continue;
            }
            checks.Add(new() { CheckId = "sb-entity", EntityType = entity.EntityType, Entity = entity.Path, Title = $"{entity.EntityType} {entity.Path}", State = ServiceBusCheckState.Observed,
                Observed = observed.Status, Detail = $"Listed with status {observed.Status ?? "not reported"}. Existence is not consumption.", Provenance = IntegrationEvidenceSource.AzureResourceManager });
            foreach (var (label, property, expected, duration) in Compared)
            {
                var want = expected(entity);
                if (want is null) continue;
                var have = observed.Properties.GetValueOrDefault(property);
                var same = have is not null && (duration ? NormalizeDuration(want) == NormalizeDuration(have) : string.Equals(want, have, StringComparison.OrdinalIgnoreCase));
                checks.Add(new()
                {
                    CheckId = $"sb-property-{property}", EntityType = entity.EntityType, Entity = entity.Path, Title = $"{entity.Path} {label}",
                    Expected = want, Observed = have ?? "not reported",
                    State = have is null ? ServiceBusCheckState.NotAssessed : same ? ServiceBusCheckState.Pass : ServiceBusCheckState.Mismatch,
                    Detail = have is null ? "Azure did not report this property." : same ? "Expected and observed match." : $"Expected {want}, observed {have}.",
                    Provenance = IntegrationEvidenceSource.AzureResourceManager,
                });
            }
            if (entity.EntityType != ServiceBusEntityType.Topic)
                checks.Add(new()
                {
                    CheckId = "sb-counts", EntityType = entity.EntityType, Entity = entity.Path, Title = $"{entity.Path} message counts",
                    State = observed.ActiveMessageCount is null && observed.DeadLetterMessageCount is null ? ServiceBusCheckState.NotAssessed : ServiceBusCheckState.Observed,
                    Observed = $"active {Show(observed.ActiveMessageCount)}, dead-letter {Show(observed.DeadLetterMessageCount)}, scheduled {Show(observed.ScheduledMessageCount)}, transfer {Show(observed.TransferMessageCount)}, transfer dead-letter {Show(observed.TransferDeadLetterMessageCount)}",
                    Detail = observed.DeadLetterMessageCount > 0
                        ? $"Dead-letter messages observed ({observed.DeadLetterMessageCount}). No threshold is configured, so this is not judged a failure."
                        : "Point-in-time counts; no threshold is configured. Zero active messages is not a pass (it can mean no traffic), and zero dead-letter messages does not verify dead-letter handling.",
                    Provenance = IntegrationEvidenceSource.AzureResourceManager,
                });
        }
        foreach (var extra in runtime.Entities.Where(o => !topology.Entities.Any(e => e.EntityType == o.EntityType && e.Name == o.Name && e.Topic == o.Topic)))
            checks.Add(new() { CheckId = "sb-unconfigured-entity", EntityType = extra.EntityType, Entity = extra.Path, Title = $"{extra.EntityType} {extra.Path}", State = ServiceBusCheckState.Observed,
                Detail = "Present in the namespace but not in the configured topology.", Provenance = IntegrationEvidenceSource.AzureResourceManager });
        return checks;
    }

    private static string Show(long? value) => value?.ToString() ?? "not reported";

    // ── IQR contribution (existing domains only) ────────────────────────────────────────────────────────────────────

    private static IntegrationCheck Check(string id, IntegrationReviewDomain domain, string subject, string title, IntegrationCheckStatus status, string evidence, string explanation,
        IntegrationEvidenceSource provenance, DateTimeOffset at, string expectation = "Configured Service Bus topology.") => new()
    {
        CheckId = id, Domain = domain, Scope = IntegrationCheckScope.Platform, SubjectId = subject, Title = title, Status = status, Expectation = expectation,
        Evidence = evidence, Explanation = explanation, Provenance = provenance, CapturedAt = at,
        Freshness = provenance == IntegrationEvidenceSource.AzureResourceManager ? IntegrationEvidenceItemFreshness.Current : IntegrationEvidenceItemFreshness.Unknown,
    };

    private static IntegrationCheckStatus RuntimeUnavailable(IntegrationEvidenceState state) => state switch
    {
        IntegrationEvidenceState.NotConfigured => IntegrationCheckStatus.NotConfigured,
        IntegrationEvidenceState.NotFound => IntegrationCheckStatus.Fail,
        _ => IntegrationCheckStatus.Unavailable,
    };

    public static (List<IntegrationCheck> Checks, List<IntegrationReviewFinding> Findings) ReviewChecks(IntegrationPlatform platform, ServiceBusEvidenceCheck check)
    {
        var id = platform.Id;
        var at = check.CompletedAt;
        var checks = new List<IntegrationCheck>();
        var findings = new List<IntegrationReviewFinding>();
        var runtime = check.Runtime;
        var observed = runtime?.State == IntegrationEvidenceState.Available;
        string RuntimeReason() => $"{IntegrationReviewLabels.EvidenceState(runtime?.State ?? IntegrationEvidenceState.NotConfigured)}: {runtime?.Reason}";

        // Configuration
        var topology = check.Configuration.First(c => c.CheckId == "sb-topology");
        checks.Add(Check("sb-topology", IntegrationReviewDomain.Configuration, id, "Service Bus topology", topology.State == ServiceBusCheckState.Configured ? IntegrationCheckStatus.Configured : IntegrationCheckStatus.NotConfigured,
            topology.Detail, "The expected queues, topics and subscriptions. Configured expectation, not runtime state.", IntegrationEvidenceSource.Configuration, at));
        var routes = check.Routes.Where(r => r.Direction != nameof(MessagingRouteDirection.Reference)).ToList();
        var mismatched = routes.Where(r => r.Configuration == ServiceBusCheckState.Mismatch).ToList();
        checks.Add(Check("sb-routes", IntegrationReviewDomain.Configuration, id, "Code routes ↔ configured topology",
            routes.Count == 0 ? IntegrationCheckStatus.NotAssessed : mismatched.Count > 0 ? IntegrationCheckStatus.Warning : routes.Any(r => r.Configuration == ServiceBusCheckState.Matched) ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.NotAssessed,
            routes.Count == 0 ? "No analyzed publisher/listener routes." : $"{routes.Count(r => r.Configuration == ServiceBusCheckState.Matched)} of {routes.Count} route(s) match the configured topology; {mismatched.Count} mismatch; {routes.Count(r => r.Configuration == ServiceBusCheckState.NotAssessed)} decided at runtime.",
            RouteMatchedNote, IntegrationEvidenceSource.SourceCode, at, "Every source publisher/listener names a configured entity."));
        foreach (var mismatch in mismatched)
            findings.Add(new()
            {
                Key = $"sb-route-mismatch|{id}|{mismatch.Application}|{mismatch.Entity}", RuleId = "sb-route-mismatch", Domain = IntegrationReviewDomain.Configuration, Severity = IntegrationFindingSeverityV2.Medium,
                Title = "Code route does not match the configured Service Bus topology", Subject = $"{mismatch.Application} → {mismatch.Entity}",
                Evidence = [$"{mismatch.Technology} {mismatch.Direction.ToLowerInvariant()} {mismatch.Entity} ({mismatch.EntitySource}).", mismatch.Detail],
                Recommendation = "Align the application's entity name or the configured topology; confirm which one reflects the deployed namespace.", AffectedIntegrations = [platform.Name],
            });
        var orphans = check.Configuration.Where(c => c.CheckId == "sb-code-reference" && c.State == ServiceBusCheckState.NotReferenced).ToList();
        if (orphans.Count > 0)
            checks.Add(Check("sb-unreferenced", IntegrationReviewDomain.Configuration, id, "Configured entities without a code reference", IntegrationCheckStatus.NeedsConfirmation,
                string.Join(", ", orphans.Select(o => o.Entity)), "No publisher, listener or reference in the analyzed source — possibly intentional; confirm. Not a defect by itself.", IntegrationEvidenceSource.SourceCode, at));

        // Security (configured access; never runtime authorization)
        var access = routes.Where(r => r.Access != ServiceBusCheckState.NotAssessed).ToList();
        if (access.Count > 0)
            checks.Add(Check("sb-access", IntegrationReviewDomain.Security, id, "Send/receive access in audited infrastructure",
                access.Any(a => a.Access == ServiceBusCheckState.Mismatch) ? IntegrationCheckStatus.Warning : IntegrationCheckStatus.Pass,
                string.Join("; ", access.Where(a => a.Access == ServiceBusCheckState.Mismatch).Select(a => a.Detail)) is { Length: > 0 } gaps ? gaps : $"{access.Count} route(s) have a matching send/receive grant.",
                "Compares code routes with the seeded, audited role scopes. Configured RBAC is not runtime authorization and does not prove correct routing.", IntegrationEvidenceSource.Infrastructure, at));

        // Connectivity (management plane)
        checks.Add(Check("sb-namespace", IntegrationReviewDomain.Connectivity, id, "Service Bus namespace metadata",
            observed ? IntegrationCheckStatus.Observed : RuntimeUnavailable(runtime?.State ?? IntegrationEvidenceState.NotConfigured),
            observed ? $"{runtime!.Namespace}: status {runtime.NamespaceStatus ?? "not reported"}, SKU {runtime.Sku ?? "not reported"}." : "",
            observed ? "Read through Azure Resource Manager. Management-plane metadata does not prove data-plane reachability from the applications." : RuntimeReason(),
            IntegrationEvidenceSource.AzureResourceManager, at));
        if (runtime?.State == IntegrationEvidenceState.NotFound)
            findings.Add(new() { Key = $"sb-namespace-not-found|{id}", RuleId = "sb-namespace-not-found", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                Title = "Service Bus namespace not found", Subject = platform.Namespace ?? platform.Name, Evidence = [runtime.Reason],
                Recommendation = "Check the configured subscription, resource group and namespace name.", AffectedIntegrations = [platform.Name] });

        // Message flow: entities available (transport); processing is application evidence.
        var entities = check.RuntimeChecks.Where(c => c.CheckId == "sb-entity").ToList();
        checks.Add(Check("sb-entities", IntegrationReviewDomain.MessageFlow, id, "Configured entities in the namespace",
            !observed ? RuntimeUnavailable(runtime?.State ?? IntegrationEvidenceState.NotConfigured) : entities.Any(e => e.State == ServiceBusCheckState.NotFound) ? IntegrationCheckStatus.Fail
                : entities.All(e => e.State == ServiceBusCheckState.Observed) ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NotAssessed,
            observed ? $"{entities.Count(e => e.State == ServiceBusCheckState.Observed)} of {entities.Count} configured entities observed" + (entities.Any(e => e.State == ServiceBusCheckState.NotFound) ? $"; not found: {string.Join(", ", entities.Where(e => e.State == ServiceBusCheckState.NotFound).Select(e => e.Entity))}." : ".") : "",
            observed ? "Transport entities exist; message processing needs application telemetry (see Application messaging)." : RuntimeReason(), IntegrationEvidenceSource.AzureResourceManager, at));
        foreach (var missing in entities.Where(e => e.State == ServiceBusCheckState.NotFound))
            findings.Add(new() { Key = $"sb-entity-not-found|{id}|{missing.Entity}", RuleId = "sb-entity-not-found", Domain = IntegrationReviewDomain.MessageFlow, Severity = IntegrationFindingSeverityV2.Medium,
                Title = $"Expected Service Bus {missing.EntityType.ToString()!.ToLowerInvariant()} not found", Subject = missing.Entity ?? "", Evidence = [missing.Detail],
                Recommendation = "Confirm whether the entity should exist in this environment; the configured topology and the namespace disagree.", AffectedIntegrations = [platform.Name] });

        // Reliability and Error handling: property comparisons grouped per property.
        foreach (var group in check.RuntimeChecks.Where(c => c.CheckId.StartsWith("sb-property-", StringComparison.Ordinal)).GroupBy(c => (c.CheckId, c.EntityType)))
        {
            var domain = group.Key.CheckId.Contains("deadLettering", StringComparison.Ordinal) ? IntegrationReviewDomain.ErrorHandling : IntegrationReviewDomain.Reliability;
            var label = group.First().Title.Split(' ').Last();
            var bad = group.Where(c => c.State == ServiceBusCheckState.Mismatch).ToList();
            checks.Add(Check($"{group.Key.CheckId}-{group.Key.EntityType?.ToString().ToLowerInvariant()}", domain, id, $"{group.Key.EntityType} {label}",
                bad.Count > 0 ? IntegrationCheckStatus.Fail : group.All(c => c.State == ServiceBusCheckState.Pass) ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.NotAssessed,
                bad.Count > 0 ? string.Join("; ", bad.Select(b => $"{b.Entity}: expected {b.Expected}, observed {b.Observed}")) : $"{group.Count(c => c.State == ServiceBusCheckState.Pass)} of {group.Count()} match (expected {group.First().Expected}).",
                "Exact comparison with the configured expectation; properties without an expectation are not assessed.", IntegrationEvidenceSource.AzureResourceManager, at, $"{label} = {group.First().Expected}"));
            foreach (var mismatch in bad)
                findings.Add(new() { Key = $"sb-property-mismatch|{id}|{mismatch.Entity}|{label}", RuleId = "sb-property-mismatch", Domain = domain, Severity = IntegrationFindingSeverityV2.Low,
                    Title = $"Service Bus {label} differs from the configured expectation", Subject = mismatch.Entity ?? "", Evidence = [$"Expected {mismatch.Expected}, observed {mismatch.Observed}."],
                    Recommendation = "Confirm which value is intended and align the namespace or the configured expectation.", AffectedIntegrations = [platform.Name] });
        }
        if (!observed)
            checks.Add(Check("sb-properties", IntegrationReviewDomain.Reliability, id, "Delivery settings (MaxDeliveryCount, LockDuration, TTL, sessions)", RuntimeUnavailable(runtime?.State ?? IntegrationEvidenceState.NotConfigured),
                "", RuntimeReason(), IntegrationEvidenceSource.AzureResourceManager, at));
        var counts = check.RuntimeChecks.Where(c => c.CheckId == "sb-counts").ToList();
        checks.Add(Check("sb-dead-letter", IntegrationReviewDomain.ErrorHandling, id, "Dead-letter messages",
            !observed ? RuntimeUnavailable(runtime?.State ?? IntegrationEvidenceState.NotConfigured) : counts.Count == 0 ? IntegrationCheckStatus.NotAssessed : IntegrationCheckStatus.Observed,
            observed ? string.Join("; ", counts.Select(c => $"{c.Entity}: {c.Observed}")) : "",
            observed ? "Point-in-time counts with no configured threshold: dead-letter messages are an attention indicator, not a failure; zero does not verify dead-letter handling." : RuntimeReason(),
            IntegrationEvidenceSource.AzureResourceManager, at));

        // Observability and Performance: nothing is credited without measured evidence.
        var metrics = check.Metrics;
        var metricsObserved = metrics is { State: IntegrationEvidenceState.Available };
        string Metric(string name, string aggregation) => metrics?.Metrics.FirstOrDefault(m => m.Metric == name && m.Aggregation == aggregation)?.Value is { } v ? v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "not reported";
        checks.Add(Check("sb-monitoring", IntegrationReviewDomain.Observability, id, "Service Bus monitoring evidence",
            metricsObserved ? IntegrationCheckStatus.Observed : metrics is null or { State: IntegrationEvidenceState.NotSupported } ? IntegrationCheckStatus.NotAssessed : RuntimeUnavailable(metrics.State),
            metricsObserved ? $"Last {metrics!.WindowHours} h: server errors {Metric("ServerErrors", "Total")}, user errors {Metric("UserErrors", "Total")}, throttled requests {Metric("ThrottledRequests", "Total")}, dead-lettered (max) {Metric("DeadletteredMessages", "Maximum")}." : "",
            metricsObserved ? "Azure Monitor platform metrics, observed with no configured threshold: zero errors is not a pass." : metrics is null ? "Azure Monitor metrics are not read in this build; no monitoring evidence is claimed for the namespace." : $"{IntegrationReviewLabels.EvidenceState(metrics.State)}: {metrics.Reason}",
            IntegrationEvidenceSource.AzureMonitor, metricsObserved ? metrics!.CapturedAt : at));
        checks.Add(Check("sb-performance", IntegrationReviewDomain.Performance, id, "Service Bus throughput / latency",
            metricsObserved ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NotAssessed,
            metricsObserved ? $"Last {metrics!.WindowHours} h: incoming {Metric("IncomingMessages", "Total")}, outgoing {Metric("OutgoingMessages", "Total")} messages; active messages avg {Metric("ActiveMessages", "Average")}, max {Metric("ActiveMessages", "Maximum")}." : "",
            metricsObserved ? "Throughput observed from Azure Monitor with no configured threshold. Latency and oldest-message age are not Service Bus platform metrics and are not assessed." : "No measured throughput, latency or oldest-message age; message counts are not performance evidence.",
            IntegrationEvidenceSource.AzureMonitor, metricsObserved ? metrics!.CapturedAt : at));
        return (checks, findings);
    }
}
