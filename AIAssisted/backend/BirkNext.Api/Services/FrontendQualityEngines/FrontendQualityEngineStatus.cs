namespace BirkNext.Api.Services.FrontendQualityEngines;

public enum FrontendQualityEngineUnavailableReason
{
    None,
    BlockedByDeploymentPolicy,
    DisabledInSystemSettings,
    RuntimeUnavailable,
    RuntimeStatusUnknown,
    NotApplicableToReview,
    AuthenticationModeUnsupported,
    /// <summary>Engine is not active for this review (disabled or deselected in the saved configuration); readiness was not probed.</summary>
    NotActiveForReview,
    /// <summary>The target is not a server-registered trusted profile (Passive Web Security: FrontendPassiveSecurity:TrustedProfiles).</summary>
    TargetNotTrusted,
    ProductionBlocked,
    /// <summary>Passive Web Security: the profile is registered, but for a different origin than this review's target.</summary>
    TargetOriginNotTrusted,
}

public sealed record FrontendQualityEngineStatus(
    FrontendQualityEngineId EngineId,
    string DisplayName,
    bool Layer1Allowed,
    bool Layer2Enabled,
    FrontendQualityEngineReadiness Layer3Readiness,
    bool Selectable,
    bool? Selected,
    bool AuthModeSupported,
    bool Available,
    bool EligibleToExecute,
    IReadOnlyList<FrontendQualityEngineUnavailableReason> Reasons);

public sealed record FrontendQualityEngineStatusQuery(
    ReviewAuthenticationMode AuthMode = ReviewAuthenticationMode.Anonymous,
    FrontendQualityEngineSelectionContext? Selection = null);

public sealed record FrontendQualityEngineStatusReport(
    IReadOnlyList<FrontendQualityEngineStatus> Engines,
    DateTime CheckedAtUtc);
