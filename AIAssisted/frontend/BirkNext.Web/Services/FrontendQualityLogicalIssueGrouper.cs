using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Pure, deterministic grouping over already-normalized source findings.</summary>
public static class FrontendQualityLogicalIssueGrouper
{
    private sealed record Rule(
        string LogicalId,
        string CanonicalTitle,
        FrontendQualityCategory Category,
        string Recommendation,
        IReadOnlySet<(FrontendQualityEngineId EngineId, string SourceRuleId)> AcceptedIdentities);

    private static readonly IReadOnlyList<Rule> Registry =
    [
        new(
            "headers:csp:missing",
            "Content Security Policy header missing",
            FrontendQualityCategory.Security,
            "Configure a restrictive Content-Security-Policy header on application responses.",
            new HashSet<(FrontendQualityEngineId, string)>
            {
                (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY"),
                (FrontendQualityEngineId.StaticSecurity, "std-csp-missing"),
                (FrontendQualityEngineId.PassiveSecurity, "10038"),
            }),
        new(
            "headers:nosniff:missing",
            "X-Content-Type-Options nosniff header missing",
            FrontendQualityCategory.Security,
            "Set X-Content-Type-Options: nosniff on application responses.",
            new HashSet<(FrontendQualityEngineId, string)>
            {
                (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-X-CONTENT-TYPE-OPTIONS"),
                (FrontendQualityEngineId.PassiveSecurity, "10021"),
            }),
    ];

    public static List<FrontendQualityLogicalIssue> Group(IReadOnlyList<FrontendQualityFinding> findings)
    {
        var indexed = findings.Select((finding, index) => new IndexedInstance(index, finding, ToInstance(finding))).ToList();
        var matched = indexed.Select(item => new MatchedInstance(item, FindRule(item.Instance))).ToList();
        var issues = new List<FrontendQualityLogicalIssue>();

        foreach (var rule in Registry)
        {
            var candidates = matched.Where(item => item.Rule == rule).Select(item => item.Item).ToList();
            if (candidates.Count == 0) continue;

            // Repeated results from one engine are location-sensitive and cannot be merged safely.
            if (candidates.Count != candidates
                    .Select(item => (item.Instance.EngineId, item.Instance.SourceRuleId))
                    .Distinct()
                    .Count())
            {
                issues.AddRange(candidates.Select(ToStandalone));
                continue;
            }

            issues.Add(ToRegisteredIssue(rule, candidates.Select(item => item.Instance).ToList()));
        }

        issues.AddRange(matched.Where(item => item.Rule is null).Select(item => ToStandalone(item.Item)));
        return issues
            .OrderBy(issue => issue.PrimarySeverity)
            .ThenByDescending(issue => EvidencePriority(issue.EvidenceStrength))
            .ThenBy(issue => issue.CanonicalTitle, StringComparer.Ordinal)
            .ThenBy(issue => issue.LogicalId, StringComparer.Ordinal)
            .ToList();
    }

    private static Rule? FindRule(FrontendQualityFindingInstance finding) =>
        string.IsNullOrWhiteSpace(finding.SourceRuleId) ? null : Registry.FirstOrDefault(rule =>
            rule.AcceptedIdentities.Contains((finding.EngineId, finding.SourceRuleId)));

    private static FrontendQualityLogicalIssue ToRegisteredIssue(
        Rule rule,
        List<FrontendQualityFindingInstance> instances)
    {
        var ordered = instances.OrderBy(instance => instance.EngineId).ThenBy(instance => instance.SourceFindingId, StringComparer.Ordinal).ToList();
        var disposition = ordered.Any(instance => instance.ReviewDisposition == FrontendQualityReviewDisposition.ManualVerificationRequired)
            ? FrontendQualityReviewDisposition.ManualVerificationRequired
            : FrontendQualityReviewDisposition.AutomatedFinding;
        return new FrontendQualityLogicalIssue
        {
            LogicalId = rule.LogicalId,
            CanonicalTitle = rule.CanonicalTitle,
            PrimarySeverity = ordered.Min(instance => instance.Severity),
            Sources = ordered.Select(instance => instance.EngineId).Distinct().Order().ToList(),
            FindingInstances = ordered,
            EvidenceStrength = StrongestEvidence(ordered),
            Confidence = ordered.Select(instance => instance.EngineId).Distinct().Count() > 1
                ? FrontendQualityEvidenceConfidence.High
                : FrontendQualityEvidenceConfidence.Moderate,
            ReviewDisposition = disposition,
            Category = rule.Category,
            Recommendation = rule.Recommendation,
            ManualVerificationRequired = disposition == FrontendQualityReviewDisposition.ManualVerificationRequired,
            GroupingReason = "Exact registered source-rule equivalence.",
        };
    }

    private static FrontendQualityLogicalIssue ToStandalone(IndexedInstance item)
    {
        var instance = item.Instance;
        return new FrontendQualityLogicalIssue
        {
            LogicalId = $"finding:{instance.EngineId}:{Uri.EscapeDataString(instance.SourceFindingId)}:{item.Index}",
            CanonicalTitle = instance.Title,
            PrimarySeverity = instance.Severity,
            Sources = [instance.EngineId],
            FindingInstances = [instance],
            EvidenceStrength = instance.EvidenceStrength,
            Confidence = null,
            ReviewDisposition = instance.ReviewDisposition,
            Category = instance.Category,
            Recommendation = instance.Recommendation,
            ManualVerificationRequired = instance.ReviewDisposition == FrontendQualityReviewDisposition.ManualVerificationRequired,
        };
    }

    private static FrontendQualityFindingInstance ToInstance(FrontendQualityFinding finding)
    {
        var engineId = finding.EngineId ?? InferEngineId(finding.SourceSystem);
        return new FrontendQualityFindingInstance
        {
            EngineId = engineId,
            SourceSystem = ReportExportService.SanitizePassive(finding.SourceSystem),
            SourceFindingId = ReportExportService.SanitizePassive(finding.Id),
            SourceRuleId = string.IsNullOrWhiteSpace(finding.SourceRuleId) ? null : ReportExportService.SanitizePassive(finding.SourceRuleId),
            Title = ReportExportService.SanitizePassive(finding.Title),
            Severity = finding.Severity,
            Category = finding.Category,
            Description = ReportExportService.SanitizePassive(finding.Description),
            Recommendation = ReportExportService.SanitizePassive(finding.Recommendation),
            SanitizedEvidence = finding.Evidence.Select(ReportExportService.SanitizePassive).ToList(),
            ExecutionState = finding.Status,
            EvidenceStrength = Strength(engineId),
            ReviewDisposition = finding.Status == CheckExecutionStatus.NotAssessed
                ? FrontendQualityReviewDisposition.ManualVerificationRequired
                : FrontendQualityReviewDisposition.AutomatedFinding,
        };
    }

    private static FrontendQualityEngineId InferEngineId(string? sourceSystem) => sourceSystem switch
    {
        "Security" or "Standards" => FrontendQualityEngineId.StaticSecurity,
        "Performance" or "BlazorWasm" or "Readiness" => FrontendQualityEngineId.PassivePerformance,
        "Browser Runtime" => FrontendQualityEngineId.BrowserRuntime,
        "Accessibility" or "axe-core" => FrontendQualityEngineId.Accessibility,
        "Lighthouse" => FrontendQualityEngineId.Lighthouse,
        "ZAP Passive" => FrontendQualityEngineId.PassiveSecurity,
        _ => FrontendQualityEngineId.StaticSecurity,
    };

    private static FrontendQualityEvidenceStrength Strength(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => FrontendQualityEvidenceStrength.DirectObservation,
        FrontendQualityEngineId.Accessibility or FrontendQualityEngineId.Lighthouse or FrontendQualityEngineId.PassiveSecurity
            => FrontendQualityEvidenceStrength.ToolDiagnostic,
        _ => FrontendQualityEvidenceStrength.StaticIndicator,
    };

    private static FrontendQualityEvidenceStrength StrongestEvidence(IEnumerable<FrontendQualityFindingInstance> instances) =>
        instances.OrderByDescending(instance => EvidencePriority(instance.EvidenceStrength)).First().EvidenceStrength;

    private static int EvidencePriority(FrontendQualityEvidenceStrength strength) => strength switch
    {
        FrontendQualityEvidenceStrength.DirectObservation => 4,
        FrontendQualityEvidenceStrength.ToolDiagnostic => 3,
        FrontendQualityEvidenceStrength.StaticIndicator => 2,
        FrontendQualityEvidenceStrength.DerivedSummary => 1,
        _ => 0,
    };

    private sealed record IndexedInstance(int Index, FrontendQualityFinding Finding, FrontendQualityFindingInstance Instance);
    private sealed record MatchedInstance(IndexedInstance Item, Rule? Rule);
}
