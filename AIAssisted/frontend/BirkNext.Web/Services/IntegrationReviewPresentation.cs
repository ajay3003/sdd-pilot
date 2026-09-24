using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pre-run state of one Integration Quality Review DOMAIN. Each value means one thing:
/// <list type="bullet">
/// <item><see cref="Included"/> — in scope, and every enabled integration has what the domain needs.</item>
/// <item><see cref="Limited"/> — in scope, but some enabled integrations lack its input.</item>
/// <item><see cref="NotAssessed"/> — in scope, but no enabled integration has its input yet (no contract, no runtime
/// traffic, no timing). Missing evidence, never a failure and never an exclusion.</item>
/// <item><see cref="Unavailable"/> — this build does not collect the domain's evidence for the enabled integration types
/// (for example messaging runtime telemetry).</item>
/// <item><see cref="NotIncluded"/> — a real exclusion: the domain does not apply to any enabled integration type.</item>
/// <item><see cref="AwaitingIntegration"/> — nothing is enabled, so there is no scope to judge a domain against.</item>
/// </list>
/// </summary>
public enum IntegrationDomainState { Included, Limited, NotAssessed, Unavailable, NotIncluded, AwaitingIntegration }

public static class IntegrationDomainStates
{
    public static string Label(IntegrationDomainState state) => state switch
    {
        IntegrationDomainState.Included => "Included",
        IntegrationDomainState.Limited => "Limited",
        IntegrationDomainState.NotAssessed => "Not assessed",
        IntegrationDomainState.Unavailable => "Unavailable",
        IntegrationDomainState.NotIncluded => "Not included",
        _ => "Waiting for integration",
    };

    public static string Tone(IntegrationDomainState state) => state switch
    {
        IntegrationDomainState.Included => "ready",
        IntegrationDomainState.Limited => "attention",
        _ => "muted",
    };
}

public sealed record IntegrationDomainCard(
    string Key,
    string Title,
    string Purpose,
    IntegrationDomainState State,
    string? Limitation);

/// <summary>What the next run covers: the ENABLED integrations. Configuration only.</summary>
public sealed record IntegrationScopeSummary(
    int Configured,
    int Enabled,
    int Complete,
    IReadOnlyList<(string Type, int Count)> ByType)
{
    public bool NothingConfigured => Configured == 0;
    public bool NothingEnabled => Enabled == 0;

    public string Headline => Enabled == 0
        ? "No integrations enabled for review"
        : $"{Enabled} integration{(Enabled == 1 ? "" : "s")} enabled for review";

    /// <summary>Configured-but-not-enabled, stated only when it is true (the Target card owns the configured total).</summary>
    public string? NotEnabledLine => Configured - Enabled is var off && off > 0 && Configured > 0
        ? Enabled == 0 ? $"{Configured} configured, none enabled" : $"{off} more configured but not enabled"
        : null;

    /// <summary>"2 Event Hub · 1 REST" — transports of the enabled integrations only.</summary>
    public string Transports => string.Join(" · ", ByType.Select(t => $"{t.Count} {t.Type}"));

    public string? IncompleteNote => Enabled - Complete is var missing && missing > 0
        ? $"{missing} enabled integration{(missing == 1 ? " has" : "s have")} missing required fields"
        : null;
}

/// <summary>
/// What evidence the review will have, per enabled integration. Every count uses the same rule the backend review uses,
/// so the pre-run page cannot promise evidence the run will not find:
/// <list type="bullet">
/// <item>Runtime: only REST/GraphQL traffic seen by the Local HTTPS Proxy, on the integration's origin and under its path.
/// Messaging runtime evidence is not collected in this build.</item>
/// <item>Timing: only observed request samples carrying a duration.</item>
/// <item>Contracts: the shared contract-metadata readiness rule; Kafka and RabbitMQ contract analysis is not implemented.</item>
/// <item>Drift: messaging message schemas only (REST/GraphQL drift is not resolved by this build), compared with the
/// previous snapshot the backend holds — which the page cannot see before a run.</item>
/// </list>
/// </summary>
public sealed record IntegrationEvidenceSummary(
    int Enabled,
    int HttpEnabled,
    int Observed,
    int Timed,
    int MessagingEnabled,
    int ContractEligible,
    int ContractReady,
    int ContractUnsupported,
    int DriftEligible,
    int RelationshipsRecorded,
    int RelationshipsOneSided)
{
    /// <summary>Card headline. With nothing enabled there is nothing to observe, which is not "no evidence".</summary>
    public string Headline =>
        Enabled == 0 ? "Not assessed yet"
        : HttpEnabled == 0 ? "Not collected for messaging"
        : Observed == 0 ? "No runtime evidence observed"
        : $"{Observed} of {HttpEnabled} observed";

    public string? MissingLine =>
        Enabled == 0 ? "No enabled integration to observe yet."
        : HttpEnabled - Observed is var missing && missing > 0
            ? $"{missing} REST/GraphQL integration{(missing == 1 ? " has" : "s have")} no runtime evidence observed"
            : null;

    /// <summary>A fact about this build, shown only when a messaging integration is in scope.</summary>
    public string? MessagingLine => MessagingEnabled == 0 ? null
        : $"Messaging runtime evidence is not collected in this build ({MessagingEnabled} messaging integration{(MessagingEnabled == 1 ? "" : "s")})";

    public const string MessagingNote = "No evidence is not the same as no traffic.";
}

