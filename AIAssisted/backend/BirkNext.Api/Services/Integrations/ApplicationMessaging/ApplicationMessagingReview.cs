using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

/// <summary>
/// Contribution of application-messaging evidence (Wolverine) to the EXISTING Integration Quality Review domains — no new domain, and no
/// domain made ready by a package. Source facts become Detected/Configured checks (Configuration; policy capability in Reliability and Error
/// handling); anything about behaviour — handler execution, retries, dead-lettering, duration — needs runtime telemetry and is otherwise
/// Not assessed with the reason. A topic receives these checks only when a person bound its consumer to an analyzed application.
/// </summary>
public static class ApplicationMessagingReview
{
    /// <summary>The analyzed application explicitly bound to this integration's consumer (exact consumer name; never inferred).</summary>
    public static ApplicationMessagingEvidence? For(IntegrationDefinition topic, ApplicationMessagingEvidenceSet? set) =>
        topic.Consumer.DisplayName is { Length: > 0 } consumer
            ? set?.Applications.FirstOrDefault(a => string.Equals(a.BoundConsumer, consumer, StringComparison.Ordinal))
            : null;

    public static List<ApplicationMessagingSummary> Summaries(IntegrationCatalog catalog, ApplicationMessagingEvidenceSet? set, IIntegrationAzureCredential? azure)
    {
        if (set is null) return [];
        return set.Applications.Where(a => a.Detection != MessagingDetection.NotDetected || a.BoundConsumer is not null).Select(a =>
        {
            var topics = catalog.Integrations.Where(i => i.Enabled && For(i, set) == a).ToList();
            var platform = catalog.Platforms.FirstOrDefault(p => topics.Any(t => t.PlatformId == p.Id));
            var (state, reason) = RuntimeReadiness(a, topics.Count, platform, azure);
            return new ApplicationMessagingSummary
            {
                ApplicationId = a.ApplicationId, ServiceName = a.ServiceName, Technology = a.Technology, Detection = a.Detection, HandlerMapping = a.HandlerMapping,
                RetryPolicy = a.RetryPolicy, Outbox = a.Outbox, ErrorHandling = a.ErrorHandling, BoundConsumer = a.BoundConsumer, BoundTopics = topics.Count,
                RuntimeState = state, RuntimeReason = reason,
            };
        }).ToList();
    }

    /// <summary>Pre-run: whether handler telemetry could be read (configuration only; nothing is contacted).</summary>
    public static (IntegrationEvidenceState State, string Reason) RuntimeReadiness(ApplicationMessagingEvidence app, int boundTopics, IntegrationPlatform? platform, IIntegrationAzureCredential? azure) =>
        app.Handlers.Count == 0 ? (IntegrationEvidenceState.NotSupported, "No Wolverine handler in this application's source; there is no handler processing to observe.")
        : boundTopics == 0 ? (IntegrationEvidenceState.NotConfigured, "Not bound to an integration consumer; the telemetry workspace is configured per integration platform.")
        : app.TelemetryRoleName is null ? (IntegrationEvidenceState.NotConfigured, "Source sets no OpenTelemetry service name, so the Application Insights role is unknown.")
        : azure?.Credential is null ? (IntegrationEvidenceState.NotConfigured, azure?.DisabledReason ?? "Azure runtime evidence is not configured for this BirkNext instance.")
        : !Guid.TryParse(platform?.RuntimeEvidence?.TelemetryWorkspaceId, out _) ? (IntegrationEvidenceState.NotConfigured, "No telemetry source configured (Log Analytics workspace id of Application Insights).")
        : (IntegrationEvidenceState.Available, "Handler log records can be read from Application Insights.");

    private static IntegrationCheck Check(string id, IntegrationReviewDomain domain, string subject, string title, IntegrationCheckStatus status, string evidence,
        string explanation, IntegrationEvidenceSource source, string? recommendation = null, DateTimeOffset? at = null, DateTimeOffset? sourceTimestamp = null,
        IntegrationEvidenceItemFreshness freshness = IntegrationEvidenceItemFreshness.Unknown) => new()
    {
        CheckId = id, Domain = domain, Scope = IntegrationCheckScope.Topic, SubjectId = subject, Title = title, Status = status,
        Expectation = "Application messaging evidence for the bound consumer application.", Evidence = evidence, Explanation = explanation,
        Recommendation = recommendation, Provenance = source, CapturedAt = at ?? DateTimeOffset.UtcNow, SourceTimestamp = sourceTimestamp, Freshness = freshness,
    };

