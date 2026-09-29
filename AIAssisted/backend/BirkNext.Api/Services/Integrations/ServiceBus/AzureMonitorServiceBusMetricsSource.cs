using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ServiceBus;

/// <summary>Read-only namespace metrics of a Service Bus namespace (Azure Monitor). Never a message operation.</summary>
public interface IServiceBusMetricsSource
{
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<ServiceBusMetricsEvidence> ReadAsync(IntegrationPlatform platform, int windowHours, CancellationToken ct);
}

/// <summary>
/// Azure Monitor platform metrics of the namespace resource (Microsoft.ServiceBus/namespaces), read with the instance's read-only Azure
/// identity (the same gate and credential as the Resource Manager metadata; Monitoring Reader or Reader on the namespace). Only documented
/// metric names are queried, each with the aggregation Azure defines for it; values are Observed with no threshold (zero is not a pass).
/// Oldest-message age is not a Service Bus platform metric and is never derived. No message is sent, received, peeked or settled.
/// </summary>
public sealed class AzureMonitorServiceBusMetricsSource(IIntegrationAzureCredential azure, ILogger<AzureMonitorServiceBusMetricsSource> logger) : IServiceBusMetricsSource
{
    public const string Adapter = "Service Bus metrics (Azure Monitor)";

    /// <summary>Documented Service Bus namespace metrics: counters use Total over the window, gauges use Average and Maximum.</summary>
    public static readonly string[] CounterMetrics = ["IncomingMessages", "OutgoingMessages", "ServerErrors", "UserErrors", "ThrottledRequests", "SuccessfulRequests"];
    public static readonly string[] GaugeMetrics = ["ActiveMessages", "DeadletteredMessages", "ScheduledMessages", "Size"];

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : !Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId, out _) || string.IsNullOrWhiteSpace(platform.ResourceGroup) || string.IsNullOrWhiteSpace(platform.Namespace)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.NotConfigured,
                "The Azure subscription id, resource group and namespace name of the Service Bus namespace are needed to read its metrics.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMonitor, IntegrationEvidenceState.Available, "Configured; read-only Azure Monitor metric queries.");

    public async Task<ServiceBusMetricsEvidence> ReadAsync(IntegrationPlatform platform, int windowHours, CancellationToken ct)
    {
        var window = Math.Clamp(windowHours, 1, 24 * 30);
        ServiceBusMetricsEvidence Missing(IntegrationEvidenceState state, string reason) => new() { State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow, WindowHours = window };
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return Missing(readiness.State, readiness.Reason);
        try
        {
            var client = new MetricsQueryClient(azure.Credential!);
            var resourceId = ArmServiceBusMetadataSource.NamespacePath(platform);
            var range = new QueryTimeRange(TimeSpan.FromHours(window));
            var observations = new List<ServiceBusMetricObservation>();
            observations.AddRange(await QueryAsync(client, resourceId, CounterMetrics, range, window, [MetricAggregationType.Total], ct));
            observations.AddRange(await QueryAsync(client, resourceId, GaugeMetrics, range, window, [MetricAggregationType.Average, MetricAggregationType.Maximum], ct));
            logger.LogInformation("Service Bus metrics for {Namespace}: {Count} metric value(s) over {Window} h.", platform.Namespace, observations.Count(o => o.Value is not null), window);
            return new ServiceBusMetricsEvidence
            {
                State = IntegrationEvidenceState.Available, Reason = $"Azure Monitor platform metrics over the last {window} h (read-only).", CapturedAt = DateTimeOffset.UtcNow,
                WindowHours = window, Metrics = observations,
            };
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException or TaskCanceledException or HttpRequestException)
        {
            var state = ex is TaskCanceledException or HttpRequestException ? IntegrationEvidenceState.Unavailable : AzureEvidence.StateOf(ex);
            logger.LogInformation("Service Bus metrics for {Namespace} unavailable: {Error}.", platform.Namespace, AzureEvidence.Describe(ex));
            return Missing(state, $"Service Bus metrics could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }

    private static async Task<IEnumerable<ServiceBusMetricObservation>> QueryAsync(MetricsQueryClient client, string resourceId, string[] metrics, QueryTimeRange range, int window,
        MetricAggregationType[] aggregations, CancellationToken ct)
    {
        var options = new MetricsQueryOptions { TimeRange = range, Granularity = TimeSpan.FromHours(window <= 24 ? 1 : 24) };
        foreach (var aggregation in aggregations) options.Aggregations.Add(aggregation);
        var response = await client.QueryResourceAsync(resourceId, metrics, options, ct);
        return response.Value.Metrics.SelectMany(metric => aggregations.Select(aggregation =>
        {
            var points = metric.TimeSeries.SelectMany(t => t.Values).ToList();
            double? value = aggregation switch
            {
                MetricAggregationType.Total => points.Any(p => p.Total is not null) ? points.Sum(p => p.Total ?? 0) : null,
                MetricAggregationType.Average => points.Any(p => p.Average is not null) ? points.Where(p => p.Average is not null).Average(p => p.Average!.Value) : null,
                _ => points.Any(p => p.Maximum is not null) ? points.Max(p => p.Maximum ?? 0) : null,
            };
            return new ServiceBusMetricObservation(metric.Name, aggregation.ToString(), value, metric.Unit.ToString());
        })).ToList();
    }
}
