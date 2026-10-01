using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>How one Event Hub runtime-evidence item stands for a platform. "Not required" is never a warning.</summary>
public enum RuntimeSourceStatus { Configured, NotConfigured, NotRequired, Assumption, Blocked }

/// <summary>One row of the flat Event Hub runtime-evidence readiness list (the form's "as entered" preview).</summary>
public sealed record RuntimeReadinessRow(string Key, string Label, RuntimeSourceStatus Status, string Value, string Detail);

/// <summary>
/// One of the four counted evidence sources (<see cref="EventHubRuntimeSources.Count"/>): what is configured, what BirkNext would read and
/// how. Configuration only — never an observation.
/// </summary>
public sealed record RuntimeEvidenceSourceView(string Key, string Label, RuntimeSourceStatus Status, string Value, IReadOnlyList<string> Reads, string Access, string? Note);

/// <summary>Whether this BirkNext instance can make Azure runtime calls at all (<c>IntegrationReview:Azure:Enabled</c>). Never about the sources.</summary>
public sealed record RuntimeExecutionView(bool Available, string Label, string Summary, string Detail);

/// <summary>The platform-level expected consumer group: an assumption for checkpoint lookups, never a confirmed consumer mapping.</summary>
public sealed record ExpectedConsumerGroupView(RuntimeSourceStatus Status, string Value, string? HowKnown, string Mapping, string? Note, string Caveat);

/// <summary>The review policy (window and optional thresholds). A policy, never an evidence source; no threshold means Observed only.</summary>
public sealed record RuntimePolicyView(int WindowHours, string LagThreshold, string CheckpointAgeThreshold, bool HasAnyThreshold, string Semantics)
{
    public string Summary => $"{WindowHours} h window · {(HasAnyThreshold ? "thresholds set" : "no thresholds")}";
}

/// <summary>
/// Pure presentation of an Event Hub platform's runtime-evidence settings, in four separate groups: runtime execution (whether this BirkNext
/// instance may call Azure), the four counted evidence sources, supporting settings (expected consumer group, Log Analytics workspace) and the
/// evaluation policy. Configuration only — nothing is contacted, and nothing here is ever "Observed".
/// </summary>
public static class EventHubRuntimeEvidencePresentation
{
    public const string IdentityText = "BirkNext uses its Azure identity for read-only metadata access. No key or connection string is stored here.";

    public static string StatusLabel(RuntimeSourceStatus status) => status switch
    {
        RuntimeSourceStatus.Configured => "Configured",
        RuntimeSourceStatus.NotRequired => "Not required",
        RuntimeSourceStatus.Assumption => "Assumption",
        RuntimeSourceStatus.Blocked => "Not available",
        _ => "Not configured",
    };

    public static string Tone(RuntimeSourceStatus status) => status switch
    {
        RuntimeSourceStatus.Configured => "ready",
        RuntimeSourceStatus.NotRequired => "muted",
        RuntimeSourceStatus.Assumption => "info",
        _ => "attention",
    };

    /// <summary>"Available" / "Not available": a capability of this instance. "Not configured" is reserved for a missing configuration item.</summary>
    public static string ExecutionStatus(bool azureEnabled) => azureEnabled ? "Available" : "Not available";

    public static string ExecutionBadge(bool azureEnabled) => $"Runtime execution: {ExecutionStatus(azureEnabled)}";

    /// <summary>Amber, never red: Azure execution being off is an instance setting, not an integration problem.</summary>
    public static string ExecutionTone(bool azureEnabled) => azureEnabled ? "ready" : "attention";

    public static RuntimeExecutionView Execution(bool azureEnabled) => azureEnabled
        ? new(true, "Available", "This BirkNext instance can read the configured sources with its Azure identity (read-only) when a review runs.",
            "Sources are read only while Integration Quality Review runs; what was observed is shown in the review result, never here.")
        : new(false, "Not available", "Azure access is not enabled for this BirkNext instance.",
            "IntegrationReview:Azure:Enabled is not true for this BirkNext instance, so no Azure call is made. This is an instance setting, not a problem with the integration; the configured sources are kept and used once it is enabled.");

    /// <summary>The four counted evidence sources: metadata, Resource Manager (hub list, consumer groups, metrics), checkpoints, telemetry.</summary>
    public static (int Configured, int Total) SourceCount(IntegrationPlatform platform) => EventHubRuntimeSources.Count(platform);

    public static (int Configured, int Total) SourceCount(IEnumerable<IntegrationPlatform> platforms) =>
        platforms.Select(SourceCount).Aggregate((0, 0), (sum, c) => (sum.Item1 + c.Configured, sum.Item2 + c.Total));

    /// <summary>"Sources configured: 4/4" — about evidence-source configuration only, never about whether Azure runs.</summary>
    public static string SourcesBadge(IEnumerable<IntegrationPlatform> platforms)
    {
        var (configured, total) = SourceCount(platforms);
        return total == 0 ? "No evidence sources" : $"Sources configured: {configured}/{total}";
    }

