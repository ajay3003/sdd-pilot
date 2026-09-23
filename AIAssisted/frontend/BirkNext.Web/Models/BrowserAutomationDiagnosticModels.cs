using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Mirrors the backend mode enum. The two modes are peers: neither is derived from the other, and the UI shows both
/// at the same level because "it works when I watch it" and "it works unattended" are different answers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticMode { Headed, Headless }

/// <summary>Mirrors the backend stage enum. Order is meaningful: it is the order the evidence is gathered in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStage
{
    Runtime, EdgeLaunch, PersistentContext, BlankPage, ControlPage, TargetNavigation, TargetControl, Cleanup,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStageState { NotRun, Running, Passed, Failed, Blocked, Cancelled }

/// <summary>The outcome of ONE mode. Every value describes behaviour; none of them names a cause.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticModeResult
{
    NotRun, Available, TargetRestricted, RuntimeUnavailable, ControlFailure, Cancelled, Failed,
}

/// <summary>The one conclusion that reads both modes together.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticComparison
{
    NotRun,
    AutomationAvailable,
    TargetRestrictedInBothModes,
    HeadlessOnlyRestricted,
    HeadedOnlyRestricted,
    PlaywrightRuntimeUnavailable,
    ControlPageFailure,
    MixedOrInconclusive,
    Cancelled,
    Blocked,
}

public sealed record BrowserAutomationDiagnosticStageResult(
    BrowserAutomationDiagnosticStage Stage,
    BrowserAutomationDiagnosticStageState State,
    string? Detail = null,
    string? Url = null,
    string? ExceptionType = null,
    long? DurationMs = null);

/// <summary>Everything one mode observed, self-contained so the two can be read side by side.</summary>
public sealed record BrowserAutomationDiagnosticModeReport
{
    public BrowserAutomationDiagnosticMode Mode { get; init; }
    public BrowserAutomationDiagnosticModeResult Result { get; init; } = BrowserAutomationDiagnosticModeResult.NotRun;
    public bool Headless { get; init; }
    public string? EdgeVersion { get; init; }
    public string ProfileDescription { get; init; } = "";
    public List<BrowserAutomationDiagnosticStageResult> Stages { get; init; } = [];
    public string? ObservedExceptionType { get; init; }

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);

    public bool TargetControlAvailable =>
        Result == BrowserAutomationDiagnosticModeResult.Available &&
        Stage(BrowserAutomationDiagnosticStage.TargetControl)?.State == BrowserAutomationDiagnosticStageState.Passed;

    public bool ControlPageProven =>
        Stage(BrowserAutomationDiagnosticStage.ControlPage)?.State == BrowserAutomationDiagnosticStageState.Passed;
}

public sealed record BrowserAutomationDiagnosticRequest
{
    public string TargetEnvironmentId { get; init; } = "";
    public string TargetEnvironmentName { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
}

public sealed record BrowserAutomationDiagnosticReport
{
    public string DiagnosticId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public BrowserAutomationDiagnosticComparison Result { get; init; }
    public string ResultLabel { get; init; } = "";
    public string Interpretation { get; init; } = "";
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

    /// <summary>The single fact the Headless Authentication &amp; Session Control Diagnostic depends on.</summary>
    public bool HeadlessTargetControlAvailable => Headless?.TargetControlAvailable == true;
}

public static class BrowserAutomationDiagnosticStages
{
    public static string ModeLabel(BrowserAutomationDiagnosticMode mode) =>
        mode == BrowserAutomationDiagnosticMode.Headless ? "Headless" : "Headed";

    /// <summary>Why a reader should care about this mode, in the terms they will have to act on.</summary>
    public static string ModeDescription(BrowserAutomationDiagnosticMode mode) =>
        mode == BrowserAutomationDiagnosticMode.Headless
            ? "No visible window — the way an unattended pipeline would run."
            : "A visible Edge window on this workstation.";

    /// <summary>What each stage means to a reader who is not holding the code.</summary>
    public static string Label(BrowserAutomationDiagnosticStage stage) => stage switch
    {
        BrowserAutomationDiagnosticStage.Runtime => "Playwright runtime",
        BrowserAutomationDiagnosticStage.EdgeLaunch => "Edge launch",
        BrowserAutomationDiagnosticStage.PersistentContext => "Browser profile",
        BrowserAutomationDiagnosticStage.BlankPage => "about:blank",
        BrowserAutomationDiagnosticStage.ControlPage => "Control page",
        BrowserAutomationDiagnosticStage.TargetNavigation => "Target navigation",
        BrowserAutomationDiagnosticStage.TargetControl => "Target application",
        _ => "Cleanup",
    };

