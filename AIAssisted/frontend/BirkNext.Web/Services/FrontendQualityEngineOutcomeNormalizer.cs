using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Deterministic adapters from existing source-engine contracts to the Phase 2E aggregate contract.</summary>
public static class FrontendQualityEngineOutcomeNormalizer
{
    public static List<FrontendQualityEngineOutcome> NormalizeAll(
        string targetUrl,
        FrontendAnalysisContext context,
        FrontendQualityReviewOrchestrationResult result,
        bool runtimeAdapterAvailable,
        bool accessibilityAdapterAvailable,
        bool lighthouseAdapterAvailable,
        bool passiveSecurityAdapterAvailable,
        bool cancellationRequested = false)
    {
        var policy = context.EngineRequirements.ToPolicy();
        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            StaticSecurity(targetUrl, context.FeatureToggles.EnableSecurityEngine, policy, result.SecurityReport, result.SecurityError, cancellationRequested),
            PassivePerformance(targetUrl, context.FeatureToggles.EnablePerformanceEngine, policy, result.PerformanceReport, result.PerformanceError, cancellationRequested),
            BrowserRuntime(targetUrl, context.FeatureToggles.EnableBrowserRuntimeEngine, policy, result.BrowserRuntimeReport, result.BrowserRuntimeError, runtimeAdapterAvailable, cancellationRequested),
            Accessibility(targetUrl, context.FeatureToggles.EnableAccessibilityEngine, policy, result.AccessibilityReport, result.AccessibilityError, accessibilityAdapterAvailable, cancellationRequested),
            Lighthouse(targetUrl, context.FeatureToggles.EnableLighthouseEngine, policy, result.LighthouseReport, result.LighthouseError, lighthouseAdapterAvailable, cancellationRequested),
            PassiveSecurity(targetUrl, context.FeatureToggles.EnablePassiveSecurityEngine, policy, result.PassiveSecurityReport, result.PassiveSecurityError, passiveSecurityAdapterAvailable, cancellationRequested),
        };

