using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>How one Event Hub runtime-evidence source stands for a platform. "Not required" is never a warning.</summary>
public enum RuntimeSourceStatus { Configured, NotConfigured, NotRequired, Assumption, Blocked }

/// <summary>One row of the Event Hub runtime-evidence readiness summary.</summary>
public sealed record RuntimeReadinessRow(string Key, string Label, RuntimeSourceStatus Status, string Value, string Detail);

/// <summary>
/// Pure presentation of an Event Hub platform's runtime-evidence settings: which sources are configured, kept apart from whether this
/// BirkNext instance may call Azure at all (<c>IntegrationReview:Azure:Enabled</c>). Configuration only — nothing is contacted.
/// </summary>
public static class EventHubRuntimeEvidencePresentation
{
    public const string IdentityText = "BirkNext uses its Azure identity for read-only metadata access. No key or connection string is stored here.";

    public static string StatusLabel(RuntimeSourceStatus status) => status switch
    {
        RuntimeSourceStatus.Configured => "Configured",
        RuntimeSourceStatus.NotRequired => "Not configured — not required",
        RuntimeSourceStatus.Assumption => "Configured assumption",
        RuntimeSourceStatus.Blocked => "Not configured",
        _ => "Not configured",
    };

    public static string Tone(RuntimeSourceStatus status) => status switch
    {
        RuntimeSourceStatus.Configured => "ready",
        RuntimeSourceStatus.NotRequired => "muted",
        RuntimeSourceStatus.Assumption => "info",
        _ => "attention",
    };

    public static string ExecutionStatus(bool azureEnabled) => azureEnabled ? "Enabled" : "Not configured";

    /// <summary>The four evidence sources the review reads: metadata, Resource Manager (consumer groups, hub list, metrics), checkpoints, telemetry.</summary>
    public static (int Configured, int Total) SourceCount(IntegrationPlatform platform) => EventHubRuntimeSources.Count(platform);

    /// <summary>"Configured" / "Partially configured" / "Not configured" over the four sources (never about Azure execution).</summary>
    public static string SourcesStatus(IEnumerable<IntegrationPlatform> platforms)
    {
        var counts = platforms.Select(SourceCount).ToList();
        return counts.Count == 0 || counts.All(c => c.Configured == 0) ? "Not configured" : counts.All(c => c.Configured == c.Total) ? "Configured" : "Partially configured";
    }

    public static string SourcesHint(IntegrationPlatform platform)
    {
        var (configured, total) = SourceCount(platform);
        return configured == 0 ? "None configured" : $"{configured} of {total} configured";
    }

    private static bool GroupListConfigured(IntegrationPlatform platform) => EventHubRuntimeSources.ResourceManager(platform);

