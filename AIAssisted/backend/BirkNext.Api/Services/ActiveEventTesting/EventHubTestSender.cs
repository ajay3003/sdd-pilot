using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>Transport outcome in generic evidence terms: Observed (accepted), Ambiguous (unknown), Unavailable (not authorized) or Failed (rejected).</summary>
public sealed record EventHubTestSendOutcome(ActiveEventEvidenceStatus Status, string Detail);

/// <summary>
/// The only Event Hub SEND path in BirkNext. It takes a policy-approved destination and a provider-generated event — never a raw string or
/// a caller-chosen hub — and sends exactly one event with SDK retries disabled, so an uncertain outcome is reported, not silently re-sent.
/// It never inspects the body.
/// </summary>
public interface IEventHubTestSender
{
    /// <summary>Configuration-only: whether an Azure identity is available (the sender's Data Sender right is never probed).</summary>
    string? UnavailableReason { get; }
    Task<EventHubTestSendOutcome> SendAsync(ApprovedEventHubDestination destination, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Seam for tests: the real factory opens an <see cref="EventHubProducerClient"/>; a fake records what would have been sent.</summary>
public interface IEventHubTestProducerFactory
{
    IEventHubTestProducer Create(string namespaceFqdn, string eventHub, TokenCredential credential, TimeSpan tryTimeout);
}

public interface IEventHubTestProducer : IAsyncDisposable
{
    Task SendAsync(EventData eventData, string? partitionKey, CancellationToken ct);
}

public sealed class AzureEventHubTestProducerFactory : IEventHubTestProducerFactory
{
    public IEventHubTestProducer Create(string namespaceFqdn, string eventHub, TokenCredential credential, TimeSpan tryTimeout) =>
        new Producer(new EventHubProducerClient(namespaceFqdn, eventHub, credential, new EventHubProducerClientOptions
        {
            // One attempt: a retried send after an unknown outcome could deliver the synthetic event twice.
            RetryOptions = new EventHubsRetryOptions { MaximumRetries = 0, TryTimeout = tryTimeout, Mode = EventHubsRetryMode.Fixed, Delay = TimeSpan.FromSeconds(1) },
        }));

    private sealed class Producer(EventHubProducerClient client) : IEventHubTestProducer
    {
        public Task SendAsync(EventData eventData, string? partitionKey, CancellationToken ct) =>
            client.SendAsync([eventData], new SendEventOptions { PartitionKey = string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey }, ct);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

public sealed class AzureEventHubTestSender(IIntegrationAzureCredential azure, IEventHubTestProducerFactory producers, ILogger<AzureEventHubTestSender> logger) : IEventHubTestSender
{
    public string? UnavailableReason => azure.Credential is null ? azure.DisabledReason : null;

    public async Task<EventHubTestSendOutcome> SendAsync(ApprovedEventHubDestination destination, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct)
    {
        if (azure.Credential is not { } credential) return new(ActiveEventEvidenceStatus.Unavailable, azure.DisabledReason);
        // A Debezium tombstone is a Kafka record with a key and a null value. How such a record appears to an AMQP producer/consumer of
        // Event Hubs is not verified, so a tombstone is never sent in an approximated form.
        if (activeEvent.Operation == ActiveEventOperation.Tombstone)
            return new(ActiveEventEvidenceStatus.Unavailable, "Tombstones are not sent: the Kafka null-value tombstone representation over the Event Hubs AMQP producer is not verified.");
        var data = new EventData(activeEvent.Body)
        {
            MessageId = activeEvent.EventId,
            ContentType = activeEvent.ContentType,
        };
        // Transport metadata only. The consumer does not need to propagate these values downstream.
        data.Properties["BirkNextRunId"] = activeEvent.Correlation.RunId.ToString("N");
        data.Properties["BirkNextScenario"] = activeEvent.ScenarioId;
        data.Properties["BirkNextSynthetic"] = true;
        foreach (var (key, value) in activeEvent.TransportProperties) data.Properties[key] = value;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            await using var producer = producers.Create(destination.NamespaceFqdn, destination.EventHub, credential, timeout);
            await producer.SendAsync(data, activeEvent.PartitionKey, bounded.Token);
            return new(ActiveEventEvidenceStatus.Observed, "The Event Hubs producer SDK reported the event accepted by the service (one attempt, no retry).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ActiveEventEvidenceStatus.Ambiguous, $"No answer within {timeout.TotalSeconds:0} s. The event may or may not have been accepted; it was not re-sent.");
        }
        catch (EventHubsException ex)
        {
            logger.LogWarning("Active event send to {Hub} failed: {Reason}", destination.EventHub, ex.Reason);
            return ex.Reason switch
            {
                EventHubsException.FailureReason.ResourceNotFound => new(ActiveEventEvidenceStatus.Failed, "Event Hub not found (nothing accepted)."),
                EventHubsException.FailureReason.MessageSizeExceeded => new(ActiveEventEvidenceStatus.Failed, "Event rejected as too large (nothing accepted)."),
                EventHubsException.FailureReason.QuotaExceeded or EventHubsException.FailureReason.ServiceBusy => new(ActiveEventEvidenceStatus.Failed, $"Event rejected by the service ({ex.Reason}); nothing accepted."),
                EventHubsException.FailureReason.ServiceTimeout or EventHubsException.FailureReason.ServiceCommunicationProblem =>
                    new(ActiveEventEvidenceStatus.Ambiguous, $"The send outcome is unknown ({ex.Reason}). The event may have been accepted; it was not re-sent."),
                _ => new(ActiveEventEvidenceStatus.Ambiguous, $"The send failed ({ex.Reason}); acceptance is unknown and it was not re-sent."),
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or AuthenticationFailedException or CredentialUnavailableException)
        {
            logger.LogWarning("Active event send to {Hub} not authorized: {Type}", destination.EventHub, ex.GetType().Name);
            return new(ActiveEventEvidenceStatus.Unavailable, $"The BirkNext identity is not authorized to send ({ex.GetType().Name}). It needs Azure Event Hubs Data Sender on this hub; nothing was accepted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Active event send to {Hub} failed: {Type}", destination.EventHub, ex.GetType().Name);
            return new(ActiveEventEvidenceStatus.Ambiguous, $"The send failed ({ex.GetType().Name}); acceptance is unknown and it was not re-sent.");
        }
    }
}