public enum IntegrationReadinessLevel { Blocked, Limited, Ready }

public sealed record IntegrationReadinessSummary(
    IntegrationReadinessLevel Level,
    string Title,
    string Message,
    IReadOnlyList<string> Limitations,
    string? ActionText = null,
    string? ActionHref = null)
{
    public bool CanRun => Level is not IntegrationReadinessLevel.Blocked;
}

/// <summary>
/// Pure projection of the Integration Quality Review's pre-run state. Three layers stay apart: CONFIGURED SCOPE (what is
/// configured and enabled), EVIDENCE AVAILABILITY (what the run will have to work with) and ASSESSMENT (which only the run
/// produces). Configured is not observed, a contract is not compatibility, and an absent value is unknown, not zero.
/// </summary>
public static class IntegrationReviewPresentation
{
    public const string TargetEnvironmentsHref = "/admin/system-settings?section=target-environments";
    public const string ConfigureAction = "Configure integrations";
    public const string ConfigureDetailsAction = "Open Integrations configuration";

    /// <summary>
    /// Target Environment → Integrations for one environment. Target Environment owns integration configuration; the review
    /// only links there. Opening it selects the environment for viewing — it does not activate it or save anything.
    /// </summary>
    public static string IntegrationsHref(string? profileId) => string.IsNullOrWhiteSpace(profileId)
        ? TargetEnvironmentsHref + "&tab=integrations"
        : TargetEnvironmentsHref + "&tab=integrations&profile=" + Uri.EscapeDataString(profileId);

    private static readonly IntegrationType[] Http = [IntegrationType.REST, IntegrationType.GraphQL];
    private static readonly IntegrationType[] Messaging = [IntegrationType.EventHub, IntegrationType.ServiceBus, IntegrationType.Kafka, IntegrationType.RabbitMQ];
    private static readonly IntegrationType[] ContractUnsupported = [IntegrationType.Kafka, IntegrationType.RabbitMQ];
    private static readonly IntegrationType[] SchemaDrift = [IntegrationType.EventHub, IntegrationType.ServiceBus, IntegrationType.Kafka];

