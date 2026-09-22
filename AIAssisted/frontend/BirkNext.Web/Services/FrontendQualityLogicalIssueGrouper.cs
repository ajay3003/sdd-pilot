using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure, deterministic grouping of source observations into the problems a person would actually act on.
///
/// Two things are grouped, and they are different:
///
/// <list type="bullet">
/// <item><b>The registry</b> names cross-engine equivalences a string could never establish — that ZAP rule 10038 and
/// the static header check are the same missing Content-Security-Policy. Those are product decisions.</item>
/// <item><b>The derived key</b> handles the ordinary case: one rule firing on many pages. The route is lifted off the
/// observation and collected as a place, so five contrast observations are one issue on five pages.</item>
/// </list>
///
/// The old implementation had only the registry, and a guard that dropped BACK to one-issue-per-observation as soon as
/// a rule fired more than once — exactly the repeated case. 57 observations produced 56 issues, and the result read as
/// 56 separate problems.
/// </summary>
public static class FrontendQualityLogicalIssueGrouper
{
    private sealed record Rule(
        string LogicalId,
        string CanonicalTitle,
        FrontendQualityCategory Category,
        string Recommendation,
        IReadOnlySet<(FrontendQualityEngineId EngineId, string SourceRuleId)> AcceptedIdentities);

    private static Rule Header(string key, string header, string title, string recommendation, params (FrontendQualityEngineId, string)[] identities) =>
        new($"headers:{key}:missing", title, FrontendQualityCategory.Security, recommendation, new HashSet<(FrontendQualityEngineId, string)>(identities));

    /// <summary>
    /// Response-header issues are ONE fact about the target that two checks both notice: the static security scan
    /// records it under Security, and the derived standards pass records it again under Standards. The primary domain
    /// is Security, because that is where the risk is; Standards keeps its source observation and appears as a related
    /// domain rather than as a second problem to fix.
    /// </summary>
    private static readonly IReadOnlyList<Rule> Registry =
    [
        Header("csp", "Content-Security-Policy", "Content Security Policy header missing",
            "Configure a restrictive Content-Security-Policy header on application responses.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY"),
            (FrontendQualityEngineId.StaticSecurity, "std-csp-missing"),
            (FrontendQualityEngineId.PassiveSecurity, "10038")),

