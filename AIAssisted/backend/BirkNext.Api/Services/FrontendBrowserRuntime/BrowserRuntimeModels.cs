using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.FrontendBrowserRuntime;

public enum BrowserRuntimeEngineStatus
{
    NotAssessed,
    Assessed,
    EngineError,
    Skipped,
    NotApplicable,
}

public enum BrowserStartupState
{
    Started,
    StartedWithErrors,
    Failed,
    TimedOut,
    NotApplicable,
}

public sealed record BrowserRuntimeOptions(
    int NavigationTimeoutMs = 30000,
    int StartupObservationMs = 5000,
    bool HeadlessMode = true,
    string ViewportWidth = "1440",
    string ViewportHeight = "900");

public sealed record BrowserRuntimeResult(
    BrowserRuntimeEngineStatus Status = BrowserRuntimeEngineStatus.NotAssessed,
    string EngineName = "Browser Runtime",
    string? BrowserName = null,
    string? BrowserVersion = null,
    string? RequestedUrl = null,
    string? FinalUrl = null,
    DateTime StartedAt = default,
    DateTime? CompletedAt = null,
    long? DurationMs = null,
    BrowserStartupState StartupState = BrowserStartupState.NotApplicable,
    int ConsoleErrorCount = 0,
    int PageErrorCount = 0,
    int CriticalResourceFailureCount = 0,
    List<BrowserRuntimeFinding>? Findings = null,
    string? EngineError = null,
    List<string>? Limitations = null,
    bool RedirectOccurred = false,
    string? FinalRedirectReason = null);

public sealed record BrowserRuntimeFinding(
    string Id,
    string Title,
    BrowserRuntimeFindingSeverity Severity,
    string Category,
    string Description,
    string Recommendation,
    List<string>? Evidence = null);

public enum BrowserRuntimeFindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public sealed record BrowserConsoleEvent(
    string Type,
    string Message,
    string? Location,
    int? LineNumber,
    int? ColumnNumber);

public sealed record BrowserPageError(
    string Message,
    string? Location,
    string? Stack);

public sealed record BrowserResourceFailure(
    string Url,
    string ResourceType,
    string FailureReason,
    int? StatusCode);

public sealed record BrowserStartupObservation(
    bool DomContentLoadedReached,
    bool LoadEventReached,
    long NavigationDurationMs,
    bool BlazorDetected,
    bool BlazorBootstrapCompleted,
    List<BrowserConsoleEvent> ConsoleEvents,
    List<BrowserPageError> PageErrors,
    List<BrowserResourceFailure> ResourceFailures,
    int CriticalResourceFailureCount);
