using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>One row of a domain table: a platform check, one topic's check, or one check shared identically by several topics.</summary>
public sealed record IntegrationCheckRow(string Subject, IntegrationCheck Check, int TopicCount);

public sealed record IntegrationReviewHeadline(string Outcome, int Systems, int Topics, int DomainsAssessed, int DomainsTotal, int Findings, int NotAssessedChecks, string Freshness, string Sources, string Window);

/// <summary>
/// Presentation of an Integration Quality Review result. Reads only the result (and its own configuration snapshot), never the
/// current catalog, so a historical run always shows what it reviewed. "Not assessed" is never rendered as zero.
/// </summary>
public static class IntegrationReviewResultPresentation
{
    public static readonly string[] Tabs =
        ["Overview", "Configuration", "Connectivity", "Contracts", "Message flow", "Reliability", "Error handling", "Security", "Observability", "Performance", "Data quality", "Findings"];

    public static IntegrationReviewDomain? DomainOf(string tab) => tab switch
    {
        "Configuration" => IntegrationReviewDomain.Configuration,
        "Connectivity" => IntegrationReviewDomain.Connectivity,
        "Contracts" => IntegrationReviewDomain.Contract,
        "Message flow" => IntegrationReviewDomain.MessageFlow,
        "Reliability" => IntegrationReviewDomain.Reliability,
        "Error handling" => IntegrationReviewDomain.ErrorHandling,
        "Security" => IntegrationReviewDomain.Security,
        "Observability" => IntegrationReviewDomain.Observability,
        "Performance" => IntegrationReviewDomain.Performance,
        "Data quality" => IntegrationReviewDomain.DataQuality,
        _ => null,
    };

    public static IntegrationReviewHeadline Headline(IntegrationReviewResult result)
    {
        var checks = result.AllChecks.ToList();
        return new(IntegrationReviewLabels.Outcome(result.Outcome), result.Systems.Count, result.TopicsReviewed,
            result.Domains.Count(d => d.ChecksAssessed > 0), result.Domains.Count, result.Findings.Count,
            checks.Count(c => !IntegrationReviewLabels.IsAssessed(c.Status)),
            result.Freshness switch
            {
                IntegrationEvidenceFreshness.ConfigurationOnly => "Configuration only — no runtime evidence",
                IntegrationEvidenceFreshness.Historical => "Historical",
                IntegrationEvidenceFreshness.Mixed => "Mixed",
                _ => $"Current (captured {result.CompletedAt:yyyy-MM-dd HH:mm} UTC)",
            },
            string.Join(", ", result.EvidenceSources.Select(IntegrationReviewLabels.Source)),
            Window(result));
    }

    /// <summary>The runtime review window the result was measured over; stated, never implied.</summary>
    public static string Window(IntegrationReviewResult result) =>
        result.ReviewWindowHours is { } hours ? $"Last {hours} h before {result.CompletedAt:yyyy-MM-dd HH:mm} UTC" : "Not recorded (configuration and probe evidence only)";

    /// <summary>Freshness of one check's evidence. Configuration and contract checks have no source time, so "—" rather than a guess.</summary>
    public static string Freshness(IntegrationCheck check) =>
        check.Provenance is IntegrationEvidenceSource.Configuration or IntegrationEvidenceSource.ContractArtifact ? "—"
        : check.SourceTimestamp is { } at ? $"{IntegrationReviewLabels.ItemFreshness(check.Freshness)} · {at:yyyy-MM-dd HH:mm} UTC"
        : IntegrationReviewLabels.IsAssessed(check.Status) ? $"Captured {check.CapturedAt:yyyy-MM-dd HH:mm} UTC" : "—";

    public static string AdapterTone(IntegrationEvidenceState state) => state switch
    {
        IntegrationEvidenceState.Available => "ready",
        IntegrationEvidenceState.NotConfigured or IntegrationEvidenceState.NotSupported => "muted",
        IntegrationEvidenceState.Stale or IntegrationEvidenceState.NotFound => "attention",
        _ => "fail",
    };

    /// <summary>"2 of 5 available" — pre-run and post-run evidence coverage in one phrase.</summary>
    public static string AdapterSummary(IReadOnlyCollection<IntegrationEvidenceAdapterStatus> adapters) =>
        adapters.Count == 0 ? "No runtime evidence sources" : $"{adapters.Count(a => a.State == IntegrationEvidenceState.Available)} of {adapters.Count} available";

