using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Typed presentation mapping for canonical outcome reasons.
/// Maps each reason to user-facing label, description, and presentation category.
/// Replaces free-text semantic inference with explicit typed mappings.
/// </summary>
public static class FrontendQualityEngineOutcomePresentation
{
    public sealed record OutcomePresentation(
        string Label,
        string Description,
        OutcomePresentationCategory Category,
        bool CountsAsAssessed);

    public enum OutcomePresentationCategory
    {
        Success,              // None - successfully assessed
        NotApplicable,        // NotSelected - not part of this review
        PolicyBlocked,        // DeploymentPolicyBlocked - policy prevents execution
        SettingsDisabled,     // DisabledInSystemSettings - disabled by config
        NotReady,             // ReadinessUnavailable - would run but not ready
        AuthUnsupported,      // AuthenticationModeUnsupported - engine doesn't support auth
        AuthRequired,         // AuthenticationRequired - auth needed but unavailable
        AuthExpired,          // AuthenticationExpired - auth session died
        AuthCancelled,        // AuthenticationCancelled - user cancelled
        OriginShift,          // UnexpectedOrigin - page navigated away during auth
        SessionUnavailable,   // SessionUnavailable - actual session is unavailable
        ResourceUnavailable,  // ResourceUnavailable - authenticated resource dead
        TargetRejected,       // TargetPolicyRejected - URL validation failed
        EngineMissing,        // EngineUnavailable - tool/runtime missing
        EngineFailed,         // EngineError - execution failure
        Cancelled,            // Cancelled - review cancelled
    }

    public static OutcomePresentation GetPresentation(FrontendQualityEngineOutcomeReason reason) =>
        reason switch
        {
            FrontendQualityEngineOutcomeReason.None =>
                new("Assessed", "Engine successfully assessed the target.", OutcomePresentationCategory.Success, true),

            FrontendQualityEngineOutcomeReason.NotSelected =>
                new("Not selected", "This engine was not selected for this review.", OutcomePresentationCategory.NotApplicable, false),

            FrontendQualityEngineOutcomeReason.BlockedByDeploymentPolicy =>
                new("Unavailable on this deployment", "Engine is blocked by deployment policy.", OutcomePresentationCategory.PolicyBlocked, false),

            FrontendQualityEngineOutcomeReason.DisabledInSystemSettings =>
                new("Disabled in System Settings", "Engine is disabled in the system configuration.", OutcomePresentationCategory.SettingsDisabled, false),

            FrontendQualityEngineOutcomeReason.ReadinessUnavailable =>
                new("Not ready", "Engine is enabled but not ready to execute.", OutcomePresentationCategory.NotReady, false),

            FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported =>
                new("Not supported for authenticated review", "This engine does not support authenticated mode.", OutcomePresentationCategory.AuthUnsupported, false),

            FrontendQualityEngineOutcomeReason.AuthenticationRequired =>
                new("Authentication required", "An authenticated browser session is required but not available.", OutcomePresentationCategory.AuthRequired, false),

            FrontendQualityEngineOutcomeReason.AuthenticationExpired =>
                new("Authentication expired", "The authenticated session has expired.", OutcomePresentationCategory.AuthExpired, false),

            FrontendQualityEngineOutcomeReason.AuthenticationCancelled =>
                new("Authentication cancelled", "The user cancelled the authentication flow.", OutcomePresentationCategory.AuthCancelled, false),

            FrontendQualityEngineOutcomeReason.UnexpectedOrigin =>
                new("Page navigated away", "The authenticated page navigated to an unexpected origin during review.", OutcomePresentationCategory.OriginShift, false),

            FrontendQualityEngineOutcomeReason.SessionUnavailable =>
                new("Session unavailable", "The authenticated session is no longer available.", OutcomePresentationCategory.SessionUnavailable, false),

            FrontendQualityEngineOutcomeReason.ResourceUnavailable =>
                new("Resource unavailable", "The authenticated browser session is no longer usable.", OutcomePresentationCategory.ResourceUnavailable, false),

            FrontendQualityEngineOutcomeReason.TargetPolicyRejected =>
                new("Target rejected", "The target URL does not meet access policy requirements.", OutcomePresentationCategory.TargetRejected, false),

            FrontendQualityEngineOutcomeReason.EngineUnavailable =>
                new("Engine unavailable", "A required engine or runtime dependency is not available.", OutcomePresentationCategory.EngineMissing, false),

            FrontendQualityEngineOutcomeReason.EngineError =>
                new("Engine error", "The engine encountered an unexpected error during execution.", OutcomePresentationCategory.EngineFailed, false),

            FrontendQualityEngineOutcomeReason.Cancelled =>
                new("Cancelled", "The review or engine execution was cancelled.", OutcomePresentationCategory.Cancelled, false),

            _ => new("Unknown", "An unexpected outcome was encountered.", OutcomePresentationCategory.EngineFailed, false),
        };

    public static string GetLabel(FrontendQualityEngineOutcomeReason reason) =>
        GetPresentation(reason).Label;

    public static string GetDescription(FrontendQualityEngineOutcomeReason reason) =>
        GetPresentation(reason).Description;

    public static OutcomePresentationCategory GetCategory(FrontendQualityEngineOutcomeReason reason) =>
        GetPresentation(reason).Category;

    public static bool IsAssessed(FrontendQualityEngineOutcomeReason reason) =>
        GetPresentation(reason).CountsAsAssessed;
}
