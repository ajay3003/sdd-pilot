using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Scope state of one Integration Quality Review DOMAIN, in the same vocabulary the other reviews use.
/// Deliberately NOT configuration or connection vocabulary: "Configured", "Connected" and "Enabled"
/// describe a setting or a transport, never whether a domain is part of the review.
/// </summary>
public enum IntegrationDomainState { Included, Limited, PartialEvidence, NotIncluded }

public static class IntegrationDomainStates
{
    public static string Label(IntegrationDomainState state) => state switch
    {
        IntegrationDomainState.Included => "Included",
        IntegrationDomainState.Limited => "Limited",
        IntegrationDomainState.PartialEvidence => "Partial evidence",
        _ => "Not included",
    };

    public static string Tone(IntegrationDomainState state) => state switch
    {
        IntegrationDomainState.Included => "ready",
        IntegrationDomainState.NotIncluded => "muted",
        _ => "attention",
    };
}

public sealed record IntegrationDomainCard(
    string Key,
    string Title,
    string Purpose,
    IntegrationDomainState State,
    string? Limitation);

/// <summary>What the next run covers, counted from the CONFIGURED integrations. Configuration only.</summary>
public sealed record IntegrationScopeSummary(
    int Configured,
    int Enabled,
    int Complete,
    IReadOnlyList<(string Type, int Count)> ByType)
{
    public string Headline => Configured == 0
        ? "No integrations configured"
        : $"{Configured} integration{(Configured == 1 ? "" : "s")} configured";

    public string EnabledLine => $"{Enabled} enabled for review";

    /// <summary>"6 Event Hub · 2 Service Bus · 1 REST" — only the transports actually present.</summary>
    public string Transports => string.Join(" · ", ByType.Select(t => $"{t.Count} {t.Type}"));

    /// <summary>Stated only when true; a fully described scope says nothing about incompleteness.</summary>
    public string? IncompleteNote => Enabled - Complete is var missing && missing > 0
        ? $"{missing} enabled integration{(missing == 1 ? " has" : "s have")} missing required fields"
        : null;
}

/// <summary>
/// What has actually been OBSERVED at runtime. Counted only from runtime observations — never from
/// configuration, reachability or a health check, none of which are evidence that an integration ran.
/// </summary>
public sealed record IntegrationRuntimeEvidenceSummary(int Observed, int EnabledWithoutEvidence, bool MessagingTelemetryAvailable)
{
    public string Headline => Observed == 0
        ? "No runtime evidence observed"
        : $"{Observed} integration{(Observed == 1 ? "" : "s")} observed";

    public string? MissingLine => EnabledWithoutEvidence == 0
        ? null
        : $"{EnabledWithoutEvidence} enabled integration{(EnabledWithoutEvidence == 1 ? " has" : "s have")} no runtime evidence";

    /// <summary>Absence of messaging telemetry is a limit on what can be known, never proof of silence.</summary>
    public const string MessagingNote = "Messaging activity cannot be assessed without telemetry; no evidence is not the same as no traffic.";
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
/// Pure projection of the Integration Quality Review's pre-run state. Three separations this file
/// exists to keep:
///
/// <list type="bullet">
/// <item>CONFIGURED is not OBSERVED. A saved integration is a statement of intent; only a runtime
/// observation is evidence that it ran.</item>
/// <item>Transport metadata is not a schema. Knowing a queue's name says nothing about its contract.</item>
/// <item>An absent value is unknown, not zero. Missing telemetry never becomes "no traffic".</item>
/// </list>
/// </summary>
public static class IntegrationReviewPresentation
{
    public const string TargetEnvironmentsHref = "/admin/system-settings?section=target-environments";