    /// <summary>
    /// A domain's rows: platform checks once, then topic checks — where every topic of a system has the same check with the same
    /// status, evidence and explanation, one row states it for all of them.
    /// </summary>
    public static IReadOnlyList<IntegrationCheckRow> Rows(IntegrationReviewResult result, IntegrationReviewDomain domain)
    {
        var rows = new List<IntegrationCheckRow>();
        foreach (var system in result.Systems)
        {
            var platformName = result.ConfigurationSnapshot.Platforms.FirstOrDefault(p => p.Id == system.PlatformId)?.Name ?? system.SystemName;
            rows.AddRange(system.PlatformChecks.Where(c => c.Domain == domain).Select(c => new IntegrationCheckRow(platformName, c, 0)));
            var topicChecks = system.Topics.SelectMany(t => t.Checks.Where(c => c.Domain == domain).Select(c => (Topic: t, Check: c))).ToList();
            foreach (var group in topicChecks.GroupBy(x => x.Check.CheckId))
            {
                var items = group.ToList();
                var identical = items.GroupBy(x => (x.Check.Status, x.Check.Evidence, x.Check.Explanation)).ToList();
                if (items.Count > 1 && identical.Count == 1)
                    rows.Add(new IntegrationCheckRow($"All {items.Count} topics · {system.SystemName}", items[0].Check, items.Count));
                else
                    rows.AddRange(items.Select(x => new IntegrationCheckRow(x.Topic.DisplayName, x.Check, 1)));
            }
        }
        return rows;
    }

    public static string StatusTone(IntegrationCheckStatus status) => status switch
    {
        IntegrationCheckStatus.Pass => "pass",
        IntegrationCheckStatus.Fail => "fail",
        IntegrationCheckStatus.Warning => "warning",
        IntegrationCheckStatus.Observed or IntegrationCheckStatus.NoIndicatorsObserved or IntegrationCheckStatus.NoRecentEvidence => "observed",
        IntegrationCheckStatus.NeedsConfirmation => "attention",
        _ => "muted",
    };

    public static string ReadinessTone(IntegrationDomainReadiness readiness) => readiness switch
    {
        IntegrationDomainReadiness.Ready or IntegrationDomainReadiness.Available => "ready",
        IntegrationDomainReadiness.Partial => "observed",
        IntegrationDomainReadiness.Limited => "attention",
        _ => "muted",
    };

    public static string DomainTone(IntegrationDomainResult domain) =>
        domain.Findings > 0 ? "fail" : domain.StateLabel == "Assessed" ? "pass" : domain.StateLabel == "Partially assessed" ? "attention" : "muted";

    // ── Event Hub runtime evidence (the run's snapshot; never the current catalog, never re-read) ─────────────────────────

    private static string Number(double value) => value.ToString(Math.Abs(value % 1) < 0.0001 ? "N0" : "N1", System.Globalization.CultureInfo.InvariantCulture);

    public static string ComparisonTone(EventHubComparisonState state) => state switch
    {
        EventHubComparisonState.ObservedMatch => "observed",
        EventHubComparisonState.DifferenceObserved or EventHubComparisonState.MissingInAzure => "warning",
        EventHubComparisonState.NotAuthorized => "attention",
        _ => "muted",
    };

    public sealed record HubRow(string Hub, string Kind, string Configured, string Observed, EventHubComparisonState State);

    /// <summary>Configured business hubs first, then additional observed, then technical/support hubs.</summary>
    public static IReadOnlyList<HubRow> HubRows(EventHubRuntimeSnapshot snapshot) =>
        snapshot.Hubs.OrderBy(h => h.Technical ? 2 : h.IntegrationId is null ? 1 : 0).ThenBy(h => h.Hub, StringComparer.Ordinal)
            .Select(h => new HubRow(h.Hub, h.Technical ? "Technical / support" : h.IntegrationId is null ? "Not configured" : "Business",
                h.IntegrationId is null ? "—" : Properties(null, h.ConfiguredPartitions, h.ConfiguredRetentionHours),
                h.State switch
                {
                    EventHubComparisonState.MissingInAzure => "Not found",
                    EventHubComparisonState.NotAuthorized or EventHubComparisonState.NotAssessed => "Not read",
                    _ => Properties(h.ObservedStatus, h.ObservedPartitions, h.ObservedRetentionHours),
                }, h.State)).ToList();

    private static string Properties(string? status, int? partitions, long? retentionHours)
    {
        var parts = new[] { status, partitions is { } p ? $"{p} partition{(p == 1 ? "" : "s")}" : null, retentionHours is { } h ? $"{h} h retention" : null }.OfType<string>().ToList();
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>A runtime source this run did not read because the instance does not call Azure: "Not read", never "Not configured" (the source may well be configured).</summary>
    private static string RuntimeState(EventHubRuntimeSnapshot snapshot, IntegrationEvidenceState state) =>
        state == IntegrationEvidenceState.Available ? "Observed"
        : state == IntegrationEvidenceState.NotConfigured && !snapshot.AzureRuntimeEnabled ? "Not read (Azure runtime not enabled)"
        : IntegrationReviewLabels.EvidenceState(state);

    public static string NamespaceLine(EventHubRuntimeSnapshot snapshot) => snapshot.NamespaceObservation switch
    {
        null => "Not read in this run",
        { State: IntegrationEvidenceState.Available } ns => "Observed match" + string.Concat(new[] { ns.Status, ns.Sku, ns.Location }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => $" · {v}"))
            + (ns.HubListState == IntegrationEvidenceState.Available ? $" · {ns.Hubs.Count} hub(s) listed" : $" · {ns.HubListReason}"),
        var ns => $"{RuntimeState(snapshot, ns.State)} — {ns.Reason}",
    };

