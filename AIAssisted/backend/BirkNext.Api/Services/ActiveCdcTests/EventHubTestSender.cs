using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

public sealed record EventHubTestSendOutcome(ActiveCdcEvidenceState State, bool Ambiguous, string Detail);

/// <summary>
/// The only Event Hub SEND path in BirkNext. It takes a policy-approved destination and a fixture-built event — never a raw string or a
/// caller-chosen hub — and sends exactly one event with SDK retries disabled, so an uncertain outcome is reported, not silently re-sent.
/// </summary>
public interface IEventHubTestSender
{
    /// <summary>Configuration-only: whether an Azure identity is available (the sender's Data Sender right is never probed).</summary>
    string? UnavailableReason { get; }
    Task<EventHubTestSendOutcome> SendAsync(ApprovedCdcDestination destination, SyntheticCdcEvent synthetic, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Seam for tests: the real factory opens an <see cref="EventHubProducerClient"/>; a fake records what would have been sent.</summary>
public interface IEventHubTestProducerFactory
{
    IEventHubTestProducer Create(string namespaceFqdn, string eventHub, TokenCredential credential, TimeSpan tryTimeout);
}

public interface IEventHubTestProducer : IAsyncDisposable
{
    Task SendAsync(EventData eventData, CancellationToken ct);
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
        public Task SendAsync(EventData eventData, CancellationToken ct) => client.SendAsync([eventData], ct);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

public sealed class AzureEventHubTestSender(IIntegrationAzureCredential azure, IEventHubTestProducerFactory producers, ILogger<AzureEventHubTestSender> logger) : IEventHubTestSender
{
    public string? UnavailableReason => azure.Credential is null ? azure.DisabledReason : null;

    public async Task<EventHubTestSendOutcome> SendAsync(ApprovedCdcDestination destination, SyntheticCdcEvent synthetic, TimeSpan timeout, CancellationToken ct)
    {
        if (azure.Credential is not { } credential) return new(ActiveCdcEvidenceState.NotAuthorized, false, azure.DisabledReason);
        var data = new EventData(synthetic.Body)
        {
            MessageId = synthetic.RunId.ToString("N"),
            ContentType = "application/json",
        };
        // Metadata only. The Person Adapter reads the body alone, so these are not propagated downstream.
        data.Properties["BirkNextRunId"] = synthetic.RunId.ToString("N");
        data.Properties["BirkNextScenario"] = synthetic.ScenarioId;
        data.Properties["BirkNextSynthetic"] = true;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            await using var producer = producers.Create(destination.NamespaceFqdn, destination.EventHub, credential, timeout);
            await producer.SendAsync(data, bounded.Token);
            return new(ActiveCdcEvidenceState.Observed, false, "The Event Hubs producer SDK reported the event accepted by the service (one attempt, no retry).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ActiveCdcEvidenceState.TimedOut, true, $"No answer within {timeout.TotalSeconds:0} s. The event may or may not have been accepted; it was not re-sent.");
        }
        catch (EventHubsException ex)
        {
            logger.LogWarning("Active CDC send to {Hub} failed: {Reason}", destination.EventHub, ex.Reason);
            return ex.Reason switch
            {
                EventHubsException.FailureReason.ResourceNotFound => new(ActiveCdcEvidenceState.Error, false, "Event Hub not found (nothing accepted)."),
                EventHubsException.FailureReason.MessageSizeExceeded => new(ActiveCdcEvidenceState.Error, false, "Event rejected as too large (nothing accepted)."),
                EventHubsException.FailureReason.QuotaExceeded or EventHubsException.FailureReason.ServiceBusy => new(ActiveCdcEvidenceState.Error, false, $"Event rejected by the service ({ex.Reason}); nothing accepted."),
                EventHubsException.FailureReason.ServiceTimeout or EventHubsException.FailureReason.ServiceCommunicationProblem =>
                    new(ActiveCdcEvidenceState.TimedOut, true, $"The send outcome is unknown ({ex.Reason}). The event may have been accepted; it was not re-sent."),
                _ => new(ActiveCdcEvidenceState.Error, true, $"The send failed ({ex.Reason}); acceptance is unknown and it was not re-sent."),
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or AuthenticationFailedException or CredentialUnavailableException)
        {
            logger.LogWarning("Active CDC send to {Hub} not authorized: {Type}", destination.EventHub, ex.GetType().Name);
            return new(ActiveCdcEvidenceState.NotAuthorized, false, $"The BirkNext identity is not authorized to send ({ex.GetType().Name}). It needs Azure Event Hubs Data Sender on this hub; nothing was accepted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Active CDC send to {Hub} failed: {Type}", destination.EventHub, ex.GetType().Name);
            return new(ActiveCdcEvidenceState.Error, true, $"The send failed ({ex.GetType().Name}); acceptance is unknown and it was not re-sent.");
        }
    }
}
