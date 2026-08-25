using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ISecurityScanner
{
    Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request);
}

public sealed class SecurityScannerAdapter : ISecurityScanner
{
    private readonly WasmSecurityApiService _inner;

    public SecurityScannerAdapter(WasmSecurityApiService inner) => _inner = inner;

    public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
        _inner.ScanAsync(request);
}

/// <summary>
/// Minimal orchestration boundary for feature-toggle and preflight logic.
/// Extracted ONLY for testability — no rendering logic included.
/// This orchestrates: preflight → toggles → scanner invocation → state tracking.
/// </summary>
public interface IFrontendQualityReviewOrchestrator
{
    Task<FrontendQualityReviewOrchestrationResult> RunAsync(
        string targetUrl,
        FrontendAnalysisContext context,
        CancellationToken cancellationToken = default);
}

public sealed record FrontendQualityReviewOrchestrationResult(
    WasmSecurityReviewReport? SecurityReport = null,
    WasmPerformanceReviewReport? PerformanceReport = null,
    BrowserRuntimeResultDto? BrowserRuntimeReport = null,
    AccessibilityResultDto? AccessibilityReport = null,
    FrontendQualityReviewReport? QualityReport = null,
    string? SecurityError = null,
    string? PerformanceError = null,
    string? BrowserRuntimeError = null,
    string? AccessibilityError = null,
    List<string>? SkippedEngines = null,
    bool PreflightBlocked = false,
    string? PreflightBlockReason = null,
    PreflightStatus PreflightStatus = PreflightStatus.Ready)
{
    public List<string> SkippedEngines { get; init; } = SkippedEngines ?? [];
}

public sealed class FrontendQualityReviewOrchestrator : IFrontendQualityReviewOrchestrator
{
    private readonly ISecurityScanner _security;
    private readonly IBlazorWasmPerformanceReviewService _performance;
    private readonly ITargetPreflightService _preflight;
    private readonly IFrontendQualityReviewService _quality;
    private readonly IFrontendBrowserRuntimeReviewApiService? _runtime;
    private readonly IFrontendAccessibilityReviewApiService? _accessibility;

    public FrontendQualityReviewOrchestrator(
        ISecurityScanner security,
        IBlazorWasmPerformanceReviewService performance,
        ITargetPreflightService preflight,
        IFrontendQualityReviewService quality,
        IFrontendBrowserRuntimeReviewApiService? runtime = null,
        IFrontendAccessibilityReviewApiService? accessibility = null)
    {
        _security = security;
        _performance = performance;
        _preflight = preflight;
        _quality = quality;
        _runtime = runtime;
        _accessibility = accessibility;
    }

