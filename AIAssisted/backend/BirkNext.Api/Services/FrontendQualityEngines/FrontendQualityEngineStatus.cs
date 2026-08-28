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
