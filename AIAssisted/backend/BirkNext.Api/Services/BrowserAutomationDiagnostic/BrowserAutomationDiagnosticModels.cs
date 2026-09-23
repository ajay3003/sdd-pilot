using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// The two ways Playwright can own Microsoft Edge.
///
/// They are run independently and neither is inferred from the other. Headless is not "the same browser without a
/// window": it is a different launch, and whether automation control survives a protected target in one says nothing
/// about the other. Unattended CI would use headless, so its result is the one that decides what is actually possible.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticMode { Headed, Headless }

/// <summary>
/// The stages of one mode, in the order they run. The ORDER is the evidence: a failure at the target means something
/// only because everything before it succeeded, and a failure before the target means this mode learned nothing about
/// the target at all.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStage
{
    /// <summary>Playwright and a Microsoft Edge installation are present.</summary>
    Runtime,
    /// <summary>Playwright started Microsoft Edge in this mode.</summary>
    EdgeLaunch,
    /// <summary>A persistent context exists against this mode's dedicated profile.</summary>
    PersistentContext,
    /// <summary>about:blank is open and Playwright can still operate on it.</summary>
    BlankPage,
    /// <summary>A neutral control page navigated and stayed under automation control.</summary>
    ControlPage,
    /// <summary>Navigation to the configured target URL was issued and the browser accepted it (response committed).</summary>
    TargetNavigation,
    /// <summary>
    /// After the navigation — and any redirect it led to — settled, a safe Playwright operation still worked on the
    /// page. This is about the BROWSER, wherever it ended up; it says nothing about which application that page is.
    /// </summary>
    TargetControl,
    /// <summary>The page and context stayed alive through a bounded observation window and answered a second probe.</summary>
    TargetStability,
    /// <summary>
    /// The page Playwright ended up controlling is the configured target application, by defined evidence. NOT the
    /// same thing as navigation succeeding: a target that redirects to its identity provider has not been reached.
    /// </summary>
    TargetApplication,
    /// <summary>This mode's browser was closed.</summary>
    Cleanup,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStageState
{
    NotRun,
    Running,
    Passed,
    Failed,
    /// <summary>The stage did not fail on its own terms: something refused it, or policy did not permit it.</summary>
    Blocked,
    Cancelled,
    /// <summary>
    /// Not reached, for an expected reason that is not a failure — the target handed off to authentication first.
    /// Informational: the stage's detail says why.
    /// </summary>
    NotReached,
    /// <summary>The evidence needed to decide this stage was not available, so no answer is given either way.</summary>
    Unknown,
    /// <summary>Completed with a problem that does not change the diagnostic's finding (a cleanup error, say).</summary>
    Warning,
}

/// <summary>
/// The Edge <c>DeveloperToolsAvailability</c> policy (HKLM/HKCU\SOFTWARE\Policies\Microsoft\Edge): 0 = allowed except
/// on force-installed extension pages, 1 = allowed, 2 = not allowed. Read-only. Absent means NotConfigured, which is
/// not the same as allowed: DevTools can be restricted by other means this value does not show.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdgeDeveloperToolsPolicyStatus { Unknown, NotConfigured, AllowedExceptForceInstalledExtensions, Allowed, Disallowed }

/// <summary>Edge policy values read once at the start of a run. Evidence about policy, never about Playwright.</summary>
public sealed record BrowserAutomationPolicySnapshot(
    BirkNext.ManagedEdge.EdgeRemoteDebuggingPolicyStatus RemoteDebugging, EdgeDeveloperToolsPolicyStatus DeveloperTools)
{
    public static readonly BrowserAutomationPolicySnapshot Unknown =
        new(BirkNext.ManagedEdge.EdgeRemoteDebuggingPolicyStatus.Unknown, EdgeDeveloperToolsPolicyStatus.Unknown);
}

/// <summary>A yes/no fact that may also be honestly unanswered. Never collapsed into a bool, because "not checked" is not "no".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationEvidenceAnswer { Unknown, Yes, No }

/// <summary>Where the browser was when the diagnostic last looked — the fact the old single "Target application" stage hid.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationFinalLocation
{
    /// <summary>Not observed: the navigation did not get far enough, or the page was gone before it could be read.</summary>
    Unknown,
    /// <summary>Same scheme, host and effective port as the configured target URL.</summary>
    TargetOrigin,
    /// <summary>A recognised authentication authority: Microsoft Entra sign-in hosts, or the configured authority.</summary>
    AuthenticationAuthority,
    /// <summary>A Defender for Cloud Apps session-control host (<c>*.mcas.ms</c>). The page is proxied, not the target origin.</summary>
    SessionControlProxy,
    /// <summary>Anywhere else. Automation may be fine, but this is not the target and not a recognised handoff.</summary>
    OtherOrigin,
}

/// <summary>When, relative to the target navigation, a failure happened. The same exception means different things at each point.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationFailurePhase
{
    None,
    /// <summary>Before the target was contacted: a runtime or control problem, never evidence about the target.</summary>
    BeforeTargetNavigation,
    /// <summary>While the browser was navigating to the target: a target-navigation restriction.</summary>
    DuringTargetNavigation,
    /// <summary>After navigation returned, while it settled or while the page was first probed: a target-control restriction.</summary>
    AfterTargetNavigation,
    /// <summary>During the stability window or its closing probe: control was lost late, after an initial success.</summary>
    DuringStabilityWindow,
}

/// <summary>A page, context or browser lifecycle signal Playwright reported. Only the kind and the time — nothing from the page.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationLifecycleEventKind { PageClosed, PageCrashed, ContextClosed, BrowserDisconnected, PageOpened }

/// <summary>
/// One main-frame navigation. The URL is sanitized: scheme, host, port and a redacted path; never a query value.
/// <paramref name="SecondaryPage"/> marks a page the target opened (a sign-in popup), not the diagnostic's own page.
/// </summary>
public sealed record BrowserAutomationNavigationStep(
    int Sequence, long AtMs, string Url, BrowserAutomationFinalLocation Location, bool SecondaryPage = false);

public sealed record BrowserAutomationLifecycleEvent(BrowserAutomationLifecycleEventKind Kind, long AtMs, BrowserAutomationFailurePhase Phase);

/// <summary>
/// What one mode actually observed at the target, kept as separate facts because they ARE separate facts: the
/// navigation was accepted, the browser is still controllable, the page is on the target origin, the target redirected
/// to authentication, the target application was identified. Any one of them can be true while another is false, and
/// a single "Target application PASS" silently merged them.
/// </summary>
public sealed record BrowserAutomationTargetEvidence
{
    /// <summary>The configured target URL, sanitized.</summary>
    public string RequestedUrl { get; init; } = "";
    /// <summary>The canonical expected origin (scheme://host[:port]) that "target origin reached" is measured against.</summary>
    public string ExpectedOrigin { get; init; } = "";

    /// <summary>The page URL at the end of the observation, sanitized. Null when the page was never readable after navigating.</summary>
    public string? FinalUrl { get; init; }
    public string? FinalScheme { get; init; }
    public string? FinalHost { get; init; }
    public string? FinalOrigin { get; init; }
    public BrowserAutomationFinalLocation FinalLocation { get; init; } = BrowserAutomationFinalLocation.Unknown;

    public BrowserAutomationEvidenceAnswer ExpectedOriginReached { get; init; } = BrowserAutomationEvidenceAnswer.Unknown;
    /// <summary>Yes when any main-frame navigation reached an authentication authority — even if it later returned to the target.</summary>
    public BrowserAutomationEvidenceAnswer AuthenticationRedirect { get; init; } = BrowserAutomationEvidenceAnswer.Unknown;
    /// <summary>The authority host that was reached, if any. A host name only.</summary>
    public string? AuthenticationHost { get; init; }
    /// <summary>True when the authentication authority was reached in a page the target opened (a popup), not in the main page.</summary>
    public bool AuthenticationInSecondaryPage { get; init; }
    /// <summary>
    /// The session-control proxy host any page passed through (<c>*.mcas.ms</c>), if one was seen. Observed, and only
    /// that: it names no policy and no cause.
    /// </summary>
    public string? SessionControlHost { get; init; }

    public BrowserAutomationEvidenceAnswer TargetApplicationIdentified { get; init; } = BrowserAutomationEvidenceAnswer.Unknown;
    /// <summary>What the identification rests on, in the reader's words.</summary>
    public string IdentificationEvidence { get; init; } = "Not assessed";
    /// <summary>Whether a structural application marker is configured for this Target Environment.</summary>
    public bool ApplicationMarkerConfigured { get; init; }
    /// <summary>Whether that marker was present in the final page. Null when not configured or not checked.</summary>
    public bool? ApplicationMarkerFound { get; init; }

    /// <summary>True when main-frame navigation went quiet within the bounded settle window; false when the bound was hit.</summary>
    public bool? NavigationSettled { get; init; }
    public long? SettleDurationMs { get; init; }
    public long StabilityWindowMs { get; init; }

    public List<BrowserAutomationNavigationStep> NavigationTrace { get; init; } = [];
    public List<BrowserAutomationLifecycleEvent> LifecycleEvents { get; init; } = [];

    /// <summary>The exception TYPE at the target, if any, and when it happened relative to the navigation.</summary>
    public string? ExceptionType { get; init; }
    public BrowserAutomationFailurePhase FailurePhase { get; init; } = BrowserAutomationFailurePhase.None;
}

/// <summary>
/// One of three controls that are routinely confused with each other and must be reported apart. A Playwright result
/// is evidence about Playwright-owned automation ONLY; it says nothing about whether a person can open DevTools, or
/// whether CDP may attach to an Edge somebody else started.
/// </summary>
public sealed record BrowserAutomationControlDimension(string Name, string State, string Basis);

/// <summary>The outcome of ONE mode. Deliberately about behaviour; no value here names a cause.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticModeResult
{
    NotRun,
    /// <summary>Playwright kept stable control, and the page it controls was identified as the target application.</summary>
    Available,
    /// <summary>
    /// Playwright kept stable control through the navigation, and the target handed off to its authentication
    /// authority. Browser automation works through the handoff; the target application itself was not yet reached.
    /// </summary>
    AvailableAtAuthenticationBoundary,
    /// <summary>
    /// Playwright kept stable control, but the page could not be identified as the target application: it is on the
    /// target origin without the configured marker, behind a session-control proxy, or somewhere else entirely.
    /// </summary>
    AvailableTargetUnconfirmed,
    /// <summary>Automation worked on the control pages in this mode and could not be retained on the target.</summary>
    TargetRestricted,
    /// <summary>Playwright or Microsoft Edge could not start in this mode, so nothing was tested.</summary>
    RuntimeUnavailable,
    /// <summary>Automation could not be proven on a neutral page in this mode, so the target was not attempted.</summary>
    ControlFailure,
    Cancelled,
    /// <summary>Something else went wrong; reported by exception type, never collapsed into "failed".</summary>
    Failed,
}

/// <summary>
/// The one sentence that reads both modes together. This is where the diagnostic earns its keep: "blocked in both",
/// "blocked only headless" and "works everywhere" lead to three completely different conversations with IT.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticComparison
{
    NotRun,
    /// <summary>
    /// Playwright remained controllable through the tested browser path in both modes. NOT a statement that the
    /// target application — let alone the authenticated application — was reached; each mode's evidence says where
    /// the browser ended up.
    /// </summary>
    AutomationAvailable,
    /// <summary>Neither mode retained control, and both proved automation works on a neutral page first.</summary>
    TargetRestrictedInBothModes,
    /// <summary>Headed works on the target; headless does not. The case that matters most for unattended CI.</summary>
    HeadlessOnlyRestricted,
    /// <summary>Headless works on the target; headed does not.</summary>
    HeadedOnlyRestricted,
    /// <summary>Neither mode could start a browser, so nothing was tested.</summary>
    PlaywrightRuntimeUnavailable,
    /// <summary>No mode proved automation on a neutral page, so no target conclusion is available.</summary>
    ControlPageFailure,
    /// <summary>The two modes disagree in a way that supports no single conclusion.</summary>
    MixedOrInconclusive,
    Cancelled,
    /// <summary>Policy or configuration did not permit the diagnostic to run at all.</summary>
    Blocked,
}

/// <summary>Result of one stage. <paramref name="Detail"/> is safe, sanitized text; never a credential or a raw body.</summary>
public sealed record BrowserAutomationDiagnosticStageResult(
    BrowserAutomationDiagnosticStage Stage,
    BrowserAutomationDiagnosticStageState State,
    string? Detail = null,
    string? Url = null,
    string? ExceptionType = null,
    long? DurationMs = null);

/// <summary>Everything one mode observed. Self-contained, so the two modes can be read side by side.</summary>
public sealed record BrowserAutomationDiagnosticModeReport
{
    public BrowserAutomationDiagnosticMode Mode { get; init; }
    public BrowserAutomationDiagnosticModeResult Result { get; init; } = BrowserAutomationDiagnosticModeResult.NotRun;
    public bool Headless { get; init; }
    public string? EdgeVersion { get; init; }
    public string ProfileDescription { get; init; } = "";
    public List<BrowserAutomationDiagnosticStageResult> Stages { get; init; } = [];
    /// <summary>The exception type observed at the point of failure, if any. The TYPE only.</summary>
    public string? ObservedExceptionType { get; init; }
    /// <summary>What this mode observed at the target. Null when the target was never attempted.</summary>
    public BrowserAutomationTargetEvidence? Target { get; init; }

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);

    /// <summary>
    /// Playwright stayed in control of this mode's browser through the target navigation and the stability window,
    /// wherever the page ended up. The broadest "automation works" fact; it does not say which page is controlled.
    /// </summary>
    public bool BrowserControlRetained =>
        Result is BrowserAutomationDiagnosticModeResult.Available
            or BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary
            or BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed
        && Stage(BrowserAutomationDiagnosticStage.TargetControl)?.State == BrowserAutomationDiagnosticStageState.Passed
        && Stage(BrowserAutomationDiagnosticStage.TargetStability)?.State == BrowserAutomationDiagnosticStageState.Passed;

    /// <summary>
    /// Control was retained AND the browser ended somewhere the authentication diagnostic can work from: the target
    /// origin itself, the target's authentication authority, or its session-control proxy. An unrelated final origin
    /// does not count — the automation survived, but not along the target's path.
    /// </summary>
    public bool ControlRetainedThroughTargetNavigation =>
        BrowserControlRetained
        && Target?.FinalLocation is BrowserAutomationFinalLocation.TargetOrigin
            or BrowserAutomationFinalLocation.AuthenticationAuthority
            or BrowserAutomationFinalLocation.SessionControlProxy;

    /// <summary>The page Playwright controls was identified as the target application. The only route to "Target application PASS".</summary>
    public bool TargetApplicationIdentified =>
        BrowserControlRetained
        && Stage(BrowserAutomationDiagnosticStage.TargetApplication)?.State == BrowserAutomationDiagnosticStageState.Passed
        && Target?.TargetApplicationIdentified == BrowserAutomationEvidenceAnswer.Yes;

    /// <summary>Automation was proven on a neutral page, which is what makes a target result meaningful at all.</summary>
    public bool ControlPageProven =>
        Stage(BrowserAutomationDiagnosticStage.ControlPage)?.State == BrowserAutomationDiagnosticStageState.Passed;
}