    public static IntegrationScopeSummary Scope(IReadOnlyList<IntegrationConfig> integrations)
    {
        var enabled = integrations.Where(i => i.Enabled).ToList();
        var byType = integrations
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

    /// <summary>
    /// Runtime evidence, matched from observed traffic to configured integrations by host or
    /// resource. Only HTTP integrations can be matched this way — the observation stream is network
    /// traffic — so messaging integrations are reported as having no evidence rather than being
    /// credited with activity nothing observed.
    /// </summary>
    public static IntegrationRuntimeEvidenceSummary RuntimeEvidence(
        IReadOnlyList<IntegrationConfig> integrations,
        IReadOnlyList<ObservedNetworkEndpoint>? observations)
    {
        var enabled = integrations.Where(i => i.Enabled).ToList();
        var observed = observations is null or { Count: 0 }
            ? 0
            : enabled.Count(i => IntegrationConfigPresenter.IsHttpIntegration(i.Type) && HasObservation(i, observations));

        return new IntegrationRuntimeEvidenceSummary(
            Observed: observed,
            EnabledWithoutEvidence: enabled.Count - observed,
            // Nothing in this build collects messaging telemetry, so it is always reported unavailable
            // rather than silently treated as an absence of messages.
            MessagingTelemetryAvailable: false);
    }

    private static bool HasObservation(IntegrationConfig integration, IReadOnlyList<ObservedNetworkEndpoint> observations)
    {
        if (string.IsNullOrWhiteSpace(integration.Endpoint)) return false;
        return Uri.TryCreate(integration.Endpoint, UriKind.Absolute, out var uri)
            && observations.Any(o => string.Equals(o.Host, uri.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One review-level state. Blocked iff the review cannot start; Limited when it runs but some
    /// evidence it would use is unavailable. Incomplete configuration and absent runtime evidence
    /// are limitations, never failures.
    /// </summary>
    public static IntegrationReadinessSummary Readiness(
        IntegrationScopeSummary scope,
        IntegrationRuntimeEvidenceSummary runtime)
    {
        if (scope.Enabled == 0)
            return new(IntegrationReadinessLevel.Blocked, "Review cannot start",
                "At least one enabled integration is required.",
                ["No integration is enabled for review"],
                "Configure target environment", TargetEnvironmentsHref);

        var limitations = new List<string>();
        if (runtime.MissingLine is { } missing) limitations.Add(missing);
        if (!runtime.MessagingTelemetryAvailable) limitations.Add("Messaging runtime telemetry is unavailable in this environment");
        if (scope.IncompleteNote is { } incomplete) limitations.Add(incomplete);

        return limitations.Count == 0
            ? new(IntegrationReadinessLevel.Ready, "Ready to review",
                $"{scope.Enabled} enabled integration{(scope.Enabled == 1 ? "" : "s")} will be reviewed.", [])
            : new(IntegrationReadinessLevel.Limited, "Ready with limitations",
                runtime.MissingLine ?? limitations[0],
                limitations, "Manage integrations", TargetEnvironmentsHref);
    }

    /// <summary>
    /// The six review domains, in review-scope words. Incomplete relationship metadata limits the
    /// Relationships domain and never removes it; absent runtime evidence limits Runtime evidence
    /// and Performance rather than making either of them report zero.
    /// </summary>
    public static IReadOnlyList<IntegrationDomainCard> Domains(
        IReadOnlyList<IntegrationConfig> integrations,
        IntegrationScopeSummary scope,
        IntegrationRuntimeEvidenceSummary runtime)
    {
        var enabled = integrations.Where(i => i.Enabled).ToList();
        var nothing = scope.Enabled == 0;

        // Relationship metadata is authoritative only where BOTH sides were recorded. It is never
        // inferred from a name, a path or a topic.
        var authoritative = enabled.Count(i => !string.IsNullOrWhiteSpace(i.LogicalProducerService) && !string.IsNullOrWhiteSpace(i.LogicalConsumerService));
        var partial = enabled.Count(i => !string.IsNullOrWhiteSpace(i.LogicalProducerService) ^ !string.IsNullOrWhiteSpace(i.LogicalConsumerService));

        // Transport configuration is not a schema: an integration having a resource says nothing
        // about whether its contract can be read.
        var httpWithEndpoint = enabled.Count(i => IntegrationConfigPresenter.IsHttpIntegration(i.Type) && !string.IsNullOrWhiteSpace(i.Endpoint));

        IntegrationDomainState Scoped(bool present, bool limited, bool partialOnly = false) =>
            nothing || !present ? IntegrationDomainState.NotIncluded
            : partialOnly ? IntegrationDomainState.PartialEvidence
            : limited ? IntegrationDomainState.Limited
            : IntegrationDomainState.Included;

        return
        [
            new("relationships", "Relationships", "Which service produces each integration and which consumes it.",
                Scoped(true, authoritative < enabled.Count, authoritative == 0 && enabled.Count > 0),
                nothing ? null
                    : authoritative == enabled.Count ? null
                    : $"{authoritative} of {enabled.Count} enabled integrations have both producer and consumer recorded"
                        + (partial > 0 ? $"; {partial} have one side only." : ".")
                        + " Unknown ownership is not inferred from names, paths or resources."),

            new("runtime", "Runtime evidence", "Which configured integrations were actually observed running.",
                Scoped(true, false, runtime.Observed < scope.Enabled),
                nothing ? null : string.Join(" ", new[] { runtime.MissingLine is { } m ? m + "." : null, IntegrationRuntimeEvidenceSummary.MessagingNote }.Where(s => s is not null))),

            new("contracts", "Contracts / schemas", "Published contracts and schemas for the configured integrations.",
                Scoped(true, true, httpWithEndpoint == 0),
                nothing ? null : "Transport configuration exists for the configured integrations, but it is not a schema; schema evidence is unavailable for messaging integrations."),

            new("compatibility", "Compatibility", "Whether contract evidence that can be compared shows a breaking change.",
                Scoped(true, true, httpWithEndpoint == 0),
                nothing ? null : "Only contract evidence that can be retrieved and compared produces a compatibility result; anything else is reported as not comparable."),

            new("drift", "Drift / history", "Structural change against the baseline recorded by previous reviews.",
                Scoped(true, true),
                nothing ? null : "A first review records the baseline rather than comparing against one; that is not the same as no drift."),

            new("performance", "Performance", "Timing observed in runtime evidence for the configured integrations.",
                Scoped(true, false, runtime.Observed == 0),
                nothing ? null
                    : runtime.Observed == 0
                        ? "No runtime timing was observed, so no performance result is derived. Configured timeouts and reachability are not performance evidence."
                        : $"Timing is available for the {runtime.Observed} observed integration{(runtime.Observed == 1 ? "" : "s")}. Messaging performance evidence is unavailable without telemetry."),
        ];
    }
}
