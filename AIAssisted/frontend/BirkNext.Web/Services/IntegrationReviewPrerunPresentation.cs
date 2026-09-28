using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of the backend's <see cref="IntegrationReviewReadiness"/> for the Integration Quality Review pre-run: what limits the
/// review, where to fix it, and what can be reviewed. Presentation only — domain readiness, headline and Run gate stay the backend's.
/// Corrective actions deep-link to Target Environment → Integrations; the pre-run never hosts configuration of its own.
/// </summary>
public static class IntegrationReviewPrerunPresentation
{
    public const string FocusMappings = "mappings";
    public const string FocusRuntime = "runtime";
    public const string FocusContracts = "contracts";

    public static string Href(string? profileId, string focus) => IntegrationReviewPresentation.IntegrationsHref(profileId) + "&focus=" + focus;

    /// <summary>Headline wording shared with the Run button: "Can run with limitations" reads as the action it permits.</summary>
    public static string ReadinessLabel(IntegrationReviewReadiness readiness) => readiness.Headline switch
    {
        "Can run with limitations" => "Run with limitations",
        var headline => headline,
    };

    public static string ReadinessTone(IntegrationReviewReadiness readiness) =>
        !readiness.CanRun ? "blocked" : readiness.Headline == "Ready" ? "ready" : "limited";

    /// <summary>Domains the backend marks anything but Not assessable (Ready, Available or Limited).</summary>
    public static int AssessableDomains(IntegrationReviewReadiness readiness) => readiness.Domains.Count(d => d.Readiness != IntegrationDomainReadiness.NotAssessable);

    /// <summary>Mappings without explicit confirmation: suggested plus unassigned/unconfirmed. Evidence strength never reduces this count.</summary>
    public static int UnconfirmedMappings(IntegrationReviewReadiness readiness) => readiness.Systems.Sum(s => s.ConsumersSuggested + s.ConsumersNeedingConfirmation);

    public static string Consumers(IntegrationSystemScope system) =>
        $"{system.ConsumersConfirmed} confirmed · {system.ConsumersSuggested} suggested · {system.ConsumersNeedingConfirmation} unconfirmed";

    public static string ConsumerGroups(IntegrationSystemScope system) =>
        system.ConsumerGroupsUnknown == 0 ? "Configured for all topics" : $"Not configured for {system.ConsumerGroupsUnknown} topic{(system.ConsumerGroupsUnknown == 1 ? "" : "s")}";

    public static string Contracts(IntegrationSystemScope system) =>
        system.ContractsConfigured == 0 ? "Not configured" : $"{system.ContractsConfigured} of {system.Topics} configured";

    // ── Runtime evidence ────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record RuntimeSource(string Name, string? Platform, IntegrationEvidenceState State, string Reason, DateTimeOffset CapturedAt);

    public static string SourceName(IntegrationEvidenceSource source) => source switch
    {
        IntegrationEvidenceSource.AzureMetadata => "Event Hub metadata",
        IntegrationEvidenceSource.AzureResourceManager => "Consumer groups",
        IntegrationEvidenceSource.CheckpointStore => "Checkpoints",
        IntegrationEvidenceSource.ApplicationInsights => "Application Insights",
        var other => IntegrationReviewLabels.Source(other),
    };

    public static IReadOnlyList<RuntimeSource> RuntimeSources(IntegrationReviewReadiness readiness)
    {
        var platforms = readiness.Systems.Select(s => s.PlatformName).Where(p => p is not null).Distinct().Count();
        return readiness.EvidenceAdapters.Select(a =>
        {
            // The backend labels each adapter "<adapter> · <platform>"; the platform is only worth repeating when there are several.
            var at = a.Adapter.LastIndexOf(" · ", StringComparison.Ordinal);
            var platform = platforms > 1 && at >= 0 ? a.Adapter[(at + 3)..] : null;
            return new RuntimeSource(SourceName(a.Source), platform, a.State, a.Reason, a.CapturedAt);
        }).ToList();
    }

    public static (int Available, int Total) RuntimeAvailability(IntegrationReviewReadiness readiness) =>
        (readiness.EvidenceAdapters.Count(a => a.State == IntegrationEvidenceState.Available), readiness.EvidenceAdapters.Count);

    /// <summary>The one reason shared by every unavailable source (typically the instance-level Azure gate), so it is stated once, not per source.</summary>
    public static string? CommonUnavailableReason(IntegrationReviewReadiness readiness)
    {
        var reasons = readiness.EvidenceAdapters.Where(a => a.State != IntegrationEvidenceState.Available).Select(a => a.Reason).Distinct().ToList();
        return reasons.Count == 1 ? reasons[0] : null;
    }

    // ── Needs attention ─────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record AttentionItem(string Key, string Label, string Value, string? ActionLabel, string? ActionHref);