    private static IntegrationCheckStatus FactStatus(MessagingFactState state) => state switch
    {
        MessagingFactState.Detected => IntegrationCheckStatus.Detected,
        MessagingFactState.Configured or MessagingFactState.Available => IntegrationCheckStatus.Configured,
        MessagingFactState.NotFound => IntegrationCheckStatus.NotConfigured,
        _ => IntegrationCheckStatus.NotAssessed,
    };

    private static string Where(MessagingFact? fact) =>
        fact is null || fact.Locations.Count == 0 ? "" : " Source: " + string.Join(", ", fact.Locations.Take(3).Select(l => $"{l.File}:{l.Line}")) + ".";

    public static IEnumerable<IntegrationCheck> Checks(IntegrationDefinition topic, ApplicationMessagingEvidence app, ApplicationMessagingRuntime runtime, DateTimeOffset analyzedAt)
    {
        var id = topic.Id;
        var name = $"{app.ApplicationId} (bound to {app.BoundConsumer})";
        yield return Check("am-detected", IntegrationReviewDomain.Configuration, id, "Application messaging technology (Wolverine)",
            app.Detection switch { MessagingDetection.Confirmed => IntegrationCheckStatus.Detected, MessagingDetection.Likely => IntegrationCheckStatus.NotAssessed, _ => IntegrationCheckStatus.NotConfigured },
            $"Wolverine {ApplicationMessagingLabels.Detection(app.Detection)} in {name}.{Where(app.Fact("registration"))}",
            $"{app.DetectionReason} Detected is not configured correctly, and configured is not executed.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        if (app.Fact("event-hub-consumer") is { } eventHub)
            yield return Check("am-event-hub-consumer", IntegrationReviewDomain.Configuration, id, "Event Hub consumer technology", IntegrationCheckStatus.Detected,
                eventHub.Detail + Where(eventHub), "Transport consumption and application messaging are separate: Wolverine is not this topic's Event Hub consumer.",
                IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        if (app.Detection == MessagingDetection.NotDetected) yield break;

        var routes = app.Routes.Select(r => r.Direction == MessagingRouteDirection.Publish
            ? $"publish {r.MessageType} → {r.EndpointKind.ToString().ToLowerInvariant()} {r.Endpoint}{(r.Condition is null ? "" : $" ({r.Condition})")}"
            : $"listen {r.EndpointKind.ToString().ToLowerInvariant()} {r.Endpoint}{(r.Topic is null ? "" : $" on topic {r.Topic}")} → {r.MessageType ?? "type decided at runtime"}").ToList();
        yield return Check("am-routes", IntegrationReviewDomain.Configuration, id, "Wolverine routes", routes.Count == 0 ? IntegrationCheckStatus.NotConfigured : IntegrationCheckStatus.Configured,
            routes.Count == 0 ? "No Wolverine endpoint routes in source." : string.Join("; ", routes) + ".",
            "Configured routes from source; a configured route is not a message sent or received.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        if (app.HandlerMapping != MessagingFactState.NotApplicable)
            yield return Check("am-handler-mapping", IntegrationReviewDomain.Configuration, id, "Handler mapping", FactStatus(app.HandlerMapping),
                string.Join("; ", app.Routes.Where(r => r.Direction == MessagingRouteDirection.Listen).Select(r => $"{r.MessageType ?? "?"} → {(r.Handlers.Count == 0 ? "no handler" : string.Join("/", r.Handlers))} [{ApplicationMessagingLabels.Fact(r.HandlerMapping)}]"))
                    is { Length: > 0 } mapping ? mapping : string.Join("; ", app.Handlers.Take(5).Select(h => $"{h.MessageType} → {h.Type}")),
                app.HandlerMapping == MessagingFactState.Available ? "Exactly one source handler per listened message type, in established discovery. A handler that exists is not a message processed."
                    : string.Join(" ", app.Routes.Where(r => r.HandlerMapping != MessagingFactState.Available && r.MappingReason is not null).Select(r => r.MappingReason!.Trim()).Distinct()) is { Length: > 0 } why ? why
                    : app.Fact("handler-discovery")?.Detail ?? "",
                IntegrationEvidenceSource.SourceCode, at: analyzedAt);

        var retry = app.Fact("retry-policy");
        yield return Check("am-retry-policy", IntegrationReviewDomain.Reliability, id, "Wolverine retry policy", FactStatus(app.RetryPolicy),
            (retry?.Detail ?? "") + Where(retry), "A configured retry policy is capability, not evidence that a retry happened.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        var outbox = app.Fact("outbox");
        if (outbox is not null)
            yield return Check("am-outbox", IntegrationReviewDomain.Reliability, id, "Durable messaging / outbox", FactStatus(app.Outbox),
                outbox.Detail + Where(outbox), "Configured message storage is not an outbox used successfully.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        yield return Check("am-retry-observed", IntegrationReviewDomain.Reliability, id, "Retries observed", IntegrationCheckStatus.NotAssessed, "",
            $"No retry telemetry: {app.Fact("telemetry-export")?.Detail ?? "Wolverine metrics are not exported."} No retry observed would not mean the policy is absent.",
            IntegrationEvidenceSource.ApplicationInsights);

        var disposition = app.Fact("error-policy");
        yield return Check("am-failure-disposition", IntegrationReviewDomain.ErrorHandling, id, "Wolverine failure disposition", FactStatus(app.ErrorHandling),
            (disposition?.Detail ?? "") + Where(disposition), "Configured error / dead-letter policy is not evidence that a message was moved.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        foreach (var route in app.Routes.Where(r => r.FailurePath.Count > 0))
            yield return Check("am-failure-publishing", IntegrationReviewDomain.ErrorHandling, id, "Failure publishing through Wolverine", IntegrationCheckStatus.Configured,
                $"{string.Join("; ", route.FailurePath)} sends {route.MessageType} → Azure Service Bus {route.EndpointKind.ToString().ToLowerInvariant()} {route.Endpoint}.",
                "Source call path on failure. Queue existence and this route do not prove an error was ever published.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);

        var handlerRuntime = app.Handlers.Count > 0;
        if (handlerRuntime)
        {
            var observed = runtime.State == IntegrationEvidenceState.Available;
            var stale = runtime.Freshness is IntegrationEvidenceItemFreshness.Historical or IntegrationEvidenceItemFreshness.Stale;
            yield return Check("am-handler-execution", IntegrationReviewDomain.MessageFlow, id, "Wolverine handler activity",
                !observed ? IntegrationCheckStatus.NotAssessed : runtime.HandlerInformationLogs > 0 && !stale ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NoRecentEvidence,
                observed ? $"{runtime.HandlerInformationLogs} information, {runtime.HandlerWarningLogs} warning, {runtime.HandlerErrorLogs} error log record(s) from handler classes in {runtime.WindowHours} h." : "",
                observed ? "Handler log records show handler activity (started or in progress); they do not prove completion or business success."
                    : $"Not assessed: {runtime.Reason}", IntegrationEvidenceSource.ApplicationInsights, at: runtime.CapturedAt, sourceTimestamp: runtime.LastHandlerLog, freshness: runtime.Freshness);
            yield return Check("am-handler-errors", IntegrationReviewDomain.ErrorHandling, id, "Handler errors observed",
                !observed ? IntegrationCheckStatus.NotAssessed : runtime.HandlerErrorLogs > 0 ? IntegrationCheckStatus.Observed : IntegrationCheckStatus.NoIndicatorsObserved,
                observed ? $"{runtime.HandlerErrorLogs} error log record(s) from handler classes in {runtime.WindowHours} h." : "",
                observed ? "Error-level handler logs in the window; none observed is not a pass." : $"Not assessed: {runtime.Reason}",
                IntegrationEvidenceSource.ApplicationInsights, at: runtime.CapturedAt, sourceTimestamp: runtime.LastHandlerLog, freshness: runtime.Freshness);
        }
        var export = app.Fact("telemetry-export");
        yield return Check("am-telemetry", IntegrationReviewDomain.Observability, id, "Wolverine tracing / metrics export",
            export is null ? IntegrationCheckStatus.NotAssessed : FactStatus(export.State), (export?.Detail ?? "") + Where(export),
            "Whether source exports Wolverine traces or metrics; configured export is not telemetry received.", IntegrationEvidenceSource.SourceCode, at: analyzedAt);
        yield return Check("am-processing-duration", IntegrationReviewDomain.Performance, id, "Handler processing duration", IntegrationCheckStatus.NotAssessed, "",
            "No measured handler duration: Wolverine duration metrics are not exported and not read. Configuration is never performance evidence.", IntegrationEvidenceSource.ApplicationInsights);
        if (app.Routes.Any(r => r.Direction == MessagingRouteDirection.Listen))
            yield return Check("am-deserialization", IntegrationReviewDomain.DataQuality, id, "Deserialization / contract failures", IntegrationCheckStatus.NotAssessed, "",
                "No deserialization-failure evidence source for Wolverine listeners in this build; no failures observed is not claimed.", IntegrationEvidenceSource.ApplicationInsights);
    }
}
