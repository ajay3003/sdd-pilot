using BirkNext.Api.Services.ManagedEdge;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// Everything the diagnostic is allowed to do, decided before a browser exists.
///
/// The diagnostic drives a real browser at a real application, so the decision of whether it may run at all is kept
/// out of the runner and made here, where it is pure and can be tested exhaustively.
/// </summary>
public static class BrowserAutomationDiagnosticPolicy
{
    /// <summary>
    /// A neutral page that is not the target, used to prove automation works at all. The contrast between this and the
    /// target is the entire value of the diagnostic: without a control page that demonstrably works, a target failure
    /// says nothing.
    /// </summary>
    public const string DefaultControlUrl = "https://example.com/";

    /// <summary>
    /// A profile of BirkNext's own, used only by this diagnostic. It is never signed into and never used for browsing,
    /// so it accumulates no credentials — which is what makes it safe to keep between runs, and what makes deleting it
    /// unnecessary.
    /// </summary>
    public static string DefaultProfileDirectory() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BirkNext", "BrowserAutomationDiagnosticEdgeProfile");

    /// <summary>How the profile is described in a report that may be pasted into a ticket: what it is, not where it is.</summary>
    public const string ProfileDescription =
        "Dedicated BirkNext diagnostic Edge profile (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnosticEdgeProfile). "
        + "Never signed in, never the normal Edge profile.";

    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ControlNavigationTimeout = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Shorter than the control timeout on purpose. A target that terminates automation does so quickly, and a target
    /// that is merely slow should not hold the diagnostic open — an unbounded wait here is how the previous manual
    /// attempts ended up hanging.
    /// </summary>
    public static readonly TimeSpan TargetNavigationTimeout = TimeSpan.FromSeconds(25);

    public const string ProductionBlockedReason =
        "The browser automation diagnostic does not run against Production. It drives a real browser at the target "
        + "application, and that is limited to Development and QA environments.";
    public const string NoTargetBlockedReason =
        "Select a Target Environment with a frontend URL. The diagnostic navigates to the target that environment defines.";
    public const string InvalidTargetBlockedReason =
        "The Target Environment's frontend URL is not an absolute http(s) URL, so there is nothing to navigate to.";
    public const string NormalProfileBlockedReason =
        "The configured profile directory is a normal Microsoft Edge profile. The diagnostic only ever runs in a "
        + "dedicated BirkNext profile, so it cannot touch your real browser data.";
    public const string RemoteRuntimeBlockedReason =
        "The diagnostic starts a browser on this workstation, so it needs BirkNext.Api running as a local workstation runtime.";

    /// <summary>An environment the diagnostic may drive a browser at. Production is excluded, and so is anything unnamed.</summary>
    public static bool IsEligibleEnvironmentType(string? environmentType) =>
        !string.IsNullOrWhiteSpace(environmentType) &&
        !string.Equals(environmentType.Trim(), "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the diagnostic may start, and if not, why — in the same words the UI shows. Returns null when it may.
    /// </summary>
    public static string? BlockedReason(
        BrowserAutomationDiagnosticRequest request, string profileDirectory, bool isLocalWorkstation)
    {
        if (!isLocalWorkstation) return RemoteRuntimeBlockedReason;
        if (!IsEligibleEnvironmentType(request.EnvironmentType)) return ProductionBlockedReason;
        if (string.IsNullOrWhiteSpace(request.TargetUrl)) return NoTargetBlockedReason;
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return InvalidTargetBlockedReason;
        // The one guard that must never be bypassed, reusing the check the managed-Edge and proxy launchers already use.
        if (!System.IO.Path.IsPathFullyQualified(profileDirectory) || ManagedEdgePreflightService.IsNormalEdgeProfile(profileDirectory))
            return NormalProfileBlockedReason;
        return null;
    }

    /// <summary>The label shown beside the result. The wording is the product's, not the runner's.</summary>
    public static string ResultLabel(BrowserAutomationDiagnosticResult result) => result switch
    {
        BrowserAutomationDiagnosticResult.Passed => "Target automation available",
        BrowserAutomationDiagnosticResult.TargetRestricted => "Target-specific automation restriction detected",
        BrowserAutomationDiagnosticResult.RuntimeUnavailable => "Browser automation runtime unavailable",
        BrowserAutomationDiagnosticResult.ControlFailure => "Automation could not be proven on a neutral page",
        BrowserAutomationDiagnosticResult.Blocked => "Diagnostic not run",
        BrowserAutomationDiagnosticResult.Cancelled => "Diagnostic cancelled",
        BrowserAutomationDiagnosticResult.Failed => "Diagnostic failed",
        _ => "Not run",
    };

    /// <summary>
    /// What the result means, stopping exactly where the evidence stops.
    ///
    /// The restricted case is the one that matters and the one most easily overstated. BirkNext saw a page close; it
    /// did not see which control closed it, and it has no way to. So the wording offers the possibilities and leaves
    /// the question open — which is also what makes it useful to take to IT rather than an answer to argue with.
    /// </summary>
    public static string Interpretation(BrowserAutomationDiagnosticResult result) => result switch
    {
        BrowserAutomationDiagnosticResult.Passed =>
            "Playwright launched Microsoft Edge and retained control of the target page. This does not mean "
            + "authentication or MFA is automated, and it does not configure Critical E2E — no sign-in was attempted.",

        BrowserAutomationDiagnosticResult.TargetRestricted =>
            "Browser automation works in general: Playwright launched Microsoft Edge and kept control of a neutral "
            + "control page. It could not retain control of the target application. A possible cause is an "
            + "organisational or browser security policy, or another target-specific browser protection — BirkNext "
            + "observes the behaviour and cannot determine which control is responsible. No sign-in was attempted.",

        BrowserAutomationDiagnosticResult.RuntimeUnavailable =>
            "Playwright could not start Microsoft Edge, so nothing was tested. This says nothing about the target "
            + "application.",

        BrowserAutomationDiagnosticResult.ControlFailure =>
            "Automation could not be proven on a neutral page, so the target was not attempted. Until a control page "
            + "works, a failure at the target would say nothing about the target.",

        BrowserAutomationDiagnosticResult.Blocked =>
            "The diagnostic did not run, so nothing was observed.",

        BrowserAutomationDiagnosticResult.Cancelled =>
            "The diagnostic was stopped before it finished. Stages that did not run were not observed.",

        BrowserAutomationDiagnosticResult.Failed =>
            "The diagnostic did not complete. The stage results show how far it got; nothing beyond that was observed.",

        _ => "The diagnostic has not run.",
    };
}
