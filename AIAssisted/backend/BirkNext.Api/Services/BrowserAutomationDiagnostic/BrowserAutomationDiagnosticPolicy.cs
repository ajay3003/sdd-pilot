using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.ManagedEdge;

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

    /// <summary>
    /// One profile per TARGET and per mode: <c>…\BrowserAutomationDiagnostic\&lt;target key&gt;\Headed|Headless</c>. The key
    /// is a hash of the Target Environment id and the target's canonical origin, so two targets never share a
    /// user-data directory (and the run lock, keyed by the directories, never makes one target wait on another).
    /// </summary>
    public static string ProfileDirectory(BrowserAutomationDiagnosticRequest request, BrowserAutomationDiagnosticMode mode) =>
        System.IO.Path.Combine(ProfileRoot(), TargetProfileKey(request),
            mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed");

    /// <summary>Stable, non-reversible folder name for a target. Contains neither the URL nor the id.</summary>
    public static string TargetProfileKey(BrowserAutomationDiagnosticRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.TargetEnvironmentId}\n{BrowserAutomationTargetLocationPolicy.CanonicalOrigin(request.TargetUrl) ?? request.TargetUrl}")))[..16]
            .ToLowerInvariant();

    /// <summary>How a profile is described in a report that may be pasted into a ticket: what it is, not where it is.</summary>
    public static string ProfileDescription(BrowserAutomationDiagnosticMode mode) =>
        $"Dedicated BirkNext diagnostic Edge profile for this target (%LOCALAPPDATA%\\BirkNext\\BrowserAutomationDiagnostic\\<target>\\"
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

    public const string UnrecognisedEnvironmentBlockedReason =
        "The browser automation diagnostic runs only against Target Environments explicitly typed as non-production "
        + "(Local, Development, QA, Test or RC). Set the Target Environment type; an unknown or custom type is not assumed safe.";
    public const string ProductionHostBlockedReason =
        "The target's hostname indicates a Production environment, so the diagnostic does not run against it, whatever "
        + "the Target Environment type says.";
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

    /// <summary>The environment types that are explicitly non-production. Anything else — Production, Custom, blank — is refused.</summary>
    public static readonly IReadOnlyList<string> NonProductionEnvironmentTypes = ["Local", "Development", "QA", "Test", "RC"];

    /// <summary>An environment the diagnostic may drive a browser at: an explicit allow-list, never "anything but Production".</summary>
    public static bool IsEligibleEnvironmentType(string? environmentType) =>
        !string.IsNullOrWhiteSpace(environmentType) &&
        NonProductionEnvironmentTypes.Contains(environmentType.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsProductionType(string? environmentType) =>
        environmentType?.Trim() is { } t &&
        (t.Equals("Production", StringComparison.OrdinalIgnoreCase) || t.Equals("Prod", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the diagnostic may start, and if not, why — in the same words the UI shows. Null when it may.</summary>
    public static string? BlockedReason(
        BrowserAutomationDiagnosticRequest request, IReadOnlyList<string> profileDirectories, bool isLocalWorkstation)
    {
        if (!isLocalWorkstation) return RemoteRuntimeBlockedReason;
        if (IsProductionType(request.EnvironmentType)) return ProductionBlockedReason;
        if (!IsEligibleEnvironmentType(request.EnvironmentType)) return UnrecognisedEnvironmentBlockedReason;
        if (string.IsNullOrWhiteSpace(request.TargetUrl)) return NoTargetBlockedReason;
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return InvalidTargetBlockedReason;
        // The type is what someone typed; the hostname is what the browser will actually visit.
        if (TargetEnvironmentDetection.TargetEnvironmentTypeClassifier.Infer(target.IdnHost) == Models.FrontendEnvironmentType.Production)
            return ProductionHostBlockedReason;
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

        // "Available" here is browser control retained through the tested path — wherever that path ended. Which page
        // was controlled is each mode's own evidence, and the interpretation says it; the comparison does not.
        var headedAvailable = headed.BrowserControlRetained;
        var headlessAvailable = headless.BrowserControlRetained;
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
        BrowserAutomationDiagnosticModeResult.Available => "Target application controllable",
        BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary => "Automation available through authentication redirect",
        BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed => "Automation available; target application not identified",
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
            "Playwright remained in control of the browser through the tested path in both headed and headless "
            + "Microsoft Edge. That is a statement about browser automation, not about which page was reached. It does "
            + "not establish that authentication, MFA or Conditional Access work under automation — that is assessed "
            + "separately by the Headless Authentication & Session Control Diagnostic. No sign-in was attempted.",

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
    /// The interpretation, completed with WHERE each mode's browser ended up. The comparison alone cannot say it:
    /// "automation available" with the browser on Microsoft Entra and "automation available" on the target application
    /// are different findings, and the second must never be read into the first.
    /// </summary>
    public static string Interpretation(
        BrowserAutomationDiagnosticComparison result,
        BrowserAutomationDiagnosticModeReport? headed,
        BrowserAutomationDiagnosticModeReport? headless)
    {
        var text = new System.Text.StringBuilder(Interpretation(result));
        var retained = new[] { headed, headless }.Where(m => m is { BrowserControlRetained: true }).Select(m => m!).ToList();
        if (retained.Count == 0)
        {
            if (NavigationFailureSentence(headed, headless) is { } failed) text.Append(' ').Append(failed);
            return text.ToString();
        }

        text.Append(' ');
        if (retained.All(m => m.Result == BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary))
        {
            var host = retained.Select(m => m.Target?.AuthenticationHost).FirstOrDefault(h => h is not null) ?? "the identity provider";
            text.Append($"{Modes(retained)}, the target redirected to authentication ({host}) and Playwright remained in "
                + "control of the browser through that handoff. This proves browser automation is available through the "
                + "authentication handoff. It does NOT prove that the authenticated target application is controllable: "
                + "the target application was not yet reached. The next diagnostic should evaluate unattended "
                + "authentication, MFA, Conditional Access and session control.");
        }
        else if (retained.All(m => m.TargetApplicationIdentified))
        {
            text.Append($"{Modes(retained)}, the page Playwright controlled was identified as the target application "
                + $"({retained[0].Target?.IdentificationEvidence.TrimEnd('.')}) and stayed controllable through the stability window.");
            if (retained.FirstOrDefault(m => m.Target?.AuthenticationRedirect == BrowserAutomationEvidenceAnswer.Yes) is { } handoff)
                text.Append($" The target also sent the browser to authentication ({handoff.Target!.AuthenticationHost}"
                    + (handoff.Target.AuthenticationInSecondaryPage ? ", in a second page it opened" : "")
                    + "): the application shell was reached, the authenticated application was not.");
            text.Append(" This does NOT prove that the authenticated target application is controllable.");
        }
        else
        {
            text.AppendJoin(' ', retained.Select(m => $"{(m.Headless ? "Headless" : "Headed")}: {LocationSentence(m)}"));
        }

        text.Append(' ').Append(DevToolsSeparation);
        return text.ToString();

        static string Modes(List<BrowserAutomationDiagnosticModeReport> modes) =>
            modes.Count == 2 ? "In both modes" : modes[0].Headless ? "In headless mode" : "In headed mode";
    }

    /// <summary>
    /// What the browser reported when the target navigation itself failed, per mode — or once, when both modes saw the
    /// same thing. The observation only: authentication was never reached, so nothing about it is said beyond that.
    /// </summary>
    private static string? NavigationFailureSentence(
        BrowserAutomationDiagnosticModeReport? headed, BrowserAutomationDiagnosticModeReport? headless)
    {
        var failed = new[] { headed, headless }
            // A target that closed the page is the restriction finding, already explained by the comparison itself.
            .Where(m => m?.Target?.NavigationFailure is { Category: not BrowserNavigationFailureCategory.TargetClosed })
            .Select(m => (Mode: m!.Headless ? "headless" : "headed", Failure: m.Target!.NavigationFailure!))
            .ToList();
        if (failed.Count == 0) return null;

        static string Observed(BrowserNavigationFailureEvidence f) =>
            f.BrowserErrorCode is { } code ? $"the browser reported {code} ({f.CategoryLabel})"
            : $"{f.ExceptionType}, with no recognised browser error code ({f.CategoryLabel})";

        var same = failed.Count == 2 && failed[0].Failure.BrowserErrorCode == failed[1].Failure.BrowserErrorCode
                   && failed[0].Failure.Category == failed[1].Failure.Category;
        var observed = same
            ? $"In both modes, target navigation failed before browser control could be established: {Observed(failed[0].Failure)}."
            : string.Join(' ', failed.Select(f =>
                $"In {f.Mode} mode, target navigation failed before browser control could be established: {Observed(f.Failure)}."));
        return observed + " Authentication was not reached, so MFA, Conditional Access and session control were not assessed.";
    }

    /// <summary>One mode's final location, in a sentence.</summary>
    public static string LocationSentence(BrowserAutomationDiagnosticModeReport mode) => mode.Target?.FinalLocation switch
    {
        BrowserAutomationFinalLocation.TargetOrigin when mode.TargetApplicationIdentified =>
            "the target application was identified and stayed controllable.",
        BrowserAutomationFinalLocation.TargetOrigin =>
            "the browser is on the target origin, but the target application was not identified.",
        BrowserAutomationFinalLocation.AuthenticationAuthority =>
            $"the target redirected to authentication ({mode.Target.AuthenticationHost}); control was retained through the handoff, and the target application was not yet reached.",
        BrowserAutomationFinalLocation.SessionControlProxy =>
            $"the page is delivered through a session-control proxy host ({mode.Target.FinalHost}); control was retained, and the target application was not identified.",
        BrowserAutomationFinalLocation.OtherOrigin =>
            $"the browser ended at {mode.Target.FinalHost ?? "another origin"}, which is neither the target origin nor a recognised authentication authority.",
        _ => "the final location could not be read.",
    };

    /// <summary>Said every time control is reported available, because it is the inference most often drawn wrongly.</summary>
    public const string DevToolsSeparation =
        "Corporate Edge visible DevTools policy is a separate control and is not inferred from this result.";

    /// <summary>
    /// What the TargetApplication stage says, from the evidence alone. PASS only when the page Playwright controls
    /// was identified as the target application; an authentication redirect is an expected boundary, not a failure.
    /// </summary>
    public static (BrowserAutomationDiagnosticStageState State, string Detail) TargetApplicationStage(BrowserAutomationTargetEvidence evidence) =>
        evidence switch
        {
            { TargetApplicationIdentified: BrowserAutomationEvidenceAnswer.Yes, AuthenticationRedirect: BrowserAutomationEvidenceAnswer.Yes } =>
                (BrowserAutomationDiagnosticStageState.Passed, $"Identified by: {evidence.IdentificationEvidence} "
                    + $"The target also sent the browser to authentication ({evidence.AuthenticationHost}"
                    + (evidence.AuthenticationInSecondaryPage ? ", in a second page the target opened" : "")
                    + "): the application shell is reached, the authenticated application is not."),
            { TargetApplicationIdentified: BrowserAutomationEvidenceAnswer.Yes } =>
                (BrowserAutomationDiagnosticStageState.Passed, $"Identified by: {evidence.IdentificationEvidence}"),
            { FinalLocation: BrowserAutomationFinalLocation.AuthenticationAuthority } =>
                (BrowserAutomationDiagnosticStageState.NotReached,
                    $"Not yet reached — authentication required first. The browser is at {evidence.AuthenticationHost}. "
                    + "Whether the application can be reached after sign-in is assessed by the Headless Authentication "
                    + "& Session Control Diagnostic."),
            { FinalLocation: BrowserAutomationFinalLocation.TargetOrigin } =>
                (BrowserAutomationDiagnosticStageState.Unknown, evidence.IdentificationEvidence),
            _ => (BrowserAutomationDiagnosticStageState.NotReached, evidence.IdentificationEvidence),
        };

    /// <summary>
    /// What a lost-control stage says. The same TargetClosedException is a navigation restriction while navigating,
    /// a control restriction just after, and a late loss during the stability window — three different findings.
    /// </summary>
    public static string RestrictionDetail(
        BrowserAutomationFailurePhase phase, string? exceptionType, BrowserAutomationTargetEvidence? target)
    {
        var signal = exceptionType is not null
            ? $"Playwright reported {exceptionType}"
            : target?.LifecycleEvents.LastOrDefault(e => e.Kind != BrowserAutomationLifecycleEventKind.PageOpened) is { } e
                ? $"Playwright reported {e.Kind} at {e.AtMs} ms"
                : "the page was no longer available";
        return phase switch
        {
            BrowserAutomationFailurePhase.DuringTargetNavigation =>
                $"{Upper(signal)} while navigating to the target application: the Playwright-controlled page or context was closed. Target-navigation restriction.",
            BrowserAutomationFailurePhase.AfterTargetNavigation =>
                $"The navigation was accepted, then {Lower(signal)} before the page could be probed. Target-control restriction.",
            BrowserAutomationFailurePhase.DuringStabilityWindow =>
                $"The navigation was accepted and the first probe succeeded, then {Lower(signal)} during the stability window. "
                + "Control was lost late, after an initial success.",
            _ => $"{Upper(signal)}.",
        };

        static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
        static string Upper(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    /// <summary>
    /// Three independent controls, reported apart. The Playwright line is the only one this run has evidence for; the
    /// other two say plainly that they were not observed, rather than being inferred from it in either direction.
    /// </summary>
    public static List<BrowserAutomationControlDimension> ControlDimensions(
        BrowserAutomationDiagnosticComparison result,
        BrowserAutomationDiagnosticModeReport? headed,
        BrowserAutomationDiagnosticModeReport? headless,
        BrowserAutomationPolicySnapshot? policies = null)
    {
        policies ??= BrowserAutomationPolicySnapshot.Unknown;
        static string ModeState(BrowserAutomationDiagnosticModeReport? mode) => mode switch
        {
            { BrowserControlRetained: true } => "available",
            { Result: BrowserAutomationDiagnosticModeResult.TargetRestricted } => "blocked on this target",
            _ => "not determined",
        };

        var playwright = result switch
        {
            BrowserAutomationDiagnosticComparison.AutomationAvailable => "Available",
            BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes => "Blocked on this target",
            BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted => "Available headed; blocked headless",
            BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted => "Available headless; blocked headed",
            _ => "Not determined",
        };

        return
        [
            new("Visible DevTools policy (F12 / Inspect)", policies.DeveloperTools switch
                {
                    EdgeDeveloperToolsPolicyStatus.Disallowed => "Disallowed by Edge policy",
                    EdgeDeveloperToolsPolicyStatus.Allowed => "Allowed by Edge policy",
                    EdgeDeveloperToolsPolicyStatus.AllowedExceptForceInstalledExtensions => "Allowed except on force-installed extensions",
                    EdgeDeveloperToolsPolicyStatus.NotConfigured => "Not configured in Edge policy",
                    _ => "Unknown",
                },
                "Read-only from the Edge DeveloperToolsAvailability policy value. It is not inferred from the Playwright "
                + "result, and a Playwright result, pass or fail, says nothing about it. Not configured does not rule out "
                + "other restrictions."),
            new("Remote debugging policy (RemoteDebuggingAllowed)", policies.RemoteDebugging switch
                {
                    EdgeRemoteDebuggingPolicyStatus.Allowed => "Allowed",
                    EdgeRemoteDebuggingPolicyStatus.Blocked => "Blocked",
                    EdgeRemoteDebuggingPolicyStatus.NotConfigured => "Not configured",
                    _ => "Unknown",
                },
                "Read-only from the Edge RemoteDebuggingAllowed policy value. It governs attaching to an Edge started "
                + "with remote debugging; Playwright launching its own Edge is observed separately below."),
            new("Playwright-owned browser automation", playwright,
                $"Observed in this run: headed {ModeState(headed)}, headless {ModeState(headless)}. Playwright launched "
                + "its own Edge against a dedicated profile."),
            new("CDP attach to an existing or protected Edge", "Not tested",
                "This diagnostic never attaches to an Edge it did not start, so it has no evidence about attach permission. "
                + "No earlier attach result is stored with this report."),
        ];
    }

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
        if (report.HeadlessAutomationControlAfterTargetNavigation)
            return report.Headless?.Target?.FinalLocation switch
            {
                BrowserAutomationFinalLocation.AuthenticationAuthority =>
                    "Headless browser automation remained controllable through the target's authentication redirect "
                    + $"({report.Headless.Target.AuthenticationHost}), so the Headless Authentication & Session Control "
                    + "Diagnostic can run and inspect the sign-in.",
                BrowserAutomationFinalLocation.SessionControlProxy =>
                    "Headless browser automation remained controllable through the target's session-control proxy, so the "
                    + "Headless Authentication & Session Control Diagnostic can run.",
                _ => "Headless browser automation remained controllable on the target origin, so the Headless "
                    + "Authentication & Session Control Diagnostic can run.",
            };

        // Control retained, but not along the target's path: say so, or "not demonstrated" contradicts a green stage.
        var elsewhere = report.Headless is { BrowserControlRetained: true } headless
            ? $" Headless automation stayed controllable, but the browser ended at {headless.Target?.FinalHost ?? "an unrecognised location"}, "
              + "which is neither the target origin nor a recognised authentication handoff."
            : "";

        // The headed-only case is the one that misleads: the reader has just watched automation drive the target
        // successfully, so "not available" reads as a contradiction unless the sentence says which mode was asked about.
        var headedQualifier = report.Headed?.ControlRetainedThroughTargetNavigation == true
            ? " Headed mode did retain control through the target navigation, but headed-only success is not "
              + "sufficient: the authentication diagnostic runs headless, as unattended CI would."
            : "";

        return "Headless browser control through the target navigation is not demonstrated, so the Headless "
            + "Authentication & Session Control Diagnostic cannot yet assess MFA, Conditional Access or session "
            + "control reliably." + elsewhere + headedQualifier;
    }
}
