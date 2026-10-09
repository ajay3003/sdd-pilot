using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>Azure Event Hubs transport adapter. It handles opaque event bytes and never inspects a project/domain payload.</summary>
public sealed class EventHubActiveEventTransportProvider(ActiveCdcPolicy policy, IEventHubTestSender sender) : IActiveEventTransportProvider
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
        var approved = policy.Approve(target.EnvironmentType, target.Endpoint, target.Resource,
            target.SafeMetadata.GetValueOrDefault("TargetUrl"));
        reason = approved.Reason;
        return approved.Approved is not null;
    }

    public async Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct)
    {
        var approval = policy.Approve(target.EnvironmentType, target.Endpoint, target.Resource, target.SafeMetadata.GetValueOrDefault("TargetUrl"));
        if (approval.Approved is null)
            return new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.SafetyBlocked,
                Detail = approval.Reason, EvidenceSource = "Event Hub destination policy", CapturedAt = DateTimeOffset.UtcNow };

        var outcome = await sender.SendAsync(approval.Approved, activeEvent, timeout, ct);
        var status = outcome.State switch
        {
            ActiveCdcEvidenceState.Observed => ActiveEventEvidenceStatus.Observed,
            ActiveCdcEvidenceState.TimedOut => ActiveEventEvidenceStatus.Ambiguous,
            ActiveCdcEvidenceState.NotAuthorized => ActiveEventEvidenceStatus.Unavailable,
            ActiveCdcEvidenceState.NotAssessed => ActiveEventEvidenceStatus.NotVerified,
            ActiveCdcEvidenceState.Error => outcome.Ambiguous ? ActiveEventEvidenceStatus.Ambiguous : ActiveEventEvidenceStatus.Failed,
            _ => ActiveEventEvidenceStatus.Failed,
        };
        return new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = status, Detail = outcome.Detail,
            EvidenceSource = "Azure Event Hubs producer SDK", CapturedAt = DateTimeOffset.UtcNow,
            CorrelationQuality = ActiveEventCorrelationQuality.Strong };
    }
}
