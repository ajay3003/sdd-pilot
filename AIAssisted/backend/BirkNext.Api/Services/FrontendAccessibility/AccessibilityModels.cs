namespace BirkNext.Api.Services.FrontendAccessibility;

public enum AccessibilityExecutionMode { AnonymousOwnedBrowser, AuthenticatedSessionPage }
public enum AccessibilityExecutionStatus { NotAssessed, Assessed, EngineError, Skipped, AuthenticationRequired }
public enum AccessibilityOutcomeReason { None, AuthenticationRequired, AuthenticationExpired, AuthenticationCancelled, UnexpectedOrigin }
public enum AccessibilityFindingKind { Violation, NeedsManualReview }
public enum AccessibilityFindingSeverity { Critical, High, Medium, Low, Info }
public enum AccessibilityReadinessState { Disabled, Ready, ChromiumUnavailable, AxeUnavailable, LaunchFailed }

public sealed record AccessibilityFinding(
    string RuleId,
    AccessibilityFindingKind Kind,
    AccessibilityFindingSeverity Severity,
    string? Impact,
    string Title,
    string Description,
    List<string> WcagTags,
    int AffectedNodeCount,
    List<string> Selectors,
    List<string> HtmlSnippets,
    List<string> FailureSummaries,
    string? HelpUrl,
    string Recommendation);

public sealed record AccessibilityReviewResult(
    AccessibilityExecutionStatus ExecutionStatus = AccessibilityExecutionStatus.NotAssessed,
    string EngineName = "Accessibility (axe-core)",
    string? AxeVersion = null,
    string? BrowserName = null,
    string? BrowserVersion = null,
    string? RequestedUrl = null,
    string? FinalUrl = null,
    DateTime StartedAt = default,
    DateTime? CompletedAt = null,
    long? DurationMs = null,
    List<string>? RuleTags = null,
    int ViolationCount = 0,
    int IncompleteCount = 0,
    int PassCount = 0,
    int InapplicableCount = 0,
    List<AccessibilityFinding>? Findings = null,
    List<string>? Limitations = null,
    string? EngineError = null,
    AccessibilityExecutionMode ExecutionMode = AccessibilityExecutionMode.AnonymousOwnedBrowser,
    AccessibilityOutcomeReason OutcomeReason = AccessibilityOutcomeReason.None)
{
    public List<string> RuleTags { get; init; } = RuleTags ?? [];
    public List<AccessibilityFinding> Findings { get; init; } = Findings ?? [];
    public List<string> Limitations { get; init; } = Limitations ?? [];
}

public sealed record AccessibilityExecutionRequest(
    string TargetUrl,
    AccessibilityExecutionMode ExecutionMode = AccessibilityExecutionMode.AnonymousOwnedBrowser,
    string? ReviewSessionId = null,
    string? ProfileId = null,
    string? AuthenticatedSessionId = null,
    AccessibilityReviewOptions? Options = null);

public sealed record AccessibilityReadinessResult(
    AccessibilityReadinessState State,
    bool Available,
    string? AxeVersion = null,
    string? BrowserName = null,
    string? BrowserVersion = null,
    string? Error = null);

public sealed record AccessibilityReviewOptions(
    int NavigationTimeoutMs = 30000,
    int StabilizationMs = 1000,
    bool Headless = true,
    string EnvironmentType = "Public");
