using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.EventHub;

/// <summary>Read-only namespace metrics of an Event Hubs namespace (Azure Monitor). Never an event operation.</summary>
public interface IEventHubMetricsSource
{
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EventHubMetricsEvidence> ReadAsync(IntegrationPlatform platform, int windowHours, CancellationToken ct);
}

/// <summary>
/// Azure Monitor platform metrics of the namespace resource (Microsoft.EventHub/namespaces), read with the instance's read-only Azure identity
/// (same gate and credential as the Resource Manager metadata; Monitoring Reader or Reader on the namespace). Only documented counter metrics
/// are queried, as Totals over the review window. Values are Observed with no threshold — zero errors is not a pass, and throughput is not
/// health. Consumer lag is not a namespace metric and is never derived here. No event is sent or received.
/// </summary>
public sealed class AzureMonitorEventHubMetricsSource(IIntegrationAzureCredential azure, ILogger<AzureMonitorEventHubMetricsSource> logger) : IEventHubMetricsSource
{
    public const string Adapter = "Event Hub metrics (Azure Monitor)";

    /// <summary>Documented Event Hubs namespace counters (Total over the window).</summary>
    public static readonly string[] CounterMetrics =
        ["IncomingMessages", "OutgoingMessages", "IncomingRequests", "SuccessfulRequests", "ServerErrors", "UserErrors", "ThrottledRequests", "IncomingBytes", "OutgoingBytes"];

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : !EventHubRuntimeSources.ResourceManager(platform)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.NotConfigured,
                "The Azure subscription id, resource group and namespace name are needed to read the namespace metrics.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.Available, "Configured; read-only Azure Monitor metric queries.");

    public async Task<EventHubMetricsEvidence> ReadAsync(IntegrationPlatform platform, int windowHours, CancellationToken ct)
    {
        var window = Math.Clamp(windowHours, 1, IntegrationRuntimeEvidenceSettings.MaxReviewWindowHours);
        EventHubMetricsEvidence Missing(IntegrationEvidenceState state, string reason) => new() { State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow, WindowHours = window };
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return Missing(readiness.State, readiness.Reason);
        try
        {
            var client = new MetricsQueryClient(azure.Credential!);
            var options = new MetricsQueryOptions { TimeRange = new QueryTimeRange(TimeSpan.FromHours(window)), Granularity = TimeSpan.FromHours(window <= 24 ? 1 : 24) };
            options.Aggregations.Add(MetricAggregationType.Total);
            var response = await client.QueryResourceAsync(ArmEventHubNamespaceSource.NamespacePath(platform), CounterMetrics, options, ct);
            var observations = response.Value.Metrics.Select(metric =>
            {
                var points = metric.TimeSeries.SelectMany(t => t.Values).ToList();
                return new EventHubMetricObservation(metric.Name, "Total", points.Any(p => p.Total is not null) ? points.Sum(p => p.Total ?? 0) : null, metric.Unit.ToString());
            }).ToList();
            logger.LogInformation("Event Hub metrics for {Namespace}: {Count} metric value(s) over {Window} h.", platform.Namespace, observations.Count(o => o.Value is not null), window);
            return new EventHubMetricsEvidence
            {
                State = IntegrationEvidenceState.Available, Reason = $"Azure Monitor platform metrics over the last {window} h (read-only).", CapturedAt = DateTimeOffset.UtcNow,
                WindowHours = window, Metrics = observations,
            };
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException or TaskCanceledException or HttpRequestException)
        {
            var state = ex is TaskCanceledException or HttpRequestException ? IntegrationEvidenceState.Unavailable : AzureEvidence.StateOf(ex);
            logger.LogInformation("Event Hub metrics for {Namespace} unavailable: {Error}.", platform.Namespace, AzureEvidence.Describe(ex));
            return Missing(state, $"Event Hub metrics could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }
}
