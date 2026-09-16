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
        TargetUnreachable,    // TargetUnreachable - network-level failure, not a timeout
        TargetHttpError,      // TargetHttpError - HTTP error status from the target
        TimedOut,             // TimedOut - real timeout over the correct access path
        EnterpriseBlocked,    // EnterpriseBrowserProtectionBlocked - CDP attach refused by browser protection
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

            FrontendQualityEngineOutcomeReason.TargetUnreachable =>
                new("Target unreachable", "The target could not be reached over the network (not a timeout).", OutcomePresentationCategory.TargetUnreachable, false),

            FrontendQualityEngineOutcomeReason.TargetHttpError =>
                new("Target HTTP error", "The target answered with an HTTP error status; see the reason for the exact status.", OutcomePresentationCategory.TargetHttpError, false),

            FrontendQualityEngineOutcomeReason.TimedOut =>
                new("Timed out", "A request was issued over the correct access path and the target did not respond within the timeout period.", OutcomePresentationCategory.TimedOut, false),

            FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable =>
                new("Authenticated context not available", "The selected authenticated testing method has no usable runtime context right now.", OutcomePresentationCategory.AuthRequired, false),

            FrontendQualityEngineOutcomeReason.AuthenticatedContextExpired =>
                new("Authenticated context expired", "The authenticated API context expired and was wiped.", OutcomePresentationCategory.AuthExpired, false),

            FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked =>
                new("Blocked by enterprise browser protection", "The browser refuses debugger attachment to the target tab; BirkNext never bypasses browser protection.", OutcomePresentationCategory.EnterpriseBlocked, false),

            FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod =>
                new("Authenticated DOM unavailable for method", "The engine needs an authenticated browser DOM, which the selected authenticated testing method cannot provide.", OutcomePresentationCategory.AuthUnsupported, false),

            FrontendQualityEngineOutcomeReason.ManualOnlyMethod =>
                new("Manual verification only", "The environment's authentication method supports manual verification only; no automated authenticated access exists.", OutcomePresentationCategory.AuthUnsupported, false),

            FrontendQualityEngineOutcomeReason.DisabledInTargetEnvironment =>
                new("Disabled", "The engine is disabled in the Target Environment configuration and was not part of this review.", OutcomePresentationCategory.SettingsDisabled, false),

            _ => new("Unknown", "An unexpected outcome was encountered.", OutcomePresentationCategory.EngineFailed, false),
        };

    /// <summary>
    /// Explicit engine readiness/outcome state for the engine table: Completed (with or without findings), Blocked, Unsupported,
    /// Failed, Timed out, Cancelled, Disabled or Not selected. "Not ready" is never used as a catch-all.
    /// </summary>
    public static string StateLabel(FrontendQualityEngineOutcome outcome) => outcome.ExecutionState switch
    {
        FrontendQualityEngineExecutionState.Assessed => outcome.FindingCount is > 0 ? "Completed — findings" : "Completed — no findings",
        FrontendQualityEngineExecutionState.TimedOut => "Timed out",
        FrontendQualityEngineExecutionState.EngineError => "Failed",
        FrontendQualityEngineExecutionState.Cancelled => "Cancelled",
        FrontendQualityEngineExecutionState.Disabled => "Disabled",
        FrontendQualityEngineExecutionState.NotApplicable when outcome.OutcomeReason == FrontendQualityEngineOutcomeReason.NotSelected => "Not selected",
        FrontendQualityEngineExecutionState.NotApplicable => "Unsupported",
        _ when IsUnsupported(outcome.OutcomeReason) => "Unsupported",
        _ when outcome.OutcomeReason == FrontendQualityEngineOutcomeReason.TimedOut => "Timed out",
        _ when outcome.OutcomeReason == FrontendQualityEngineOutcomeReason.EngineError => "Failed",
        _ => "Blocked",
    };

    /// <summary>CSS modifier for the state badge.</summary>
    public static string StateClass(FrontendQualityEngineOutcome outcome) => StateLabel(outcome) switch
    {
        "Completed — findings" => "completed-findings",
        "Completed — no findings" => "completed",
        "Timed out" => "timedout",
        "Failed" => "failed",
        "Cancelled" => "cancelled",
        "Disabled" or "Not selected" => "inactive",
        "Unsupported" => "unsupported",
        _ => "blocked",
    };

    /// <summary>"Assessed" only when the engine actually ran against the target and produced a result; everything else is "Not assessed".</summary>
    public static string AssessmentLabel(FrontendQualityEngineOutcome outcome) =>
        outcome.ExecutionState == FrontendQualityEngineExecutionState.Assessed ? "Assessed"
        : outcome.OutcomeReason == FrontendQualityEngineOutcomeReason.NotSelected ? "Not selected"
        : !outcome.Enabled || outcome.ExecutionState == FrontendQualityEngineExecutionState.Disabled ? "Disabled"
        : "Not assessed";

    /// <summary>The engine cannot work with the selected access mode/method (as opposed to a prerequisite that is merely missing).</summary>
    public static bool IsUnsupported(FrontendQualityEngineOutcomeReason reason) => reason is
        FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported or
        FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod or
        FrontendQualityEngineOutcomeReason.ManualOnlyMethod;

    public static string AccessKindLabel(FrontendQualityEngineAccessKind kind) => kind switch
    {
        FrontendQualityEngineAccessKind.PublicHttp => "Public HTTP",
        FrontendQualityEngineAccessKind.AuthenticatedHttp => "Authenticated HTTP",
        FrontendQualityEngineAccessKind.AuthenticatedBrowserSession => "Authenticated browser session",
        FrontendQualityEngineAccessKind.BrowserRuntime => "Browser runtime",
        _ => kind.ToString(),
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