    public static string SourcesTone(IEnumerable<IntegrationPlatform> platforms)
    {
        var (configured, total) = SourceCount(platforms);
        return total > 0 && configured == total ? "ready" : "attention";
    }

    public static string SourcesHint(IntegrationPlatform platform)
    {
        var (configured, total) = SourceCount(platform);
        return configured == 0 ? "None configured" : $"{configured} of {total} configured";
    }

    public static IReadOnlyList<RuntimeEvidenceSourceView> Sources(IntegrationPlatform platform)
    {
        var r = platform.RuntimeEvidence;
        var metadata = EventHubRuntimeSources.Metadata(platform)
            ? new RuntimeEvidenceSourceView("metadata", "Event Hub metadata", RuntimeSourceStatus.Configured, platform.NamespaceFqdn!,
                ["Hub existence", "Partitions", "Last enqueued position"], "Read-only management operations.", null)
            : new RuntimeEvidenceSourceView("metadata", "Event Hub metadata", RuntimeSourceStatus.NotConfigured, "Not configured",
                ["Hub existence", "Partitions", "Last enqueued position"], "Read-only management operations.",
                string.IsNullOrWhiteSpace(platform.NamespaceFqdn) ? "The namespace FQDN is not configured." : "Reading Event Hub metadata is not enabled.");

        string[] armReads = ["Namespace and hub list", "Consumer groups of each hub (observed, never created)", "Namespace metrics (Azure Monitor)"];
        var groups = EventHubRuntimeSources.ResourceManager(platform)
            ? new RuntimeEvidenceSourceView("groups", "Consumer groups and hub list", RuntimeSourceStatus.Configured,
                $"{(string.IsNullOrWhiteSpace(r!.SubscriptionName) ? r.SubscriptionId!.Trim() : $"{r.SubscriptionName!.Trim()} ({r.SubscriptionId!.Trim()})")} · {platform.ResourceGroup}",
                armReads, "Azure Resource Manager GET requests only (Reader).", null)
            : new RuntimeEvidenceSourceView("groups", "Consumer groups and hub list", RuntimeSourceStatus.NotConfigured, "Not configured",
                armReads, "Azure Resource Manager GET requests only (Reader).", "Needs the Azure subscription id, and the namespace and its resource group on the platform.");

        string[] checkpointReads = ["Checkpoint blobs of the consumer group (listing)"];
        const string checkpointAccess = "Read-only listing (Storage Blob Data Reader); no checkpoint is written, leased or deleted.";
        var checkpoint = r?.ResolvedCheckpointContainerUrl() is { } container
            ? new RuntimeEvidenceSourceView("checkpoint", "Checkpoint store", RuntimeSourceStatus.Configured, container, checkpointReads, checkpointAccess,
                $"{IntegrationRuntimeEvidenceSettings.ProvenanceLabel(r.CheckpointProvenance)}{(string.IsNullOrWhiteSpace(r.CheckpointSourceNote) ? "." : $" — {r.CheckpointSourceNote}")}")
            : new RuntimeEvidenceSourceView("checkpoint", "Checkpoint store", RuntimeSourceStatus.NotConfigured, "Not configured", checkpointReads, checkpointAccess,
                "Needs the checkpoint Blob endpoint and container of the consumer.");

        string[] telemetryReads = ["Bounded aggregate telemetry queries over the review window"];
        var telemetry = r?.ApplicationInsightsResourceId() is not null
            ? new RuntimeEvidenceSourceView("appinsights", "Application Insights", RuntimeSourceStatus.Configured,
                $"{r.ApplicationInsightsResourceName!.Trim()} · {r.ApplicationInsightsResourceGroup!.Trim()}", telemetryReads,
                "Queried by resource. The connection string is never read or stored.",
                r.ApplicationInsightsConfigured == true ? "Application Insights configured on the consumer: yes." : null)
            : Guid.TryParse(r?.TelemetryWorkspaceId?.Trim(), out var workspace)
                // The workspace is the telemetry source here; the count's fourth source is telemetry, whichever way it is read.
                ? new RuntimeEvidenceSourceView("appinsights", "Telemetry", RuntimeSourceStatus.Configured, $"Log Analytics workspace {workspace:D}", telemetryReads,
                    "Workspace-schema queries. No key is read or stored.", null)
                : new RuntimeEvidenceSourceView("appinsights", "Application Insights", RuntimeSourceStatus.NotConfigured, "Not configured", telemetryReads,
                    "Queried by resource. The connection string is never read or stored.",
                    "Needs the Application Insights resource name and resource group (and the subscription id), or a Log Analytics workspace id.");

        return [metadata, groups, checkpoint, telemetry];
    }