    public async Task<FrontendQualityReviewOrchestrationResult> RunAsync(
        string targetUrl,
        FrontendAnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new FrontendQualityReviewOrchestrationResult();

        // ── Preflight validation ────────────────────────────────
        try
        {
            var preflightResult = await _preflight.CheckTargetAsync(targetUrl);
            result = result with
            {
                PreflightStatus = preflightResult.Status,
                PreflightBlockReason = preflightResult.Message
            };

            // Block scanners on error statuses
            if (preflightResult.Status is PreflightStatus.Unreachable
                or PreflightStatus.InvalidTarget
                or PreflightStatus.AuthenticationRequired
                or PreflightStatus.ScannerUnavailable)
            {
                return result with
                {
                    PreflightBlocked = true,
                    PreflightBlockReason = preflightResult.Message
                };
            }
        }
        catch (Exception ex)
        {
            return result with
            {
                PreflightBlocked = true,
                PreflightBlockReason = $"Preflight error: {ex.Message}"
            };
        }

        // ── Security scanner — respects toggle ──────────────────
        if (context.FeatureToggles.EnableSecurityEngine)
        {
            try
            {
                var scanRequest = BuildScanRequest(context);
                var (report, error) = await _security.ScanAsync(scanRequest);
                result = result with
                {
                    SecurityReport = report,
                    SecurityError = error
                };
            }
            catch (Exception ex)
            {
                result = result with { SecurityError = ex.Message };
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Security"] };
        }

        // ── Performance scanner — respects toggle ───────────────
        if (context.FeatureToggles.EnablePerformanceEngine)
        {
            try
            {
                var perfReport = await _performance.RunReviewAsync(
                    targetUrl,
                    context.ActiveProfile.Performance,
                    cancellationToken);
                result = result with { PerformanceReport = perfReport };
            }
            catch (Exception ex)
            {
                result = result with { PerformanceError = ex.Message };
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Performance"] };
        }

        // ── Browser Runtime scanner — respects toggle ───────────────
        if (context.FeatureToggles.EnableBrowserRuntimeEngine && _runtime != null)
        {
            try
            {
                var runtimeReport = await _runtime.ReviewAsync(targetUrl, 30000, 5000, cancellationToken);
                result = result with { BrowserRuntimeReport = runtimeReport };
            }
            catch (Exception ex)
            {
                result = result with { BrowserRuntimeError = ex.Message };
            }
        }
        else if (context.FeatureToggles.EnableBrowserRuntimeEngine)
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }

        if (context.FeatureToggles.EnableAccessibilityEngine && _accessibility is not null)
        {
            try
            {
                var accessibilityReport = await _accessibility.ReviewAsync(
                    targetUrl,
                    context.ActiveProfile.EnvironmentType.ToString(),
                    context.RequiresAuthentication,
                    cancellationToken);
                result = result with { AccessibilityReport = accessibilityReport };
            }
            catch (Exception ex)
            {
                result = result with { AccessibilityError = ex.Message };
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Accessibility"] };
        }

        if (result.SecurityReport is null && result.PerformanceReport is null)
            return result;

        var qualityReport = _quality.BuildReport(targetUrl, result.SecurityReport, result.PerformanceReport);
        var runtimeFailed = result.BrowserRuntimeReport?.Status == BrowserRuntimeEngineStatusDto.EngineError
            || result.BrowserRuntimeError is not null;
        var accessibilityFailed = result.AccessibilityReport?.ExecutionStatus == AccessibilityExecutionStatusDto.EngineError
            || result.AccessibilityError is not null;
        var accessibilitySkipped = result.AccessibilityReport?.ExecutionStatus is
            AccessibilityExecutionStatusDto.Skipped or AccessibilityExecutionStatusDto.AuthenticationRequired;
        if (accessibilitySkipped && !result.SkippedEngines.Contains("Accessibility"))
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Accessibility"] };
        var accessibilityAssessed = result.AccessibilityReport?.ExecutionStatus == AccessibilityExecutionStatusDto.Assessed;
        var enrichedReport = ApplyAccessibility(qualityReport, result.AccessibilityReport, accessibilityAssessed);

        return result with
        {
            QualityReport = CopyReport(
                enrichedReport,
                runtimeFailed || accessibilityFailed ? AssessmentCompleteness.Partial : enrichedReport.Completeness,
                enrichedReport.FailedEngines
                    .Concat(runtimeFailed ? ["Browser Runtime"] : [])
                    .Concat(accessibilityFailed ? ["Accessibility"] : [])
                    .Distinct().ToList(),
                enrichedReport.SkippedEngines.Concat(result.SkippedEngines).Distinct().ToList(),
                result.PreflightStatus,
                result.PreflightBlockReason)
        };
    }

    private static FrontendQualityReviewReport CopyReport(
        FrontendQualityReviewReport report,
        AssessmentCompleteness? completeness,
        List<string> failedEngines,
        List<string> skippedEngines,
        PreflightStatus preflightStatus,
        string? preflightMessage) => new()
    {
        TargetUrl = report.TargetUrl, FinalUrl = report.FinalUrl, GeneratedAt = report.GeneratedAt,
        CompletedAt = report.CompletedAt, DurationMs = report.DurationMs, OverallScore = report.OverallScore,
        PerformanceScore = report.PerformanceScore, SecurityScore = report.SecurityScore,
        AccessibilityScore = report.AccessibilityScore, StandardsScore = report.StandardsScore,
        WasmScore = report.WasmScore, ReadinessScore = report.ReadinessScore, Findings = report.Findings,
        CategoryScores = report.CategoryScores, Recommendations = report.Recommendations, Risks = report.Risks,
        Limitations = report.Limitations, IsBlazorWasm = report.IsBlazorWasm, ErrorMessage = report.ErrorMessage,
        Completeness = completeness, PreflightStatus = preflightStatus, PreflightMessage = preflightMessage,
        RedirectOccurred = report.RedirectOccurred, AssessedEngines = report.AssessedEngines,
        FailedEngines = failedEngines, SkippedEngines = skippedEngines,
        AccessibilityReport = report.AccessibilityReport
    };

    private static FrontendQualityReviewReport ApplyAccessibility(
        FrontendQualityReviewReport report,
        AccessibilityResultDto? accessibility,
        bool assessed)
    {
        if (accessibility is null) return report;
        var accessibilityFindings = (accessibility.Findings ?? []).Select(f => new FrontendQualityFinding
        {
            Id = $"axe-{f.Kind}-{f.RuleId}", Title = f.Title, Severity = f.Severity,
            Category = FrontendQualityCategory.Accessibility, Description = f.Description,
            Recommendation = f.Recommendation,
            Evidence = [.. f.Selectors.Select(s => $"Selector: {s}"), .. f.FailureSummaries],
            SourceSystem = "axe-core",
            Status = f.Kind == AccessibilityFindingKindDto.Violation ? CheckExecutionStatus.Failed : CheckExecutionStatus.NotAssessed
        }).ToList();
        var scores = report.CategoryScores.Select(score => score.Category == FrontendQualityCategory.Accessibility
            ? new FrontendQualityCategoryScore
            {
                Category = score.Category, Score = null, FindingCount = accessibilityFindings.Count,
                Critical = accessibilityFindings.Count(f => f.Severity == FrontendQualitySeverity.Critical),
                High = accessibilityFindings.Count(f => f.Severity == FrontendQualitySeverity.High),
                Assessed = assessed,
                NotAssessedReason = assessed ? "No numeric score: automated checks do not establish WCAG conformance." : accessibility.EngineError
            }
            : score).ToList();
        return new FrontendQualityReviewReport
        {
            TargetUrl = report.TargetUrl, FinalUrl = report.FinalUrl, GeneratedAt = report.GeneratedAt,
            OverallScore = report.OverallScore, PerformanceScore = report.PerformanceScore,
            SecurityScore = report.SecurityScore, AccessibilityScore = null, StandardsScore = report.StandardsScore,
            WasmScore = report.WasmScore, ReadinessScore = report.ReadinessScore,
            Findings = report.Findings.Where(f => f.Category != FrontendQualityCategory.Accessibility).Concat(accessibilityFindings).ToList(),
            CategoryScores = scores, Recommendations = report.Recommendations, Risks = report.Risks,
            Limitations = report.Limitations.Concat(accessibility.Limitations ?? []).Distinct().ToList(),
            IsBlazorWasm = report.IsBlazorWasm, Completeness = report.Completeness,
            AssessedEngines = assessed ? report.AssessedEngines.Append("Accessibility").Distinct().ToList() : report.AssessedEngines,
            FailedEngines = report.FailedEngines, SkippedEngines = report.SkippedEngines,
            AccessibilityReport = accessibility
        };
    }

    private static WasmScanRequest BuildScanRequest(FrontendAnalysisContext ctx)
    {
        var allowedHosts = ctx.AllowedBackendDomains
            .Concat(ctx.AllowedRestHosts)
            .Distinct()
            .ToList();

        var clientIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.SecuritySettings.ExpectedClientId))
            clientIds.Add(ctx.SecuritySettings.ExpectedClientId);

        var knownSafe = ctx.AllowedGraphQlEndpoints
            .Concat(ctx.AllowedCdnHosts)
            .ToList();

        return new WasmScanRequest
        {
            TargetUrl = ctx.TargetUrl,
            EnvironmentName = ctx.ActiveProfile.EnvironmentType.ToString(),
            ExpectedApiGatewayBasePath = ctx.ActiveProfile.ExpectedApiGateway,
            AllowedBackendHostnames = allowedHosts,
            AllowedAuthority = ctx.SecuritySettings.ExpectedAuthority,
            AllowedClientIds = clientIds,
            KnownSafeDomains = knownSafe
        };
    }
}
