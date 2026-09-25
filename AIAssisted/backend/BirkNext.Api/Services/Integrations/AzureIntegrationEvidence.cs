using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// The BirkNext instance's Azure identity for read-only integration evidence. Off unless <c>IntegrationReview:Azure:Enabled</c> is true; then
/// DefaultAzureCredential without interactive sign-in (Managed Identity / workload identity / environment, optionally a user-assigned
/// identity via <c>IntegrationReview:Azure:ManagedIdentityClientId</c>). No secret is read from configuration or the UI.
/// </summary>
public interface IIntegrationAzureCredential
{
    TokenCredential? Credential { get; }
    string DisabledReason { get; }
}

public sealed class IntegrationAzureCredential : IIntegrationAzureCredential
{
    public const string DisabledMessage = "Azure runtime evidence is disabled for this BirkNext instance (IntegrationReview:Azure:Enabled is not true).";

    public IntegrationAzureCredential(IConfiguration configuration)
    {
        if (!configuration.GetValue("IntegrationReview:Azure:Enabled", false)) return;
        Credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ManagedIdentityClientId = configuration["IntegrationReview:Azure:ManagedIdentityClientId"],
        });
    }

    public TokenCredential? Credential { get; }
    public string DisabledReason => DisabledMessage;
}

internal static class AzureEvidence
{
    public static IntegrationEvidenceState StateOf(Exception ex) => ex switch
    {
        UnauthorizedAccessException => IntegrationEvidenceState.NotAuthorized,
        CredentialUnavailableException => IntegrationEvidenceState.NotAuthorized,
        AuthenticationFailedException => IntegrationEvidenceState.NotAuthorized,
        RequestFailedException { Status: 401 or 403 } => IntegrationEvidenceState.NotAuthorized,
        RequestFailedException { Status: 404 } => IntegrationEvidenceState.NotFound,
        EventHubsException { Reason: EventHubsException.FailureReason.ResourceNotFound } => IntegrationEvidenceState.NotFound,
        _ => IntegrationEvidenceState.Error,
    };

    /// <summary>An exception reduced to its type and, for service errors, status/code — never a message that could echo a token or payload.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        RequestFailedException rfe => $"{ex.GetType().Name} (HTTP {rfe.Status}{(string.IsNullOrWhiteSpace(rfe.ErrorCode) ? "" : $", {rfe.ErrorCode}")})",
        EventHubsException ehe => $"{ex.GetType().Name} ({ehe.Reason})",
        _ => ex.GetType().Name,
    };
}