public sealed record BrowserAutomationDiagnosticRequest
{
    public string TargetEnvironmentId { get; init; } = "";
    public string TargetEnvironmentName { get; init; } = "";
    /// <summary>Canonical Target Environment URL. Never hard-coded, and never defaulted to a known host.</summary>
    public string TargetUrl { get; init; } = "";
    /// <summary>Development / QA / Production / … — the production guard reads this.</summary>
    public string EnvironmentType { get; init; } = "";
    /// <summary>
    /// The Target Environment's expected authentication authority, when configured. Used only to RECOGNISE an
    /// authentication redirect; the diagnostic never navigates to it and never signs in.
    /// </summary>
    public string? Authority { get; init; }
}

/// <summary>
/// The diagnostic report: both modes, and what they mean together. Everything in it is intended to be readable by IT.
/// </summary>
public sealed record BrowserAutomationDiagnosticReport
{
    public string DiagnosticId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public BrowserAutomationDiagnosticComparison Result { get; init; } = BrowserAutomationDiagnosticComparison.NotRun;
    public string ResultLabel { get; init; } = "";
    /// <summary>What was observed, and nothing more. Never names an organisational control as the cause.</summary>
    public string Interpretation { get; init; } = "";
    /// <summary>Only present when the diagnostic could not start; explains why in the reader's terms.</summary>
    public string? BlockedReason { get; init; }