    public static IntegrationScopeSummary Scope(IReadOnlyList<IntegrationConfig> integrations)
    {
        var enabled = integrations.Where(i => i.Enabled).ToList();
        var byType = enabled
            .GroupBy(i => IntegrationConfigPresenter.TypeLabel(i.Type))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Type: g.Key, Count: g.Count()))
            .ToList();

        return new IntegrationScopeSummary(
            Configured: integrations.Count,
            Enabled: enabled.Count,
            Complete: enabled.Count(IntegrationConfigPresenter.IsComplete),
            ByType: byType);
    }

    public static IntegrationEvidenceSummary Evidence(
        IReadOnlyList<IntegrationConfig> integrations,
        IReadOnlyList<ObservedNetworkEndpoint>? observations)
    {
        var enabled = integrations.Where(i => i.Enabled).ToList();
        var http = enabled.Where(i => Http.Contains(i.Type)).ToList();
        var matched = http.Select(i => Correlated(i, observations)).ToList();
        var eligible = enabled.Where(i => !ContractUnsupported.Contains(i.Type)).ToList();

        return new IntegrationEvidenceSummary(
            Enabled: enabled.Count,
            HttpEnabled: http.Count,
            Observed: matched.Count(m => m.Count > 0),
            Timed: matched.Count(m => m.Any(o => o.Samples.Any(s => s.DurationMs > 0))),
            MessagingEnabled: enabled.Count(i => Messaging.Contains(i.Type)),
            ContractEligible: eligible.Count,
            ContractReady: eligible.Count(i => ReadinessOf(i) == ContractMetadataReadiness.Ready),
            ContractUnsupported: enabled.Count - eligible.Count,
            DriftEligible: enabled.Count(i => SchemaDrift.Contains(i.Type)
                && (!string.IsNullOrWhiteSpace(i.ContractName) || i.ContractSourceType != ContractSourceType.Unknown)),
            RelationshipsRecorded: enabled.Count(i => !string.IsNullOrWhiteSpace(i.LogicalProducerService) && !string.IsNullOrWhiteSpace(i.LogicalConsumerService)),
            RelationshipsOneSided: enabled.Count(i => !string.IsNullOrWhiteSpace(i.LogicalProducerService) ^ !string.IsNullOrWhiteSpace(i.LogicalConsumerService)));
    }

    /// <summary>The shared readiness rule, computed on a copy so projecting never mutates configuration.</summary>
    private static ContractMetadataReadiness ReadinessOf(IntegrationConfig i)
    {
        var probe = new IntegrationConfig
        {
            Type = i.Type, LogicalProducerService = i.LogicalProducerService, LogicalConsumerService = i.LogicalConsumerService,
            ContractName = i.ContractName, ContractSourceType = i.ContractSourceType, ContractSourceLocation = i.ContractSourceLocation,
        };
        probe.ComputeReadiness();
        return probe.ContractMetadataReadiness;
    }

    /// <summary>
    /// The backend's correlation (RuntimeEvidencePopulationService): proxy-observed REST/GraphQL traffic on the same
    /// scheme, host and port, under the integration's configured path. Never by display name.
    /// </summary>
    private static List<ObservedNetworkEndpoint> Correlated(IntegrationConfig integration, IReadOnlyList<ObservedNetworkEndpoint>? observations)
    {
        if (observations is null or { Count: 0 } || string.IsNullOrWhiteSpace(integration.Endpoint)
            || !Uri.TryCreate(integration.Endpoint.Trim(), UriKind.Absolute, out var configured))
            return [];
        var configuredPath = configured.AbsolutePath.TrimEnd('/');
        return observations
            .Where(NetworkEvidencePolicy.IsApiCandidate)
            .Where(o => o.Source == EndpointDiscoverySource.AuthenticatedProxyTraffic)
            .Where(o => o.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl)
            .Where(o => string.Equals(configured.Scheme, o.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(configured.Host, o.Host, StringComparison.OrdinalIgnoreCase)
                && configured.Port == o.Port)
            .Where(o =>
            {
                if (configuredPath is "" or "/") return true;
                var observed = (o.Path ?? "").TrimEnd('/');
                return observed.Equals(configuredPath, StringComparison.Ordinal)
                    || observed.StartsWith(configuredPath + "/", StringComparison.Ordinal);
            })
            .ToList();
    }

    /// <summary>
    /// The one Run rule (the page's own gate): at least one ENABLED integration. Contracts and runtime evidence are never
    /// prerequisites; their absence limits the review.
    /// </summary>
    public static IntegrationReadinessSummary Readiness(IntegrationScopeSummary scope, IntegrationEvidenceSummary evidence, string? profileId = null)
    {
        var href = IntegrationsHref(profileId);
        if (scope.NothingConfigured)
            return new(IntegrationReadinessLevel.Blocked, "Review cannot start",
                "No integrations are configured for this Target Environment. Configure one and enable it for review.",
                [], ConfigureAction, href);
        if (scope.NothingEnabled)
            return new(IntegrationReadinessLevel.Blocked, "Review cannot start",
                $"None of the {scope.Configured} configured integration{(scope.Configured == 1 ? " is" : "s is")} enabled for review.",
                [], ConfigureAction, href);

        var limitations = new List<string>();
        if (evidence.MissingLine is { } missing) limitations.Add(missing);
        if (evidence.MessagingLine is { } messaging) limitations.Add(messaging);
        if (evidence.ContractEligible - evidence.ContractReady is var noContract && noContract > 0)
            limitations.Add($"{noContract} integration{(noContract == 1 ? " has" : "s have")} no contract source configured");
        if (evidence.ContractUnsupported > 0)
            limitations.Add($"Contract analysis is not supported for {evidence.ContractUnsupported} Kafka/RabbitMQ integration{(evidence.ContractUnsupported == 1 ? "" : "s")}");
        if (scope.IncompleteNote is { } incomplete) limitations.Add(incomplete);

        var reviewed = $"{scope.Enabled} enabled integration{(scope.Enabled == 1 ? "" : "s")} will be reviewed.";
        return limitations.Count == 0
            ? new(IntegrationReadinessLevel.Ready, "Ready to review", reviewed, [])
            : new(IntegrationReadinessLevel.Limited, "Review can run with limitations",
                $"{reviewed} {limitations.Count} limitation{(limitations.Count == 1 ? "" : "s")} on the available evidence.",
                limitations, ConfigureAction, href);
    }

    /// <summary>
    /// The six review domains, each judged by its own prerequisite. With nothing enabled every domain waits for scope —
    /// it is not "excluded", there is simply nothing to review yet.
    /// </summary>
    public static IReadOnlyList<IntegrationDomainCard> Domains(IntegrationScopeSummary scope, IntegrationEvidenceSummary e, bool previousReviewInSession = false)
    {
        const string relationshipsPurpose = "Which service produces each integration and which consumes it.";
        const string runtimePurpose = "Which configured integrations were actually observed running.";
        const string contractsPurpose = "Published contracts and schemas for the configured integrations.";
        const string compatibilityPurpose = "Whether retrieved contract evidence shows a breaking change.";
        const string driftPurpose = "Structural change against the baseline recorded by a previous review.";
        const string performancePurpose = "Timing observed in runtime evidence for the configured integrations.";

        if (scope.NothingEnabled)
            return
            [
                new("relationships", "Relationships", relationshipsPurpose, IntegrationDomainState.AwaitingIntegration, null),
                new("runtime", "Runtime evidence", runtimePurpose, IntegrationDomainState.AwaitingIntegration, null),
                new("contracts", "Contracts / schemas", contractsPurpose, IntegrationDomainState.AwaitingIntegration, null),
                new("compatibility", "Compatibility", compatibilityPurpose, IntegrationDomainState.AwaitingIntegration, null),
                new("drift", "Drift / history", driftPurpose, IntegrationDomainState.AwaitingIntegration, null),
                new("performance", "Performance", performancePurpose, IntegrationDomainState.AwaitingIntegration, null),
            ];

        string Of(int n, int total, string noun) => $"{n} of {total} {noun}{(total == 1 ? "" : "s")}";

        // Relationships: configuration metadata only — never runtime evidence, never inferred from names.
        var relationships = e.RelationshipsRecorded == e.Enabled
            ? new IntegrationDomainCard("relationships", "Relationships", relationshipsPurpose, IntegrationDomainState.Included, null)
            : new IntegrationDomainCard("relationships", "Relationships", relationshipsPurpose,
                e.RelationshipsRecorded > 0 ? IntegrationDomainState.Limited : IntegrationDomainState.NotAssessed,
                $"{Of(e.RelationshipsRecorded, e.Enabled, "enabled integration")} have both producer and consumer recorded"
                    + (e.RelationshipsOneSided > 0 ? $"; {e.RelationshipsOneSided} have one side only." : ".")
                    + " Unknown ownership is not inferred from names, paths or resources.");

        // Runtime: observed REST/GraphQL traffic only.
        var runtime = e.HttpEnabled == 0
            ? new IntegrationDomainCard("runtime", "Runtime evidence", runtimePurpose, IntegrationDomainState.Unavailable,
                "Runtime evidence comes from observed REST and GraphQL traffic; messaging runtime evidence is not collected in this build.")
            : e.Observed == 0
                ? new IntegrationDomainCard("runtime", "Runtime evidence", runtimePurpose, IntegrationDomainState.NotAssessed,
                    $"No runtime evidence observed yet. {IntegrationEvidenceSummary.MessagingNote}")
                : e.Observed < e.HttpEnabled || e.MessagingEnabled > 0
                    ? new IntegrationDomainCard("runtime", "Runtime evidence", runtimePurpose, IntegrationDomainState.Limited,
                        $"{Of(e.Observed, e.HttpEnabled, "REST/GraphQL integration")} observed."
                            + (e.MessagingEnabled > 0 ? " Messaging runtime evidence is not collected." : ""))
                    : new IntegrationDomainCard("runtime", "Runtime evidence", runtimePurpose, IntegrationDomainState.Included, null);

        // Contracts: a configured contract source (shared readiness rule). Transport metadata is not a schema.
        var contracts = e.ContractEligible == 0
            ? new IntegrationDomainCard("contracts", "Contracts / schemas", contractsPurpose, IntegrationDomainState.NotIncluded,
                "Contract analysis is not supported for Kafka or RabbitMQ integrations.")
            : e.ContractReady == e.ContractEligible && e.ContractUnsupported == 0
                ? new IntegrationDomainCard("contracts", "Contracts / schemas", contractsPurpose, IntegrationDomainState.Included,
                    "Contract sources are configured; retrieval is attempted during review.")
                : new IntegrationDomainCard("contracts", "Contracts / schemas", contractsPurpose, IntegrationDomainState.Limited,
                    e.ContractReady == 0
                        ? "No contract source is configured. Transport configuration is not a schema."
                        : $"{Of(e.ContractReady, e.Enabled, "integration")} have a contract source configured.");

        // Compatibility: needs contract evidence that can be retrieved and compared.
        var compatibility = e.ContractEligible == 0
            ? new IntegrationDomainCard("compatibility", "Compatibility", compatibilityPurpose, IntegrationDomainState.NotIncluded,
                "Contract comparison is not supported for Kafka or RabbitMQ integrations.")
            : e.ContractReady == 0
                ? new IntegrationDomainCard("compatibility", "Compatibility", compatibilityPurpose, IntegrationDomainState.NotAssessed,
                    "Insufficient contract evidence: no contract source is configured to compare.")
                : new IntegrationDomainCard("compatibility", "Compatibility", compatibilityPurpose,
                    e.ContractReady == e.ContractEligible && e.ContractUnsupported == 0 ? IntegrationDomainState.Included : IntegrationDomainState.Limited,
                    $"Compared for {Of(e.ContractReady, e.Enabled, "integration")} once the contract is retrieved. A configured contract is not a compatibility result.");

        // Drift: messaging message schemas against the previous snapshot; the baseline is resolved at run.
        var drift = e.MessagingEnabled == 0
            ? new IntegrationDomainCard("drift", "Drift / history", driftPurpose, IntegrationDomainState.Unavailable,
                "Drift is compared for messaging schemas only; REST and GraphQL drift is not assessed in this build.")
            : e.DriftEligible == 0
                ? new IntegrationDomainCard("drift", "Drift / history", driftPurpose, IntegrationDomainState.NotAssessed,
                    "No message schema is configured to compare against a baseline.")
                : new IntegrationDomainCard("drift", "Drift / history", driftPurpose,
                    previousReviewInSession && e.DriftEligible == e.MessagingEnabled ? IntegrationDomainState.Included : IntegrationDomainState.Limited,
                    previousReviewInSession
                        ? "Compared with the baseline recorded by the previous review. No change since a baseline is not compatibility."
                        : "Compared with a previous baseline if one exists; a first review records the baseline, which is not the same as no drift.");

        // Performance: real timing samples only — never configuration, reachability or a zero.
        var performance = e.HttpEnabled == 0
            ? new IntegrationDomainCard("performance", "Performance", performancePurpose, IntegrationDomainState.Unavailable,
                "Timing comes from observed REST and GraphQL traffic; messaging timing is not collected in this build.")
            : e.Observed == 0
                ? new IntegrationDomainCard("performance", "Performance", performancePurpose, IntegrationDomainState.NotAssessed,
                    "No timing evidence: no runtime traffic observed. Configured timeouts and reachability are not performance evidence.")
                : e.Timed == 0
                    ? new IntegrationDomainCard("performance", "Performance", performancePurpose, IntegrationDomainState.NotAssessed,
                        "Runtime traffic was observed, but no timing samples were recorded.")
                    : e.Timed < e.HttpEnabled || e.MessagingEnabled > 0
                        ? new IntegrationDomainCard("performance", "Performance", performancePurpose, IntegrationDomainState.Limited,
                            $"Timing available for {Of(e.Timed, e.HttpEnabled, "REST/GraphQL integration")}.")
                        : new IntegrationDomainCard("performance", "Performance", performancePurpose, IntegrationDomainState.Included, null);

        return [relationships, runtime, contracts, compatibility, drift, performance];
    }
}
