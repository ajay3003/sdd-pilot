using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// The multi-stage source path inside the existing IQR domains. Source-defined ≠ configured ≠ observed in Azure ≠ processed: each Service Bus
/// entity keeps the four states apart, an outbox row is never delivery, a developer test is reused (never duplicated) and never "passed".
/// </summary>
public static class IqrPathReview
{
    public static int EvidenceCount(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots) => snapshots.Select(s => s.IntegrationPath).OfType<IntegrationPathEvidence>().Sum(p => domain switch
    {
        IntegrationReviewDomain.Contract => p.Boundaries.Count + p.Events.Count,
        IntegrationReviewDomain.MessageFlow => p.Hops.Count,
        IntegrationReviewDomain.Reliability => p.Rules.Count(r => r.Kind is "OutboxCreation" or "Dispatcher" or "Idempotency" or "DuplicateHandling" or "SessionId") + p.ChangeDetection.Count,
        IntegrationReviewDomain.Security => p.Minimization.SensitiveFieldsEntering.Count + p.Rules.Count(r => r.Kind is "Priority" or "EventPrivacy"),
        IntegrationReviewDomain.DataQuality => p.Fields.Count,
        IntegrationReviewDomain.Observability => p.Outbox?.EnvelopeFields.Count > 0 ? 1 : 0,
        IntegrationReviewDomain.ErrorHandling => p.Outbox?.Retry is { } retry && retry != "Not resolved" ? 1 : 0,
        _ => 0,
    });

    /// <summary>Per-domain Observed / Missing lines for a run, from the snapshots it used and the run's own Service Bus configuration and runtime evidence.</summary>
    public static (List<string> Observed, List<string> Missing) Domain(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots, IntegrationReviewResult result)
    {
        var observed = new List<string>();
        var missing = new List<string>();
        foreach (var p in snapshots.Select(s => s.IntegrationPath).OfType<IntegrationPathEvidence>())
        {
            string Covered(IEnumerable<SourceCoverageStatus> c) => c.Any(x => x is SourceCoverageStatus.DeveloperUnitCovered or SourceCoverageStatus.DeveloperIntegrationCovered or SourceCoverageStatus.DeveloperContractCovered)
                ? "developer test exists (reused)" : "source trace only";
            switch (domain)
            {
                case IntegrationReviewDomain.Contract:
                    foreach (var b in p.Boundaries)
                        observed.Add($"Contract boundary {b.From} → {b.To}: implementation contract {b.ImplementationContract}; formal schema {b.FormalSchema}; developer contract tests {b.DeveloperContractTests}");
                    foreach (var e in p.Events)
                        observed.Add($"Event contract {e.EventType} ({string.Join(", ", e.Topics)}): implementation contract from source; formal schema {e.FormalSchema}; {Covered(e.Coverage)}");
                    if (p.Boundaries.Any(b => b.DeveloperTestIds.Count == 0)) missing.Add("No developer contract test spans the adapter → ingestion boundary; the source trace is the only cross-boundary evidence");
                    missing.Add("Formal schemas are not available for the source-derived contracts");
                    break;
                case IntegrationReviewDomain.MessageFlow:
                    foreach (var h in p.Hops)
                        observed.Add($"{h.From} → {h.To}: {h.SourceState}; {Covered(h.Coverage)}; runtime {h.RuntimeState}");
                    observed.AddRange(ServiceBusStates(p, result));
                    missing.Add("Outbox → Service Bus delivery and subscriber processing are not observed by source analysis");
                    break;
                case IntegrationReviewDomain.Reliability:
                    foreach (var r in p.Rules.Where(r => r.Kind is "OutboxCreation" or "Dispatcher" or "Idempotency" or "DuplicateHandling" or "SessionId"))
                        observed.Add($"{r.Title}: {r.BirkNextAction}");
                    if (p.Outbox is { } o) observed.Add($"Outbox transaction: {o.Transaction}. Retry: {o.Retry}. Ordering: {o.Ordering}. Message id: {o.MessageId}");
                    foreach (var c in p.ChangeDetection) observed.Add($"Change detection {c.Input} → {c.Entity}: tracks {string.Join(", ", c.TrackedFields)}; {c.Gate}");
                    missing.AddRange(TopologyConsistency(p, result));
                    missing.AddRange(p.Gaps.Where(g => g.Title.Contains("change-tracked", StringComparison.Ordinal)).Select(g => $"{g.Title} — {g.Detail}"));
                    break;
                case IntegrationReviewDomain.Security:
                    var m = p.Minimization;
                    if (m.SensitiveFieldsEntering.Count > 0)
                        observed.Add($"Data minimization (field names only): {m.SensitiveFieldsEntering.Count} sensitive field(s) enter; emitted raw: {(m.EmittedRaw.Count == 0 ? "none found" : string.Join(", ", m.EmittedRaw))}; reduced to metadata: {string.Join(", ", m.ReducedMetadata.Take(6))}");
                    foreach (var r in p.Rules.Where(r => r.Kind is "Priority")) observed.Add($"{r.Title}: {r.BirkNextAction}");
                    missing.AddRange(m.PotentialExposures.Select(x => $"Potential data-minimization issue: {x}"));
                    missing.AddRange(m.InternalRawCopies.Select(x => $"Raw values retained internally: {x} (confirm intent and access control)"));
                    missing.Add("Data minimization is a separate control from authorization; neither is proven at runtime by source analysis");
                    break;
                case IntegrationReviewDomain.DataQuality:
                    var counts = p.Fields.SelectMany(f => f.Steps).GroupBy(s => s.Transformation).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
                    observed.Add($"Field traces: {p.Fields.Count} origin field(s); transformations {string.Join(", ", counts)} (structural, source-defined)");
                    missing.Add("Structural transformations do not establish business data correctness");
                    break;
                case IntegrationReviewDomain.Observability:
                    if (p.Outbox is { EnvelopeFields.Count: > 0 } ob) observed.Add($"Envelope {ob.Envelope} carries {string.Join(", ", ob.EnvelopeFields)} (source-defined identifiers for correlation)");
                    missing.Add("Correlation in deployed telemetry is runtime evidence");
                    break;
                case IntegrationReviewDomain.ErrorHandling:
                    if (p.Outbox is { } oe && oe.Retry != "Not resolved") observed.Add($"Outbox dispatcher failure handling: {oe.Retry}");
                    break;
            }
        }
        return (observed, missing);
    }