    /// <summary>Consumer-group comparisons grouped: "Expected $Default (Configured assumption): Observed match on 16 of 16 hub(s) · Application mapping: Needs confirmation".</summary>
    public static IReadOnlyList<string> GroupLines(EventHubRuntimeSnapshot snapshot) =>
        snapshot.ConsumerGroups.Count == 0 ? ["Not read in this run"]
        : snapshot.ConsumerGroups.GroupBy(g => (g.Expected, g.ExpectedProvenance, g.State, g.Mapping))
            .Select(g => $"{(g.Key.Expected is null ? "Expected: not configured" : $"Expected {g.Key.Expected} ({g.Key.ExpectedProvenance})")}: {EventHubComparisonLabels.State(g.Key.State)} on {g.Count()} of {snapshot.ConsumerGroups.Count} hub(s) · Application mapping: {g.Key.Mapping}")
            .ToList();

    /// <summary>Checkpoint configuration and checkpoint runtime evidence as two separate statements.</summary>
    public static IReadOnlyList<string> CheckpointLines(EventHubRuntimeSnapshot snapshot)
    {
        if (snapshot.Checkpoints.Count == 0) return ["Not read in this run"];
        var configuration = string.Join(", ", snapshot.Checkpoints.GroupBy(c => c.Configuration).Select(g => $"{g.Key} ({g.Count()} hub(s))"));
        var runtime = string.Join(", ", snapshot.Checkpoints.GroupBy(c => c.Runtime)
            .Select(g => $"{(g.Key is { } state ? RuntimeState(snapshot, state) : "Not assessed")} ({g.Count()} hub(s))"));
        var latest = snapshot.Checkpoints.Select(c => c.LastUpdated).Max();
        return [$"Checkpoint configuration: {configuration}", $"Checkpoint runtime evidence: {runtime}{(latest is { } at ? $" · latest checkpoint {at:yyyy-MM-dd HH:mm} UTC" : "")}"];
    }

    public static IReadOnlyList<(string Label, string Value)> MonitoringRows(EventHubRuntimeSnapshot snapshot) =>
    [
        ("Application Insights", snapshot.ApplicationInsights is { Length: > 0 } ai ? $"{ai} — Configured" : "Not configured"),
        ("Runtime telemetry", RuntimeState(snapshot, snapshot.TelemetryState)),
        ("Container App logs", string.Equals(snapshot.ContainerAppsLogDestination, "azure-monitor", StringComparison.OrdinalIgnoreCase) ? "Azure Monitor" : snapshot.ContainerAppsLogDestination ?? "Not stated"),
        ("Dedicated Log Analytics workspace", snapshot.LogAnalytics),
    ];

    /// <summary>Namespace metrics as Observed values — never judged without a threshold.</summary>
    public static string MetricsLine(EventHubRuntimeSnapshot snapshot) => snapshot.Metrics switch
    {
        null => "Not read in this run",
        { State: IntegrationEvidenceState.Available } m => string.Join(" · ", new[]
        {
            ("Incoming", "IncomingMessages"), ("outgoing", "OutgoingMessages"), ("server errors", "ServerErrors"), ("user errors", "UserErrors"), ("throttled", "ThrottledRequests"),
        }.Select(x => $"{x.Item1} {(m.Total(x.Item2) is { } v ? Number(v) : "not returned")}")) + $" over {m.WindowHours} h — Observed, no threshold",
        var m => $"{RuntimeState(snapshot, m.State)} — {m.Reason}",
    };

    /// <summary>What a run executed / did not assess. Runs recorded before these lists existed say so rather than showing an empty list.</summary>
    public static bool ScopeRecorded(IntegrationReviewResult result) => result.WhatWasTested.Count > 0 || result.WhatWasNotAssessed.Count > 0;

    /// <summary>"3 of 16 checks assessed" — never "0 failures" for a domain nothing could assess.</summary>
    public static string Coverage(IntegrationDomainResult domain) =>
        domain.ChecksTotal == 0 ? "No checks" : $"{domain.ChecksAssessed} of {domain.ChecksTotal} checks assessed";
}
