using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Presentation of application-messaging (Wolverine) evidence. Source facts read as Detected / Configured / Available; runtime
/// processing is Not assessed until an Integration Quality Review run reads handler telemetry. Tones are cosmetic: every state is a word.
/// </summary>
public static class ApplicationMessagingPresentation
{
    public static string Tone(MessagingDetection detection) => detection switch
    {
        MessagingDetection.Confirmed => "info",
        MessagingDetection.Likely => "attention",
        _ => "muted",
    };

    public static string Tone(MessagingFactState state) => state switch
    {
        MessagingFactState.Detected or MessagingFactState.Configured or MessagingFactState.Available => "info",
        MessagingFactState.NotAssessable => "attention",
        _ => "muted",
    };

    /// <summary>Applications worth listing: Wolverine confirmed or likely, or explicitly bound by a person.</summary>
    public static IReadOnlyList<ApplicationMessagingEvidence> Listed(ApplicationMessagingEvidenceSet? set) =>
        set?.Applications.Where(a => a.Detection != MessagingDetection.NotDetected || a.BoundConsumer is not null).ToList() ?? [];

    public static IReadOnlyList<string> NotDetected(ApplicationMessagingEvidenceSet? set) =>
        set?.Applications.Where(a => a.Detection == MessagingDetection.NotDetected && a.BoundConsumer is null).Select(a => a.ApplicationId).ToList() ?? [];

    /// <summary>Consumer names a person can bind an application to: exactly the consumers configured in Integrations.</summary>
    public static IReadOnlyList<string> Consumers(IntegrationCatalog catalog) =>
        catalog.Integrations.Select(i => i.Consumer.DisplayName).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public static ApplicationMessagingEvidence? BoundTo(IntegrationDefinition definition, ApplicationMessagingEvidenceSet? set) =>
        definition.Consumer.DisplayName is { Length: > 0 } consumer ? set?.Applications.FirstOrDefault(a => a.BoundConsumer == consumer) : null;

    public static string Locations(MessagingFact fact) =>
        fact.Locations.Count == 0 ? IntegrationReviewLabels.Source(fact.Source) : $"{IntegrationReviewLabels.Source(fact.Source)}: {string.Join(", ", fact.Locations.Take(3).Select(l => $"{l.File}:{l.Line}"))}";

    public static string Route(MessagingRoute route) => route.Direction == MessagingRouteDirection.Publish
        ? $"{route.MessageType} → Azure Service Bus {route.EndpointKind.ToString().ToLowerInvariant()} {route.Endpoint}"
        : $"Azure Service Bus {route.EndpointKind.ToString().ToLowerInvariant()} {route.Endpoint}{(route.Topic is null ? "" : $" (topic {route.Topic})")} → {route.MessageType ?? "type decided at runtime"} → {(route.Handlers.Count == 0 ? "no handler" : string.Join(", ", route.Handlers))}";

    /// <summary>What the evidence means in one sentence — configuration is never presented as verified behaviour.</summary>
    public static string Meaning(ApplicationMessagingEvidence app) => app.Detection switch
    {
        MessagingDetection.NotDetected => "Wolverine is not configured in this application's source.",
        MessagingDetection.Likely => "Wolverine is probably used, but its registration is in source that was not analyzed.",
        _ when app.Handlers.Count == 0 => "Wolverine is configured for sending only; there is no Wolverine handler to observe at runtime.",
        _ => "Wolverine is configured, but runtime behaviour has not been verified.",
    };

    /// <summary>Pre-run supporting copy for an application summary (runtime state is configuration-only before the run).</summary>
    public static string PrerunCopy(ApplicationMessagingSummary summary) => summary.RuntimeState == IntegrationEvidenceState.Available
        ? "Source/build evidence is available. Runtime handler logs will be read during the review."
        : $"Source/build evidence is available. Runtime processing evidence is not yet available: {summary.RuntimeReason}";
}