    public static IReadOnlyList<RuntimeReadinessRow> Readiness(IntegrationPlatform platform, bool azureEnabled)
    {
        var r = platform.RuntimeEvidence;
        var rows = new List<RuntimeReadinessRow>
        {
            azureEnabled
                ? new("azure", "Azure runtime execution", RuntimeSourceStatus.Configured, "Enabled", "This BirkNext instance may read the configured sources with its Azure identity (read-only).")
                : new("azure", "Azure runtime execution", RuntimeSourceStatus.Blocked, "Disabled for this BirkNext instance",
                    "IntegrationReview:Azure:Enabled is not true for this BirkNext instance, so no Azure call is made. The configured sources are kept and used once it is enabled."),
            r?.EventHubMetadata == true && !string.IsNullOrWhiteSpace(platform.NamespaceFqdn)
                ? new("metadata", "Event Hub metadata", RuntimeSourceStatus.Configured, platform.NamespaceFqdn!, "Hub existence, partitions and last enqueued position (management operations only).")
                : new("metadata", "Event Hub metadata", RuntimeSourceStatus.NotConfigured, "Not configured",
                    string.IsNullOrWhiteSpace(platform.NamespaceFqdn) ? "The namespace FQDN is not configured." : "Reading Event Hub metadata is not enabled."),
            GroupListConfigured(platform)
                ? new("groups", "Consumer-group list", RuntimeSourceStatus.Configured,
                    $"{(string.IsNullOrWhiteSpace(r!.SubscriptionName) ? r.SubscriptionId!.Trim() : $"{r.SubscriptionName!.Trim()} ({r.SubscriptionId!.Trim()})")} · {platform.ResourceGroup}",
                    "Azure Resource Manager GET of the hub's consumer groups (observed, never created).")
                : new("groups", "Consumer-group list", RuntimeSourceStatus.NotConfigured, "Not configured",
                    "Needs the Azure subscription id, and the namespace and its resource group on the platform."),
            ExpectedGroup(r),
            r?.ResolvedCheckpointContainerUrl() is { } container
                ? new("checkpoint", "Checkpoint store", RuntimeSourceStatus.Configured, container,
                    $"{IntegrationRuntimeEvidenceSettings.ProvenanceLabel(r.CheckpointProvenance)}{(string.IsNullOrWhiteSpace(r.CheckpointSourceNote) ? "" : $" — {r.CheckpointSourceNote}")} Read-only listing; no checkpoint is written, leased or deleted.")
                : new("checkpoint", "Checkpoint store", RuntimeSourceStatus.NotConfigured, "Not configured", "Needs the checkpoint Blob endpoint and container of the consumer."),
            r?.ApplicationInsightsResourceId() is not null
                ? new("appinsights", "Application Insights", RuntimeSourceStatus.Configured, $"{r.ApplicationInsightsResourceName!.Trim()} · {r.ApplicationInsightsResourceGroup!.Trim()}",
                    $"Queried by resource (bounded aggregate queries).{(r.ApplicationInsightsConfigured == true ? " Application Insights configured on the consumer: yes." : "")} The connection string is never read or stored.")
                : Guid.TryParse(r?.TelemetryWorkspaceId?.Trim(), out _)
                    ? new("appinsights", "Application Insights", RuntimeSourceStatus.NotRequired, "Queried through the Log Analytics workspace", "The workspace below is the telemetry source.")
                    : new("appinsights", "Application Insights", RuntimeSourceStatus.NotConfigured, "Not configured", "Needs the Application Insights resource name and resource group (and the subscription id)."),
            Guid.TryParse(r?.TelemetryWorkspaceId?.Trim(), out var workspace)
                ? new("workspace", "Log Analytics workspace", RuntimeSourceStatus.Configured, workspace.ToString("D"), "Optional; used for workspace-schema telemetry queries.")
                : new("workspace", "Log Analytics workspace", RuntimeSourceStatus.NotRequired, "None",
                    string.Equals(r?.ContainerAppsLogDestination?.Trim(), "azure-monitor", StringComparison.OrdinalIgnoreCase)
                        ? "The Container Apps environment sends logs to Azure Monitor (no Log Analytics configuration); telemetry is read through Application Insights."
                        : "Telemetry is read through Application Insights; a workspace is optional and does not block any other check."),
            Policy(r),
        };
        return rows;
    }

    private static RuntimeReadinessRow ExpectedGroup(IntegrationRuntimeEvidenceSettings? r)
    {
        if (string.IsNullOrWhiteSpace(r?.ExpectedConsumerGroup))
            return new("expected-group", "Expected consumer group", RuntimeSourceStatus.NotConfigured, "Not configured",
                "Without an expected group, topics without their own consumer group get no checkpoint lookup; no default group is assumed.");
        var provenance = r.ExpectedConsumerGroupProvenance is IntegrationValueProvenance.NotSpecified or IntegrationValueProvenance.ConfirmedByPerson or IntegrationValueProvenance.ConfiguredOnIntegration
            ? IntegrationValueProvenance.ConfiguredAssumption : r.ExpectedConsumerGroupProvenance;
        return new("expected-group", "Expected consumer group", RuntimeSourceStatus.Assumption,
            $"{r.ExpectedConsumerGroup.Trim()} — {IntegrationRuntimeEvidenceSettings.ProvenanceLabel(provenance)} · Mapping: Needs confirmation",
            $"Used for checkpoint lookups of topics without their own group; never a confirmed mapping and never a Pass.{(string.IsNullOrWhiteSpace(r.ExpectedConsumerGroupNote) ? "" : $" {r.ExpectedConsumerGroupNote.Trim()}")}");
    }

    private static RuntimeReadinessRow Policy(IntegrationRuntimeEvidenceSettings? r)
    {
        var window = r?.ReviewWindowHours ?? IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours;
        var lag = r?.MaxConsumerLagEvents is { } l ? $"lag ≤ {l} events" : "no lag threshold";
        var age = r?.MaxCheckpointAgeMinutes is { } a ? $"checkpoint ≤ {a} min" : "no checkpoint-age threshold";
        return new("policy", "Review policy", RuntimeSourceStatus.Configured, $"{window} h window · {lag} · {age}",
            r?.MaxConsumerLagEvents is null || r.MaxCheckpointAgeMinutes is null
                ? "Without a threshold, lag and checkpoint age are Observed only — never Pass."
                : "Lag and checkpoint age are judged against these thresholds (not for an assumed consumer group).");
    }

    /// <summary>One sentence after saving: what is configured, and whether Azure execution is on.</summary>
    public static string SavedSummary(IntegrationPlatform platform, bool azureEnabled)
    {
        var (configured, total) = SourceCount(platform);
        return $"Saved. {configured} of {total} runtime evidence sources configured · Azure runtime execution: {ExecutionStatus(azureEnabled)}"
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
