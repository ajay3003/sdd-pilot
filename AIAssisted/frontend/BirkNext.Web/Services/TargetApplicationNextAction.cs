using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>One recommended next step on the Target Application tab, derived from actual state. <see cref="Action"/> is a tab id
/// ("auth", "endpoint", "general"), "check" (target detection) or "application" (open Change application).</summary>
public sealed record TargetNextAction(string Label, string Action, string Reason);

/// <summary>The facts the Target Application summary reads — observed target evidence and saved configuration kept apart from runtime state.</summary>
public sealed record TargetApplicationFacts(string? ApplicationName, string? TargetUrl, TargetEnvironmentDetectionResult? Detection, bool DetectionStale,
    bool AuthenticatedSessionAvailable, bool AuthenticationConfigured);

/// <summary>
/// Next-action logic for the Target Application tab (no fixed sequence — each answer is derived from the state):
/// no application → Select application; no URL → Configure the Frontend URL; not checked / stale / unreachable → Check the target;
/// authentication required and no authenticated session → Continue to Authentication; otherwise → Open Endpoint Discovery (source-only
/// discovery never needs a sign-in, so it is also offered as the alternative).
/// </summary>
public static class TargetApplicationNextActions
{
    public static TargetNextAction Next(TargetApplicationFacts f)
    {
        if (string.IsNullOrWhiteSpace(f.ApplicationName)) return new("Select application", "application", "This environment does not say which application it represents yet.");
        if (string.IsNullOrWhiteSpace(f.TargetUrl)) return new("Configure the Frontend URL", "general", "A target check needs the environment's Frontend URL.");
        if (f.Detection is null) return new("Check target", "check", "The target has not been checked in this session.");
        if (f.DetectionStale) return new("Check target again", "check", "The Frontend URL changed since the last check.");
        if (!f.Detection.Success || f.Detection.Reachability is not (TargetReachability.Reachable or TargetReachability.AuthenticationRequired))
            return new("Check target again", "check", f.Detection.Message is { Length: > 0 } m ? m : "The target could not be reached.");
        if (f.Detection.AuthenticationRequired && !f.AuthenticatedSessionAvailable)
            return new("Continue to Authentication", "auth", f.AuthenticationConfigured
                ? "The target requires sign-in; authentication is configured but no authenticated session exists yet."
                : $"The target requires sign-in ({AuthenticationPresentation.ProviderLabel(f.Detection.DetectedAuthenticationType)} detected); set up authentication next.");
        return new("Open Endpoint Discovery", "endpoint", f.Detection.AuthenticationRequired ? "An authenticated session is available." : "The target does not require sign-in.");
    }

    public static string Reachability(TargetEnvironmentDetectionResult? d) => d is null ? "Not checked"
        : !d.Success ? "Check failed" : d.Reachability switch { TargetReachability.Reachable => "Reachable", TargetReachability.AuthenticationRequired => "Reachable · sign-in required", TargetReachability.Unreachable => "Unreachable",
            TargetReachability.Timeout => "Timed out", TargetReachability.TlsError => "TLS error", var r => r.ToString() };

    public static SemanticStatus ReachabilityStatus(TargetEnvironmentDetectionResult? d) => d is null ? SemanticStatus.NotAssessed
        : !d.Success ? SemanticStatus.Failed : d.Reachability is TargetReachability.Reachable or TargetReachability.AuthenticationRequired ? SemanticStatus.Available : SemanticStatus.Unavailable;
}