        if (outcomes.Select(o => o.EngineId).Distinct().Count() != Enum.GetValues<FrontendQualityEngineId>().Length)
            throw new InvalidOperationException("Frontend quality normalization must produce exactly one outcome for every known engine.");
        return outcomes;
    }

    public static List<FrontendQualityEngineOutcome> PreflightBlocked(
        string targetUrl,
        FrontendAnalysisContext context,
        PreflightStatus status,
        string? reason)
    {
        var policy = context.EngineRequirements.ToPolicy();
        var state = status switch
        {
            PreflightStatus.AuthenticationRequired => FrontendQualityEngineExecutionState.AuthenticationRequired,
            PreflightStatus.Unreachable or PreflightStatus.ScannerUnavailable => FrontendQualityEngineExecutionState.Unavailable,
            _ => FrontendQualityEngineExecutionState.SafetyBlocked,
        };
        return Enum.GetValues<FrontendQualityEngineId>().Select(id => Base(
            id, DisplayName(id), Enabled(id, context.FeatureToggles), policy.GetRequirement(id),
            Enabled(id, context.FeatureToggles) ? state : FrontendQualityEngineExecutionState.Disabled,
            targetUrl, failure: reason)).ToList();
    }

    public static FrontendQualityEngineOutcome StaticSecurity(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        WasmSecurityReviewReport? report, string? error, bool cancelled = false)
    {
        var state = BasicState(enabled, report is not null && string.IsNullOrWhiteSpace(report.ErrorMessage),
            error ?? report?.ErrorMessage, adapterAvailable: true, cancelled);
        return Base(FrontendQualityEngineId.StaticSecurity, "Static Security", enabled,
            policy.GetRequirement(FrontendQualityEngineId.StaticSecurity), state,
            report?.TargetUrl ?? targetUrl, findings: report?.Findings.Count,
            evidence: report?.Findings.Sum(f => f.Evidence.Count), failure: error ?? report?.ErrorMessage,
            started: report?.ScannedAt == default ? null : report?.ScannedAt,
            limitations: report?.Limitations, strength: FrontendQualityEvidenceStrength.StaticIndicator);
    }

    public static FrontendQualityEngineOutcome PassivePerformance(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        WasmPerformanceReviewReport? report, string? error, bool cancelled = false)
    {
        var state = BasicState(enabled, report is not null && string.IsNullOrWhiteSpace(report.ErrorMessage),
            error ?? report?.ErrorMessage, adapterAvailable: true, cancelled);
        return Base(FrontendQualityEngineId.PassivePerformance, "Passive Performance", enabled,
            policy.GetRequirement(FrontendQualityEngineId.PassivePerformance), state,
            report?.TargetUrl ?? targetUrl, findings: report?.Findings.Count,
            evidence: report?.Findings.Sum(f => f.Evidence.Count), failure: error ?? report?.ErrorMessage,
            started: report?.ReviewedAt == default ? null : report?.ReviewedAt,
            limitations: report?.Limitations, strength: FrontendQualityEvidenceStrength.StaticIndicator);
    }

    public static FrontendQualityEngineOutcome BrowserRuntime(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        BrowserRuntimeResultDto? report, string? error, bool adapterAvailable, bool cancelled = false)
    {
        var state = !enabled ? FrontendQualityEngineExecutionState.Disabled
            : !adapterAvailable ? FrontendQualityEngineExecutionState.Unavailable
            : error is not null && report is null ? FrontendQualityEngineExecutionState.EngineError
            : report?.Status switch
            {
                BrowserRuntimeEngineStatusDto.Assessed => FrontendQualityEngineExecutionState.Assessed,
                BrowserRuntimeEngineStatusDto.EngineError => FrontendQualityEngineExecutionState.EngineError,
                BrowserRuntimeEngineStatusDto.NotApplicable => FrontendQualityEngineExecutionState.NotApplicable,
                BrowserRuntimeEngineStatusDto.Skipped when Contains(report.EngineError, "cancel") => FrontendQualityEngineExecutionState.Cancelled,
                BrowserRuntimeEngineStatusDto.Skipped => FrontendQualityEngineExecutionState.SafetyBlocked,
                _ when cancelled => FrontendQualityEngineExecutionState.Cancelled,
                _ => FrontendQualityEngineExecutionState.Unavailable,
            };
        return Base(FrontendQualityEngineId.BrowserRuntime, "Browser Runtime", enabled,
            policy.GetRequirement(FrontendQualityEngineId.BrowserRuntime), state,
            report?.RequestedUrl ?? targetUrl, report?.FinalUrl, report?.BrowserName, report?.BrowserVersion,
            findings: report?.Findings?.Count, evidence: report?.Findings?.Sum(f => f.Evidence?.Count ?? 0),
            failure: error ?? report?.EngineError, started: NullIfDefault(report?.StartedAt), completed: report?.CompletedAt,
            duration: report?.DurationMs, limitations: report?.Limitations,
            toolName: "Playwright Chromium", strength: FrontendQualityEvidenceStrength.DirectObservation);
    }

    public static FrontendQualityEngineOutcome Accessibility(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        AccessibilityResultDto? report, string? error, bool adapterAvailable, bool cancelled = false)
    {
        var state = !enabled ? FrontendQualityEngineExecutionState.Disabled
            : !adapterAvailable ? FrontendQualityEngineExecutionState.Unavailable
            : error is not null && report is null ? FrontendQualityEngineExecutionState.EngineError
            : report?.ExecutionStatus switch
            {
                AccessibilityExecutionStatusDto.Assessed => FrontendQualityEngineExecutionState.Assessed,
                AccessibilityExecutionStatusDto.EngineError => FrontendQualityEngineExecutionState.EngineError,
                AccessibilityExecutionStatusDto.AuthenticationRequired => FrontendQualityEngineExecutionState.AuthenticationRequired,
                AccessibilityExecutionStatusDto.Skipped => FrontendQualityEngineExecutionState.SafetyBlocked,
                _ when cancelled => FrontendQualityEngineExecutionState.Cancelled,
                _ => FrontendQualityEngineExecutionState.Unavailable,
            };
        return Base(FrontendQualityEngineId.Accessibility, "Accessibility", enabled,
            policy.GetRequirement(FrontendQualityEngineId.Accessibility), state,
            report?.RequestedUrl ?? targetUrl, report?.FinalUrl, report?.BrowserName, report?.BrowserVersion,
            findings: report?.Findings?.Count, evidence: report?.Findings?.Sum(f => f.Selectors.Count + f.FailureSummaries.Count),
            failure: error ?? report?.EngineError, started: NullIfDefault(report?.StartedAt), completed: report?.CompletedAt,
            duration: report?.DurationMs, limitations: report?.Limitations, toolName: "axe-core",
            toolVersion: report?.AxeVersion, strength: FrontendQualityEvidenceStrength.ToolDiagnostic,
            manual: report is null ? null : ["Manual accessibility testing remains required; automated results do not establish WCAG conformance."]);
    }

    public static FrontendQualityEngineOutcome Lighthouse(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        LighthouseResultDto? report, string? error, bool adapterAvailable, bool cancelled = false)
    {
        var state = !enabled ? FrontendQualityEngineExecutionState.Disabled
            : !adapterAvailable ? FrontendQualityEngineExecutionState.Unavailable
            : error is not null && report is null ? FrontendQualityEngineExecutionState.EngineError
            : report?.ExecutionStatus switch
            {
                LighthouseExecutionStatusDto.Assessed => FrontendQualityEngineExecutionState.Assessed,
                LighthouseExecutionStatusDto.TimedOut => FrontendQualityEngineExecutionState.TimedOut,
                LighthouseExecutionStatusDto.EngineError => FrontendQualityEngineExecutionState.EngineError,
                LighthouseExecutionStatusDto.AuthenticationRequired => FrontendQualityEngineExecutionState.AuthenticationRequired,
                LighthouseExecutionStatusDto.Skipped => FrontendQualityEngineExecutionState.SafetyBlocked,
                _ when cancelled => FrontendQualityEngineExecutionState.Cancelled,
                _ => FrontendQualityEngineExecutionState.Unavailable,
            };
        return Base(FrontendQualityEngineId.Lighthouse, "Lighthouse", enabled,
            policy.GetRequirement(FrontendQualityEngineId.Lighthouse), state,
            report?.RequestedUrl ?? targetUrl, report?.FinalUrl, report?.BrowserName, report?.BrowserVersion,
            findings: report?.Audits?.Count, evidence: report?.Metrics?.Count,
            failure: error ?? report?.EngineError, duration: report?.DurationMs, limitations: report?.Limitations,
            toolName: string.IsNullOrWhiteSpace(report?.NodeVersion) ? "Lighthouse" : $"Lighthouse (Node {Safe(report.NodeVersion)})",
            toolVersion: report?.LighthouseVersion, strength: FrontendQualityEvidenceStrength.ToolDiagnostic);
    }

    public static FrontendQualityEngineOutcome PassiveSecurity(
        string targetUrl, bool enabled, FrontendQualityEngineRequirementPolicy policy,
        PassiveSecurityResultDto? report, string? error, bool adapterAvailable, bool cancelled = false)
    {
        var state = !enabled ? FrontendQualityEngineExecutionState.Disabled
            : !adapterAvailable ? FrontendQualityEngineExecutionState.Unavailable
            : error is not null && report is null ? FrontendQualityEngineExecutionState.EngineError
            : report?.ExecutionStatus switch
            {
                PassiveSecurityExecutionStatusDto.Assessed => FrontendQualityEngineExecutionState.Assessed,
                PassiveSecurityExecutionStatusDto.TimedOut => FrontendQualityEngineExecutionState.TimedOut,
                PassiveSecurityExecutionStatusDto.EngineError => FrontendQualityEngineExecutionState.EngineError,
                PassiveSecurityExecutionStatusDto.AuthenticationRequired => FrontendQualityEngineExecutionState.AuthenticationRequired,
                PassiveSecurityExecutionStatusDto.Skipped when Contains(report.EngineError, "disabled") => FrontendQualityEngineExecutionState.Unavailable,
                PassiveSecurityExecutionStatusDto.Skipped => FrontendQualityEngineExecutionState.SafetyBlocked,
                _ when cancelled => FrontendQualityEngineExecutionState.Cancelled,
                _ => FrontendQualityEngineExecutionState.Unavailable,
            };
        return Base(FrontendQualityEngineId.PassiveSecurity, "Passive Security", enabled,
            policy.GetRequirement(FrontendQualityEngineId.PassiveSecurity), state,
            report?.RequestedUrl ?? targetUrl, report?.FinalUrl,
            findings: report?.Findings?.Count, evidence: report?.Findings?.Count(f => !string.IsNullOrWhiteSpace(f.Evidence)),
            failure: error ?? report?.EngineError, started: report?.StartedAt, completed: report?.CompletedAt,
            duration: report?.DurationMs, limitations: report?.Limitations, toolName: "OWASP ZAP Passive",
            toolVersion: report?.ZapVersion, strength: FrontendQualityEvidenceStrength.ToolDiagnostic);
    }

    public static List<FrontendQualityFinding> NormalizeBrowserRuntimeFindings(BrowserRuntimeResultDto? report) =>
        report?.Status != BrowserRuntimeEngineStatusDto.Assessed ? [] : (report.Findings ?? []).Select(f => new FrontendQualityFinding
        {
            Id = $"runtime-{f.Id}",
            Title = Safe(f.Title),
            Severity = f.Severity switch
            {
                BrowserRuntimeFindingSeverityDto.Critical => FrontendQualitySeverity.Critical,
                BrowserRuntimeFindingSeverityDto.High => FrontendQualitySeverity.High,
                BrowserRuntimeFindingSeverityDto.Medium => FrontendQualitySeverity.Medium,
                BrowserRuntimeFindingSeverityDto.Low => FrontendQualitySeverity.Low,
                _ => FrontendQualitySeverity.Info,
            },
            Category = f.Category == "ResourceFailure" ? FrontendQualityCategory.Performance : FrontendQualityCategory.BlazorWasm,
            Description = Safe(f.Description),
            Recommendation = Safe(f.Recommendation),
            Evidence = (f.Evidence ?? []).Select(Safe).ToList(),
            SourceSystem = "Browser Runtime",
            EngineId = FrontendQualityEngineId.BrowserRuntime,
            SourceRuleId = f.Id,
            Status = CheckExecutionStatus.Failed,
        }).ToList();

    public static BrowserRuntimeResultDto SanitizeBrowserRuntimeReport(BrowserRuntimeResultDto report) => report with
    {
        EngineName = Safe(report.EngineName),
        BrowserName = SafeNullable(report.BrowserName),
        BrowserVersion = SafeNullable(report.BrowserVersion),
        RequestedUrl = SafeNullable(report.RequestedUrl),
        FinalUrl = SafeNullable(report.FinalUrl),
        EngineError = SafeNullable(report.EngineError),
        Limitations = (report.Limitations ?? []).Select(Safe).ToList(),
        Findings = (report.Findings ?? []).Select(f => f with
        {
            Id = Safe(f.Id),
            Title = Safe(f.Title),
            Category = Safe(f.Category),
            Description = Safe(f.Description),
            Recommendation = Safe(f.Recommendation),
            Evidence = (f.Evidence ?? []).Select(Safe).ToList(),
        }).ToList(),
    };

    private static FrontendQualityEngineExecutionState BasicState(
        bool enabled, bool assessed, string? error, bool adapterAvailable, bool cancelled) =>
        !enabled ? FrontendQualityEngineExecutionState.Disabled
        : !adapterAvailable ? FrontendQualityEngineExecutionState.Unavailable
        : assessed ? FrontendQualityEngineExecutionState.Assessed
        : cancelled ? FrontendQualityEngineExecutionState.Cancelled
        : error is not null ? FrontendQualityEngineExecutionState.EngineError
        : FrontendQualityEngineExecutionState.Unavailable;

    private static FrontendQualityEngineOutcome Base(
        FrontendQualityEngineId id, string displayName, bool enabled,
        FrontendQualityEngineRequirement requirement, FrontendQualityEngineExecutionState state,
        string? requested = null, string? final = null, string? browser = null, string? browserVersion = null,
        int? findings = null, int? evidence = null, string? failure = null, DateTime? started = null,
        DateTime? completed = null, long? duration = null, List<string>? limitations = null,
        string? toolName = null, string? toolVersion = null, FrontendQualityEvidenceStrength? strength = null,
        List<string>? manual = null) => new()
        {
            EngineId = id,
            DisplayName = displayName,
            Enabled = enabled,
            Requirement = requirement,
            ReadinessState = state == FrontendQualityEngineExecutionState.Unavailable
                ? FrontendQualityEngineReadinessState.Unavailable
                : state is FrontendQualityEngineExecutionState.Disabled or FrontendQualityEngineExecutionState.NotApplicable
                    ? FrontendQualityEngineReadinessState.NotApplicable
                    : FrontendQualityEngineReadinessState.NotEvaluated,
            ReadinessReason = state == FrontendQualityEngineExecutionState.Unavailable ? SafeNullable(failure) : null,
            ExecutionState = state,
            RequestedTarget = SafeNullable(requested),
            FinalTarget = SafeNullable(final),
            StartedAt = started,
            CompletedAt = completed,
            DurationMs = duration,
            ToolName = SafeNullable(toolName),
            ToolVersion = SafeNullable(toolVersion),
            BrowserName = SafeNullable(browser),
            BrowserVersion = SafeNullable(browserVersion),
            FindingCount = findings,
            EvidenceCount = evidence,
            SanitizedFailureReason = state is FrontendQualityEngineExecutionState.Assessed or FrontendQualityEngineExecutionState.Disabled
                ? null : SafeNullable(failure),
            Limitations = (limitations ?? []).Select(Safe).ToList(),
            ManualTestingObligations = (manual ?? []).Select(Safe).ToList(),
            Evidence = strength.HasValue && state == FrontendQualityEngineExecutionState.Assessed
                ? [new FrontendQualityEvidenceDescriptor
                {
                    Strength = strength.Value,
                    Disposition = FrontendQualityReviewDisposition.AutomatedFinding,
                    Confidence = FrontendQualityEvidenceConfidence.High,
                }]
                : [],
        };

    private static bool Enabled(FrontendQualityEngineId id, FrontendAnalysisFeatureToggles toggles) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => toggles.EnableSecurityEngine,
        FrontendQualityEngineId.PassivePerformance => toggles.EnablePerformanceEngine,
        FrontendQualityEngineId.BrowserRuntime => toggles.EnableBrowserRuntimeEngine,
        FrontendQualityEngineId.Accessibility => toggles.EnableAccessibilityEngine,
        FrontendQualityEngineId.Lighthouse => toggles.EnableLighthouseEngine,
        FrontendQualityEngineId.PassiveSecurity => toggles.EnablePassiveSecurityEngine,
        _ => false,
    };

    private static string DisplayName(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Static Security",
        FrontendQualityEngineId.PassivePerformance => "Passive Performance",
        FrontendQualityEngineId.BrowserRuntime => "Browser Runtime",
        FrontendQualityEngineId.Accessibility => "Accessibility",
        FrontendQualityEngineId.Lighthouse => "Lighthouse",
        FrontendQualityEngineId.PassiveSecurity => "Passive Security",
        _ => id.ToString(),
    };

    private static bool Contains(string? value, string token) => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
    private static DateTime? NullIfDefault(DateTime? value) => value is null || value == default ? null : value;
    private static string Safe(string? value) => ReportExportService.SanitizePassive(value);
    private static string? SafeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : Safe(value);
}
