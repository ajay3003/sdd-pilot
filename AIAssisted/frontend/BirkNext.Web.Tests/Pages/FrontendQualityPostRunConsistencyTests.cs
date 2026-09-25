using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// One count model on every post-run surface. The live result read "4 derived indicators" in the metrics, in QA
/// Readiness and in the Source findings hint, and "3 derived indicators" in the Review items header: the header recounted
/// report.LogicalIssues as "not actionable and not informational", which dropped the Info-severity derived API probe —
/// the same record the informational count then counted a second time.
/// </summary>
public sealed class FrontendQualityPostRunConsistencyTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Url };
        profile.Features.EnableBrowserRuntimeEngine = false;
        profile.Features.EnableAccessibilityEngine = false;
        profile.Features.EnableLighthouseEngine = false;
        profile.Features.EnablePassiveSecurityEngine = false;
        profile.Features.EnableBrowserQualityEngine = false;
        profile.Features.EnablePerformanceQualityEngine = false;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Url, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private static FrontendQualityFinding Finding(
        string id, string title, FrontendQualityCategory category, FrontendQualitySeverity severity,
        FrontendQualityEngineId engine = FrontendQualityEngineId.PassivePerformance, string? ruleId = null,
        FrontendQualityFindingOrigin origin = FrontendQualityFindingOrigin.Source, string? page = null) => new()
        {
            Id = id, Title = page is null ? title : $"{title} — {page}", Category = category, Severity = severity,
            Description = title, Recommendation = $"Resolve {title}.", Evidence = page is null ? [] : [$"Page: {page}"],
            SourceSystem = engine.ToString(), EngineId = engine, SourceRuleId = ruleId ?? id, Origin = origin,
            Status = CheckExecutionStatus.Failed,
        };

    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id, FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed, bool enabled = true,
        int? evidence = null, int? findings = null) => new()
        {
            EngineId = id, DisplayName = id.ToString(), Enabled = enabled, Requirement = requirement,
            ExecutionState = enabled ? state : FrontendQualityEngineExecutionState.Disabled,
            EvidenceCount = evidence, FindingCount = findings,
        };

    /// <summary>
    /// The live shape: the same contrast defect on three routes, a missing CSP seen by Security and Standards, two
    /// Info source observations, three derived QA Readiness indicators and one Info-severity derived API probe.
    /// </summary>
    private static FrontendQualityReviewReport Report()
    {
        var findings = new List<FrontendQualityFinding>();
        findings.AddRange(new[] { "/", "/admin/operations", "/admin/user-access" }.Select((route, i) =>
            Finding($"bq-contrast-{i}", "Contrast (Minimum)", FrontendQualityCategory.Accessibility, FrontendQualitySeverity.High,
                FrontendQualityEngineId.BrowserQuality, "a11y-contrast", page: route)));
        findings.Add(Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security,
            FrontendQualitySeverity.High, FrontendQualityEngineId.StaticSecurity, "HDR-MISSING-CONTENT-SECURITY-POLICY"));
        findings.Add(Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards,
            FrontendQualitySeverity.Critical, FrontendQualityEngineId.StaticSecurity, "std-csp-missing"));
        findings.Add(Finding("perf-cache", "Static assets not cached", FrontendQualityCategory.Performance, FrontendQualitySeverity.Medium));
        findings.Add(Finding("info-sri", "Subresource integrity not used", FrontendQualityCategory.Standards, FrontendQualitySeverity.Info,
            FrontendQualityEngineId.StaticSecurity));
        findings.Add(Finding("info-server", "Server header present", FrontendQualityCategory.Security, FrontendQualitySeverity.Info,
            FrontendQualityEngineId.StaticSecurity));
        findings.AddRange(new[] { "Large application JavaScript payload", "Large uncompressed assets", "Large CSS payload" }
            .Select((title, i) => Finding($"rdy-risk-{i}", title, FrontendQualityCategory.Readiness, FrontendQualitySeverity.High,
                origin: FrontendQualityFindingOrigin.Derived)));
        findings.Add(Finding("api-openapi", "OpenAPI description not published", FrontendQualityCategory.Performance,
            FrontendQualitySeverity.Info, origin: FrontendQualityFindingOrigin.Derived));

        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity, evidence: 5, findings: 6),
            Outcome(FrontendQualityEngineId.PassivePerformance, evidence: 2, findings: 3),
            Outcome(FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineRequirement.Optional, evidence: 3, findings: 3),
            Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable),
            Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, enabled: false),
        };

        return new FrontendQualityReviewReport
        {
            TargetUrl = Url,
            TargetEnvironment = new FrontendReviewTargetIdentity("dev", "M2LB DEV", "Development", Url, DateTime.UtcNow),
            GeneratedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            EngineOutcomes = outcomes, Coverage = FrontendQualityCoverage.Evaluate(outcomes),
            ReleaseDisposition = FrontendQualityReleaseDisposition.ReviewRequired,
            Findings = findings,
            LogicalIssues = FrontendQualityLogicalIssueGrouper.Group(findings),
            // Risks are critical/high finding TITLES; Limitations are about the method.
            Risks = ["Missing security header: Content-Security-Policy", "Development configuration served"],
            Limitations = ["Accessibility is assessed only when the optional axe-core browser engine executes."],
            Wcag = new WcagAssessment
            {
                Profile = WcagProfiles.Norwegian, PagesWithEvidence = 3,
                Results = [new WcagCriterionResult
                {
                    Definition = WcagRegistry.All.Single(d => d.CriterionId == "1.4.3"),
                    Page = "/", Status = WcagStatus.Fail, LastTested = DateTimeOffset.UtcNow,
                }],
            },
            TargetAccess = new FrontendQualityTargetAccessContext
            {
                EnvironmentName = "M2LB DEV", EnvironmentType = "Development", TargetUrl = Url,
                RequiresAuthentication = false, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            },
            ActiveEngines = FrontendQualityActiveEngines.Resolve(Context()),
        };
    }

    private IRenderedComponent<FrontendQualityReview> Result(FrontendQualityReviewReport report)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context());
        var orchestrator = new Mock<IFrontendQualityReviewOrchestrator>();
        orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityReviewOrchestrationResult(QualityReport: report));
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(orchestrator.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();

        var page = Render<FrontendQualityReview>();
        page.Find("[data-testid=fqr-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-result-summary]"));
        return page;
    }

    private static string Hint(IRenderedComponent<FrontendQualityReview> page, string id) =>
        page.Find($"[data-testid={id}] .disclosure-hint").TextContent.Trim();

    private static void Open(IRenderedComponent<FrontendQualityReview> page, string id)
    {
        if (page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded") != "true")
            page.Find($"[data-testid={id}-toggle]").Click();
    }

    // ── Derived indicators: one number everywhere ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DerivedIndicatorCountIsTheSameOnEverySurface()
    {
        var report = Report();
        var view = FrontendQualityResultPresentation.Build(report);
        view.DerivedIndicatorCount.Should().Be(4, "three QA Readiness indicators and the derived API probe");

        var page = Result(report);
        page.Find("[data-testid=fqr-metric-derived]").TextContent.Trim().Should().Be("4");
        Hint(page, "fqr-all-findings").Should().Contain("4 derived indicators");
        Hint(page, "fqr-all-logical-issues").Should().Contain("4 derived indicators").And.NotContain("3 derived");
        // The domains split the same four: three in QA Readiness, one filed under Performance.
        view.Domain(FrontendQualityCategory.Readiness).FindingCount.Should().Be(3);
        view.Domain(FrontendQualityCategory.Performance).DerivedIndicatorCount.Should().Be(1);
        view.Domains.Sum(d => d.Derived ? d.FindingCount ?? 0 : d.DerivedIndicatorCount).Should().Be(view.DerivedIndicatorCount);

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Should().Contain("<strong>Derived indicators:</strong> 4");
    }

    [Fact]
    public void ActionableInformationalAndDerivedAreDisjointAndCoverEveryItem()
    {
        var view = FrontendQualityResultPresentation.Build(Report());
        var actionable = view.ActionableIssues.Select(i => i.LogicalId).ToHashSet();
        var informational = view.InformationalIssues.Select(i => i.LogicalId).ToHashSet();
        var derived = view.DerivedIssues.Select(i => i.LogicalId).ToHashSet();

        actionable.Intersect(informational).Should().BeEmpty();
        actionable.Intersect(derived).Should().BeEmpty();
        informational.Intersect(derived).Should().BeEmpty("the derived Info probe is a derived indicator only");
        (actionable.Count + informational.Count + derived.Count).Should().Be(view.LogicalIssues.Count);

        view.InformationalIssueCount.Should().Be(2, "the two Info SOURCE observations; the derived Info probe is not one");
        view.LogicalIssueCount.Should().Be(actionable.Count);
        view.DerivedIssues.Sum(i => i.SourceFindingCount).Should().Be(view.DerivedIndicatorCount);
    }

    [Fact]
    public void ReviewItemsListsEachKindUnderItsOwnHeading()
    {
        var report = Report();
        var view = FrontendQualityResultPresentation.Build(report);
        var page = Result(report);
        Open(page, "fqr-all-logical-issues");

        page.Find("[data-testid=fqr-review-items-actionable]").TextContent.Should().Be($"Actionable logical issues ({view.LogicalIssueCount})");
        page.Find("[data-testid=fqr-review-items-informational]").TextContent.Should().Be("Informational observations (2)");
        page.Find("[data-testid=fqr-review-items-derived]").TextContent.Should().Be("Derived indicators (4)");
        var derivedCards = page.FindAll("[data-testid=fqr-issue-derived]");
        derivedCards.Should().HaveCount(view.DerivedIssues.Count);
        // A derived card never counts its records as source findings, and is never also labelled Informational.
        page.FindAll("[data-testid=fqr-logical-issue]")
            .Where(card => card.QuerySelector("[data-testid=fqr-issue-derived]") is not null)
            .Should().OnlyContain(card => card.QuerySelector("[data-testid=fqr-issue-informational]") == null
                && card.QuerySelector("[data-testid=fqr-issue-scale]")!.TextContent.Contains("derived indicator"));
    }

    // ── Source findings: unique records, cross-domain counted once ────────────────────────────────────────────────

    [Fact]
    public void SourceFindingsAreUniqueRecordsAndCrossDomainIssuesAreNotDoubleCounted()
    {
        var report = Report();
        var view = FrontendQualityResultPresentation.Build(report);
        var sourceIds = report.Findings.Where(f => f.Origin == FrontendQualityFindingOrigin.Source).Select(f => f.Id).ToList();

        view.SourceFindingCount.Should().Be(sourceIds.Distinct().Count()).And.Be(8);
        // Every source record counted in exactly one domain, so the domains sum to the headline.
        view.Domains.Where(d => !d.Derived).Sum(d => d.FindingCount ?? 0).Should().Be(view.SourceFindingCount);
        // The CSP seen by Standards and Security is one logical issue, owned by Security and related to Standards.
        var csp = view.LogicalIssues.Where(i => i.FindingInstances.Any(f => f.SourceFindingId is "sec-csp" or "std-csp")).ToList();
        csp.Should().ContainSingle();
        view.Domain(FrontendQualityCategory.Standards).ContributedToOtherDomains.Should().Be(1);
        view.Domains.Sum(d => d.LogicalIssueCount ?? 0).Should().Be(view.LogicalIssueCount, "an issue counts once, under its primary domain");
    }

    [Fact]
    public void CriticalHighSourceFindingsAndCriticalHighLogicalIssuesStayDistinct()
    {
        var view = FrontendQualityResultPresentation.Build(Report());
        view.CriticalHighSourceCount.Should().Be(5, "three contrast observations and two CSP observations");
        view.ActionableIssues.Count(i => i.PrimarySeverity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High)
            .Should().Be(2, "one contrast defect, one missing CSP");
        view.Release!.Reasons.Should().Contain("2 critical or high logical issues to resolve.");
    }

    // ── Engine coverage ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FrontendQualityEngineId.StaticSecurity, 5, 6, "5 evidence items", "6 findings")]
    [InlineData(FrontendQualityEngineId.BrowserQuality, 5, 15, "5 pages with evidence", "15 findings")]
    [InlineData(FrontendQualityEngineId.PerformanceQuality, 1, 1, "1 page with evidence", "1 finding")]
    [InlineData(FrontendQualityEngineId.Lighthouse, 12, 30, "12 metrics", "30 audits")]
    [InlineData(FrontendQualityEngineId.Accessibility, 4, 2, "4 element/failure references", "2 findings")]
    [InlineData(FrontendQualityEngineId.PassiveSecurity, 3, 7, "3 findings with evidence", "7 findings")]
    public void EngineCoverageValuesAreLabelledInTheEnginesOwnUnit(FrontendQualityEngineId id, int evidence, int findings, string evidenceLabel, string findingsLabel)
    {
        var outcome = Outcome(id, evidence: evidence, findings: findings);
        FrontendQualityEngineOutcomePresentation.EvidenceLabel(outcome).Should().Be(evidenceLabel);
        FrontendQualityEngineOutcomePresentation.FindingsLabel(outcome).Should().Be(findingsLabel);
    }

    [Fact]
    public void EngineCoverageTableAndExportHaveNoSlashColumn()
    {
        var report = Report();
        var page = Result(report);
        Open(page, "fqr-engine-coverage");
        var headers = page.FindAll("[data-testid=fqr-engine-coverage] thead th").Select(th => th.TextContent.Trim()).ToList();
        headers.Should().Contain("Evidence records").And.Contain("Findings").And.NotContain("Evidence / findings");
        var row = page.Find("[data-testid=fqr-engine-coverage] tr[data-engine-id=BrowserQuality]");
        row.QuerySelector("[data-testid=fqr-engine-evidence]")!.TextContent.Should().Be("3 pages with evidence");
        row.QuerySelector("[data-testid=fqr-engine-findings]")!.TextContent.Should().Be("3 findings");
        page.Find("[data-testid=fqr-engine-coverage] tr[data-engine-id=Lighthouse] [data-testid=fqr-engine-evidence]").TextContent.Should().Be("—");

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Should().Contain("Evidence records").And.Contain("3 pages with evidence").And.NotContain("Evidence / findings");
    }

    [Fact]
    public void OptionalDenominatorIsActiveEnginesAndTheExcludedOnesAreNamed()
    {
        var report = Report();
        var view = FrontendQualityResultPresentation.Build(report);
        // Browser Quality assessed, Lighthouse unavailable; Browser Runtime disabled and outside the denominator.
        view.Completeness!.OptionalTotal.Should().Be(2);
        view.Completeness.OptionalAssessed.Should().Be(1);
        Hint(Result(report), "fqr-engine-coverage")
            .Should().Be("Required 2 of 2 assessed · Optional 1 of 2 active assessed · 1 not active, excluded");
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryReviewDetailsSectionStartsCollapsed_AndRawEvidenceIsNestedAndNotBuilt()
    {
        var page = Result(Report());
        foreach (var id in new[] { "fqr-all-logical-issues", "fqr-all-findings", "fqr-engine-coverage", "fqr-result-technical",
                     "fqr-engine-scores", "fqr-result-diagnostics" })
        {
            page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded").Should().Be("false", id);
            page.Find($"[data-testid={id}-body]").HasAttribute("hidden").Should().BeTrue(id);
        }

        Open(page, "fqr-result-diagnostics");
        var raw = page.Find("[data-testid=fqr-result-diagnostics-body] [data-testid=fqr-technical-raw]");
        raw.Should().NotBeNull("raw evidence lives inside Technical details");
        page.Find("[data-testid=fqr-technical-raw-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll("[data-testid=fqr-technical-evidence]").Should().BeEmpty("raw evidence is not rendered until asked for");
        page.Find("[data-testid=fqr-technical-raw-toggle]").Click();
        page.Find("[data-testid=fqr-technical-evidence]").Should().NotBeNull();
    }

    // ── Limitations are about the method; findings stay findings ──────────────────────────────────────────────────

    [Fact]
    public void TechnicalLimitationsListMethodLimitsOnly_NotFindingTitles()
    {
        var page = Result(Report());
        Open(page, "fqr-result-diagnostics");
        page.FindAll("[data-testid=fqr-technical-risks]").Should().BeEmpty();
        var limits = page.Find("[data-testid=fqr-technical-limitations]").TextContent;
        limits.Should().Contain("axe-core");
        limits.Should().NotContain("Content-Security-Policy").And.NotContain("Development configuration");
        page.Find("#fqr-tech-limits").TextContent.Should().Be("Review limitations");
    }

    [Fact]
    public void AMethodLimitationContainedInALongerOneIsStatedOnce()
    {
        var report = Report();
        report.Limitations.Clear();
        report.Limitations.AddRange([
            "Accessibility is assessed only when the optional axe-core browser engine executes. Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.",
            "Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.",
            "Lighthouse provides synthetic lab measurements.",
        ]);
        FrontendQualityResultPresentation.MethodLimitations(report).Should().HaveCount(2);

        var page = Result(report);
        Open(page, "fqr-result-diagnostics");
        page.FindAll("[data-testid=fqr-technical-limitations] li").Should().HaveCount(2);
        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Split("<li>Automated tooling cannot verify").Length.Should().Be(1, "the shorter duplicate is not its own export item");
    }

    [Fact]
    public void AnEngineThatDidNotAssessHasNoCounts()
    {
        var outcome = Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional,
            FrontendQualityEngineExecutionState.Unavailable, evidence: 0, findings: 0);
        FrontendQualityEngineOutcomePresentation.EvidenceLabel(outcome).Should().Be("—", "0 metrics beside Unavailable reads as a clean run");
        FrontendQualityEngineOutcomePresentation.FindingsLabel(outcome).Should().Be("—");
    }

    // ── Wording ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CriteriaAreCountedInTheSelectedProfile_NeverInScopeOrApplicable()
    {
        var page = Result(Report());
        var criteria = WcagProfiles.Norwegian.CriteriaInScope;
        page.Find("[data-testid=fqr-result-profile-scope]").TextContent.Should().Be($"{criteria} criteria in selected profile");
        var accessibility = page.FindAll("[data-testid=fqr-domain-result]").Single(d => d.GetAttribute("data-category") == "Accessibility");
        accessibility.TextContent.Should().Contain($"{criteria} criteria in selected profile")
            .And.NotContain("criteria in scope").And.NotContain("applicable");
    }

    [Fact]
    public void TargetAccessSeparatesConfiguredProviderScopeAndAccessUsed()
    {
        var report = Report();
        var page = Result(report);
        Open(page, "fqr-result-technical");
        var auth = page.Find("[data-testid=fqr-access-auth]");
        auth.PreviousElementSibling!.TextContent.Should().Be("Authentication configured");
        auth.TextContent.Should().Be("Microsoft Entra ID");
        page.Find("[data-testid=fqr-access-scope]").TextContent.Should().Be("Public only");
        // Manual verification may legitimately read "Not required"; the target facts never do.
        page.Find("[data-testid=fqr-access-group-target]").TextContent.Should().NotContain("Not required");

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Should().Contain("<strong>Authentication configured:</strong></dt><dd>Microsoft Entra ID</dd>")
            .And.Contain("<strong>Review scope:</strong></dt><dd>Public only</dd>");
    }

    [Fact]
    public void KeyIssuesPointToTheSectionThatExists()
    {
        var report = Report();
        for (var i = 0; i < 5; i++)
            report.Findings.Add(Finding($"extra-{i}", $"Extra issue {i}", FrontendQualityCategory.Performance, FrontendQualitySeverity.Low));
        report.LogicalIssues.Clear();
        report.LogicalIssues.AddRange(FrontendQualityLogicalIssueGrouper.Group(report.Findings));
        Result(report).Find("[data-testid=fqr-key-issues-more]").TextContent.Should().Contain("in Review items below");
    }
}