/// <summary>
/// Event Hub metadata through the data-plane MANAGEMENT operations only (GetEventHubProperties / GetPartitionProperties): hub existence,
/// partition ids, last enqueued sequence number and time. No receive link is opened, no event is read, nothing is sent; the consumer-group
/// argument is required by the client type and is never used to read (the properties calls do not touch a consumer group).
/// </summary>
public sealed class AzureEventHubMetadataSource(IIntegrationAzureCredential azure, ILogger<AzureEventHubMetadataSource> logger) : IEventHubMetadataSource
{
    public const string Adapter = "Event Hub metadata";

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : platform.RuntimeEvidence?.EventHubMetadata != true ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.NotConfigured, "Event Hub metadata is not enabled for this platform.")
        : string.IsNullOrWhiteSpace(platform.NamespaceFqdn) ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.NotConfigured, "No namespace FQDN is configured.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.Available, "Configured; read with the instance's Azure identity (Azure Event Hubs Data Receiver or Listen on the namespace).");

    public async Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
    {
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return EvidenceResult<EventHubRuntimeMetadata>.Missing(readiness.State, IntegrationEvidenceSource.AzureMetadata, readiness.Reason);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var options = new EventHubConsumerClientOptions { RetryOptions = new EventHubsRetryOptions { MaximumRetries = 1, TryTimeout = TimeSpan.FromSeconds(10) } };
        await using var client = new EventHubConsumerClient(EventHubConsumerClient.DefaultConsumerGroupName, platform.NamespaceFqdn!, hubName, azure.Credential!, options);
        try
        {
            var properties = await client.GetEventHubPropertiesAsync(timeout.Token);
            var partitions = new List<PartitionRuntime>();
            foreach (var id in properties.PartitionIds)
            {
                var partition = await client.GetPartitionPropertiesAsync(id, timeout.Token);
                partitions.Add(new PartitionRuntime(id, partition.LastEnqueuedSequenceNumber, partition.IsEmpty ? null : partition.LastEnqueuedTime, partition.IsEmpty));
            }
            return EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata, new EventHubRuntimeMetadata(true, partitions));
        }
        catch (EventHubsException ex) when (ex.Reason == EventHubsException.FailureReason.ResourceNotFound)
        {
            // The service answered: the hub does not exist. That is evidence, not an access problem.
            return EvidenceResult<EventHubRuntimeMetadata>.Available(IntegrationEvidenceSource.AzureMetadata, new EventHubRuntimeMetadata(false, []), "The service reported the Event Hub as not found.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Event Hub metadata for {Hub} unavailable: {Error}.", hubName, AzureEvidence.Describe(ex));
            var state = ex is OperationCanceledException ? IntegrationEvidenceState.Unavailable : AzureEvidence.StateOf(ex);
            return EvidenceResult<EventHubRuntimeMetadata>.Missing(state, IntegrationEvidenceSource.AzureMetadata,
                state == IntegrationEvidenceState.NotAuthorized ? $"Runtime Event Hub metadata access unauthorized ({AzureEvidence.Describe(ex)})." : $"Event Hub metadata could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }
}

/// <summary>Consumer groups of a hub from Azure Resource Manager — a read-only GET (Reader role). Never creates or changes a group.</summary>
public sealed class ArmConsumerGroupSource(IIntegrationAzureCredential azure, HttpClient http, ILogger<ArmConsumerGroupSource> logger) : IEventHubConsumerGroupSource
{
    public const string Adapter = "Consumer groups (Azure Resource Manager)";

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : string.IsNullOrWhiteSpace(platform.RuntimeEvidence?.SubscriptionId) || string.IsNullOrWhiteSpace(platform.ResourceGroup) || string.IsNullOrWhiteSpace(platform.Namespace)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured, "Subscription id, resource group and namespace are needed to list consumer groups.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.Available, "Configured; read-only consumer-group list (Reader on the namespace).");

    public async Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform platform, string hubName, CancellationToken ct)
    {
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return EvidenceResult<ConsumerGroupList>.Missing(readiness.State, IntegrationEvidenceSource.AzureResourceManager, readiness.Reason);
        try
        {
            var token = await azure.Credential!.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct);
            var url = $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(platform.RuntimeEvidence!.SubscriptionId!)}/resourceGroups/{Uri.EscapeDataString(platform.ResourceGroup!)}" +
                      $"/providers/Microsoft.EventHub/namespaces/{Uri.EscapeDataString(platform.Namespace!)}/eventhubs/{Uri.EscapeDataString(hubName)}/consumergroups?api-version=2024-01-01";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotAuthorized, IntegrationEvidenceSource.AzureResourceManager, $"Consumer-group list unauthorized (HTTP {(int)response.StatusCode}).");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotFound, IntegrationEvidenceSource.AzureResourceManager, "The hub or namespace was not found in Azure Resource Manager.");
            if (!response.IsSuccessStatusCode)
                return EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.Error, IntegrationEvidenceSource.AzureResourceManager, $"Consumer-group list failed (HTTP {(int)response.StatusCode}).");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var names = document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(v => v.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToList() : [];
            return EvidenceResult<ConsumerGroupList>.Available(IntegrationEvidenceSource.AzureResourceManager, new ConsumerGroupList(names));
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationFailedException or CredentialUnavailableException or JsonException or TaskCanceledException)
        {
            logger.LogInformation("Consumer-group list for {Hub} unavailable: {Error}.", hubName, AzureEvidence.Describe(ex));
            return EvidenceResult<ConsumerGroupList>.Missing(AzureEvidence.StateOf(ex), IntegrationEvidenceSource.AzureResourceManager, $"Consumer groups could not be listed ({AzureEvidence.Describe(ex)}).");
        }
    }
}