        Header("nosniff", "X-Content-Type-Options", "X-Content-Type-Options nosniff header missing",
            "Set X-Content-Type-Options: nosniff on application responses.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-X-CONTENT-TYPE-OPTIONS"),
            (FrontendQualityEngineId.PassiveSecurity, "10021")),

        Header("hsts", "Strict-Transport-Security", "Strict-Transport-Security (HSTS) header missing",
            "Add 'Strict-Transport-Security: max-age=31536000; includeSubDomains' to all HTTPS responses.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-STRICT-TRANSPORT-SECURITY"),
            (FrontendQualityEngineId.StaticSecurity, "std-hsts-missing"),
            (FrontendQualityEngineId.PassiveSecurity, "10035")),

        Header("permissions-policy", "Permissions-Policy", "Permissions-Policy header missing",
            "Add a 'Permissions-Policy' header to your server or CDN configuration.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-PERMISSIONS-POLICY"),
            (FrontendQualityEngineId.StaticSecurity, "std-permissionspolicy-missing")),

        Header("referrer-policy", "Referrer-Policy", "Referrer-Policy header missing",
            "Add a 'Referrer-Policy' header to your server or CDN configuration.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-REFERRER-POLICY"),
            (FrontendQualityEngineId.StaticSecurity, "std-referrerpolicy-missing")),

        Header("frame-options", "X-Frame-Options", "Clickjacking protection header missing",
            "Set X-Frame-Options: DENY, or a frame-ancestors directive in the Content-Security-Policy.",
            (FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-X-FRAME-OPTIONS"),
            (FrontendQualityEngineId.StaticSecurity, "std-xframeoptions-missing"),
            (FrontendQualityEngineId.PassiveSecurity, "10020")),
    ];

    public static List<FrontendQualityLogicalIssue> Group(IReadOnlyList<FrontendQualityFinding> findings)
    {
        var instances = findings.Select(ToInstance).ToList();

        // One pass, one key per observation: a registered equivalence when the product declares one, otherwise the
        // rule-and-subject key. Every observation lands in exactly one group, so nothing is counted twice and nothing
        // is dropped.
        var groups = new List<(string Key, Rule? Rule, List<FrontendQualityFindingInstance> Instances)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var instance in instances)
        {
            var rule = FindRule(instance);
            var key = rule?.LogicalId ?? FrontendQualityIssueIdentity.DerivedKey(instance);
            if (index.TryGetValue(key, out var position))
            {
                groups[position].Instances.Add(instance);
                continue;
            }
            index[key] = groups.Count;
            groups.Add((key, rule, [instance]));
        }

        return groups
            .Select(group => ToIssue(group.Key, group.Rule, group.Instances))
            // Severest first, then the best-evidenced, then a stable order so two runs of the same review agree.
            .OrderBy(issue => issue.PrimarySeverity)
            .ThenByDescending(issue => EvidencePriority(issue.EvidenceStrength))
            .ThenByDescending(issue => issue.AffectedPages.Count)
            .ThenBy(issue => issue.CanonicalTitle, StringComparer.Ordinal)
            .ThenBy(issue => issue.LogicalId, StringComparer.Ordinal)
            .ToList();
    }

    private static Rule? FindRule(FrontendQualityFindingInstance finding) =>
        string.IsNullOrWhiteSpace(finding.SourceRuleId) ? null : Registry.FirstOrDefault(rule =>
            rule.AcceptedIdentities.Contains((finding.EngineId, finding.SourceRuleId)));

    private static FrontendQualityLogicalIssue ToIssue(
        string key, Rule? rule, List<FrontendQualityFindingInstance> instances)
    {
        // Evidence is listed by SOURCE, which is how a reader checks provenance: all of one engine's observations
        // together, in a stable order two runs of the same review will agree on.
        var ordered = instances
            .OrderBy(instance => instance.EngineId)
            .ThenBy(instance => instance.Page ?? "", StringComparer.Ordinal)
            .ThenBy(instance => instance.SourceFindingId, StringComparer.Ordinal)
            .ToList();

        // Severity reconciliation: grouped observations can disagree (the static scan calls a missing CSP Critical,
        // the standards pass calls it High). The highest supported severity wins, and every source severity survives
        // on its own instance — nothing is invented and nothing is quietly lowered.
        var severity = ordered.Min(instance => instance.Severity);

        // The severest observation names the issue and owns its domain, whatever order the evidence is listed in.
        var lead = ordered.OrderBy(instance => instance.Severity).First();

        // The domain of the severest observation leads; every other domain the issue was observed in is related.
        var category = rule?.Category ?? lead.Category;
        var related = ordered.Select(instance => instance.Category).Distinct().Where(c => c != category).Order().ToList();

        var pages = ordered
            .Select(instance => instance.Page)
            .Where(page => !string.IsNullOrWhiteSpace(page))
            .Select(page => page!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var engines = ordered.Select(instance => instance.EngineId).Distinct().Order().ToList();
        var disposition = ordered.Any(instance => instance.ReviewDisposition == FrontendQualityReviewDisposition.ManualVerificationRequired)
            ? FrontendQualityReviewDisposition.ManualVerificationRequired
            : FrontendQualityReviewDisposition.AutomatedFinding;

        return new FrontendQualityLogicalIssue
        {
            LogicalId = rule?.LogicalId ?? key,
            CanonicalTitle = rule?.CanonicalTitle ?? lead.Subject ?? lead.Title,
            PrimarySeverity = severity,
            Sources = engines,
            FindingInstances = ordered,
            EvidenceStrength = StrongestEvidence(ordered),
            // Corroboration, and only corroboration. Two independent engines reaching the same conclusion is stronger
            // than one engine reaching it repeatedly, and a SINGLE observation gets no confidence value at all — there
            // is nothing to corroborate it, and a label would be a judgement the evidence does not support.
            Confidence = ordered.Count == 1 ? null
                : engines.Count > 1 ? FrontendQualityEvidenceConfidence.High
                : FrontendQualityEvidenceConfidence.Moderate,
            ReviewDisposition = disposition,
            Category = category,
            RelatedCategories = related,
            Recommendation = rule?.Recommendation ?? lead.Recommendation,
            ManualVerificationRequired = disposition == FrontendQualityReviewDisposition.ManualVerificationRequired,
            AffectedPages = pages,
            // Nothing here asks for a change; an absent optional capability is worth knowing, not worth counting.
            Informational = ordered.All(instance => instance.Severity == FrontendQualitySeverity.Info),
            // A conclusion restating observations reported elsewhere is not a new problem.
            Derived = ordered.All(instance => instance.Origin == FrontendQualityFindingOrigin.Derived),
            GroupingReason = rule is not null
                ? "Registered cross-source equivalence."
                : pages.Count > 1
                    ? $"Same source rule and subject observed on {pages.Count} pages."
                    : ordered.Count > 1
                        ? "Same source rule and subject."
                        : null,
        };
    }

    private static FrontendQualityFindingInstance ToInstance(FrontendQualityFinding finding)
    {
        var engineId = finding.EngineId ?? InferEngineId(finding.SourceSystem);
        var page = FrontendQualityIssueIdentity.Page(finding);
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
            Page = page is null ? null : ReportExportService.SanitizePassive(page),
            Subject = ReportExportService.SanitizePassive(FrontendQualityIssueIdentity.Subject(finding)),
            Origin = finding.Origin,
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
        BrowserQualityRules.CompanionSource or BrowserQualityRules.CorrelatedSource or "BirkNext Browser Quality" => FrontendQualityEngineId.BrowserQuality,
        PerformanceQualitySources.EngineName => FrontendQualityEngineId.PerformanceQuality,
        _ => FrontendQualityEngineId.StaticSecurity,
    };

    private static FrontendQualityEvidenceStrength Strength(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime or FrontendQualityEngineId.BrowserQuality => FrontendQualityEvidenceStrength.DirectObservation,
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
}
