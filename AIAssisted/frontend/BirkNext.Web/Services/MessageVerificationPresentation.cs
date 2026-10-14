using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Tester-facing words for Message Verification. Evidence stages are never shown as passes; only a verdict can be Passed.</summary>
public static class MessageVerificationPresentation
{
    public const string PackId = "skolenaervaer";
    public const string JourneyId = "rapportmottak";
    public const string ScenarioId = "message-verification";

    /// <summary>The order the message travels; every system is shown even without evidence.</summary>
    public static readonly IReadOnlyList<string> Systems = ["Altinn", "MM", "Skoletjenesten", "Period and coverage", "Bufdata"];

    public static string Verdict(MessageVerificationVerdict verdict) => verdict switch
    {
        MessageVerificationVerdict.Passed => "Passed",
        MessageVerificationVerdict.Failed => "Failed",
        MessageVerificationVerdict.Blocked => "Blocked",
        MessageVerificationVerdict.NotRun => "Not run",
        _ => "Not evaluated",
    };

    public static string VerdictTone(MessageVerificationVerdict verdict) => verdict switch
    {
        MessageVerificationVerdict.Passed => "ready",
        MessageVerificationVerdict.Failed => "danger",
        MessageVerificationVerdict.Blocked => "attention",
        _ => "neutral",
    };

    public static string Stage(MessageEvidenceStage stage) => stage switch
    {
        MessageEvidenceStage.Generated => "Generated (not submitted)",
        MessageEvidenceStage.Submitted => "Submitted (not confirmed)",
        MessageEvidenceStage.Confirmed => "Confirmed by Altinn",
        MessageEvidenceStage.TransportAccepted => "Transport accepted",
        MessageEvidenceStage.MmProcessingObserved => "MM processing observed",
        MessageEvidenceStage.SkoleHttpResponseObserved => "HTTP answer observed",
        MessageEvidenceStage.SkoleProcessingObserved => "Processing observed",
        MessageEvidenceStage.InternalOutcomeVerified => "Internal outcome verified",
        MessageEvidenceStage.FinalOutcomeVerified => "Final outcome verified",
        MessageEvidenceStage.NotObserved => "Not observed",
        MessageEvidenceStage.AccessDenied => "Access denied",
        MessageEvidenceStage.Unavailable => "Unavailable",
        _ => "Not evaluated",
    };

    /// <summary>Evidence is never green: an observed stage is not a pass.</summary>
    public static string StageTone(MessageEvidenceStage stage) => stage is MessageEvidenceStage.AccessDenied or MessageEvidenceStage.Unavailable ? "attention" : "neutral";

    public static string Provider(MessageEvidenceProviderState state) => state switch
    {
        MessageEvidenceProviderState.Available => "Evidence found",
        MessageEvidenceProviderState.QueryCompletedNoEvidence => "Read: no matching evidence",
        MessageEvidenceProviderState.NotConfigured => "Not configured",
        MessageEvidenceProviderState.AccessDenied => "Access denied",
        MessageEvidenceProviderState.Unavailable => "Unavailable",
        MessageEvidenceProviderState.Blocked => "Blocked",
        MessageEvidenceProviderState.NotApplicable => "No provider",
        _ => "Not evaluated",
    };

    public static string ProviderTone(MessageEvidenceProviderState state) => state switch
    {
        MessageEvidenceProviderState.AccessDenied or MessageEvidenceProviderState.Unavailable or MessageEvidenceProviderState.Blocked => "attention",
        _ => "neutral",
    };

    public static string Certainty(MessageCorrelationCertainty certainty) => certainty switch
    {
        MessageCorrelationCertainty.ExactIdentifierMatch => "Exact identifier match",
        MessageCorrelationCertainty.ExplicitMappingObserved => "Explicit mapping observed",
        MessageCorrelationCertainty.PartialCorrelation => "Partial correlation",
        MessageCorrelationCertainty.TesterAsserted => "Stated by the tester (not verified)",
        _ => "Uncorrelated",
    };
}
