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
        FrontendQualityEngineExecutionSnapshot? snapshot = null,
        CancellationToken cancellationToken = default);
}

public sealed record FrontendQualityReviewOrchestrationResult(
    WasmSecurityReviewReport? SecurityReport = null,
    WasmPerformanceReviewReport? PerformanceReport = null,
    BrowserRuntimeResultDto? BrowserRuntimeReport = null,
    AccessibilityResultDto? AccessibilityReport = null,
    LighthouseResultDto? LighthouseReport = null,
    PassiveSecurityResultDto? PassiveSecurityReport = null,
    FrontendQualityReviewReport? QualityReport = null,
    string? SecurityError = null,
    string? PerformanceError = null,
    string? BrowserRuntimeError = null,
    string? AccessibilityError = null,
    string? LighthouseError = null,
    string? PassiveSecurityError = null,
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
    private readonly IFrontendLighthouseReviewApiService? _lighthouse;
    private readonly IFrontendPassiveSecurityApiService? _passiveSecurity;
    private readonly IAuthenticatedBrowserSessionService? _authenticatedSessions;
    private readonly IFrontendQualityEngineStatusApiService? _engineStatusService;

    public FrontendQualityReviewOrchestrator(
        ISecurityScanner security,
        IBlazorWasmPerformanceReviewService performance,
        ITargetPreflightService preflight,
        IFrontendQualityReviewService quality,
        IFrontendBrowserRuntimeReviewApiService? runtime = null,
        IFrontendAccessibilityReviewApiService? accessibility = null,
        IFrontendLighthouseReviewApiService? lighthouse = null,
        IFrontendPassiveSecurityApiService? passiveSecurity = null,
        IAuthenticatedBrowserSessionService? authenticatedSessions = null,
        IFrontendQualityEngineStatusApiService? engineStatusService = null)
    {
        _security = security;
        _performance = performance;
        _preflight = preflight;
        _quality = quality;
        _runtime = runtime;
        _accessibility = accessibility;
        _lighthouse = lighthouse;
        _passiveSecurity = passiveSecurity;
        _authenticatedSessions = authenticatedSessions;
        _engineStatusService = engineStatusService;
    }

    public async Task<FrontendQualityReviewOrchestrationResult> RunAsync(
        string targetUrl,
        FrontendAnalysisContext context,
        FrontendQualityEngineExecutionSnapshot? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        var result = new FrontendQualityReviewOrchestrationResult();
        var authenticatedReference = context.RequiresAuthentication && _authenticatedSessions is not null
            ? await _authenticatedSessions.GetExecutionReferenceAsync(context)
            : null;

        snapshot ??= CaptureDefaultSnapshot(context);

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
                or PreflightStatus.ScannerUnavailable)
            {
                var blocked = result with
                {
                    PreflightBlocked = true,
                    PreflightBlockReason = preflightResult.Message
                };
                return blocked with { QualityReport = BuildPreflightReport(targetUrl, context, blocked) };
            }
            if (preflightResult.Status == PreflightStatus.AuthenticationRequired && authenticatedReference is null)
            {
                var blocked = result with { PreflightBlocked = true, PreflightBlockReason = preflightResult.Message };
                return blocked with { QualityReport = BuildPreflightReport(targetUrl, context, blocked) };
            }
        }
        catch (Exception ex)
        {
            var blocked = result with
            {
                PreflightBlocked = true,
                PreflightBlockReason = $"Preflight error: {ex.Message}"
            };
            return blocked with { QualityReport = BuildPreflightReport(targetUrl, context, blocked, PreflightStatus.InvalidTarget) };
        }

        // ── Security scanner — uses snapshot eligibility ──────────────────
        // Note: Security is not in the 4 required engines; kept for compatibility
        if (context.FeatureToggles.EnableSecurityEngine && !context.RequiresAuthentication)
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
        if (context.FeatureToggles.EnablePerformanceEngine && !context.RequiresAuthentication)
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

        // ── Browser Runtime scanner — uses snapshot eligibility + Layer 3 readiness ─────────────
        var browserRuntimeEligible = IsEngineEligibleToExecute(snapshot, FrontendQualityEngineIdDto.BrowserRuntime) && _runtime != null;
        if (browserRuntimeEligible)
        {
            // Pre-execution Layer 3 readiness revalidation (5-second timeout)
            var runtimeReady = await RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto.BrowserRuntime, cancellationToken);
            if (!runtimeReady)
            {
                result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
            }
            else
            {
                try
                {
                    var runtimeReport = authenticatedReference is null
                        ? await _runtime.ReviewAsync(targetUrl, 30000, 5000, cancellationToken)
                        : await _runtime.ReviewAsync(new BrowserRuntimeApiExecutionRequest(
                            targetUrl,
                            BrowserRuntimeExecutionModeDto.AuthenticatedSessionPage,
                            authenticatedReference.ReviewSessionId,
                            authenticatedReference.ProfileId,
                            authenticatedReference.SessionId), cancellationToken);
                    result = result with { BrowserRuntimeReport = runtimeReport };
                }
                catch (Exception ex)
                {
                    result = result with { BrowserRuntimeError = ex.Message };
                }
            }
        }
        else if (!browserRuntimeEligible && _runtime != null)
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }

        var accessibilityEligible = IsEngineEligibleToExecute(snapshot, FrontendQualityEngineIdDto.Accessibility) && _accessibility is not null;
        if (accessibilityEligible)
        {
            // Pre-execution Layer 3 readiness revalidation (5-second timeout)
            var accessibilityReady = await RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto.Accessibility, cancellationToken);
            if (!accessibilityReady)
            {
                result = result with { SkippedEngines = [.. result.SkippedEngines, "Accessibility"] };
            }
            else
            {
                try
                {
                    var accessibilityReport = authenticatedReference is null
                        ? await _accessibility.ReviewAsync(
                            targetUrl,
                            context.ActiveProfile.EnvironmentType.ToString(),
                            false,
                            cancellationToken)
                        : await _accessibility.ReviewAsync(new AccessibilityApiExecutionRequest(
                            targetUrl,
                            AccessibilityExecutionModeDto.AuthenticatedSessionPage,
                            authenticatedReference.ReviewSessionId,
                            authenticatedReference.ProfileId,
                            authenticatedReference.SessionId,
                            context.ActiveProfile.EnvironmentType.ToString()), cancellationToken);
                    result = result with { AccessibilityReport = accessibilityReport };
                }
                catch (Exception ex)
                {
                    result = result with { AccessibilityError = ex.Message };
                }
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Accessibility"] };
        }

        var lighthouseEligible = IsEngineEligibleToExecute(snapshot, FrontendQualityEngineIdDto.Lighthouse) && _lighthouse is not null;
        if (lighthouseEligible)
        {
            // Pre-execution Layer 3 readiness revalidation (5-second timeout)
            var lighthouseReady = await RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto.Lighthouse, cancellationToken);
            if (!lighthouseReady)
            {
                result = result with { SkippedEngines = [.. result.SkippedEngines, "Lighthouse"] };
            }
            else
            {
                try
                {
                    result = result with { LighthouseReport = await _lighthouse.ReviewAsync(targetUrl, context.RequiresAuthentication, cancellationToken) };
                }
                catch (Exception ex)
                {
                    result = result with { LighthouseError = ex.Message };
                }
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Lighthouse"] };
        }

        var passiveSecurityEligible = IsEngineEligibleToExecute(snapshot, FrontendQualityEngineIdDto.PassiveSecurity) && _passiveSecurity is not null;
        if (passiveSecurityEligible)
        {
            // Pre-execution Layer 3 readiness revalidation (5-second timeout)
            var passiveSecurityReady = await RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto.PassiveSecurity, cancellationToken);
            if (!passiveSecurityReady)
            {
                result = result with { SkippedEngines = [.. result.SkippedEngines, "Passive Security"] };
            }
            else
            {
                try
                {
                    result = result with { PassiveSecurityReport = await _passiveSecurity.ReviewAsync(targetUrl,
                        context.ActiveProfile.Id, context.ActiveProfile.TargetUrl ?? "", context.ActiveProfile.EnvironmentType.ToString(), context.RequiresAuthentication, cancellationToken) };
                }
                catch (Exception ex) { result = result with { PassiveSecurityError = ex.Message }; }
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Passive Security"] };
        }

        var qualityReport = _quality.BuildReport(targetUrl, result.SecurityReport, result.PerformanceReport);
        var accessibilitySkipped = result.AccessibilityReport?.ExecutionStatus is
            AccessibilityExecutionStatusDto.Skipped or AccessibilityExecutionStatusDto.AuthenticationRequired;
        if (accessibilitySkipped && !result.SkippedEngines.Contains("Accessibility"))
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Accessibility"] };
        var accessibilityAssessed = result.AccessibilityReport?.ExecutionStatus == AccessibilityExecutionStatusDto.Assessed;
        var lighthouseSkipped = result.LighthouseReport?.ExecutionStatus is LighthouseExecutionStatusDto.Skipped or LighthouseExecutionStatusDto.AuthenticationRequired;
        var passiveSkipped = result.PassiveSecurityReport?.ExecutionStatus is PassiveSecurityExecutionStatusDto.Skipped or PassiveSecurityExecutionStatusDto.AuthenticationRequired;
        if (passiveSkipped && !result.SkippedEngines.Contains("Passive Security")) result = result with { SkippedEngines = [.. result.SkippedEngines, "Passive Security"] };
        if (lighthouseSkipped && !result.SkippedEngines.Contains("Lighthouse"))
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Lighthouse"] };
        var enrichedReport = ApplyBrowserRuntime(qualityReport, result.BrowserRuntimeReport);
        enrichedReport = ApplyAccessibility(enrichedReport, result.AccessibilityReport, accessibilityAssessed);
        enrichedReport = ApplyLighthouse(enrichedReport, result.LighthouseReport);
        enrichedReport = ApplyPassiveSecurity(enrichedReport, result.PassiveSecurityReport);
        var outcomes = FrontendQualityEngineOutcomeNormalizer.NormalizeAll(
            targetUrl, context, result, _runtime is not null, _accessibility is not null,
            _lighthouse is not null, _passiveSecurity is not null, cancellationToken.IsCancellationRequested);

        return result with
        {
            QualityReport = CopyReport(
                enrichedReport,
                outcomes,
                result.PreflightStatus,
                result.PreflightBlockReason,
                context.ReleasePolicy)
        };
    }

    private static FrontendQualityReviewReport CopyReport(
        FrontendQualityReviewReport report,
        List<FrontendQualityEngineOutcome> outcomes,
        PreflightStatus preflightStatus,
        string? preflightMessage,
        FrontendQualityReleasePolicySettings releasePolicy)
    {
        var coverage = FrontendQualityCoverage.Evaluate(outcomes);
        var issues = FrontendQualityLogicalIssueGrouper.Group(report.Findings);
        var manualItems = FrontendQualityDecisionSupportService.BuildManualReviewItems(issues, outcomes);
        var disposition = FrontendQualityDecisionSupportService.EvaluateReleaseDisposition(
            coverage, outcomes, issues, manualItems, releasePolicy);
        return new FrontendQualityReviewReport
        {
        TargetUrl = report.TargetUrl, FinalUrl = report.FinalUrl, GeneratedAt = report.GeneratedAt,
        CompletedAt = report.CompletedAt, DurationMs = report.DurationMs, OverallScore = report.OverallScore,
        PerformanceScore = report.PerformanceScore, SecurityScore = report.SecurityScore,
        AccessibilityScore = report.AccessibilityScore, StandardsScore = report.StandardsScore,
        WasmScore = report.WasmScore, ReadinessScore = report.ReadinessScore, Findings = report.Findings,
        LogicalIssues = issues, ManualReviewItems = manualItems,
        CategoryScores = report.CategoryScores, Recommendations = report.Recommendations, Risks = report.Risks,
        Limitations = report.Limitations, IsBlazorWasm = report.IsBlazorWasm, ErrorMessage = report.ErrorMessage,
        Coverage = coverage, ReleaseDisposition = disposition, EngineOutcomes = outcomes,
        PreflightStatus = preflightStatus, PreflightMessage = preflightMessage,
        RedirectOccurred = report.RedirectOccurred,
        AccessibilityReport = report.AccessibilityReport, LighthouseReport = report.LighthouseReport,
        PassiveSecurityReport = report.PassiveSecurityReport, BrowserRuntimeReport = report.BrowserRuntimeReport
        };
    }

    private static FrontendQualityReviewReport BuildPreflightReport(
        string targetUrl,
        FrontendAnalysisContext context,
        FrontendQualityReviewOrchestrationResult result,
        PreflightStatus? statusOverride = null)
    {
        var status = statusOverride ?? result.PreflightStatus;
        var outcomes = FrontendQualityEngineOutcomeNormalizer.PreflightBlocked(
            targetUrl, context, status, result.PreflightBlockReason);
        return new FrontendQualityReviewReport
        {
            TargetUrl = targetUrl,
            GeneratedAt = DateTime.UtcNow,
            EngineOutcomes = outcomes,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes),
            ReleaseDisposition = FrontendQualityReleaseDisposition.Blocked,
            PreflightStatus = status,
            PreflightMessage = result.PreflightBlockReason,
        };
    }

    private static FrontendQualityReviewReport ApplyBrowserRuntime(
        FrontendQualityReviewReport report,
        BrowserRuntimeResultDto? runtime)
    {
        if (runtime is null) return report;
        var runtimeFindings = FrontendQualityEngineOutcomeNormalizer.NormalizeBrowserRuntimeFindings(runtime);
        var sanitizedRuntime = FrontendQualityEngineOutcomeNormalizer.SanitizeBrowserRuntimeReport(runtime);
        return new FrontendQualityReviewReport
        {
            TargetUrl = report.TargetUrl, FinalUrl = report.FinalUrl, GeneratedAt = report.GeneratedAt,
            CompletedAt = report.CompletedAt, DurationMs = report.DurationMs, OverallScore = report.OverallScore,
            PerformanceScore = report.PerformanceScore, SecurityScore = report.SecurityScore,
            AccessibilityScore = report.AccessibilityScore, StandardsScore = report.StandardsScore,
            WasmScore = report.WasmScore, ReadinessScore = report.ReadinessScore,
            Findings = report.Findings.Concat(runtimeFindings).ToList(), CategoryScores = report.CategoryScores,
            Recommendations = report.Recommendations, Risks = report.Risks,
            Limitations = report.Limitations.Concat(sanitizedRuntime.Limitations ?? []).Distinct().ToList(),
            IsBlazorWasm = report.IsBlazorWasm, ErrorMessage = report.ErrorMessage,
            PreflightStatus = report.PreflightStatus, PreflightMessage = report.PreflightMessage,
            RedirectOccurred = report.RedirectOccurred, AssessedEngines = report.AssessedEngines,
            FailedEngines = report.FailedEngines, SkippedEngines = report.SkippedEngines,
            AccessibilityReport = report.AccessibilityReport, LighthouseReport = report.LighthouseReport,
            PassiveSecurityReport = report.PassiveSecurityReport, BrowserRuntimeReport = sanitizedRuntime,
        };
    }

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
            EngineId = FrontendQualityEngineId.Accessibility,
            SourceRuleId = f.RuleId,
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
            IsBlazorWasm = report.IsBlazorWasm, Coverage = report.Coverage, ReleaseDisposition = report.ReleaseDisposition,
            EngineOutcomes = report.EngineOutcomes, Completeness = report.Completeness,
            AssessedEngines = assessed ? report.AssessedEngines.Append("Accessibility").Distinct().ToList() : report.AssessedEngines,
            FailedEngines = report.FailedEngines, SkippedEngines = report.SkippedEngines,
            AccessibilityReport = accessibility, LighthouseReport = report.LighthouseReport,
            PassiveSecurityReport = report.PassiveSecurityReport, BrowserRuntimeReport = report.BrowserRuntimeReport
        };
    }

    private static FrontendQualityReviewReport ApplyLighthouse(FrontendQualityReviewReport report, LighthouseResultDto? lighthouse)
    {
        if (lighthouse is null) return report;
        var findings = (lighthouse.Audits ?? []).Select(a => new FrontendQualityFinding
        {
            Id = $"lighthouse-{a.AuditId}", Title = a.Title, Severity = FrontendQualitySeverity.Medium,
            Category = FrontendQualityCategory.Performance, Description = a.Description ?? a.Title,
            Recommendation = "Review the Lighthouse diagnostic and optimize the measured rendering or delivery path.",
            Evidence = [a.DisplayValue ?? $"Audit score: {a.Score}"], SourceSystem = "Lighthouse",
            EngineId = FrontendQualityEngineId.Lighthouse, SourceRuleId = a.AuditId,
            Status = CheckExecutionStatus.Failed
        });
        return new FrontendQualityReviewReport
        {
            TargetUrl = report.TargetUrl, FinalUrl = report.FinalUrl, GeneratedAt = report.GeneratedAt,
            CompletedAt = report.CompletedAt, DurationMs = report.DurationMs, OverallScore = report.OverallScore,
            PerformanceScore = report.PerformanceScore, SecurityScore = report.SecurityScore,
            AccessibilityScore = report.AccessibilityScore, StandardsScore = report.StandardsScore,
            WasmScore = report.WasmScore, ReadinessScore = report.ReadinessScore,
            Findings = report.Findings.Concat(findings).ToList(), CategoryScores = report.CategoryScores,
            Recommendations = report.Recommendations, Risks = report.Risks,
            Limitations = report.Limitations.Concat(lighthouse.Limitations ?? []).Distinct().ToList(),
            IsBlazorWasm = report.IsBlazorWasm, ErrorMessage = report.ErrorMessage, Coverage = report.Coverage,
            ReleaseDisposition = report.ReleaseDisposition, EngineOutcomes = report.EngineOutcomes, Completeness = report.Completeness,
            PreflightStatus = report.PreflightStatus, PreflightMessage = report.PreflightMessage,
            RedirectOccurred = report.RedirectOccurred,
            AssessedEngines = lighthouse.ExecutionStatus == LighthouseExecutionStatusDto.Assessed
                ? report.AssessedEngines.Append("Lighthouse").Distinct().ToList() : report.AssessedEngines,
            FailedEngines = report.FailedEngines, SkippedEngines = report.SkippedEngines,
            AccessibilityReport = report.AccessibilityReport, LighthouseReport = lighthouse,
            PassiveSecurityReport = report.PassiveSecurityReport, BrowserRuntimeReport = report.BrowserRuntimeReport
        };
    }

    private static FrontendQualityReviewReport ApplyPassiveSecurity(FrontendQualityReviewReport report, PassiveSecurityResultDto? passive)
    {
        if (passive is null) return report;
        var assessed = passive.ExecutionStatus == PassiveSecurityExecutionStatusDto.Assessed;
        var findings = assessed ? (passive.Findings ?? []).Select(f => new FrontendQualityFinding
        {
            Id = $"zap-{f.PluginId}-{f.AlertRef}", Title = f.Name, Severity = f.Risk switch
            { "High" => FrontendQualitySeverity.High, "Medium" => FrontendQualitySeverity.Medium, "Low" => FrontendQualitySeverity.Low, _ => FrontendQualitySeverity.Info },
            Category = FrontendQualityCategory.Security, Description = f.Description, Recommendation = f.Solution,
            Evidence = string.IsNullOrWhiteSpace(f.Evidence) ? [$"URL: {f.Url}; confidence: {f.Confidence}; instances: {f.InstancesCount}"] : [$"URL: {f.Url}; confidence: {f.Confidence}; instances: {f.InstancesCount}", f.Evidence],
            SourceSystem = "ZAP Passive", Status = CheckExecutionStatus.Failed
            , EngineId = FrontendQualityEngineId.PassiveSecurity, SourceRuleId = f.PluginId
        }) : [];
        return new FrontendQualityReviewReport
        {
            TargetUrl=report.TargetUrl, FinalUrl=report.FinalUrl, GeneratedAt=report.GeneratedAt, CompletedAt=report.CompletedAt,
            DurationMs=report.DurationMs, OverallScore=report.OverallScore, PerformanceScore=report.PerformanceScore,
            SecurityScore=report.SecurityScore, AccessibilityScore=report.AccessibilityScore, StandardsScore=report.StandardsScore,
            WasmScore=report.WasmScore, ReadinessScore=report.ReadinessScore, Findings=report.Findings.Concat(findings).ToList(),
            CategoryScores=report.CategoryScores, Recommendations=report.Recommendations, Risks=report.Risks,
            Limitations=report.Limitations.Concat(passive.Limitations ?? []).Distinct().ToList(), IsBlazorWasm=report.IsBlazorWasm,
            ErrorMessage=report.ErrorMessage, Coverage=report.Coverage, ReleaseDisposition=report.ReleaseDisposition,
            EngineOutcomes=report.EngineOutcomes, Completeness=report.Completeness, PreflightStatus=report.PreflightStatus,
            PreflightMessage=report.PreflightMessage, RedirectOccurred=report.RedirectOccurred,
            AssessedEngines=assessed ? report.AssessedEngines.Append("Passive Security").Distinct().ToList() : report.AssessedEngines,
            FailedEngines=report.FailedEngines, SkippedEngines=report.SkippedEngines, AccessibilityReport=report.AccessibilityReport,
            LighthouseReport=report.LighthouseReport, PassiveSecurityReport=passive
            , BrowserRuntimeReport=report.BrowserRuntimeReport
        };
    }

    private static bool IsEngineEligibleToExecute(
        FrontendQualityEngineExecutionSnapshot snapshot,
        FrontendQualityEngineIdDto engineId)
    {
        var layer1 = snapshot.Layer1Allowed.TryGetValue(engineId, out var l1) && l1;
        var layer2 = snapshot.Layer2Enabled.TryGetValue(engineId, out var l2) && l2;
        var selected = snapshot.SelectedEngines.TryGetValue(engineId, out var s) && s;
        var authSupported = snapshot.AuthModeSupported.TryGetValue(engineId, out var auth) && auth;

        return layer1 && layer2 && selected && authSupported;
    }

    private static FrontendQualityEngineExecutionSnapshot CaptureDefaultSnapshot(FrontendAnalysisContext context)
    {
        var snapshot = new FrontendQualityEngineExecutionSnapshot
        {
            AuthMode = context.RequiresAuthentication && context.IsAuthenticatedSessionAvailable
                ? ReviewAuthenticationModeDto.Authenticated
                : ReviewAuthenticationModeDto.Anonymous,
            CapturedAtUtc = DateTime.UtcNow,
        };

        // For default snapshot (no explicit UI selection), use feature toggles
        // ReviewEngineSelection is only used when explicitly captured from the UI
        snapshot.SelectedEngines[FrontendQualityEngineIdDto.BrowserRuntime] = context.FeatureToggles.EnableBrowserRuntimeEngine;
        snapshot.SelectedEngines[FrontendQualityEngineIdDto.Accessibility] = context.FeatureToggles.EnableAccessibilityEngine;
        snapshot.SelectedEngines[FrontendQualityEngineIdDto.Lighthouse] = context.FeatureToggles.EnableLighthouseEngine;
        snapshot.SelectedEngines[FrontendQualityEngineIdDto.PassiveSecurity] = context.FeatureToggles.EnablePassiveSecurityEngine;

        // Set defaults for layer 1 & 2 based on feature toggles
        snapshot.Layer1Allowed[FrontendQualityEngineIdDto.BrowserRuntime] = true;
        snapshot.Layer1Allowed[FrontendQualityEngineIdDto.Accessibility] = true;
        snapshot.Layer1Allowed[FrontendQualityEngineIdDto.Lighthouse] = true;
        snapshot.Layer1Allowed[FrontendQualityEngineIdDto.PassiveSecurity] = true;

        snapshot.Layer2Enabled[FrontendQualityEngineIdDto.BrowserRuntime] = context.FeatureToggles.EnableBrowserRuntimeEngine;
        snapshot.Layer2Enabled[FrontendQualityEngineIdDto.Accessibility] = context.FeatureToggles.EnableAccessibilityEngine;
        snapshot.Layer2Enabled[FrontendQualityEngineIdDto.Lighthouse] = context.FeatureToggles.EnableLighthouseEngine;
        snapshot.Layer2Enabled[FrontendQualityEngineIdDto.PassiveSecurity] = context.FeatureToggles.EnablePassiveSecurityEngine;

        // Set auth support based on authentication mode
        var isAuthenticated = context.RequiresAuthentication && context.IsAuthenticatedSessionAvailable;
        snapshot.AuthModeSupported[FrontendQualityEngineIdDto.BrowserRuntime] = true;
        snapshot.AuthModeSupported[FrontendQualityEngineIdDto.Accessibility] = true;
        snapshot.AuthModeSupported[FrontendQualityEngineIdDto.Lighthouse] = !isAuthenticated;
        snapshot.AuthModeSupported[FrontendQualityEngineIdDto.PassiveSecurity] = !isAuthenticated;

        return snapshot;
    }

    private async Task<bool> RevalidateEngineReadinessAsync(
        FrontendQualityEngineIdDto engineId,
        CancellationToken cancellationToken)
    {
        // FAIL-CLOSED: If readiness infrastructure is unavailable, engine must NOT execute.
        // Layer 3 enforcement cannot be bypassed by missing infrastructure.
        if (_engineStatusService is null)
            return false;

        try
        {
            // 5-second timeout for readiness check
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var readiness = await _engineStatusService.RevalidateEngineReadinessAsync(engineId, cts.Token);

            // Null response = unavailable
            return readiness?.IsAvailable ?? false;
        }
        catch (OperationCanceledException)
        {
            // Timeout: Layer 3 check failed → engine unavailable
            return false;
        }
        catch (Exception)
        {
            // Service/provider error: Layer 3 check failed → engine unavailable
            return false;
        }
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