    /// <summary>For each Service Bus entity the source publishes to: source-defined, configured, observed in Azure and runtime processed — never merged.</summary>
    public static List<string> ServiceBusStates(IntegrationPathEvidence path, IntegrationReviewResult result)
    {
        var lines = new List<string>();
        if (path.ServiceBus is not { } sb) return lines;
        var topology = result.ConfigurationSnapshot.Platforms.Select(p => p.ServiceBusTopology).OfType<ServiceBusTopology>().ToList();
        var runtime = result.ServiceBusSnapshot.Select(s => s.Runtime).OfType<ServiceBusRuntimeEvidence>().ToList();
        foreach (var publication in sb.Publications)
        {
            var configured = topology.SelectMany(t => t.Topics.Concat(t.Queues)).FirstOrDefault(e => e.Name.Equals(publication.Entity, StringComparison.OrdinalIgnoreCase));
            var subscriptions = topology.SelectMany(t => t.Subscriptions).Where(s => string.Equals(s.Topic, publication.Entity, StringComparison.OrdinalIgnoreCase)).ToList();
            var observedEntity = runtime.Where(r => r.State == IntegrationEvidenceState.Available).SelectMany(r => r.Entities).FirstOrDefault(e => e.Name.Equals(publication.Entity, StringComparison.OrdinalIgnoreCase) && e.Topic is null);
            var runtimeState = runtime.Count == 0 ? "Not assessed (no Service Bus runtime read in this run)"
                : observedEntity is not null ? "Observed in Azure"
                : runtime.Any(r => r.State == IntegrationEvidenceState.Available) ? "Not found in the Azure metadata read" : $"Not assessed ({IntegrationReviewLabels.EvidenceState(runtime[0].State)})";
            lines.Add($"Service Bus {publication.Entity}: source-defined publisher ({string.Join(", ", publication.EventTypes.Take(4))}) · configured: {(configured is null ? "not in the configured topology" : $"yes, {subscriptions.Count} subscription(s){(subscriptions.Count == 0 ? "" : $" ({string.Join(", ", subscriptions.Select(s => s.Name))})")}")} · {runtimeState} · runtime processing: not assessed");
        }
        return lines;
    }

    /// <summary>Source behavior vs configured broker settings: a publisher's session id without session-enabled subscriptions, or resends without duplicate detection.</summary>
    public static List<string> TopologyConsistency(IntegrationPathEvidence path, IntegrationReviewResult result)
    {
        var lines = new List<string>();
        if (path.ServiceBus is not { } sb) return lines;
        var topology = result.ConfigurationSnapshot.Platforms.Select(p => p.ServiceBusTopology).OfType<ServiceBusTopology>().ToList();
        foreach (var publication in sb.Publications)
        {
            var topic = topology.SelectMany(t => t.Topics).FirstOrDefault(e => e.Name.Equals(publication.Entity, StringComparison.OrdinalIgnoreCase));
            var subscriptions = topology.SelectMany(t => t.Subscriptions).Where(s => string.Equals(s.Topic, publication.Entity, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!publication.SessionId.StartsWith("Not", StringComparison.Ordinal))
                foreach (var s in subscriptions.Where(s => s.RequiresSession == false))
                    lines.Add($"Cross-layer observation (needs confirmation): {publication.Entity} messages carry a session id ({publication.SessionId}), but configured subscription {s.Name} does not require sessions, so per-entity ordering is not enforced by the broker");
            if (topic?.RequiresDuplicateDetection == false && path.Outbox?.MessageId.Contains("reused on every send attempt", StringComparison.Ordinal) == true)
                lines.Add($"Cross-layer observation: duplicate detection is off for {publication.Entity}; a resend of the same outbox message id (after a failure between send and status update) reaches subscribers again, so subscribers must be idempotent");
        }
        return lines;
    }
}
