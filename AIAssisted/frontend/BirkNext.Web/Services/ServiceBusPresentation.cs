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

    public static readonly (string Label, string Metric, string Aggregation)[] MetricRows =
    [
        ("Incoming messages (total)", "IncomingMessages", "Total"), ("Outgoing messages (total)", "OutgoingMessages", "Total"),
        ("Active messages (average / max)", "ActiveMessages", "Average"), ("Dead-lettered messages (max)", "DeadletteredMessages", "Maximum"),
        ("Server errors (total)", "ServerErrors", "Total"), ("User errors (total)", "UserErrors", "Total"), ("Throttled requests (total)", "ThrottledRequests", "Total"),
    ];
}
