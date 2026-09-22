using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// The stages of the diagnostic, in the order they run. The ORDER is the evidence: a failure at the target means
/// something only because everything before it succeeded, and a failure before the target means the diagnostic never
/// learned anything about the target at all.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStage
{
    /// <summary>Playwright and a Microsoft Edge installation are present.</summary>
    Runtime,
    /// <summary>Playwright launched its own persistent Edge context against a dedicated profile.</summary>
    EdgeLaunch,
    /// <summary>about:blank is open and Playwright can still operate on it.</summary>
    BlankPage,
    /// <summary>A neutral control page navigated and stayed under automation control.</summary>
    ControlPage,
    /// <summary>Navigation to the configured target URL was attempted.</summary>
    TargetNavigation,
    /// <summary>The target page/context was still under automation control after navigating.</summary>
    TargetControl,
    /// <summary>The diagnostic browser was closed.</summary>
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
}

/// <summary>
/// The one-line answer. Deliberately about BEHAVIOUR: the diagnostic observes what the browser did, and no category
/// here names a cause it cannot see.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticResult
{
    /// <summary>Not run yet.</summary>
    NotRun,
    /// <summary>Playwright kept control of the target. Says nothing about authentication or MFA.</summary>
    Passed,
    /// <summary>
    /// Automation worked on the control pages and could not be retained on the target. The contrast is the finding;
    /// the cause is not something this tool can establish.
    /// </summary>
    TargetRestricted,
    /// <summary>Playwright or Microsoft Edge could not start, so nothing was tested.</summary>
    RuntimeUnavailable,
    /// <summary>
    /// Automation could not be proven even on a neutral page. Whatever happens at the target afterwards proves
    /// nothing, so the target is not attempted.
    /// </summary>
    ControlFailure,
    /// <summary>Policy or configuration did not permit the diagnostic to run.</summary>
    Blocked,
    /// <summary>The diagnostic itself failed in a way that is not one of the above.</summary>
    Failed,
    /// <summary>The user stopped it. Not a failure.</summary>
    Cancelled,
}

/// <summary>Result of one stage. <paramref name="Detail"/> is safe, sanitized text; never a credential or a raw body.</summary>
public sealed record BrowserAutomationDiagnosticStageResult(
    BrowserAutomationDiagnosticStage Stage,
    BrowserAutomationDiagnosticStageState State,
    string? Detail = null,
    string? Url = null,
    string? ExceptionType = null,
    long? DurationMs = null);

/// <summary>What the diagnostic was asked to do.</summary>
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
/// The diagnostic report. Everything in it is intended to be readable by IT: stage outcomes, the exception TYPE, and
/// an interpretation that stops at what was observed.
/// </summary>
public sealed record BrowserAutomationDiagnosticReport
{
    public string DiagnosticId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public BrowserAutomationDiagnosticResult Result { get; init; } = BrowserAutomationDiagnosticResult.NotRun;
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
    /// <summary>A description of the profile, never the raw path when it would carry the user's name.</summary>
    public string ProfileDescription { get; init; } = "";

    public List<BrowserAutomationDiagnosticStageResult> Stages { get; init; } = [];

    /// <summary>The exception type observed at the point of failure, if any. The TYPE only — never a message with state in it.</summary>
    public string? ObservedExceptionType { get; init; }

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);
}
