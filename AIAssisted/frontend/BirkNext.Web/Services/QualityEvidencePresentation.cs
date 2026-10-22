using BirkNext.ApiReview;
using BirkNext.QualityEvidence;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared quality evidence from the reviews' own recorded results (FQR history, AQR history) — never by rediscovering or re-running. An FQR
/// finding becomes an item of its category's kind; an AQR report emits API / security / performance items from the same executed requests.
/// </summary>
public static class QualityEvidencePresentation
{
    public static QualityEvidenceKind Kind(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Security => QualityEvidenceKind.Security,
        FrontendQualityCategory.Performance => QualityEvidenceKind.Performance,
        FrontendQualityCategory.Accessibility => QualityEvidenceKind.Accessibility,
        _ => QualityEvidenceKind.Browser,
    };

    public static string Provider(FrontendQualityEngineId? engine) => engine switch
    {
        FrontendQualityEngineId.StaticSecurity => "http-security",
        FrontendQualityEngineId.PassivePerformance => "http-performance",
        FrontendQualityEngineId.PassiveSecurity => "passive-web-security",
        FrontendQualityEngineId.Lighthouse => "lighthouse",
        FrontendQualityEngineId.BrowserQuality => "browser-companion",
        FrontendQualityEngineId.PerformanceQuality => "browser-performance-quality",
        _ => "playwright-browser",
    };

    /// <summary>Items of one recorded FQR report (findings as observations; their verdicts stay in the review).</summary>
    public static IReadOnlyList<QualityEvidenceItem> FromFrontendReview(FrontendQualityReviewReport report, string environmentId, string executionId)
    {
        // FQR records target + environment identity (no auth digest): its context is the review's recorded target and environment.
        var context = new QualityEvidenceContext(report.TargetEnvironment?.TargetUrl ?? report.TargetUrl, report.TargetEnvironment?.EnvironmentId ?? environmentId, "Frontend review", null);
        var at = new DateTimeOffset(DateTime.SpecifyKind(report.CompletedAt ?? report.GeneratedAt, DateTimeKind.Utc));
        return [.. report.Findings.Select(f => new QualityEvidenceItem(Kind(f.Category), Provider(f.EngineId), executionId, report.FinalUrl ?? report.TargetUrl,
            f.SourceRuleId ?? f.Title, f.Severity.ToString(), null, at, context, "Finding (observation + rule)"))];
    }

    /// <summary>Security observations of both reviews for cross-review deduplication (one logical HSTS issue, many sources).</summary>
    public static IEnumerable<(string Source, string RuleOrHeader, string Surface)> SecurityObservations(FrontendQualityReviewReport? frontend, ApiReviewReport? api)
    {
        foreach (var f in frontend?.Findings.Where(f => f.Category == FrontendQualityCategory.Security) ?? [])
            yield return ($"Frontend Quality Review · {FrontendQualityActiveEngines.DisplayName(f.EngineId ?? FrontendQualityEngineId.StaticSecurity)}", $"{f.SourceRuleId} {f.Title}", frontend!.FinalUrl ?? frontend.TargetUrl);
        foreach (var t in api?.Targets ?? [])
            foreach (var h in t.SecurityHeaders.Where(h => h.Result == SecurityHeaderOutcome.Missing))
                yield return ("API Quality Review", h.Header, t.Target.Origin);
    }

    public static string Label(QualityEvidenceKind kind) => kind switch
    {
        QualityEvidenceKind.Api => "API",
        QualityEvidenceKind.Security => "Security",
        QualityEvidenceKind.Performance => "Performance",
        QualityEvidenceKind.Accessibility => "Accessibility",
        _ => "Browser",
    };
}