    public static ExpectedConsumerGroupView ExpectedGroup(IntegrationRuntimeEvidenceSettings? r)
    {
        const string mapping = "Needs confirmation";
        if (string.IsNullOrWhiteSpace(r?.ExpectedConsumerGroup))
            return new(RuntimeSourceStatus.NotConfigured, "Not configured", null, mapping, null,
                "Without an expected group, topics without their own consumer group get no checkpoint lookup; no default group is assumed.");
        var provenance = r.ExpectedConsumerGroupProvenance is IntegrationValueProvenance.NotSpecified or IntegrationValueProvenance.ConfirmedByPerson or IntegrationValueProvenance.ConfiguredOnIntegration
            ? IntegrationValueProvenance.ConfiguredAssumption : r.ExpectedConsumerGroupProvenance;
        return new(RuntimeSourceStatus.Assumption, r.ExpectedConsumerGroup.Trim(), IntegrationRuntimeEvidenceSettings.ProvenanceLabel(provenance), mapping,
            string.IsNullOrWhiteSpace(r.ExpectedConsumerGroupNote) ? null : r.ExpectedConsumerGroupNote.Trim(),
            "Used for checkpoint lookups of topics without their own group. Seeing this group on a hub does not confirm which group a consumer reads with: never a confirmed mapping and never a Pass.");
    }

    public static RuntimeReadinessRow Workspace(IntegrationRuntimeEvidenceSettings? r) =>
        Guid.TryParse(r?.TelemetryWorkspaceId?.Trim(), out var workspace)
            ? new("workspace", "Log Analytics workspace", RuntimeSourceStatus.Configured, workspace.ToString("D"), "Optional; used for workspace-schema telemetry queries.")
            : new("workspace", "Log Analytics workspace", RuntimeSourceStatus.NotRequired, "None",
                string.Equals(r?.ContainerAppsLogDestination?.Trim(), "azure-monitor", StringComparison.OrdinalIgnoreCase)
                    ? "The Container Apps environment sends logs to Azure Monitor (no Log Analytics configuration); telemetry is read through Application Insights."
                    : "Telemetry is read through Application Insights; a workspace is optional and does not block any other check.");

    public static RuntimePolicyView Policy(IntegrationRuntimeEvidenceSettings? r)
    {
        var window = r?.ReviewWindowHours ?? IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours;
        var lag = r?.MaxConsumerLagEvents is { } l ? $"≤ {l} events" : "Not configured";
        var age = r?.MaxCheckpointAgeMinutes is { } a ? $"≤ {a} min" : "Not configured";
        var semantics = r?.MaxConsumerLagEvents is null && r?.MaxCheckpointAgeMinutes is null
            ? "Without thresholds, lag and checkpoint age are reported as Observed only — never Pass."
            : r?.MaxConsumerLagEvents is null || r.MaxCheckpointAgeMinutes is null
                ? "Without a threshold, lag or checkpoint age is reported as Observed only — never Pass. A configured threshold judges its measure (not for an assumed consumer group)."
                : "Lag and checkpoint age are judged against these thresholds (not for an assumed consumer group, which stays Observed only).";
        return new(window, lag, age, r?.MaxConsumerLagEvents is not null || r?.MaxCheckpointAgeMinutes is not null, semantics);
    }

    /// <summary>Flat preview of everything entered in the form, group by group (execution, sources, supporting settings, policy).</summary>
    public static IReadOnlyList<RuntimeReadinessRow> Readiness(IntegrationPlatform platform, bool azureEnabled)
    {
        var r = platform.RuntimeEvidence;
        var execution = Execution(azureEnabled);
        var group = ExpectedGroup(r);
        var policy = Policy(r);
        return
        [
            new("azure", "Runtime execution", azureEnabled ? RuntimeSourceStatus.Configured : RuntimeSourceStatus.Blocked, execution.Label, execution.Summary),
            .. Sources(platform).Select(s => new RuntimeReadinessRow(s.Key, s.Label, s.Status, s.Value, s.Note ?? s.Access)),
            new("expected-group", "Expected consumer group", group.Status,
                group.Status == RuntimeSourceStatus.Assumption ? $"{group.Value} · Mapping: {group.Mapping}" : group.Value, group.Caveat),
            Workspace(r),
            new("policy", "Evaluation policy", RuntimeSourceStatus.Configured,
                $"{policy.WindowHours} h window · lag threshold: {policy.LagThreshold} · checkpoint-age threshold: {policy.CheckpointAgeThreshold}", policy.Semantics),
        ];
    }

    /// <summary>One sentence after saving: how many evidence sources are configured, and — separately — whether runtime execution is available.</summary>
    public static string SavedSummary(IntegrationPlatform platform, bool azureEnabled)
    {
        var (configured, total) = SourceCount(platform);
        return $"Saved. Evidence sources: {configured} of {total} configured · {ExecutionBadge(azureEnabled)}"
            + (azureEnabled ? "." : " — nothing is read from Azure until IntegrationReview:Azure:Enabled is true for this instance.");
    }

    /// <summary>Splits a legacy container URL (https://acct.blob.core.windows.net/container) into endpoint + container for the form.</summary>
    public static (string? Endpoint, string? Container) SplitContainerUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query)) return (null, null);
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 || path.Contains('/') ? (null, null) : ($"{uri.Scheme}://{uri.Authority}/", path);
    }
}
