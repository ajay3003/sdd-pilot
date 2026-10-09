using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Domain-neutral presentation of Active Event readiness and runs, shared by every provider. It never upgrades a state: Not verified stays
/// Not verified, transport acceptance is never shown as a pass, and provider-specific details are shown as provider metadata only.
/// </summary>
public static class ActiveEventPresentation
{
    /// <summary>The prerequisite groups shown before the Run button, in the order a user resolves them.</summary>
    public static readonly ActiveEventReadinessCategory[] CategoryOrder =
    [
        ActiveEventReadinessCategory.SourceContract, ActiveEventReadinessCategory.CdcCapture, ActiveEventReadinessCategory.Integration,
        ActiveEventReadinessCategory.TrustedEnvironment, ActiveEventReadinessCategory.Destination, ActiveEventReadinessCategory.Transport,
        ActiveEventReadinessCategory.Authentication, ActiveEventReadinessCategory.Authorization, ActiveEventReadinessCategory.SyntheticData,
        ActiveEventReadinessCategory.DownstreamVerification, ActiveEventReadinessCategory.Scenario, ActiveEventReadinessCategory.Execution,
    ];

    public static string Category(ActiveEventReadinessCategory category) => category switch
    {
        ActiveEventReadinessCategory.SourceContract => "Source contract",
        ActiveEventReadinessCategory.CdcCapture => "CDC capture",
        ActiveEventReadinessCategory.Integration => "Integration configuration",
        ActiveEventReadinessCategory.TrustedEnvironment => "Trusted environment",
        ActiveEventReadinessCategory.Destination => "Destination",
        ActiveEventReadinessCategory.Transport => "Transport",
        ActiveEventReadinessCategory.Authentication => "Authentication",
        ActiveEventReadinessCategory.Authorization => "Authorization",
        ActiveEventReadinessCategory.SyntheticData => "Synthetic test data policy",
        ActiveEventReadinessCategory.DownstreamVerification => "Downstream verification",
        ActiveEventReadinessCategory.Scenario => "Scenario support",
        _ => "Execution",
    };

    public static string State(ActiveEventReadinessState state) => state switch
    {
        ActiveEventReadinessState.Ready => "Ready",
        ActiveEventReadinessState.Blocked => "Not ready",
        ActiveEventReadinessState.NotConfigured => "Not configured",
        ActiveEventReadinessState.NotAvailable => "Not available",
        ActiveEventReadinessState.Partial => "Partial",
        ActiveEventReadinessState.Optional => "Optional",
        _ => "Unknown",
    };

    public static string Tone(ActiveEventReadinessState state) => state switch
    {
        ActiveEventReadinessState.Ready => "ready",
        ActiveEventReadinessState.Partial or ActiveEventReadinessState.Optional or ActiveEventReadinessState.Unknown => "attention",
        _ => "blocked",
    };

    public static string Status(ActiveEventRunStatus status) => status switch
    {
        ActiveEventRunStatus.Running => "Running",
        ActiveEventRunStatus.Completed => "Completed",
        ActiveEventRunStatus.CompletedWithLimitedEvidence => "Limited evidence",
        ActiveEventRunStatus.Failed => "Failed",
        ActiveEventRunStatus.SafetyBlocked => "Blocked",
        ActiveEventRunStatus.Inconclusive => "Inconclusive",
        _ => "Cancelled",
    };

    public static string Tone(ActiveEventRunStatus status) => status switch
    {
        ActiveEventRunStatus.Completed => "ready",
        ActiveEventRunStatus.Failed or ActiveEventRunStatus.SafetyBlocked => "blocked",
        _ => "attention",
    };

    public static string Stage(ActiveEventEvidenceStage stage) => stage switch
    {
        ActiveEventEvidenceStage.Generated => "Generated",
        ActiveEventEvidenceStage.SendAttempted => "Send attempted",
        ActiveEventEvidenceStage.TransportAccepted => "Transport accepted",
        ActiveEventEvidenceStage.ConsumerActivityObserved => "Consumer activity",
        ActiveEventEvidenceStage.ConsumerContinuityObserved => "Consumer continuity",
        _ => "Downstream verified",
    };

