using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.HeadlessAuthDiagnostic;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessStage { Runtime, HeadlessEdgeLaunch, HeadlessBrowserControl, TargetNavigation, AuthenticationDetection, IdentityProviderDetection, NonInteractiveContinuation, MfaDetection, ConditionalAccessObservation, SessionControlObservation, AuthenticatedReturn, AuthenticatedSessionVerification, PostAuthenticationAutomationControl, HeadlessReadiness, Cleanup }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessStageState { NotRun, Running, Passed, Blocked, Failed, Unknown, NotApplicable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessBlocker { None, BrowserAutomationBlocked, TargetNavigationBlocked, InteractiveAuthenticationRequired, InteractiveMfaRequired, ConditionalAccessBlocked, SessionControlHeadlessRestriction, AuthenticationSessionNotEstablished, AutomationControlLostAfterAuthentication, IdentityNotAvailableForAutomation, Unknown,
    /// <summary>Control was lost AFTER a session-control signal was observed. Sequence only — never a statement that session control caused it.</summary>
    AutomationControlLostAfterObservedSessionControl }
/// <summary>Where a reported value comes from. An IT reader weighs "observed" and "derived" differently, and should.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessEvidenceProvenance { Observed, Derived, Configured, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessReadiness { Ready, NotReady, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HeadlessRunStatus { Completed, Blocked, Cancelled }

public sealed record HeadlessDiagnosticRequest
{
    public string TargetEnvironmentId { get; init; } = "";
    public string TargetEnvironmentName { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public string? Authority { get; init; }
    public string? AuthenticationType { get; init; }
    public bool RequiresAuthentication { get; init; }
}
public sealed record HeadlessStageResult(HeadlessStage Stage, HeadlessStageState State = HeadlessStageState.NotRun,
    string? Detail = null, long DurationMs = 0, string? ExceptionType = null, DateTimeOffset? ObservedAt = null);
/// <summary>One sanitized main-frame navigation. <paramref name="SecondaryPage"/> marks a page the target opened (an MSAL sign-in popup).</summary>
public sealed record HeadlessNavigation(DateTimeOffset Timestamp, string Url, bool SecondaryPage = false);
public sealed record HeadlessEvidenceItem(string Name, string Value, HeadlessEvidenceProvenance Provenance);
public sealed record HeadlessPrerequisite(bool Available, string Reason, string? DiagnosticId = null);

public sealed class HeadlessDiagnosticReport
{
    public string DiagnosticId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; set; }
    public string TargetEnvironmentName { get; set; } = "";
    public string TargetEnvironmentType { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string? PrerequisiteDiagnosticId { get; set; }
    public HeadlessRunStatus Status { get; set; }
    public HeadlessReadiness Readiness { get; set; } = HeadlessReadiness.Unknown;
    public HeadlessBlocker PrimaryBlocker { get; set; } = HeadlessBlocker.None;
    public string Interpretation { get; set; } = "";
    public string BrowserAutomation { get; set; } = "Not tested";
    public string HeadlessBrowser { get; set; } = "Not tested";
    public string TargetControl { get; set; } = "Not tested";
    public string IdentityProvider { get; set; } = "Unknown";
    public string InteractiveAuthentication { get; set; } = "Unknown / not reached";
    public string NonInteractiveAuthentication { get; set; } = "Unknown / not reached";
    public string Mfa { get; set; } = "Unknown / not reached";
    public string ConditionalAccess { get; set; } = "Unknown / not reached";
    public string SessionControl { get; set; } = "Unknown / not reached";
    public string SessionControlCompatibility { get; set; } = "Unknown / requires IT confirmation";
    public string AuthenticatedReturn { get; set; } = "Not detected";
    public string AuthenticatedSession { get; set; } = "Unknown / not verified";
    public string PostAuthenticationControl { get; set; } = "Not tested";
    public string? ConditionalAccessErrorCode { get; set; }
    /// <summary>
    /// Safe identifiers from a Microsoft Entra error page, only when one was shown on a Microsoft Entra host: the
    /// AADSTS code, and the correlation/request ids and timestamp IT uses to find the sign-in log entry. Nothing else
    /// from the page is kept. Null when not shown or not recognisable.
    /// </summary>
    public string? EntraErrorCode { get; set; }
    public string? EntraCorrelationId { get; set; }
    public string? EntraRequestId { get; set; }
    public string? EntraErrorTimestamp { get; set; }
    /// <summary>Each headline value with its provenance, so observed facts and derived readings are never confused.</summary>
    public List<HeadlessEvidenceItem> Evidence { get; set; } = [];
    public string? EdgeVersion { get; set; }
    public string? PlaywrightVersion { get; set; }
    public string IdentityContext { get; set; } = "Current diagnostic context (fresh dedicated profile; no credentials supplied)";
    public string AutomationIdentity { get; set; } = "Not configured";
    public List<HeadlessStageResult> Stages { get; set; } = Enum.GetValues<HeadlessStage>().Select(s => new HeadlessStageResult(s)).ToList();
    public List<HeadlessNavigation> Navigation { get; set; } = [];
    public List<string> Observations { get; set; } = [];
    public HeadlessStageResult Stage(HeadlessStage stage) => Stages.Single(s => s.Stage == stage);
}

/// <summary>Allowlist output: never retain arbitrary URL paths, query values, fragments, userinfo or page text.</summary>
public static class HeadlessEvidenceSanitizer
{
    public static string Url(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "[redacted URL]";
        var category = uri.AbsolutePath.EndsWith("/authorize", StringComparison.OrdinalIgnoreCase) ? "/[tenant]/authorize"
            : uri.AbsolutePath.Contains("callback", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("signin-oidc", StringComparison.OrdinalIgnoreCase) ? "/[callback]"
            : uri.AbsolutePath == "/" ? "/" : "/[path]";
        return $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{category}"
            + (uri.Query.Length > 0 || uri.Fragment.Length > 0 ? "?[redacted]" : "");
    }
    public static string Label(string? value) => new((value ?? "").Where(c => char.IsLetterOrDigit(c) || " ._-".Contains(c)).Take(100).ToArray());
}

public static class HeadlessItReport
{
    public const string Title = "Headless Authentication & Session Control Diagnostic";
    public static string BlockerLabel(HeadlessBlocker blocker) => blocker switch
    {
        HeadlessBlocker.BrowserAutomationBlocked => "Browser automation blocked",
        HeadlessBlocker.TargetNavigationBlocked => "Target navigation blocked",
        HeadlessBlocker.InteractiveAuthenticationRequired => "Interactive authentication required",
        HeadlessBlocker.InteractiveMfaRequired => "Interactive MFA required",
        HeadlessBlocker.ConditionalAccessBlocked => "Conditional Access blocked",
        HeadlessBlocker.SessionControlHeadlessRestriction => "Explicit session-control headless restriction observed",
        HeadlessBlocker.AutomationControlLostAfterObservedSessionControl => "Automation control lost after an observed session-control signal (sequence, not cause)",
        HeadlessBlocker.AuthenticationSessionNotEstablished => "Authentication session not established",
        HeadlessBlocker.AutomationControlLostAfterAuthentication => "Automation control lost after authentication",
        HeadlessBlocker.IdentityNotAvailableForAutomation => "Identity not available for automation",
        _ => blocker.ToString()
    };
    public static string Build(HeadlessDiagnosticReport r)
    {
        var text = new StringBuilder(Title.ToUpperInvariant()).AppendLine().AppendLine();
        foreach (var (label, value) in new (string, string?)[] {
            ("Diagnostic ID", r.DiagnosticId), ("Started (UTC)", r.StartedAt.ToString("O")), ("Completed (UTC)", r.CompletedAt.ToString("O")),
            ("Target Environment", r.TargetEnvironmentName), ("Environment type", r.TargetEnvironmentType), ("Target", r.TargetUrl),
            ("Browser mode", "Microsoft Edge / Playwright / Headless"), ("Run status", r.Status.ToString()),
            ("Browser automation prerequisite", r.BrowserAutomation), ("Headless browser", r.HeadlessBrowser), ("Target automation", r.TargetControl),
            ("Identity context", r.IdentityContext), ("Dedicated automation identity", r.AutomationIdentity),
            ("Automation authentication strategy", "Not configured"), ("Identity provider", r.IdentityProvider),
            ("Interactive authentication", r.InteractiveAuthentication), ("Non-interactive authentication", r.NonInteractiveAuthentication),
            ("MFA", r.Mfa), ("Conditional Access", r.ConditionalAccess), ("Defender for Cloud Apps / MCAS session control", r.SessionControl),
            ("Session-control headless compatibility", r.SessionControlCompatibility), ("Authenticated return", r.AuthenticatedReturn),
            ("Authenticated application session", r.AuthenticatedSession), ("Automation after authentication", r.PostAuthenticationControl),
            ("HEADLESS READINESS", r.Readiness == HeadlessReadiness.NotReady ? "NOT READY" : r.Readiness.ToString().ToUpperInvariant()),
            ("PRIMARY HEADLESS BLOCKER", $"{BlockerLabel(r.PrimaryBlocker)} ({r.PrimaryBlocker})"), ("Interpretation", r.Interpretation),
            ("Safe CA error code", r.ConditionalAccessErrorCode ?? "Not observed"),
            ("Entra error code", r.EntraErrorCode ?? "Not observed"), ("Entra correlation ID", r.EntraCorrelationId ?? "Not observed"),
            ("Entra request ID", r.EntraRequestId ?? "Not observed"), ("Entra error timestamp", r.EntraErrorTimestamp ?? "Not observed") })
            text.AppendLine($"{label}: {value}");
        if (r.Evidence.Count > 0)
        {
            text.AppendLine().AppendLine("Evidence provenance (Observed = seen in this run; Derived = inferred from observations; Configured = from settings):");
            foreach (var item in r.Evidence) text.AppendLine($"  {item.Name}: {item.Value} [{item.Provenance}]");
        }
        foreach (var observation in r.Observations) text.AppendLine($"Additional observation: {observation}");
        text.AppendLine().AppendLine("Questions for IT/security:");
        if (r.PrimaryBlocker == HeadlessBlocker.BrowserAutomationBlocked)
            text.AppendLine("Can Playwright retain control of this target in the approved runtime? Authentication cannot yet be assessed.");
        else
        {
            text.AppendLine("What approved non-interactive authentication design should a dedicated QA automation identity use for unattended DEV/QA CI execution?");
            if (r.Mfa == "Required") text.AppendLine("Does Conditional Access require interactive MFA for this application and the proposed QA identity?");
            if (r.ConditionalAccess.Contains("observed", StringComparison.OrdinalIgnoreCase))
                text.AppendLine("Can IT correlate the observed Conditional Access signal with Entra sign-in logs?");
            if (r.SessionControl.Contains("signal", StringComparison.OrdinalIgnoreCase) || r.SessionControl.Contains("restriction", StringComparison.OrdinalIgnoreCase))
                text.AppendLine("Does the observed session-control path support headless Edge? Confirm an approved DEV/QA automation policy if needed.");
            if (r.PrimaryBlocker == HeadlessBlocker.AutomationControlLostAfterObservedSessionControl)
                text.AppendLine("Control was lost after a session-control signal was observed. This establishes sequence, not causality: can IT confirm whether session control affected the automated browser?");
            if (r.EntraCorrelationId is not null || r.EntraRequestId is not null)
                text.AppendLine("Can IT look up the Entra sign-in log entry for the correlation/request ID above?");
        }
        text.AppendLine("A future dedicated QA automation identity may behave differently from the current diagnostic context.");
        text.AppendLine("No MFA bypass was attempted. No credentials or tokens were captured. No security settings were modified.");
        return text.ToString();
    }
}