    public string TargetEnvironmentId { get; init; } = "";
    public string TargetEnvironmentName { get; init; } = "";
    public string TargetEnvironmentType { get; init; } = "";
    /// <summary>The configured target URL, SANITIZED for display and reports.</summary>
    public string TargetUrl { get; init; } = "";
    public string ControlUrl { get; init; } = "";
    /// <summary>
    /// The exact configured URL, used only server-side to bind evidence to the target it was gathered for. Never
    /// serialized: it may carry values the sanitized <see cref="TargetUrl"/> deliberately removes.
    /// </summary>
    [JsonIgnore] public string CorrelationTargetUrl { get; init; } = "";

    public string? EdgeVersion { get; init; }
    public string? PlaywrightVersion { get; init; }
    public string? OperatingSystem { get; init; }

    /// <summary>Headed first, headless second — the order they ran in.</summary>
    public List<BrowserAutomationDiagnosticModeReport> Modes { get; init; } = [];

    /// <summary>
    /// Corporate DevTools UI, Playwright-owned automation and CDP attach to an existing Edge — three independent
    /// dimensions, reported apart so that none is ever inferred from another.
    /// </summary>
    public List<BrowserAutomationControlDimension> ControlDimensions { get; init; } = [];

    public BrowserAutomationDiagnosticModeReport? Mode(BrowserAutomationDiagnosticMode mode) =>
        Modes.FirstOrDefault(m => m.Mode == mode);

    public BrowserAutomationDiagnosticModeReport? Headed => Mode(BrowserAutomationDiagnosticMode.Headed);
    public BrowserAutomationDiagnosticModeReport? Headless => Mode(BrowserAutomationDiagnosticMode.Headless);

    /// <summary>
    /// The ONE fact the Headless Authentication &amp; Session Control Diagnostic depends on.
    ///
    /// Meaning, precisely: headless Playwright remained in stable control of the browser through the navigation to the
    /// target, and the browser ended on the target origin or at the target's authentication handoff (identity provider
    /// or session-control proxy). It does NOT mean the target application was identified: reaching Microsoft Entra
    /// with control intact is exactly what the authentication diagnostic needs in order to inspect the sign-in, so
    /// requiring "Target application PASS" here would block the one diagnosis that can explain it.
    ///
    /// Headless and nothing else: headed success says nothing about unattended execution. Exposed as a single
    /// property so no consumer has to re-derive the rule.
    /// </summary>
    public bool HeadlessAutomationControlAfterTargetNavigation => Headless?.ControlRetainedThroughTargetNavigation == true;
}