/// <summary>
/// Consumer checkpoints from the EventProcessorClient blob checkpoint store — the authoritative position of a processor-based consumer.
/// Layout: {fqdn}/{hub}/{consumer group}/checkpoint/{partition} with "sequencenumber"/"offset" metadata (all lower-case). Read-only listing
/// with metadata; no blob is written, leased or deleted.
/// </summary>
public sealed class BlobCheckpointEvidenceSource(IIntegrationAzureCredential azure, ILogger<BlobCheckpointEvidenceSource> logger) : ICheckpointEvidenceSource
{
    public const string Adapter = "Consumer checkpoints (blob checkpoint store)";

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.CheckpointStore, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : !Uri.TryCreate(platform.RuntimeEvidence?.CheckpointContainerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.CheckpointStore, IntegrationEvidenceState.NotConfigured, "No checkpoint evidence source configured (checkpoint store container URL).")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.CheckpointStore, IntegrationEvidenceState.Available, "Configured; read-only listing (Storage Blob Data Reader on the container).");

    public static string Prefix(string fqdn, string hub, string consumerGroup, string kind) =>
        $"{fqdn.ToLowerInvariant()}/{hub.ToLowerInvariant()}/{consumerGroup.ToLowerInvariant()}/{kind}/";

    public async Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct)
    {
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return EvidenceResult<CheckpointEvidence>.Missing(readiness.State, IntegrationEvidenceSource.CheckpointStore, readiness.Reason);
        try
        {
            var container = new BlobContainerClient(new Uri(platform.RuntimeEvidence!.CheckpointContainerUrl!), azure.Credential!);
            var partitions = new List<PartitionCheckpoint>();
            await foreach (var blob in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, Prefix(platform.NamespaceFqdn ?? "", hubName, consumerGroup, "checkpoint"), ct))
                partitions.Add(new PartitionCheckpoint(blob.Name[(blob.Name.LastIndexOf('/') + 1)..],
                    blob.Metadata.TryGetValue("sequencenumber", out var seq) && long.TryParse(seq, out var s) ? s : null,
                    blob.Metadata.TryGetValue("offset", out var off) && long.TryParse(off, out var o) ? o : null,
                    blob.Properties.LastModified));
            var owners = 0;
            await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, Prefix(platform.NamespaceFqdn ?? "", hubName, consumerGroup, "ownership"), ct)) owners++;
            return partitions.Count == 0
                ? EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotFound, IntegrationEvidenceSource.CheckpointStore, $"No checkpoint is recorded for consumer group \"{consumerGroup}\" on this hub.")
                : EvidenceResult<CheckpointEvidence>.Available(IntegrationEvidenceSource.CheckpointStore, new CheckpointEvidence(consumerGroup, partitions, owners));
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException or UriFormatException)
        {
            logger.LogInformation("Checkpoints for {Hub}/{ConsumerGroup} unavailable: {Error}.", hubName, consumerGroup, AzureEvidence.Describe(ex));
            return EvidenceResult<CheckpointEvidence>.Missing(AzureEvidence.StateOf(ex), IntegrationEvidenceSource.CheckpointStore, $"Checkpoint store could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }
}

/// <summary>
/// Consumer telemetry from the workspace-based Application Insights resource: three bounded, aggregate KQL queries per consumer role in the
/// review window (exceptions, traces, Event Hubs dependency calls). Only counts and timestamps come back; no row, message or payload.
/// </summary>
public sealed class LogAnalyticsTelemetrySource(IIntegrationAzureCredential azure, ILogger<LogAnalyticsTelemetrySource> logger) : ITelemetryEvidenceSource
{
    public const string Adapter = "Consumer telemetry (Application Insights)";

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.ApplicationInsights, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : !Guid.TryParse(platform.RuntimeEvidence?.TelemetryWorkspaceId, out _)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.ApplicationInsights, IntegrationEvidenceState.NotConfigured, "No telemetry source configured (Log Analytics workspace id of Application Insights).")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.ApplicationInsights, IntegrationEvidenceState.Available, "Configured; bounded aggregate queries (Log Analytics Reader).");

    /// <summary>KQL string literal for a role name (the only interpolated value; quotes and backslashes escaped).</summary>
    internal static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static string ExceptionsQuery(string role) =>
        $"AppExceptions | where AppRoleName == {Literal(role)} | summarize total=count(), " +
        "deser=countif(ExceptionType has_any (\"JsonException\",\"SerializationException\",\"AvroException\") or OuterMessage has \"deserializ\"), " +
        "auth=countif(ExceptionType has_any (\"UnauthorizedAccessException\",\"AuthenticationFailedException\") or OuterMessage has_any (\"401\",\"403\",\"Unauthorized\")), " +
        "last=max(TimeGenerated)";

    internal static string TracesQuery(string role) =>
        $"AppTraces | where AppRoleName == {Literal(role)} | summarize processing=countif(Message has_any (\"EventHub\",\"partition\",\"checkpoint\")), " +
        "retry=countif(Message has \"retry\"), deadletter=countif(Message has_any (\"dead-letter\",\"deadletter\",\"poison\")), " +
        "correlated=countif(isnotempty(OperationId)), last=max(TimeGenerated)";

    internal static string DependenciesQuery(string role) =>
        $"AppDependencies | where AppRoleName == {Literal(role)} and (DependencyType has \"Event Hubs\" or Target has \"servicebus.windows.net\") " +
        "| summarize calls=count(), failed=countif(Success == false), median=percentile(DurationMs, 50), last=max(TimeGenerated)";

    public async Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct)
    {
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return EvidenceResult<ConsumerTelemetry>.Missing(readiness.State, IntegrationEvidenceSource.ApplicationInsights, readiness.Reason);
        var client = new LogsQueryClient(azure.Credential!);
        var workspace = platform.RuntimeEvidence!.TelemetryWorkspaceId!;
        var range = new QueryTimeRange(TimeSpan.FromHours(windowHours));
        var options = new LogsQueryOptions { ServerTimeout = TimeSpan.FromSeconds(30) };
        try
        {
            var exceptions = (await client.QueryWorkspaceAsync(workspace, ExceptionsQuery(roleName), range, options, ct)).Value.Table.Rows.FirstOrDefault();
            var traces = (await client.QueryWorkspaceAsync(workspace, TracesQuery(roleName), range, options, ct)).Value.Table.Rows.FirstOrDefault();
            var dependencies = (await client.QueryWorkspaceAsync(workspace, DependenciesQuery(roleName), range, options, ct)).Value.Table.Rows.FirstOrDefault();
            static long L(LogsTableRow? row, string column) => row?.GetInt64(column) ?? 0;
            static DateTimeOffset? T(LogsTableRow? row, string column) => row?.GetDateTimeOffset(column);
            var last = new[] { T(exceptions, "last"), T(traces, "last"), T(dependencies, "last") }.Max();
            return EvidenceResult<ConsumerTelemetry>.Available(IntegrationEvidenceSource.ApplicationInsights, new ConsumerTelemetry(
                L(exceptions, "total"), L(exceptions, "deser"), L(exceptions, "auth"), L(traces, "retry"), L(traces, "deadletter"),
                L(traces, "processing"), L(traces, "correlated"), L(dependencies, "calls"), L(dependencies, "failed"), dependencies?.GetDouble("median"),
                last, T(exceptions, "last"), windowHours));
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        {
            logger.LogInformation("Telemetry for role {Role} unavailable: {Error}.", roleName, AzureEvidence.Describe(ex));
            return EvidenceResult<ConsumerTelemetry>.Missing(AzureEvidence.StateOf(ex), IntegrationEvidenceSource.ApplicationInsights,
                AzureEvidence.StateOf(ex) == IntegrationEvidenceState.NotAuthorized ? $"Telemetry access unauthorized ({AzureEvidence.Describe(ex)})." : $"Telemetry query failed ({AzureEvidence.Describe(ex)}).");
        }
    }
}
