using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>Mirrors the backend stage enum. Order is meaningful: it is the order the evidence is gathered in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStage
{
    Runtime, EdgeLaunch, BlankPage, ControlPage, TargetNavigation, TargetControl, Cleanup,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStageState { NotRun, Running, Passed, Failed, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticResult
{
    NotRun, Passed, TargetRestricted, RuntimeUnavailable, ControlFailure, Blocked, Failed, Cancelled,
}

public sealed record BrowserAutomationDiagnosticStageResult(
    BrowserAutomationDiagnosticStage Stage,
    BrowserAutomationDiagnosticStageState State,
    string? Detail = null,
    string? Url = null,
    string? ExceptionType = null,
    long? DurationMs = null);

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
    public BrowserAutomationDiagnosticResult Result { get; init; }
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
    public string ProfileDescription { get; init; } = "";

    public List<BrowserAutomationDiagnosticStageResult> Stages { get; init; } = [];
    public string? ObservedExceptionType { get; init; }

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);
}

public static class BrowserAutomationDiagnosticStages
{
    /// <summary>What each stage means to a reader who is not holding the code.</summary>
    public static string Label(BrowserAutomationDiagnosticStage stage) => stage switch
    {
        BrowserAutomationDiagnosticStage.Runtime => "Playwright runtime",
        BrowserAutomationDiagnosticStage.EdgeLaunch => "Edge launch",
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

    public static string ResultTone(BrowserAutomationDiagnosticResult result) => result switch
    {
        BrowserAutomationDiagnosticResult.Passed => "ready",
        // A detected restriction is a successful diagnosis, not a crash.
        BrowserAutomationDiagnosticResult.TargetRestricted => "warning",
        BrowserAutomationDiagnosticResult.Blocked or BrowserAutomationDiagnosticResult.Cancelled => "muted",
        BrowserAutomationDiagnosticResult.NotRun => "muted",
        _ => "attention",
    };
}

public static class BrowserAutomationDiagnosticReportText
{
    /// <summary>
    /// The plain-text report, written to be pasted into a ticket or read out in a meeting.
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
        text.AppendLine($"Browser profile: {report.ProfileDescription}");
        text.AppendLine($"Diagnostic id: {report.DiagnosticId}");
        text.AppendLine($"Run at: {report.StartedAt:u}");
        text.AppendLine();

        foreach (var stage in report.Stages)
        {
            var line = $"{BrowserAutomationDiagnosticStages.Label(stage.Stage)}: {BrowserAutomationDiagnosticStages.StateLabel(stage.State)}";
            if (!string.IsNullOrWhiteSpace(stage.ExceptionType)) line += $" ({stage.ExceptionType})";
            text.AppendLine(line);
        }

        if (!string.IsNullOrWhiteSpace(report.ObservedExceptionType))
        {
            text.AppendLine();
            text.AppendLine($"Observed exception: {report.ObservedExceptionType}");
        }

        if (!string.IsNullOrWhiteSpace(report.BlockedReason))
        {
            text.AppendLine();
            text.AppendLine($"Not run: {report.BlockedReason}");
        }

        text.AppendLine();
        text.AppendLine("Interpretation:");
        text.AppendLine(report.Interpretation);
        text.AppendLine();
        // Said explicitly, because it is the first thing anyone asks when a browser was pointed at an application.
        text.AppendLine("No login, MFA, credential or token was attempted or captured at any point.");
        return text.ToString();
    }
}
