using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Presentation of Service Bus transport evidence. Configuration, runtime metadata and application routes are shown as separate layers;
/// every state is a word (tones are cosmetic). Nothing here re-decides a result — the backend's check is displayed as it is.
/// </summary>
public static class ServiceBusPresentation
{
    public const string Focus = "servicebus";

    public static string Tone(ServiceBusCheckState state) => state switch
    {
        ServiceBusCheckState.Pass or ServiceBusCheckState.Matched => "ready",
        ServiceBusCheckState.Mismatch or ServiceBusCheckState.NotFound => "fail",
        ServiceBusCheckState.Observed or ServiceBusCheckState.Configured => "info",
        ServiceBusCheckState.NotAuthorized or ServiceBusCheckState.Error or ServiceBusCheckState.Unavailable or ServiceBusCheckState.Stale or ServiceBusCheckState.NotReferenced => "attention",
        _ => "muted",
    };

    public static string Tone(ServiceBusEvidenceState state) => state switch
    {
        ServiceBusEvidenceState.Consistent => "ready",
        ServiceBusEvidenceState.IssueDetected => "fail",
        ServiceBusEvidenceState.Partial => "attention",
        _ => "muted",
    };

    /// <summary>Runtime source configuration as far as the page can tell (the instance-level Azure gate is only known to the backend).</summary>
    public static (string Label, string Detail) RuntimeConfiguration(IntegrationPlatform platform) =>
        Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId, out _) && !string.IsNullOrWhiteSpace(platform.ResourceGroup) && !string.IsNullOrWhiteSpace(platform.Namespace)
            ? ("Configured", "Metadata is read when Azure runtime evidence is enabled for this BirkNext instance.")
            : ("Not configured", "The Azure subscription id of the namespace is not configured, so runtime metadata cannot be read.");

    /// <summary>
    /// Wolverine routes that name an entity of the configured topology by exact name (a quick pre-test count). Kind, topic and access are
    /// compared by the backend when Test Service Bus runs.
    /// </summary>
    public static (int Named, int Total) WolverineRoutes(ServiceBusTopology? topology, ApplicationMessagingEvidenceSet? messaging)
    {
        if (topology is null || messaging is null) return (0, 0);
        var routes = messaging.Applications.SelectMany(a => a.Routes).Where(r => r.Direction != MessagingRouteDirection.Reference).ToList();
        var named = routes.Count(r => r.EntityName is not null && topology.Entities.Any(e => e.Name == r.EntityName && (e.EntityType != ServiceBusEntityType.Subscription || e.Topic == r.TopicName)));
        return (named, routes.Count);
    }

    public static string Expected(ServiceBusEntityExpectation e)
    {
        var parts = new List<string>();
        if (e.MaxDeliveryCount is { } mdc) parts.Add($"MaxDeliveryCount {mdc}");
        if (e.LockDuration is { } lockDuration) parts.Add($"LockDuration {lockDuration}");
        if (e.DefaultMessageTimeToLive is { } ttl) parts.Add($"TTL {ttl}");
        if (e.RequiresSession is { } session) parts.Add(session ? "sessions" : "no sessions");
        if (e.DeadLetteringOnMessageExpiration is true) parts.Add("dead-letter on expiry");
        if (e.RequiresDuplicateDetection is { } dup) parts.Add(dup ? "duplicate detection" : "no duplicate detection");
        return parts.Count == 0 ? "No expected properties" : string.Join(" · ", parts);
    }

    public static (int Observed, int NotFound, int Total) Entities(ServiceBusEvidenceCheck check, ServiceBusEntityType type)
    {
        var rows = check.RuntimeChecks.Where(c => c.CheckId == "sb-entity" && c.EntityType == type).ToList();
        return (rows.Count(r => r.State == ServiceBusCheckState.Observed), rows.Count(r => r.State == ServiceBusCheckState.NotFound), rows.Count);
    }

    /// <summary>Totals of the observed point-in-time counts; null when no entity reported the count (never zero for "unknown").</summary>
    public static (long? Active, long? DeadLetter, long? Scheduled) Counts(ServiceBusEvidenceCheck check)
    {
        var entities = check.Runtime?.Entities.Where(e => e.EntityType != ServiceBusEntityType.Topic).ToList() ?? [];
        long? Sum(Func<ServiceBusEntityObservation, long?> pick) => entities.Any(e => pick(e) is not null) ? entities.Sum(e => pick(e) ?? 0) : null;
        return (Sum(e => e.ActiveMessageCount), Sum(e => e.DeadLetterMessageCount), Sum(e => e.ScheduledMessageCount));
    }

    public static bool IsStale(ServiceBusEvidenceCheck check, DateTimeOffset now) =>
        check.Runtime is { State: IntegrationEvidenceState.Available } runtime && now - runtime.CapturedAt > TimeSpan.FromHours(Math.Max(1, check.WindowHours));

    public static string Count(long? value) => value?.ToString() ?? "Not reported";

    /// <summary>Code-route comparison status: an older analysis is "Needs re-analysis", never a meaningful 0 of 0.</summary>
    public static (string Label, string Tone) RouteStatus(ServiceBusEvidenceCheck check)
    {
        if (check.RouteAnalysis == ServiceBusRouteAnalysis.NeedsReanalysis) return (ServiceBusRouteAnalysis.NeedsReanalysis, "attention");
        if (check.RouteAnalysis == ServiceBusRouteAnalysis.NotAnalyzed) return ("Not analyzed", "muted");
        var routes = check.Routes.Where(r => r.Direction != nameof(MessagingRouteDirection.Reference)).ToList();
        return routes.Any(r => r.Configuration == ServiceBusCheckState.Mismatch) ? ("Mismatch", "fail") : routes.Count == 0 ? ("Not assessed", "muted") : ("Matched", "ready");
    }

    /// <summary>A metric value as observed ("Not reported" when Azure returned none — never 0 for unknown).</summary>
    public static string Metric(ServiceBusMetricsEvidence? metrics, string name, string aggregation) =>
        metrics?.Metrics.FirstOrDefault(m => m.Metric == name && m.Aggregation == aggregation)?.Value is { } v ? v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "Not reported";

    // ── Pre-run sections: declared topology, application messaging, runtime access and policy are separate layers ──

    /// <summary>
    /// Whether BirkNext can read deployed Service Bus metadata: the namespace's runtime access configuration (subscription, resource group,
    /// namespace) AND this instance's Azure gate. A missing subscription is a runtime-access blocker, never a topology problem.
    /// </summary>
    public static ServiceBusRuntimeAccess RuntimeAccess(IntegrationPlatform platform, bool azureEnabled)
    {
        var subscription = Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId?.Trim(), out _);
        // Every blocker is named: the namespace's own configuration first, then this instance's Azure gate.
        var reasons = new List<string>();
        if (!subscription) reasons.Add("Azure subscription is not configured for this Service Bus namespace.");
        if (string.IsNullOrWhiteSpace(platform.ResourceGroup)) reasons.Add("The resource group of the namespace is not configured.");
        if (string.IsNullOrWhiteSpace(platform.Namespace)) reasons.Add("The namespace is not configured.");
        if (!azureEnabled) reasons.Add("Azure access is not enabled for this BirkNext instance (IntegrationReview:Azure:Enabled).");
        return new(reasons.Count == 0, reasons.Count == 0 ? "Available" : "Not available", reasons, subscription ? platform.RuntimeEvidence!.SubscriptionId!.Trim() : null);
    }

    /// <summary>What cannot be verified while runtime access is not available.</summary>
    public static readonly string[] UnverifiedWithoutRuntime = ["Namespace metadata", "Queues", "Topics", "Subscriptions", "Runtime entity properties", "Message counts"];

    /// <summary>Compact expected properties ("max 10 deliveries · 1 min lock · 14 d TTL · no sessions · DLQ on expiry"); the exact Azure names are in <see cref="Expected"/>.</summary>
    public static string ExpectedCompact(ServiceBusEntityExpectation e)
    {
        var parts = new List<string>();
        if (e.MaxDeliveryCount is { } mdc) parts.Add($"max {mdc} deliveries");
        if (e.LockDuration is { } lockDuration) parts.Add($"{Duration(lockDuration)} lock");
        if (e.DefaultMessageTimeToLive is { } ttl) parts.Add($"{Duration(ttl)} TTL");
        if (e.RequiresSession is { } session) parts.Add(session ? "sessions" : "no sessions");
        if (e.DeadLetteringOnMessageExpiration is true) parts.Add("DLQ on expiry");
        if (e.RequiresDuplicateDetection is { } dup) parts.Add(dup ? "duplicate detection" : "no duplicate detection");
        return parts.Count == 0 ? "None declared" : string.Join(" · ", parts);
    }

    /// <summary>An ISO 8601 duration as "1 min" / "14 d"; an unparseable value is shown as stored.</summary>
    public static string Duration(string iso)
    {
        try
        {
            var t = System.Xml.XmlConvert.ToTimeSpan(iso.Trim());
            return t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} d"
                : t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} h"
                : t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0 ? $"{t.TotalMinutes:0} min"
                : $"{t.TotalSeconds:0} s";
        }
        catch (FormatException) { return iso; }
    }

    /// <summary>Exact declared Azure property values (name, value) — Unknown properties are omitted, never shown as a default.</summary>
    public static IReadOnlyList<(string Name, string Value)> ExactProperties(ServiceBusEntityExpectation e)
    {
        var rows = new List<(string, string)>();
        if (e.MaxDeliveryCount is { } mdc) rows.Add(("MaxDeliveryCount", mdc.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (e.LockDuration is { } l) rows.Add(("LockDuration", l));
        if (e.DefaultMessageTimeToLive is { } ttl) rows.Add(("DefaultMessageTimeToLive", ttl));
        if (e.RequiresSession is { } s) rows.Add(("RequiresSession", s ? "true" : "false"));
        if (e.DeadLetteringOnMessageExpiration is { } d) rows.Add(("DeadLetteringOnMessageExpiration", d ? "true" : "false"));
        if (e.DeadLetteringOnFilterEvaluationExceptions is { } f) rows.Add(("DeadLetteringOnFilterEvaluationExceptions", f ? "true" : "false"));
        if (e.RequiresDuplicateDetection is { } dup) rows.Add(("RequiresDuplicateDetection", dup ? "true" : "false"));
        if (e.MaxSizeInMegabytes is { } size) rows.Add(("MaxSizeInMegabytes", size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>
    /// Consumers of a queue or subscription as declared. Namespace-wide receive rights are a capability on every entity, never an
    /// entity-specific consumer mapping.
    /// </summary>
    public static (string Consumers, string? Supporting) Consumers(ServiceBusEntityExpectation e, ServiceBusTopology topology) =>
        e.Consumers.Count > 0 ? (string.Join(", ", e.Consumers), null)
        : topology.NamespaceReceivers.Count > 0 ? ("No entity-specific consumer", "Namespace-wide receive rights exist (not a confirmed mapping)")
        : ("None configured", null);

    /// <summary>One row per subscription, plus one row for a topic without a subscription ("No subscription configured" — neutral, never Missing).</summary>
    public static IReadOnlyList<ServiceBusTopicRow> TopicRows(ServiceBusTopology topology) =>
        topology.Topics.Select(topic =>
        {
            var subscriptions = topology.Subscriptions.Where(s => s.Topic == topic.Name).ToList();
            return new ServiceBusTopicRow(topic, subscriptions.Select(s =>
            {
                var (consumers, supporting) = Consumers(s, topology);
                return new ServiceBusSubscriptionRow(s, consumers, supporting, s.Consumers.Count > 0 ? "Configured" : "No consumer configured");
            }).ToList());
        }).ToList();

    /// <summary>Application-messaging (Wolverine) state as this card shows it — source findings only; handler execution is never assessed here.</summary>
    public static (string Source, string SourceTone, string Routes, string RoutesTone) Messaging(ServiceBusTopology? topology, ApplicationMessagingEvidenceSet? messaging)
    {
        if (messaging is null) return ("Not analyzed", "muted", "Not assessed", "muted");
        if (messaging.AnalyzerVersion < 2) return (ServiceBusRouteAnalysis.NeedsReanalysis, "attention", "Not assessed", "muted");
        var (named, total) = WolverineRoutes(topology, messaging);
        return ("Analyzed", "info", total == 0 ? "No routes found in source" : $"{named} of {total} name a declared entity", total == 0 ? "muted" : "info");
    }

    public static readonly (string Label, string Metric, string Aggregation)[] MetricRows =
    [
        ("Incoming messages (total)", "IncomingMessages", "Total"), ("Outgoing messages (total)", "OutgoingMessages", "Total"),
        ("Active messages (average / max)", "ActiveMessages", "Average"), ("Dead-lettered messages (max)", "DeadletteredMessages", "Maximum"),
        ("Server errors (total)", "ServerErrors", "Total"), ("User errors (total)", "UserErrors", "Total"), ("Throttled requests (total)", "ThrottledRequests", "Total"),
    ];
}

/// <summary>Runtime access of a Service Bus namespace: available, or why not (configuration input — never evidence itself).</summary>
public sealed record ServiceBusRuntimeAccess(bool Available, string Label, IReadOnlyList<string> Reasons, string? SubscriptionId);

public sealed record ServiceBusSubscriptionRow(ServiceBusEntityExpectation Subscription, string Consumers, string? Supporting, string MappingState);

/// <summary>A declared topic with its declared subscriptions (empty = no subscription configured).</summary>
public sealed record ServiceBusTopicRow(ServiceBusEntityExpectation Topic, IReadOnlyList<ServiceBusSubscriptionRow> Subscriptions);
