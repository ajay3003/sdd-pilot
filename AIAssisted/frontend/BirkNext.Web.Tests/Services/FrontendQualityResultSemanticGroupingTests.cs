using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// What the post-run result counts, and what it refuses to count twice.
///
/// The review reported 57 observations as 56 problems, added three QA Readiness items that restated performance risks
/// it had already reported, and produced one recommendation card per observation. The distinctions these tests hold in
/// place are the ones that made those numbers wrong:
///
/// <list type="bullet">
/// <item>A source finding is an observation. A logical issue is a problem. One problem can be seen many times.</item>
/// <item>A derived indicator is a conclusion about observations already reported; it is never a new observation.</item>
/// <item>The same problem in two domains is one problem with two domains interested in it.</item>
/// <item>A recommendation is a job. The same job asked for five times is one job.</item>
/// </list>
/// </summary>
public sealed class FrontendQualityResultSemanticGroupingTests
{
    private static FrontendQualityFinding Finding(
        string id, string title, FrontendQualityCategory category, FrontendQualityEngineId engine,
        string? ruleId = null, FrontendQualitySeverity severity = FrontendQualitySeverity.Medium,
        string? page = null, string recommendation = "Fix it.",
        FrontendQualityFindingOrigin origin = FrontendQualityFindingOrigin.Source) => new()
        {
            Id = id,
            Title = page is null ? title : $"{title} — {page}",
            Severity = severity,
            Category = category,
            Description = title,
            Recommendation = recommendation,
            Evidence = page is null ? [] : [$"Page: {page}"],
            SourceSystem = engine.ToString(),
            EngineId = engine,
            SourceRuleId = ruleId,
            Origin = origin,
            Status = CheckExecutionStatus.Failed,
        };

    /// <summary>The same rule firing on five routes, exactly as the page-specific engines emit it.</summary>
    private static List<FrontendQualityFinding> ContrastOnFivePages() =>
        new[] { "/", "/admin/child-specific-roles", "/admin/general-roles", "/admin/operations", "/admin/user-access" }
            .Select((route, i) => Finding($"bq-contrast-{i}", "Contrast (Minimum)", FrontendQualityCategory.Accessibility,
                FrontendQualityEngineId.BrowserQuality, "a11y-contrast", FrontendQualitySeverity.High, route,
                "Increase the contrast ratio of text against its background."))
            .ToList();

    // ── §12, §57, §65. One defect, five places ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameIssueOnManyPagesIsOneLogicalIssueWithManyAffectedPages()
    {
        var findings = ContrastOnFivePages();

        var issue = FrontendQualityLogicalIssueGrouper.Group(findings).Should().ContainSingle().Subject;

        issue.CanonicalTitle.Should().Be("Contrast (Minimum)", "the route is where it was seen, not what it is");
        issue.AffectedPages.Should().HaveCount(5);
        issue.SourceFindingCount.Should().Be(5, "every occurrence is kept and inspectable");
        issue.ScaleLabel.Should().Be("5 affected pages · 5 source findings");
        // 65. And one job, not five.
        FrontendQualityRecommendationGrouper.Build([issue]).Should().ContainSingle();
    }