    public static string StateLabel(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "PASS",
        BrowserAutomationDiagnosticStageState.Failed => "FAIL",
        BrowserAutomationDiagnosticStageState.Blocked => "BLOCKED",
        BrowserAutomationDiagnosticStageState.Running => "Running…",
        BrowserAutomationDiagnosticStageState.Cancelled => "Cancelled",
        _ => "Not run",
    };

    /// <summary>
    /// Tone only; the label always carries the meaning. A blocked target is the diagnostic WORKING, so it gets the
    /// attention tone rather than the one used for something that crashed.
    /// </summary>
    public static string Tone(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "ready",
        BrowserAutomationDiagnosticStageState.Blocked => "warning",
        BrowserAutomationDiagnosticStageState.Failed => "attention",
        BrowserAutomationDiagnosticStageState.Running => "pending",
        _ => "muted",
    };

    public static string Glyph(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "✓",
        BrowserAutomationDiagnosticStageState.Blocked => "!",
        BrowserAutomationDiagnosticStageState.Failed => "✕",
        _ => "○",
    };

    /// <summary>The short verdict for one mode. Never a bare "failed".</summary>
    public static string ModeResultLabel(BrowserAutomationDiagnosticModeResult result) => result switch
    {
        BrowserAutomationDiagnosticModeResult.Available => "Automation available",
        BrowserAutomationDiagnosticModeResult.TargetRestricted => "Restricted for this target",
        BrowserAutomationDiagnosticModeResult.RuntimeUnavailable => "Browser could not start",
        BrowserAutomationDiagnosticModeResult.ControlFailure => "Not proven on a neutral page",
        BrowserAutomationDiagnosticModeResult.Cancelled => "Cancelled",
        BrowserAutomationDiagnosticModeResult.Failed => "Unexpected failure",
        _ => "Not run",
    };

    public static string ModeResultTone(BrowserAutomationDiagnosticModeResult result) => result switch
    {
        BrowserAutomationDiagnosticModeResult.Available => "ready",
        // A detected restriction is a successful diagnosis, not a crash.
        BrowserAutomationDiagnosticModeResult.TargetRestricted => "warning",
        BrowserAutomationDiagnosticModeResult.NotRun or BrowserAutomationDiagnosticModeResult.Cancelled => "muted",
        _ => "attention",
    };

    public static string ResultTone(BrowserAutomationDiagnosticComparison result) => result switch
    {
        BrowserAutomationDiagnosticComparison.AutomationAvailable => "ready",
        BrowserAutomationDiagnosticComparison.TargetRestrictedInBothModes
            or BrowserAutomationDiagnosticComparison.HeadlessOnlyRestricted
            or BrowserAutomationDiagnosticComparison.HeadedOnlyRestricted => "warning",
        BrowserAutomationDiagnosticComparison.Blocked
            or BrowserAutomationDiagnosticComparison.Cancelled
            or BrowserAutomationDiagnosticComparison.NotRun => "muted",
        _ => "attention",
    };
}

/// <summary>
/// What the headless result means for the Headless Authentication &amp; Session Control Diagnostic.
///
/// The frontend does not re-derive the rule; it reads the one fact the backend publishes
/// (<see cref="BrowserAutomationDiagnosticReport.HeadlessTargetControlAvailable"/>) and says what follows from it.
/// </summary>
public static class BrowserAutomationDiagnosticPrerequisite
{
    public static string Headline(BrowserAutomationDiagnosticReport report) =>
        report.HeadlessTargetControlAvailable
            ? "Headless authentication diagnostic can run"
            : "Headless authentication diagnostic cannot run yet";

    public static string Tone(BrowserAutomationDiagnosticReport report) =>
        report.HeadlessTargetControlAvailable ? "ready" : "warning";

