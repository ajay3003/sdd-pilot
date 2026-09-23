using BirkNext.Api.Services.ManagedEdge;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// Everything the diagnostic is allowed to do, and what its two modes mean together — decided without a browser, so it
/// is pure and can be tested exhaustively.
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
    /// One profile per mode, under a shared diagnostic root. Separate because the two modes run one after the other
    /// against the same Edge installation, and a shared user-data directory is how two Chromium launches end up
    /// fighting over a lock rather than reporting what they were asked to report.
    ///
    /// Neither is ever signed into and neither is browsed in, so they accumulate no credentials — which is what makes
    /// keeping them between runs safe, and deleting them pointless.
    /// </summary>
    public static string ProfileRoot() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BirkNext", "BrowserAutomationDiagnostic");

    public static string ProfileDirectory(BrowserAutomationDiagnosticMode mode) =>
        System.IO.Path.Combine(ProfileRoot(), mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed");

    /// <summary>How a profile is described in a report that may be pasted into a ticket: what it is, not where it is.</summary>
    public static string ProfileDescription(BrowserAutomationDiagnosticMode mode) =>
        $"Dedicated BirkNext diagnostic Edge profile (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnostic\\"
        + $"{(mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed")}). "
        + "Never signed in, never the normal Edge profile.";

    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ControlNavigationTimeout = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Shorter than the control timeout on purpose. A target that terminates automation does so quickly, and a target
    /// that is merely slow should not hold the diagnostic open.
    /// </summary>
    public static readonly TimeSpan TargetNavigationTimeout = TimeSpan.FromSeconds(25);
    /// <summary>The post-navigation controllability probe is a single read; it should answer fast or not at all.</summary>
    public static readonly TimeSpan ControlCheckTimeout = TimeSpan.FromSeconds(10);

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
    public const string AlreadyRunningReason =
        "A browser automation diagnostic is already running for this Target Environment. Wait for it to finish; two "
        + "runs would share a browser profile and report each other's contention rather than the target's behaviour.";

    /// <summary>An environment the diagnostic may drive a browser at. Production is excluded, and so is anything unnamed.</summary>
    public static bool IsEligibleEnvironmentType(string? environmentType) =>
        !string.IsNullOrWhiteSpace(environmentType) &&
        !string.Equals(environmentType.Trim(), "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the diagnostic may start, and if not, why — in the same words the UI shows. Null when it may.</summary>
    public static string? BlockedReason(
        BrowserAutomationDiagnosticRequest request, IReadOnlyList<string> profileDirectories, bool isLocalWorkstation)
    {
        if (!isLocalWorkstation) return RemoteRuntimeBlockedReason;
        if (!IsEligibleEnvironmentType(request.EnvironmentType)) return ProductionBlockedReason;
        if (string.IsNullOrWhiteSpace(request.TargetUrl)) return NoTargetBlockedReason;
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return InvalidTargetBlockedReason;
        // The one guard that must never be bypassed, reusing the check the managed-Edge and proxy launchers already
        // use — and applied to EVERY mode's profile, not just the first.
        foreach (var directory in profileDirectories)
            if (!System.IO.Path.IsPathFullyQualified(directory) || ManagedEdgePreflightService.IsNormalEdgeProfile(directory))
                return NormalProfileBlockedReason;
        return null;
    }

    // ── Reading the two modes together ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The comparison. Each branch is a different conversation with IT, which is why they are kept apart rather than
    /// collapsed into "something is blocked".
    /// </summary>
    public static BrowserAutomationDiagnosticComparison Compare(
        BrowserAutomationDiagnosticModeReport headed, BrowserAutomationDiagnosticModeReport headless)
    {
        if (headed.Result == BrowserAutomationDiagnosticModeResult.Cancelled ||
            headless.Result == BrowserAutomationDiagnosticModeResult.Cancelled)
            return BrowserAutomationDiagnosticComparison.Cancelled;

        // Nothing started anywhere: there is no evidence about the target, in either direction.
        if (headed.Result == BrowserAutomationDiagnosticModeResult.RuntimeUnavailable &&
            headless.Result == BrowserAutomationDiagnosticModeResult.RuntimeUnavailable)
            return BrowserAutomationDiagnosticComparison.PlaywrightRuntimeUnavailable;

        // Neither mode proved automation on a neutral page, so neither mode's target outcome means anything.
        if (!headed.ControlPageProven && !headless.ControlPageProven)
            return headed.Result == BrowserAutomationDiagnosticModeResult.ControlFailure ||
                   headless.Result == BrowserAutomationDiagnosticModeResult.ControlFailure
                ? BrowserAutomationDiagnosticComparison.ControlPageFailure
                : BrowserAutomationDiagnosticComparison.MixedOrInconclusive;

        var headedAvailable = headed.TargetControlAvailable;
        var headlessAvailable = headless.TargetControlAvailable;
        var headedRestricted = headed.Result == BrowserAutomationDiagnosticModeResult.TargetRestricted;
        var headlessRestricted = headless.Result == BrowserAutomationDiagnosticModeResult.TargetRestricted;

        if (headedAvailable && headlessAvailable) return BrowserAutomationDiagnosticComparison.AutomationAvailable;
        if (headedRestricted && headlessRestricted) return BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes;
        if (headedAvailable && headlessRestricted) return BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted;
        if (headlessAvailable && headedRestricted) return BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted;

        // One mode reached a conclusion and the other did not get far enough to agree or disagree with it. Saying
        // "restricted" here would report a one-mode observation as a two-mode finding.
        return BrowserAutomationDiagnosticComparison.MixedOrInconclusive;
    }

    public static string ModeResultLabel(BrowserAutomationDiagnosticModeResult result) => result switch
    {
        BrowserAutomationDiagnosticModeResult.Available => "Target automation available",
        BrowserAutomationDiagnosticModeResult.TargetRestricted => "Target automation blocked",
        BrowserAutomationDiagnosticModeResult.RuntimeUnavailable => "Browser could not start",
        BrowserAutomationDiagnosticModeResult.ControlFailure => "Automation unproven on a neutral page",
        BrowserAutomationDiagnosticModeResult.Cancelled => "Cancelled",
        BrowserAutomationDiagnosticModeResult.Failed => "Failed",
        _ => "Not run",
    };

    public static string ResultLabel(BrowserAutomationDiagnosticComparison result) => result switch
    {
        BrowserAutomationDiagnosticComparison.AutomationAvailable => "Browser automation available",
        BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes =>
            "Target-specific automation restriction detected in both modes",
        BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted => "Headless-specific automation restriction detected",
        BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted => "Headed-specific automation restriction detected",
        BrowserAutomationDiagnosticComparison.PlaywrightRuntimeUnavailable => "Browser automation runtime unavailable",
        BrowserAutomationDiagnosticComparison.ControlPageFailure => "Automation could not be proven on a neutral page",
        BrowserAutomationDiagnosticComparison.MixedOrInconclusive => "Inconclusive",
        BrowserAutomationDiagnosticComparison.Cancelled => "Diagnostic cancelled",
        BrowserAutomationDiagnosticComparison.Blocked => "Diagnostic not run",
        _ => "Not run",
    };

    /// <summary>
    /// What the comparison means, stopping exactly where the evidence stops.
    ///
    /// The restricted cases are the ones that matter and the ones most easily overstated. BirkNext saw a page close;
    /// it did not see which control closed it, and it has no way to. In particular it does NOT say DevTools is
    /// disabled — CDP worked fine on neutral pages, so a global claim would be contradicted by this very run. The
    /// wording offers the possibilities and leaves the question open, which is what makes it worth taking to IT.
    /// </summary>
    public static string Interpretation(BrowserAutomationDiagnosticComparison result) => result switch
    {
        BrowserAutomationDiagnosticComparison.AutomationAvailable =>
            "Playwright retained control of the target in both headed and headless Microsoft Edge. Browser automation "
            + "is not the blocker. This does not establish that authentication, MFA or Conditional Access work under "
            + "automation — that is assessed separately by the Headless Authentication & Session Control Diagnostic. "
            + "No sign-in was attempted.",

        BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes =>
            "Playwright browser automation works on neutral pages in both headed and headless Microsoft Edge, and "
            + "automation control cannot be retained on the configured target in either mode. This is consistent with "
            + "a target or security-context-specific browser automation restriction. It does not identify which "
            + "organisational control is responsible, and it does not mean browser developer tooling is disabled "
            + "generally — the same automation worked on the control page. No sign-in was attempted.",

        BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted =>
            "Playwright retained control of the target in headed Microsoft Edge but not in headless. Automation works "
            + "on neutral pages in both modes, so this is consistent with a restriction that distinguishes headless "
            + "automation for this target or security context. This matters for unattended CI, which has no visible "
            + "browser. BirkNext cannot determine which control is responsible. No sign-in was attempted.",

        BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted =>
            "Playwright retained control of the target in headless Microsoft Edge but not in headed. Automation works "
            + "on neutral pages in both modes. BirkNext observes the behaviour and cannot determine which control is "
            + "responsible. No sign-in was attempted.",

        BrowserAutomationDiagnosticComparison.PlaywrightRuntimeUnavailable =>
            "Playwright could not start Microsoft Edge in either mode, so nothing was tested. This says nothing about "
            + "the target application.",

        BrowserAutomationDiagnosticComparison.ControlPageFailure =>
            "Automation could not be proven on a neutral page, so no conclusion about the target is available. Until a "
            + "control page works, a failure at the target would say nothing about the target.",

        BrowserAutomationDiagnosticComparison.MixedOrInconclusive =>
            "The two modes did not produce results that support a single conclusion. Read each mode's stages: one of "
            + "them did not get far enough to agree or disagree with the other.",

        BrowserAutomationDiagnosticComparison.Cancelled =>
            "The diagnostic was stopped before it finished. Stages that did not run were not observed.",

        BrowserAutomationDiagnosticComparison.Blocked =>
            "The diagnostic did not run, so nothing was observed.",

        _ => "The diagnostic has not run.",
    };

    /// <summary>
    /// How this diagnostic relates to browser developer tooling, stated once so nobody has to guess.
    ///
    /// Playwright drives Chromium and Edge through automation interfaces that use the DevTools Protocol internally.
    /// That is not the same as a person opening DevTools, and a restriction that denies automation on one target is
    /// not the same as DevTools being switched off — which this run disproves by controlling the neutral page.
    /// </summary>
    public const string ArchitectureNote =
        "Playwright controls Microsoft Edge through browser automation interfaces that rely on Chromium DevTools "
        + "Protocol capabilities internally. Headless mode does not avoid those capabilities: it is the same "
        + "automation without a visible window, which is why both modes are tested separately rather than one being "
        + "inferred from the other. A restriction observed here applies to automation control of the configured "
        + "target; it is not evidence that browser developer tooling is disabled generally.";

    /// <summary>
    /// The prerequisite line the authentication diagnostic's readiness is built on, in the reader's words.
    /// Headless, and only headless: a target that can only be automated with a window open on somebody's desk is not
    /// a target that unattended CI can authenticate against.
    /// </summary>
    public static string AuthenticationPrerequisite(BrowserAutomationDiagnosticReport report)
    {
        if (report.HeadlessTargetControlAvailable)
            return "Headless target control is available, so the Headless Authentication & Session Control Diagnostic can run.";

        // The headed-only case is the one that misleads: the reader has just watched automation drive the target
        // successfully, so "not available" reads as a contradiction unless the sentence says which mode was asked about.
        var headedQualifier = report.Headed?.TargetControlAvailable == true
            ? " Headed mode did retain control of the target, but headed-only success is not sufficient: the "
              + "authentication diagnostic runs headless, as unattended CI would."
            : "";

        return "Headless target control is not available, so the Headless Authentication & Session Control Diagnostic "
            + "cannot yet assess MFA, Conditional Access or session control reliably." + headedQualifier;
    }
}
