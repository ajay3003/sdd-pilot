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
    Runtime, EdgeLaunch, PersistentContext, BlankPage, ControlPage,
    TargetNavigation, TargetControl, TargetStability, TargetApplication,
    Cleanup,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticStageState { NotRun, Running, Passed, Failed, Blocked, Cancelled, NotReached, Unknown }

/// <summary>The outcome of ONE mode. Every value describes behaviour; none of them names a cause.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationDiagnosticModeResult
{
    NotRun, Available, AvailableAtAuthenticationBoundary, AvailableTargetUnconfirmed,
    TargetRestricted, RuntimeUnavailable, ControlFailure, Cancelled, Failed,
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationEvidenceAnswer { Unknown, Yes, No }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationFinalLocation { Unknown, TargetOrigin, AuthenticationAuthority, SessionControlProxy, OtherOrigin }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationFailurePhase { None, BeforeTargetNavigation, DuringTargetNavigation, AfterTargetNavigation, DuringStabilityWindow }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAutomationLifecycleEventKind { PageClosed, PageCrashed, ContextClosed, BrowserDisconnected, PageOpened }

public sealed record BrowserAutomationDiagnosticStageResult(
    BrowserAutomationDiagnosticStage Stage,
    BrowserAutomationDiagnosticStageState State,
    string? Detail = null,
    string? Url = null,
    string? ExceptionType = null,
    long? DurationMs = null);

/// <summary>One main-frame navigation, already sanitized by the backend.</summary>
public sealed record BrowserAutomationNavigationStep(
    int Sequence, long AtMs, string Url, BrowserAutomationFinalLocation Location, bool SecondaryPage = false);

public sealed record BrowserAutomationLifecycleEvent(BrowserAutomationLifecycleEventKind Kind, long AtMs, BrowserAutomationFailurePhase Phase);

/// <summary>What one mode observed at the target, as separate facts. Mirrors the backend record.</summary>
public sealed record BrowserAutomationTargetEvidence
{
    public string RequestedUrl { get; init; } = "";
    public string ExpectedOrigin { get; init; } = "";
    public string? FinalUrl { get; init; }
    public string? FinalScheme { get; init; }
    public string? FinalHost { get; init; }
    public string? FinalOrigin { get; init; }
    public BrowserAutomationFinalLocation FinalLocation { get; init; }
    public BrowserAutomationEvidenceAnswer ExpectedOriginReached { get; init; }
    public BrowserAutomationEvidenceAnswer AuthenticationRedirect { get; init; }
    public string? AuthenticationHost { get; init; }
    public bool AuthenticationInSecondaryPage { get; init; }
    public string? SessionControlHost { get; init; }
    public BrowserAutomationEvidenceAnswer TargetApplicationIdentified { get; init; }
    public string IdentificationEvidence { get; init; } = "";
    public bool ApplicationMarkerConfigured { get; init; }
    public bool? ApplicationMarkerFound { get; init; }
    public bool? NavigationSettled { get; init; }
    public long? SettleDurationMs { get; init; }
    public long StabilityWindowMs { get; init; }
    public List<BrowserAutomationNavigationStep> NavigationTrace { get; init; } = [];
    public List<BrowserAutomationLifecycleEvent> LifecycleEvents { get; init; } = [];
    public string? ExceptionType { get; init; }
    public BrowserAutomationFailurePhase FailurePhase { get; init; }
}

/// <summary>One of three independent controls. Mirrors the backend record; never derived on the frontend.</summary>
public sealed record BrowserAutomationControlDimension(string Name, string State, string Basis);

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
    public BrowserAutomationTargetEvidence? Target { get; init; }

    public BrowserAutomationDiagnosticStageResult? Stage(BrowserAutomationDiagnosticStage stage) =>
        Stages.FirstOrDefault(s => s.Stage == stage);

    /// <summary>Same rule as the backend: control survived the navigation AND the stability window, wherever it ended.</summary>
    public bool BrowserControlRetained =>
        Result is BrowserAutomationDiagnosticModeResult.Available
            or BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary
            or BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed
        && Stage(BrowserAutomationDiagnosticStage.TargetControl)?.State == BrowserAutomationDiagnosticStageState.Passed
        && Stage(BrowserAutomationDiagnosticStage.TargetStability)?.State == BrowserAutomationDiagnosticStageState.Passed;

    public bool ControlRetainedThroughTargetNavigation =>
        BrowserControlRetained
        && Target?.FinalLocation is BrowserAutomationFinalLocation.TargetOrigin
            or BrowserAutomationFinalLocation.AuthenticationAuthority
            or BrowserAutomationFinalLocation.SessionControlProxy;

    public bool TargetApplicationIdentified =>
        BrowserControlRetained
        && Stage(BrowserAutomationDiagnosticStage.TargetApplication)?.State == BrowserAutomationDiagnosticStageState.Passed
        && Target?.TargetApplicationIdentified == BrowserAutomationEvidenceAnswer.Yes;

    public bool ControlPageProven =>
        Stage(BrowserAutomationDiagnosticStage.ControlPage)?.State == BrowserAutomationDiagnosticStageState.Passed;
}

