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
    /// <summary>Navigation to the configured target URL was attempted.</summary>
    TargetNavigation,
    /// <summary>The target page/context was still under automation control after navigating.</summary>
    TargetControl,
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
}

/// <summary>The outcome of ONE mode. Deliberately about behaviour; no value here names a cause.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticModeResult
{
    NotRun,
    /// <summary>Playwright kept control of the target in this mode.</summary>
    Available,
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
    /// <summary>Both modes retained control of the target.</summary>
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

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);

    /// <summary>This mode kept control of the target. The single fact the authentication prerequisite is built on.</summary>
    public bool TargetControlAvailable =>
        Result == BrowserAutomationDiagnosticModeResult.Available &&
        Stage(BrowserAutomationDiagnosticStage.TargetControl)?.State == BrowserAutomationDiagnosticStageState.Passed;

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
    public string TargetUrl { get; init; } = "";
    public string ControlUrl { get; init; } = "";

    public string? EdgeVersion { get; init; }
    public string? PlaywrightVersion { get; init; }
    public string? OperatingSystem { get; init; }

    /// <summary>Headed first, headless second — the order they ran in.</summary>
    public List<BrowserAutomationDiagnosticModeReport> Modes { get; init; } = [];

    public BrowserAutomationDiagnosticModeReport? Mode(BrowserAutomationDiagnosticMode mode) =>
        Modes.FirstOrDefault(m => m.Mode == mode);

    public BrowserAutomationDiagnosticModeReport? Headed => Mode(BrowserAutomationDiagnosticMode.Headed);
    public BrowserAutomationDiagnosticModeReport? Headless => Mode(BrowserAutomationDiagnosticMode.Headless);

    /// <summary>
    /// The ONE fact the Headless Authentication &amp; Session Control Diagnostic depends on.
    ///
    /// It is deliberately the HEADLESS mode's target control and nothing else: headed success says nothing about
    /// unattended execution, and a review that could only be automated with a visible window on someone's desk is not
    /// a review that can run in CI. Exposed as a single property so no consumer has to re-derive the rule.
    /// </summary>
    public bool HeadlessTargetControlAvailable => Headless?.TargetControlAvailable == true;
}