    public static string Explanation(BrowserAutomationDiagnosticReport report)
    {
        if (report.HeadlessTargetControlAvailable)
            return "Headless mode kept control of this target, so MFA, Conditional Access and session control can be "
                + "assessed against it.";

        // The headed-only case is the one that misleads: the reader has just watched automation drive the target,
        // so "cannot run" reads as a contradiction unless the sentence says which mode was asked about.
        return report.Headed?.TargetControlAvailable == true
            ? "Headed mode kept control of this target, but the authentication diagnostic runs headless, as an "
              + "unattended pipeline would. Headed-only success is not sufficient, so MFA, Conditional Access and "
              + "session control cannot be assessed yet."
            : "Headless mode did not keep control of this target, so MFA, Conditional Access and session control "
              + "cannot be assessed yet.";
    }
}

public static class BrowserAutomationDiagnosticReportText
{
    /// <summary>
    /// The plain-text report, written to be pasted into a ticket or read out in a meeting.
    ///
    /// Each mode gets its own section, because the difference between them is the finding: an IT reader who is told
    /// only "automation is restricted" will ask "in which mode?", and a reader told only about headless will assume
    /// the workstation is broken. The overall interpretation comes last, once both sets of evidence have been read.
    ///
    /// It carries only what IT needs to act on: which stages passed, which one did not, the exception TYPE, and an
    /// interpretation that stops at what was observed. No cookie, token, credential, browser storage or profile path —
    /// the profile is described rather than located, because the path contains the employee's user name.
    /// </summary>
    public static string Build(BrowserAutomationDiagnosticReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("Browser Automation Diagnostic");
        text.AppendLine($"Result: {report.ResultLabel}");
        text.AppendLine();
        text.AppendLine($"Target: {report.TargetEnvironmentName} ({report.TargetEnvironmentType})");
        text.AppendLine($"Target URL: {report.TargetUrl}");
        text.AppendLine($"Control URL: {report.ControlUrl}");
        text.AppendLine($"Edge: {report.EdgeVersion ?? "unknown"}");
        text.AppendLine($"Playwright: {report.PlaywrightVersion ?? "unknown"}");
        text.AppendLine($"OS: {report.OperatingSystem ?? "unknown"}");
        text.AppendLine($"Diagnostic id: {report.DiagnosticId}");
        text.AppendLine($"Run at: {report.StartedAt:u}");

        if (!string.IsNullOrWhiteSpace(report.BlockedReason))
        {
            text.AppendLine();
            text.AppendLine($"NOT RUN: {report.BlockedReason}");
        }

        AppendMode(text, report.Headed, BrowserAutomationDiagnosticMode.Headed);
        AppendMode(text, report.Headless, BrowserAutomationDiagnosticMode.Headless);

        text.AppendLine();
        text.AppendLine("OVERALL INTERPRETATION");
        text.AppendLine(report.Interpretation);

        text.AppendLine();
        text.AppendLine("HEADLESS AUTHENTICATION DIAGNOSTIC");
        text.AppendLine(BrowserAutomationDiagnosticPrerequisite.Explanation(report));

        text.AppendLine();
        // Said explicitly, because it is the first thing anyone asks when a browser was pointed at an application.
        text.AppendLine("No login, MFA, credential or token was attempted or captured at any point.");
        return text.ToString();
    }

    private static void AppendMode(
        StringBuilder text, BrowserAutomationDiagnosticModeReport? mode, BrowserAutomationDiagnosticMode kind)
    {
        text.AppendLine();
        text.AppendLine($"{BrowserAutomationDiagnosticStages.ModeLabel(kind).ToUpperInvariant()} MODE");

        if (mode is null)
        {
            text.AppendLine("Not run.");
            return;
        }

        text.AppendLine($"Result: {BrowserAutomationDiagnosticStages.ModeResultLabel(mode.Result)}");
        text.AppendLine($"Edge: {mode.EdgeVersion ?? "unknown"}");
        text.AppendLine($"Browser profile: {mode.ProfileDescription}");

        foreach (var stage in mode.Stages)
        {
            var line = $"  {BrowserAutomationDiagnosticStages.Label(stage.Stage)}: "
                + BrowserAutomationDiagnosticStages.StateLabel(stage.State);
            if (!string.IsNullOrWhiteSpace(stage.ExceptionType)) line += $" ({stage.ExceptionType})";
            text.AppendLine(line);
        }

        if (!string.IsNullOrWhiteSpace(mode.ObservedExceptionType))
            text.AppendLine($"Observed exception: {mode.ObservedExceptionType}");
    }
}
