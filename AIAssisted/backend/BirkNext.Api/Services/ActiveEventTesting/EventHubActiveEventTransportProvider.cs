using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// Azure Event Hubs transport adapter. It handles opaque event bytes and never inspects a project/domain payload. The destination policy
/// (environment, trusted target URL, name markers, exact enrollment) is re-evaluated immediately before every send.
/// </summary>
public sealed class EventHubActiveEventTransportProvider(ActiveEventPolicy policy, IEventHubTestSender sender, TimeProvider clock) : IActiveEventTransportProvider
{
    public string TransportType => "EventHub";

    public bool CanSend(ActiveEventTrustedTarget target, out string reason)
    {
        if (!string.Equals(target.TransportType, TransportType, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(target.Endpoint) || string.IsNullOrWhiteSpace(target.Resource))
        {
            reason = "The trusted target does not contain an Event Hubs namespace and resource.";
            return false;
        }
        if (sender.UnavailableReason is { } unavailable)
        {
            reason = unavailable;
            return false;
        }
        var approved = policy.Approve(target.EnvironmentType, target.Endpoint, target.Resource, target.TargetUrl);
        reason = approved.Reason;
        return approved.Approved is not null;
    }

    public async Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct)
    {
        var approval = policy.Approve(target.EnvironmentType, target.Endpoint, target.Resource, target.TargetUrl);
        if (approval.Approved is null)
            return new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.SafetyBlocked,
                Detail = approval.Reason, EvidenceSource = "Event Hub destination policy (re-checked before send)", CapturedAt = clock.GetUtcNow() };

        var outcome = await sender.SendAsync(approval.Approved, activeEvent, timeout, ct);
        return new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = outcome.Status, Detail = outcome.Detail,
            EvidenceSource = "Azure Event Hubs producer SDK", CapturedAt = clock.GetUtcNow(),
            CorrelationQuality = outcome.Status == ActiveEventEvidenceStatus.Observed ? ActiveEventCorrelationQuality.Strong : ActiveEventCorrelationQuality.Unavailable };
    }
}