public sealed record BrowserAutomationDiagnosticRequest
{
    public string TargetEnvironmentId { get; init; } = "";
    public string TargetEnvironmentName { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    /// <summary>The expected authentication authority, used only to RECOGNISE a redirect to it.</summary>
    public string? Authority { get; init; }
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

    public List<BrowserAutomationControlDimension> ControlDimensions { get; init; } = [];

    public BrowserAutomationDiagnosticModeReport? Mode(BrowserAutomationDiagnosticMode mode) =>
        Modes.FirstOrDefault(m => m.Mode == mode);

    public BrowserAutomationDiagnosticModeReport? Headed => Mode(BrowserAutomationDiagnosticMode.Headed);
    public BrowserAutomationDiagnosticModeReport? Headless => Mode(BrowserAutomationDiagnosticMode.Headless);

    /// <summary>
    /// The single fact the Headless Authentication &amp; Session Control Diagnostic depends on: headless automation
    /// stayed controllable through the target navigation, ending on the target origin or at its authentication handoff.
    /// Not "the target application was identified".
    /// </summary>
    public bool HeadlessAutomationControlAfterTargetNavigation => Headless?.ControlRetainedThroughTargetNavigation == true;
}

public static class BrowserAutomationDiagnosticStages
{
    /// <summary>The stages that happen AT the target. The card shows them as target evidence rather than as setup.</summary>
    public static readonly IReadOnlySet<BrowserAutomationDiagnosticStage> TargetStages = new HashSet<BrowserAutomationDiagnosticStage>
    {
        BrowserAutomationDiagnosticStage.TargetNavigation, BrowserAutomationDiagnosticStage.TargetControl,
        BrowserAutomationDiagnosticStage.TargetStability, BrowserAutomationDiagnosticStage.TargetApplication,
    };

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
        BrowserAutomationDiagnosticStage.TargetNavigation => "Initial navigation",
        BrowserAutomationDiagnosticStage.TargetControl => "Browser control after navigation",
        BrowserAutomationDiagnosticStage.TargetStability => "Stability check",
        BrowserAutomationDiagnosticStage.TargetApplication => "Target application",
        _ => "Cleanup",
    };

    public static string StateLabel(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "PASS",
        BrowserAutomationDiagnosticStageState.Failed => "FAIL",
        BrowserAutomationDiagnosticStageState.Blocked => "BLOCKED",
        BrowserAutomationDiagnosticStageState.Running => "Running…",
        BrowserAutomationDiagnosticStageState.Cancelled => "Cancelled",
        BrowserAutomationDiagnosticStageState.NotReached => "NOT YET REACHED",
        BrowserAutomationDiagnosticStageState.Unknown => "UNKNOWN",
        _ => "Not run",
    };