    /// <summary>The few high-value limitations, each with the one place that fixes it. Only what currently limits the review is listed.</summary>
    public static IReadOnlyList<AttentionItem> NeedsAttention(IntegrationReviewReadiness readiness, string? profileId)
    {
        var items = new List<AttentionItem>();
        if (!readiness.CanRun) return items;
        var unconfirmed = UnconfirmedMappings(readiness);
        if (unconfirmed > 0)
            items.Add(new("mappings", "Mappings", $"{unconfirmed} need{(unconfirmed == 1 ? "s" : "")} confirmation", "Review mappings", Href(profileId, FocusMappings)));
        var (available, total) = RuntimeAvailability(readiness);
        if (total > 0 && available < total)
            items.Add(new("runtime", "Runtime evidence", $"{available} of {total} available", "Configure runtime evidence", Href(profileId, FocusRuntime)));
        var topics = readiness.Systems.Sum(s => s.Topics);
        var contracts = readiness.Systems.Sum(s => s.ContractsConfigured);
        if (topics > 0 && contracts < topics)
            items.Add(new("contracts", "Contracts", contracts == 0 ? "Not configured" : $"{contracts} of {topics} topics", "Manage contracts", Href(profileId, FocusContracts)));
        var groups = readiness.Systems.Where(s => s.Kind == IntegrationKind.EventHub).Sum(s => s.ConsumerGroupsUnknown);
        if (groups > 0)
            items.Add(new("groups", "Consumer groups", $"Not configured for {groups} topic{(groups == 1 ? "" : "s")}", null, null));
        // Service Bus is its own transport: its runtime metadata and route consistency are separate limitations (never Event Hub sources).
        foreach (var serviceBus in readiness.ServiceBus)
        {
            if (serviceBus.RoutesMismatched > 0)
                items.Add(new("servicebus-routes", "Service Bus routes", $"{serviceBus.RoutesMismatched} of {serviceBus.RoutesTotal} do not match the configured topology", "Review Service Bus", Href(profileId, ServiceBusPresentation.Focus)));
            if (serviceBus.RuntimeState != IntegrationEvidenceState.Available)
                items.Add(new("servicebus-runtime", "Service Bus runtime", IntegrationReviewLabels.EvidenceState(serviceBus.RuntimeState), "Configure Service Bus runtime evidence", Href(profileId, ServiceBusPresentation.Focus)));
        }
        return items;
    }

    // ── Mapping evidence (session) ──────────────────────────────────────────────────────────────────────────────────────

    public sealed record MappingEvidenceSummary(int Unconfirmed, int Tested, int Strong, int Partial, int NoSupport, int NotTestable, int Error)
    {
        public int NotTested => Unconfirmed - Tested;
    }

    /// <summary>
    /// Session mapping checks for mappings that are still unconfirmed in the CURRENT catalog. A check of a mapping that was later confirmed
    /// (or deleted, or disabled) is no longer counted; Strong evidence stays "not confirmed". Null when nothing was tested.
    /// </summary>
    public static MappingEvidenceSummary? MappingEvidence(IntegrationCatalog? catalog, IReadOnlyDictionary<string, IntegrationMappingEvidenceCheck> checks)
    {
        if (catalog is null || checks.Count == 0) return null;
        var unconfirmed = catalog.Integrations.Where(i => i.Enabled && i.Consumer.MappingState != ConsumerMappingState.Confirmed).ToList();
        var tested = unconfirmed.Select(i => checks.TryGetValue(i.Id, out var c) ? c : null).Where(c => c is not null).Select(c => c!.OverallState).ToList();
        if (tested.Count == 0) return null;
        int Count(IntegrationMappingEvidenceState state) => tested.Count(s => s == state);
        return new(unconfirmed.Count, tested.Count, Count(IntegrationMappingEvidenceState.StrongEvidence), Count(IntegrationMappingEvidenceState.PartialEvidence),
            Count(IntegrationMappingEvidenceState.NoSupportingEvidence), Count(IntegrationMappingEvidenceState.NotTestable), Count(IntegrationMappingEvidenceState.Error));
    }

    // ── Domain actions ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The corrective action for a limited or not-assessable domain, when one exists in Integrations. Never changes the domain state.</summary>
    public static (string Label, string Href)? DomainAction(IntegrationDomainReadinessRow row, IntegrationReviewReadiness readiness, string? profileId)
    {
        if (row.Readiness is IntegrationDomainReadiness.Ready or IntegrationDomainReadiness.Available) return null;
        var (available, total) = RuntimeAvailability(readiness);
        var runtimeMissing = total > 0 && available < total;
        return row.Domain switch
        {
            IntegrationReviewDomain.Contract or IntegrationReviewDomain.DataQuality => ("Manage contracts", Href(profileId, FocusContracts)),
            IntegrationReviewDomain.Configuration => ("Open Integrations", IntegrationReviewPresentation.IntegrationsHref(profileId)),
            IntegrationReviewDomain.Connectivity or IntegrationReviewDomain.MessageFlow or IntegrationReviewDomain.Reliability or IntegrationReviewDomain.ErrorHandling
                or IntegrationReviewDomain.Security or IntegrationReviewDomain.Observability or IntegrationReviewDomain.Performance when runtimeMissing
                => ("Configure runtime evidence", Href(profileId, FocusRuntime)),
            _ => null,
        };
    }
}