    // 11. A rule that reports genuinely different subjects keeps them apart, even on the same page set.
    [Fact]
    public void OneRuleReportingDifferentSubjectsStaysSeparatePerSubject()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("pq-1", "Slow GraphQL operation: HentNodtilganger", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PerformanceQuality, "pq-graphql-slow", page: "/admin/user-access"),
            Finding("pq-2", "Slow GraphQL operation: HentNodtilganger", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PerformanceQuality, "pq-graphql-slow", page: "/admin/operations"),
            Finding("pq-3", "Slow GraphQL operation: HentBrukere", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PerformanceQuality, "pq-graphql-slow", page: "/admin/user-access"),
        };

        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);

        issues.Should().HaveCount(2, "two operations are two things to look at");
        issues.Single(i => i.CanonicalTitle.Contains("HentNodtilganger")).AffectedPages.Should().HaveCount(2);
        issues.Single(i => i.CanonicalTitle.Contains("HentBrukere")).AffectedPages.Should().ContainSingle();
    }

    // ── §9, §10, §55, §64. The same problem seen by two domains ───────────────────────────────────────────────────

    [Fact]
    public void TheSameHeaderIssueInSecurityAndStandardsIsOneIssueWithARelatedDomain()
    {
        var findings = new List<FrontendQualityFinding>
        {
            // What the static security scan records, under Security.
            Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security,
                FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", FrontendQualitySeverity.High),
            // What the derived standards pass records, under Standards, about the same response.
            Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards,
                FrontendQualityEngineId.StaticSecurity, "std-csp-missing", FrontendQualitySeverity.Critical),
        };

        var issue = FrontendQualityLogicalIssueGrouper.Group(findings).Should().ContainSingle().Subject;

        issue.LogicalId.Should().Be("headers:csp:missing");
        issue.SourceFindingCount.Should().Be(2);
        // 10, 55. Primary domain is where the risk is; the other domain is related, not a second problem.
        issue.Category.Should().Be(FrontendQualityCategory.Security);
        issue.RelatedCategories.Should().Equal(FrontendQualityCategory.Standards);
        // 56. Severities disagreed; the highest supported one wins and both survive on their instances.
        issue.PrimarySeverity.Should().Be(FrontendQualitySeverity.Critical);
        issue.FindingInstances.Select(i => i.Severity)
            .Should().BeEquivalentTo([FrontendQualitySeverity.High, FrontendQualitySeverity.Critical]);
    }

    // 64. Permissions-Policy behaves exactly as CSP does; the registry is not a one-off.
    [Fact]
    public void PermissionsPolicyDeduplicatesAcrossSecurityAndStandardsToo()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("sec-pp", "Missing security header: Permissions-Policy", FrontendQualityCategory.Security,
                FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-PERMISSIONS-POLICY", FrontendQualitySeverity.Low),
            Finding("std-pp", "Permissions-Policy header missing", FrontendQualityCategory.Standards,
                FrontendQualityEngineId.StaticSecurity, "std-permissionspolicy-missing", FrontendQualitySeverity.Low),
        };

        var issue = FrontendQualityLogicalIssueGrouper.Group(findings).Should().ContainSingle().Subject;

        issue.LogicalId.Should().Be("headers:permissions-policy:missing");
        issue.SourceFindingCount.Should().Be(2);
        issue.RelatedCategories.Should().Equal(FrontendQualityCategory.Standards);
    }

    // ── §8, §26, §61, §68. Derived and informational never become problems ────────────────────────────────────────

    [Fact]
    public void DerivedIndicatorsAndInformationalObservationsAreNotActionableIssues()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("perf-js", "Large application JavaScript payload", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PassivePerformance, "PERF-JS", FrontendQualitySeverity.High),
            // QA Readiness restating the same risk it drew from the performance evidence above. It carries no source
            // rule id of its own — it is a conclusion, not a check — so it stands as its own issue and is marked
            // derived rather than merging into the observation it restates.
            Finding("rdy-risk-js", "Large application JavaScript payload", FrontendQualityCategory.Readiness,
                FrontendQualityEngineId.PassivePerformance, severity: FrontendQualitySeverity.High,
                origin: FrontendQualityFindingOrigin.Derived),
            // An absent optional capability: worth knowing, nothing to do about it.
            Finding("wasm-sw", "No service worker / PWA support detected", FrontendQualityCategory.BlazorWasm,
                FrontendQualityEngineId.PassivePerformance, "WASM-SW", FrontendQualitySeverity.Info),
        };

        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);

        issues.Single(i => i.FindingInstances.All(f => f.Origin == FrontendQualityFindingOrigin.Derived))
            .Derived.Should().BeTrue();
        issues.Single(i => i.CanonicalTitle.Contains("service worker")).Informational.Should().BeTrue();
        // 61. Neither is counted as something to fix, and neither is deleted.
        issues.Count(i => i.IsActionable).Should().Be(1);
        issues.Should().HaveCount(3, "every observation is still represented");
        // 14. And neither produces a recommendation nobody could close.
        FrontendQualityRecommendationGrouper.Build(issues).Should().ContainSingle();
    }

    // ── §14, §15, §67. Recommendations are jobs ───────────────────────────────────────────────────────────────────

    [Fact]
    public void IdenticalRemediationsAcrossDifferentIssuesBecomeOneTheme()
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("a", "Large uncompressed asset: app.js", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PassivePerformance, "PERF-COMPRESS-JS", FrontendQualitySeverity.Medium,
                recommendation: "Enable Brotli or gzip compression for static assets."),
            Finding("b", "Large uncompressed asset: app.css", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PassivePerformance, "PERF-COMPRESS-CSS", FrontendQualitySeverity.High,
                recommendation: "Enable Brotli or gzip compression for static assets."),
        };

        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);
        var themes = FrontendQualityRecommendationGrouper.Build(issues);

        issues.Should().HaveCount(2, "two assets are two issues");
        var theme = themes.Should().ContainSingle().Subject;
        theme.Issues.Should().HaveCount(2);
        theme.SourceFindingCount.Should().Be(2);
        // 49. Priority follows the worst issue the job fixes, never the number of them.
        theme.Priority.Should().Be(FrontendQualitySeverity.High);
        theme.PriorityLabel.Should().Be("High priority");
    }

    [Fact]
    public void ARecommendationCarriesItsAffectedPagesAndSourceCount()
    {
        var themes = FrontendQualityRecommendationGrouper.Build(
            FrontendQualityLogicalIssueGrouper.Group(ContrastOnFivePages()));

        var theme = themes.Should().ContainSingle().Subject;
        theme.AffectedPages.Should().HaveCount(5);
        theme.SourceFindingCount.Should().Be(5);
        theme.ScaleLabel.Should().Be("1 logical issue · 5 affected pages · 5 source findings");
    }

    // ── §29. The whole point, on a realistic mixture ──────────────────────────────────────────────────────────────

    [Fact]
    public void LogicalIssuesAreMeaningfullyFewerThanSourceObservations()
    {
        var findings = new List<FrontendQualityFinding>();
        findings.AddRange(ContrastOnFivePages());
        findings.AddRange(new[] { "/", "/admin/operations", "/admin/user-access" }
            .Select((route, i) => Finding($"pq-api-{i}", "Repeated authenticated API call", FrontendQualityCategory.Performance,
                FrontendQualityEngineId.PerformanceQuality, "pq-repeated-rest", FrontendQualitySeverity.Medium, route,
                "Cache or de-duplicate the repeated request.")));
        findings.Add(Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security,
            FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY", FrontendQualitySeverity.High));
        findings.Add(Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards,
            FrontendQualityEngineId.StaticSecurity, "std-csp-missing", FrontendQualitySeverity.Critical));

        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);

        findings.Should().HaveCount(10);
        issues.Should().HaveCount(3, "contrast, repeated API calls and the missing CSP");
        // 63. Nothing is lost: every observation is still attached to exactly one issue.
        issues.Sum(i => i.SourceFindingCount).Should().Be(findings.Count);
        issues.SelectMany(i => i.FindingInstances).Select(i => i.SourceFindingId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(findings.Count);
    }
}