    /// <summary>
    /// Tone only; the label always carries the meaning. A blocked target is the diagnostic WORKING, so it gets the
    /// attention tone rather than the one used for something that crashed; "not yet reached" is an expected boundary
    /// (authentication first), so it is informational, never red.
    /// </summary>
    public static string Tone(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "ready",
        BrowserAutomationDiagnosticStageState.Blocked => "warning",
        BrowserAutomationDiagnosticStageState.Failed => "attention",
        BrowserAutomationDiagnosticStageState.Running => "pending",
        BrowserAutomationDiagnosticStageState.NotReached or BrowserAutomationDiagnosticStageState.Unknown => "info",
        _ => "muted",
    };

    public static string Glyph(BrowserAutomationDiagnosticStageState state) => state switch
    {
        BrowserAutomationDiagnosticStageState.Passed => "✓",
        BrowserAutomationDiagnosticStageState.Blocked => "!",
        BrowserAutomationDiagnosticStageState.Failed => "✕",
        BrowserAutomationDiagnosticStageState.NotReached or BrowserAutomationDiagnosticStageState.Unknown => "i",
        _ => "○",
    };

    /// <summary>The short verdict for one mode. Never a bare "failed", and never "target" when the target was not reached.</summary>
    public static string ModeResultLabel(BrowserAutomationDiagnosticModeResult result) => result switch
    {
        BrowserAutomationDiagnosticModeResult.Available => "Target application controllable",
        BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary => "Available through authentication redirect",
        BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed => "Available; target not identified",
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
        // Automation works, and the target was not (yet) reached: neither a failure nor an unqualified green.
        BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary
            or BrowserAutomationDiagnosticModeResult.AvailableTargetUnconfirmed => "info",
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

/// <summary>One labelled fact about what a mode saw at the target.</summary>
public sealed record BrowserAutomationTargetFact(string Key, string Label, string Value, string Tone);

/// <summary>
/// The target evidence as a reader needs it — visible without opening anything. Each fact is its own row because
/// each can be true while the next is false: the navigation was accepted, the browser ended on Entra, control was
/// retained, the target application was not reached.
/// </summary>
public static class BrowserAutomationTargetFacts
{
    public static IReadOnlyList<BrowserAutomationTargetFact> For(BrowserAutomationDiagnosticModeReport mode)
    {
        var target = mode.Target;
        if (target is null) return [];

        BrowserAutomationTargetFact Stage(string key, BrowserAutomationDiagnosticStage stage)
        {
            var state = mode.Stage(stage)?.State ?? BrowserAutomationDiagnosticStageState.NotRun;
            return new(key, BrowserAutomationDiagnosticStages.Label(stage),
                BrowserAutomationDiagnosticStages.StateLabel(state), BrowserAutomationDiagnosticStages.Tone(state));
        }

        var facts = new List<BrowserAutomationTargetFact>
        {
            new("requested", "Requested target", target.RequestedUrl, "muted"),
            Stage("navigation", BrowserAutomationDiagnosticStage.TargetNavigation),
            new("final-location", "Final location", target.FinalUrl ?? "Not observed", "muted"),
            new("final-host", "Final host", target.FinalHost ?? "Not observed", "muted"),
            new("expected-origin", "Expected target origin", Answer(target.ExpectedOriginReached),
                target.ExpectedOriginReached == BrowserAutomationEvidenceAnswer.Yes ? "ready" : "info"),
            // An authentication redirect is the expected boundary before sign-in, not a failure: informational.
            new("auth-redirect", "Authentication redirect", target.AuthenticationRedirect switch
            {
                BrowserAutomationEvidenceAnswer.Yes when target.AuthenticationInSecondaryPage =>
                    $"DETECTED (second page: {target.AuthenticationHost})",
                BrowserAutomationEvidenceAnswer.Yes => "DETECTED",
                BrowserAutomationEvidenceAnswer.No => "NOT DETECTED",
                _ => "UNKNOWN",
            }, "info"),
            // Observed only — a session-control hop names no policy and no cause.
            new("session-control", "Session-control proxy", target.SessionControlHost is { } session ? $"OBSERVED ({session})" : "NOT OBSERVED", "info"),
            Stage("control", BrowserAutomationDiagnosticStage.TargetControl),
            Stage("stability", BrowserAutomationDiagnosticStage.TargetStability),
            Stage("application", BrowserAutomationDiagnosticStage.TargetApplication),
            // Said explicitly on every run, because "Target application PASS" is the line most easily over-read.
            new("authenticated-application", "Authenticated application", "NOT ASSESSED — no sign-in is attempted", "muted"),
        };
        if (target.ExceptionType is { Length: > 0 } exception)
            facts.Add(new("exception", "Exception", $"{exception} ({PhaseLabel(target.FailurePhase)})", "warning"));
        return facts;
    }

    public static string Answer(BrowserAutomationEvidenceAnswer answer) => answer switch
    {
        BrowserAutomationEvidenceAnswer.Yes => "YES",
        BrowserAutomationEvidenceAnswer.No => "NO",
        _ => "UNKNOWN",
    };

    public static string PhaseLabel(BrowserAutomationFailurePhase phase) => phase switch
    {
        BrowserAutomationFailurePhase.BeforeTargetNavigation => "before target navigation",
        BrowserAutomationFailurePhase.DuringTargetNavigation => "during target navigation",
        BrowserAutomationFailurePhase.AfterTargetNavigation => "after navigation, before control was proven",
        BrowserAutomationFailurePhase.DuringStabilityWindow => "during the stability window",
        _ => "no failure",
    };

    public static string LocationLabel(BrowserAutomationFinalLocation location) => location switch
    {
        BrowserAutomationFinalLocation.TargetOrigin => "target origin",
        BrowserAutomationFinalLocation.AuthenticationAuthority => "authentication authority",
        BrowserAutomationFinalLocation.SessionControlProxy => "session-control proxy",
        BrowserAutomationFinalLocation.OtherOrigin => "other origin",
        _ => "unknown",
    };
}

/// <summary>
/// What the headless result means for the Headless Authentication &amp; Session Control Diagnostic.
///
/// The frontend does not re-derive the rule; it reads the one fact the backend publishes
/// (<see cref="BrowserAutomationDiagnosticReport.HeadlessAutomationControlAfterTargetNavigation"/>) and says what
/// follows from it.
/// </summary>
public static class BrowserAutomationDiagnosticPrerequisite
{
    public static string Headline(BrowserAutomationDiagnosticReport report) =>
        report.HeadlessAutomationControlAfterTargetNavigation
            ? "Headless authentication diagnostic can run"
            : "Headless authentication diagnostic cannot run yet";

    public static string Tone(BrowserAutomationDiagnosticReport report) =>
        report.HeadlessAutomationControlAfterTargetNavigation ? "ready" : "warning";

    public static string Explanation(BrowserAutomationDiagnosticReport report)
    {
        if (report.HeadlessAutomationControlAfterTargetNavigation)
            return report.Headless?.Target?.FinalLocation == BrowserAutomationFinalLocation.AuthenticationAuthority
                ? $"Headless automation stayed in control through the redirect to {report.Headless.Target.AuthenticationHost}. "
                  + "That authentication handoff is what the authentication diagnostic inspects next: MFA, Conditional "
                  + "Access and session control can be assessed from here. The target application itself was not yet reached."
                : "Headless automation stayed in control through the navigation to this target, so MFA, Conditional "
                  + "Access and session control can be assessed against it.";

        if (report.Headless is { BrowserControlRetained: true } elsewhere)
            return $"Headless automation stayed in control, but the browser ended at {elsewhere.Target?.FinalHost ?? "an unrecognised location"}, "
                + "which is neither the target origin nor a recognised authentication handoff. MFA, Conditional Access "
                + "and session control cannot be assessed yet.";

        // The headed-only case is the one that misleads: the reader has just watched automation drive the target,
        // so "cannot run" reads as a contradiction unless the sentence says which mode was asked about.
        return report.Headed?.ControlRetainedThroughTargetNavigation == true
            ? "Headed mode kept control through the target navigation, but the authentication diagnostic runs "
              + "headless, as an unattended pipeline would. Headed-only success is not sufficient, so MFA, Conditional "
              + "Access and session control cannot be assessed yet."
            : "Headless mode did not keep control through the target navigation, so MFA, Conditional Access and "
              + "session control cannot be assessed yet.";
    }
}

public static class BrowserAutomationDiagnosticReportText
{
    /// <summary>
    /// The plain-text report, written to be pasted into a ticket or read out in a meeting.
    ///
    /// Each mode gets its own section, because the difference between them is the finding. Within a mode the TARGET
    /// evidence is spelled out fact by fact — requested URL, where the browser actually ended, whether that was the
    /// target origin or an authentication redirect, whether control survived, whether the application was identified
    /// — because "Target application PASS" on its own is the sentence that was once wrong for exactly this reason.
    ///
    /// It carries only what IT needs to act on: sanitized URLs, stage states, the exception TYPE, and an interpretation
    /// that stops at what was observed. No cookie, token, credential, query value, browser storage or profile path.
    /// </summary>
    public static string Build(BrowserAutomationDiagnosticReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("BROWSER AUTOMATION DIAGNOSTIC");
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

        if (report.ControlDimensions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("INDEPENDENT CONTROLS (not inferred from one another)");
            foreach (var dimension in report.ControlDimensions)
                text.AppendLine($"  {dimension.Name}: {dimension.State} — {dimension.Basis}");
        }

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

        foreach (var stage in mode.Stages.Where(s => !BrowserAutomationDiagnosticStages.TargetStages.Contains(s.Stage)))
            text.AppendLine(StageLine(stage));

        if (mode.Target is { } target)
        {
            text.AppendLine("  At the target:");
            foreach (var fact in BrowserAutomationTargetFacts.For(mode))
                text.AppendLine($"    {fact.Label}: {fact.Value}");
            if (mode.Stage(BrowserAutomationDiagnosticStage.TargetApplication) is { Detail.Length: > 0 } application)
                text.AppendLine($"    Target application evidence: {application.Detail}");
            if (target.NavigationTrace.Count > 0)
            {
                text.AppendLine("    Navigation trace (sanitized):");
                foreach (var step in target.NavigationTrace)
                    text.AppendLine($"      {step.Sequence}. {step.Url} [{BrowserAutomationTargetFacts.LocationLabel(step.Location)}"
                        + $"{(step.SecondaryPage ? ", second page" : "")}, {step.AtMs} ms]");
            }
            foreach (var e in target.LifecycleEvents)
                text.AppendLine($"    Browser event: {e.Kind} at {e.AtMs} ms ({BrowserAutomationTargetFacts.PhaseLabel(e.Phase)})");
        }
        else
        {
            foreach (var stage in mode.Stages.Where(s => BrowserAutomationDiagnosticStages.TargetStages.Contains(s.Stage)))
                text.AppendLine(StageLine(stage));
        }

        if (!string.IsNullOrWhiteSpace(mode.ObservedExceptionType))
            text.AppendLine($"Observed exception: {mode.ObservedExceptionType}");
    }

    private static string StageLine(BrowserAutomationDiagnosticStageResult stage)
    {
        var line = $"  {BrowserAutomationDiagnosticStages.Label(stage.Stage)}: {BrowserAutomationDiagnosticStages.StateLabel(stage.State)}";
        if (!string.IsNullOrWhiteSpace(stage.ExceptionType)) line += $" ({stage.ExceptionType})";
        return line;
    }
}