    public static string Evidence(ActiveEventEvidenceStatus status) => status switch
    {
        ActiveEventEvidenceStatus.Observed => "Observed",
        ActiveEventEvidenceStatus.NotObserved => "Not observed",
        ActiveEventEvidenceStatus.NotVerified => "Not verified",
        ActiveEventEvidenceStatus.Unavailable => "Unavailable",
        ActiveEventEvidenceStatus.Ambiguous => "Ambiguous",
        ActiveEventEvidenceStatus.Failed => "Failed",
        ActiveEventEvidenceStatus.SafetyBlocked => "Blocked",
        ActiveEventEvidenceStatus.UnexpectedResult => "Unexpected result",
        _ => "Not attempted",
    };

    public static string Tone(ActiveEventEvidenceStatus status) => status switch
    {
        ActiveEventEvidenceStatus.Observed => "ready",
        ActiveEventEvidenceStatus.Failed or ActiveEventEvidenceStatus.SafetyBlocked or ActiveEventEvidenceStatus.UnexpectedResult => "blocked",
        _ => "neutral",
    };

    public static string Operation(ActiveEventOperation operation) => operation switch
    {
        ActiveEventOperation.Create => "Create",
        ActiveEventOperation.Update => "Update",
        ActiveEventOperation.Delete => "Delete",
        ActiveEventOperation.ReadSnapshot => "Snapshot read",
        ActiveEventOperation.Tombstone => "Tombstone",
        _ => "Custom",
    };

    /// <summary>One line that never claims more than the evidence: transport acceptance alone is "accepted", not "passed".</summary>
    public static string Headline(ActiveEventRunResult run)
    {
        var accepted = run.Evidence.Count(e => e.Stage == ActiveEventEvidenceStage.TransportAccepted && e.Status == ActiveEventEvidenceStatus.Observed);
        return run.Status switch
        {
            ActiveEventRunStatus.Running => run.SendAttempted ? $"Running — {accepted} of {Math.Max(run.Events.Count, run.Scenario.ExpectedEventCount)} event(s) accepted so far" : "Preparing — nothing sent yet",
            ActiveEventRunStatus.Completed => "Completed — every required stage was observed",
            ActiveEventRunStatus.CompletedWithLimitedEvidence => run.Scenario.RequiresDownstreamVerification &&
                !run.Evidence.Any(e => e.Stage == ActiveEventEvidenceStage.DownstreamVerified && e.Status == ActiveEventEvidenceStatus.Observed)
                    ? "Transport accepted — downstream result not verified"
                    : "Limited evidence — a required stage was not observed",
            ActiveEventRunStatus.SafetyBlocked => run.SendAttempted ? "Blocked — a send was refused" : "Blocked — nothing was sent",
            ActiveEventRunStatus.Inconclusive => "Inconclusive — a send outcome or required evidence is uncertain",
            ActiveEventRunStatus.Failed => "Failed — the transport or the downstream check reported a failure",
            _ => run.SendAttempted ? "Cancelled — events already sent cannot be recalled" : "Cancelled — nothing was sent",
        };
    }

    /// <summary>The safe identity shown for an event: provider-supplied display metadata, else the correlation's source identity.</summary>
    public static string SyntheticIdentity(ActiveEventGeneratedSummary item) =>
        item.SafeMetadata.GetValueOrDefault("syntheticIdentity") is { Length: > 0 } shown ? shown : item.Correlation.SafeSourceIdentity ?? "—";

    /// <summary>Provider metadata keys rendered as provider details (never in the shared columns).</summary>
    public static IEnumerable<KeyValuePair<string, string>> ProviderDetails(ActiveEventGeneratedSummary item) =>
        item.SafeMetadata.Where(pair => pair.Value.Length > 0 && pair.Key is not ("sequenceLabel" or "role" or "syntheticIdentity"));
}
