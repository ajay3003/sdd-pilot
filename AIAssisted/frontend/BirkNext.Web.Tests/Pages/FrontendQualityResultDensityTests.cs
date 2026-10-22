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
/// The finished review as a decision surface.
///
/// What it used to open with: a headline of "57 findings", thirty expanded performance cards, a category grid whose
/// numbers added up to a total that already counted the same problems twice, and a recommendation grid with one card
/// per observation. The release decision — the thing a reader comes here to find — was below all of that, inside
/// Review details, under the engine matrix.
///
/// These tests assert the information architecture: what is visible without interaction, what is one click away, and
/// which numbers may appear beside each other.
/// </summary>
public sealed class FrontendQualityResultDensityTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Url };
        foreach (var off in new Action[]
        {
            () => profile.Features.EnableBrowserRuntimeEngine = false,
            () => profile.Features.EnableAccessibilityEngine = false,
            () => profile.Features.EnableLighthouseEngine = false,
            () => profile.Features.EnablePassiveSecurityEngine = false,
            () => profile.Features.EnableBrowserQualityEngine = false,
            () => profile.Features.EnablePerformanceQualityEngine = false,
        }) off();
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Url, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed,
        FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required) => new()
        {
            EngineId = id, DisplayName = id.ToString(), Enabled = true,
            Requirement = requirement, ExecutionState = state,
        };

    private static FrontendQualityFinding Finding(
        string id, string title, FrontendQualityCategory category, FrontendQualitySeverity severity,
        string? page = null, FrontendQualityEngineId engine = FrontendQualityEngineId.PassivePerformance,
        string? ruleId = null, FrontendQualityFindingOrigin origin = FrontendQualityFindingOrigin.Source) => new()
        {
            Id = id, Title = page is null ? title : $"{title} — {page}", Category = category, Severity = severity,
            Description = title, Recommendation = $"Resolve {title}.",
            Evidence = page is null ? [] : [$"Page: {page}"],
            SourceSystem = engine.ToString(), EngineId = engine, SourceRuleId = ruleId, Origin = origin,
            Status = CheckExecutionStatus.Failed,
        };

    /// <summary>
    /// The shape of the live result: one contrast defect on five routes, repeated API calls on three, a missing CSP
    /// reported by both Security and Standards, and three derived QA Readiness indicators restating the rest.
    /// </summary>
    private static FrontendQualityReviewReport RealisticReport()
    {
        var findings = new List<FrontendQualityFinding>();
        findings.AddRange(new[] { "/", "/admin/general-roles", "/admin/operations", "/admin/user-access", "/admin/child-specific-roles" }
            .Select((route, i) => Finding($"bq-contrast-{i}", "Contrast (Minimum)", FrontendQualityCategory.Accessibility,
                FrontendQualitySeverity.High, route, FrontendQualityEngineId.BrowserQuality, "a11y-contrast")));
        findings.AddRange(new[] { "/", "/admin/operations", "/admin/user-access" }
            .Select((route, i) => Finding($"pq-api-{i}", "Repeated authenticated API call", FrontendQualityCategory.Performance,
                FrontendQualitySeverity.Medium, route, FrontendQualityEngineId.PerformanceQuality, "pq-repeated-rest")));
        findings.Add(Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security,
            FrontendQualitySeverity.High, engine: FrontendQualityEngineId.StaticSecurity, ruleId: "HDR-MISSING-CONTENT-SECURITY-POLICY"));
        findings.Add(Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards,
            FrontendQualitySeverity.Critical, engine: FrontendQualityEngineId.StaticSecurity, ruleId: "std-csp-missing"));
        findings.AddRange(new[] { "Large application JavaScript payload", "Large uncompressed assets", "Large CSS payload" }
            .Select((title, i) => Finding($"rdy-risk-{i}", title, FrontendQualityCategory.Readiness,
                FrontendQualitySeverity.High, origin: FrontendQualityFindingOrigin.Derived)));

        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity),
            Outcome(FrontendQualityEngineId.PassivePerformance),
            Outcome(FrontendQualityEngineId.BrowserQuality, requirement: FrontendQualityEngineRequirement.Optional),
            Outcome(FrontendQualityEngineId.PerformanceQuality, requirement: FrontendQualityEngineRequirement.Optional),
            Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineRequirement.Optional),
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
            Wcag = new WcagAssessment
            {
                Profile = WcagProfiles.Norwegian,
                PagesWithEvidence = 5,
                Results = [new WcagCriterionResult
                {
                    Definition = WcagRegistry.All.Single(d => d.CriterionId == "1.4.3"),
                    Page = "/", Status = WcagStatus.Fail, LastTested = DateTimeOffset.UtcNow,
                }],
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

    /// <summary>Text a reader can actually see: a collapsed disclosure body carries <c>hidden</c> and is not rendered.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static bool Collapsed(IRenderedComponent<FrontendQualityReview> page, string id) =>
        page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded") == "false"
        && page.Find($"[data-testid={id}-body]").HasAttribute("hidden");

    // ── §5, §6, §28. The headline ─────────────────────────────────────────────────────────────────────────────────

    // 5, 35. The release decision is in the result, not under the engine matrix.
    [Fact]
    public void TheReleaseDecisionIsStatedInTheResultSummary()
    {
        var page = Result(RealisticReport());

        var summary = page.Find("[data-testid=fqr-result-summary]");
        var release = summary.QuerySelector("[data-testid=fqr-release]")!;
        release.QuerySelector("[data-testid=fqr-release-label]")!.TextContent.Trim().Should().Be("Review required");
        release.GetAttribute("data-disposition").Should().Be("ReviewRequired");
        // 35. And it never claims to be an approval.
        release.QuerySelector("[data-testid=fqr-release-statement]")!.TextContent
            .Should().Contain("before a release decision can be made");
    }

    // 7, 28. Three different numbers, each named for what it counts.
    [Fact]
    public void TheHeadlineSeparatesObservationsFromProblemsFromDerivedIndicators()
    {
        var report = RealisticReport();
        var page = Result(report);

        var metrics = page.Find("[data-testid=fqr-result-metrics]");
        // 8. The three derived QA indicators are excluded from the source-finding total, and named separately.
        metrics.QuerySelector("[data-testid=fqr-metric-source]")!.TextContent.Trim().Should().Be("10");
        metrics.QuerySelector("[data-testid=fqr-metric-derived]")!.TextContent.Trim().Should().Be("3");
        report.Findings.Should().HaveCount(13, "every observation is still on the report");

        // 29. And the problems are meaningfully fewer than the observations.
        var logical = int.Parse(metrics.QuerySelector("[data-testid=fqr-metric-logical]")!.TextContent.Trim());
        logical.Should().Be(3, "contrast, repeated API calls and the missing CSP");
        logical.Should().BeLessThan(10);

        // 28. No bare "N findings" headline anywhere in the summary.
        VisibleText(page.Find("[data-testid=fqr-result-summary]")).Should().NotContain("13 findings");
    }

    // 3, §50. The single completeness word is gone; coverage states its count AND whether it blocks, never left to inference.
    [Fact]
    public void CompletenessIsShownOnItsFourDimensions_NeverAsOneWord()
    {
        var page = Result(RealisticReport());

        var completeness = page.Find("[data-testid=fqr-completeness]");
        page.Find("[data-testid=fqr-completeness-execution]").TextContent.Should().Contain("Completed");
        completeness.QuerySelector("[data-testid=fqr-completeness-required]")!.TextContent.Should().Contain("2 of 2 completed");
        completeness.QuerySelector("[data-testid=fqr-completeness-required-meaning]")!.TextContent.Trim().Should().Be("Satisfied");
        completeness.QuerySelector("[data-testid=fqr-completeness-optional]")!.TextContent.Should().Contain("2 of 3 completed");
        completeness.QuerySelector("[data-testid=fqr-completeness-optional-meaning]")!.TextContent.Trim().Should().Be("Non-blocking");
        completeness.QuerySelector("[data-testid=fqr-completeness-manual]")!.TextContent.Should().Contain("Required");

        page.Markup.Should().NotContain("Assessment Completeness");
        completeness.TextContent.Should().NotContain("Full");
    }

    // ── §32, §34, §75. What is visible by default ─────────────────────────────────────────────────────────────────

    // 32. The result leads with a few issues, not with every observation; the full list is its own view.
    [Fact]
    public void KeyIssuesAreVisibleAndTheFullListIsNot()
    {
        var page = Result(RealisticReport());

        page.Find("[data-testid=fqr-key-issues]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(5);
        page.Find("[data-testid=fqr-tabpanel-summary]").HasAttribute("hidden").Should().BeFalse();
        page.Find("[data-testid=fqr-tabpanel-issues]").HasAttribute("hidden").Should().BeTrue("every issue is one view away");
    }

    // §65, §67, §68. A domain card is status, counts, one reason and ONE action — its issues are not repeated inline.
    [Fact]
    public void DomainCardsCarryOneActionAndNoInlineFindings()
    {
        var page = Result(RealisticReport());
        var domains = page.Find("[data-testid=fqr-domain-results]");

        domains.QuerySelectorAll("[data-testid=fqr-domain-finding-list], [data-testid=fqr-logical-issue]").Should().BeEmpty();
        var performance = domains.QuerySelectorAll("[data-testid=fqr-domain-result]").Single(d => d.GetAttribute("data-category") == "Performance");
        performance.QuerySelectorAll("button").Should().ContainSingle()
            .Which.TextContent.Trim().Should().Be("Review performance issues");

        // The action opens Issues filtered to that domain.
        page.Find("[data-testid=fqr-domain-review-issues][data-category=Performance]").Click();
        page.Find("[data-testid=fqr-tabpanel-issues]").HasAttribute("hidden").Should().BeFalse();
        page.Find("[data-testid=fqr-issue-filter-domain]").GetAttribute("value").Should().Be("Performance");
        page.Find("[data-testid=fqr-tabpanel-issues]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Should().OnlyContain(i => i.TextContent.Contains("Repeated authenticated API call"));
    }

    // 30, 41, 44, 75, 76. Raw evidence, the engine diagnostics, the scores and the technical details are all kept and all closed.
    [Fact]
    public void EveryDiagnosticSurfaceIsRetainedAndCollapsed()
    {
        var page = Result(RealisticReport());

        foreach (var id in new[] { "fqr-engine-coverage", "fqr-cookie-security", "fqr-result-diagnostics" })
        {
            page.FindAll($"[data-testid={id}-toggle]").Should().ContainSingle($"{id} is still available");
            Collapsed(page, id).Should().BeTrue($"{id} opens on request");
        }

        // §48. Engine scores sit inside Technical details, never in the primary review.
        page.Find("[data-testid=fqr-result-diagnostics-body] [data-testid=fqr-engine-scores]").Should().NotBeNull();
        VisibleText(page.Find(".fqr-page")).Should().NotContain("Legacy static review score");
    }

    // §25. Five result-level views; only the Summary is visible after a run, the evidence view ends with the diagnostics.
    [Fact]
    public void ResultViewsAreSummaryIssuesCoverageEvidenceHistory()
    {
        var page = Result(RealisticReport());

        page.FindAll("[data-testid=fqr-result-tabs] [role=tab]").Select(t => t.TextContent.Trim())
            .Should().Equal("Summary", "Issues (3)", "Coverage", "Evidence", "History");
        page.FindAll("[role=tabpanel]:not([hidden])").Select(p => p.Id).Should().Equal("fqr-tabpanel-summary");

        page.Find("[data-testid=fqr-detailed-evidence]")
            .QuerySelectorAll(":scope > .disclosure > .disclosure-toggle .disclosure-text")
            .Select(t => t.TextContent.Trim())
            .Should().Equal("Cookie security", "Shared quality evidence", "Technical details");
    }

    // ── §14, §48, §80. Recommendations ────────────────────────────────────────────────────────────────────────────

    // 14, 65, 67. One card per job, carrying the scale of the work rather than repeating it.
    [Fact]
    public void RecommendationsAreOnePerRemediation_NotOnePerObservation()
    {
        var page = Result(RealisticReport());

        var cards = page.Find("[data-testid=fqr-recommendation-themes]").QuerySelectorAll("[data-testid=fqr-recommendation]");
        cards.Should().HaveCount(3, "contrast, repeated API calls and the missing CSP");

        var contrast = cards.Single(c => c.TextContent.Contains("Contrast", StringComparison.OrdinalIgnoreCase));
        contrast.QuerySelector("[data-testid=fqr-recommendation-scale]")!.TextContent
            .Should().Contain("5 affected pages").And.Contain("5 source findings");
    }

    // ── §16, §53, §70, §79. The accessibility summary ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheAccessibilitySummaryIsFiveNumbersWithAStatedRelationship()
    {
        var page = Result(RealisticReport());

        var summary = page.Find("[data-testid=fqr-accessibility-summary]");
        int Count(string name) => int.Parse(summary.QuerySelector($"[data-count={name}] dd")!.TextContent.Trim());

        // 51. The partition always adds up to the profile.
        (Count("with-evidence") + Count("without-evidence")).Should().Be(Count("profile"));
        Count("profile").Should().Be(WcagProfiles.Norwegian.CriteriaInScope);

        // 17. ONE primary manual number. The near-synonym pair that read as competing totals is gone.
        summary.QuerySelectorAll("[data-count=manual]").Should().ContainSingle();
        VisibleText(summary).Should().NotContain("Require manual review");
        VisibleText(summary).Should().NotContain("Not yet assessed");

        // 54. No invented pass count, and no conformance claim.
        VisibleText(summary).Should().NotContain("Passed criteria");
        summary.QuerySelector("[data-testid=fqr-a11y-caveat]")!.TextContent
            .Should().Be("No automated failure detected does not establish WCAG conformance.");

        // 50. The overlap is explained where the detail is, not as five peer cards.
        page.Find("[data-testid=fqr-a11y-overlap-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=fqr-a11y-overlap-body]").TextContent.Should().Contain("divide the profile");
    }

    // ── §37, §38. Manual follow-up ────────────────────────────────────────────────────────────────────────────────

    // 38. Two obligations that mean different things, kept apart.
    [Fact]
    public void MandatoryAssessmentAndRecommendedVerificationAreSeparate()
    {
        var report = RealisticReport();
        var withManual = new FrontendQualityReviewReport
        {
            TargetUrl = report.TargetUrl, TargetEnvironment = report.TargetEnvironment, GeneratedAt = report.GeneratedAt,
            CompletedAt = report.CompletedAt, EngineOutcomes = report.EngineOutcomes, Coverage = report.Coverage,
            ReleaseDisposition = report.ReleaseDisposition, Findings = report.Findings, LogicalIssues = report.LogicalIssues,
            Wcag = report.Wcag, ActiveEngines = report.ActiveEngines,
            ManualReviewItems =
            [
                new() { Title = "Browser Quality manual verification", Reason = "Confirm with repeated runs.", Source = "Browser Quality" },
            ],
        };

        var page = Result(withManual);

        var followUp = page.Find("[data-testid=fqr-manual-followup]");
        followUp.QuerySelector("[data-testid=fqr-manual-mandatory]")!.TextContent
            .Should().Contain("Accessibility").And.Contain("require a person to assess them")
            .And.Contain("not a gap in the automated coverage");
        followUp.QuerySelector("[data-testid=fqr-manual-recommended]")!.TextContent
            .Should().Contain("Recommended human verification");
        // The two blocks are distinct elements, so neither reads as the other.
        followUp.QuerySelectorAll("[data-testid=fqr-manual-mandatory], [data-testid=fqr-manual-recommended]").Should().HaveCount(2);
    }

    // ── §81. The default page is short ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A blunt but honest density guard. The old default rendered every domain's findings expanded, a category grid,
    /// one recommendation card per observation, the engine matrix, the score row and the whole technical dump — so the
    /// reader scrolled past everything to reach anything. This pins that the result a person sees without interacting
    /// stays a summary, and it fails loudly if a future change re-expands one of those blocks by default.
    /// </summary>
    [Fact]
    public void TheDefaultResultIsASummary_NotEveryObservation()
    {
        var report = RealisticReport();
        var page = Result(report);

        var visible = VisibleText(page.Find(".fqr-page"));

        // Every individual observation title is available; none of them is on screen by default.
        foreach (var route in new[] { "/admin/general-roles", "/admin/child-specific-roles" })
            visible.Should().NotContain(route, "per-page occurrences live behind the issue that groups them");
        visible.Should().NotContain("Large CSS payload", "derived indicators are not part of the default reading");

        // What IS on screen: the decision, the scale, the coverage meaning and the work.
        visible.Should().Contain("Release decision").And.Contain("Logical issues").And.Contain("Required coverage")
            .And.Contain("Recommended actions").And.Contain("Contrast (Minimum)");

        // The whole report is still reachable — one view away, not removed.
        page.Find("[data-testid=fqr-tab-evidence]").Click();
        VisibleText(page.Find("[data-testid=fqr-all-findings]"))
            .Should().Contain("/admin/general-roles").And.Contain("Large CSS payload");
    }

    // ── §45. Evidence listed once ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdenticalEvidenceRecordsAreListedOnceWithTheirSources()
    {
        var shared = "asset: app.js (4.2 MB)";
        var report = RealisticReport();
        var findings = report.Findings.Select(f => f.Id.StartsWith("rdy-risk")
            ? new FrontendQualityFinding
            {
                Id = f.Id, Title = f.Title, Category = f.Category, Severity = f.Severity, Description = f.Description,
                Recommendation = f.Recommendation, Evidence = [shared], SourceSystem = f.SourceSystem,
                EngineId = f.EngineId, SourceRuleId = f.SourceRuleId, Origin = f.Origin, Status = f.Status,
            }
            : f).ToList();
        var withShared = new FrontendQualityReviewReport
        {
            TargetUrl = report.TargetUrl, TargetEnvironment = report.TargetEnvironment, GeneratedAt = report.GeneratedAt,
            CompletedAt = report.CompletedAt, EngineOutcomes = report.EngineOutcomes, Coverage = report.Coverage,
            ReleaseDisposition = report.ReleaseDisposition, Findings = findings,
            LogicalIssues = FrontendQualityLogicalIssueGrouper.Group(findings), Wcag = report.Wcag,
            ActiveEngines = report.ActiveEngines,
        };

        var page = Result(withShared);
        page.Find("[data-testid=fqr-result-diagnostics-toggle]").Click();
        // Raw evidence is nested one level further and rendered only on request.
        page.FindAll("[data-testid=fqr-technical-evidence]").Should().BeEmpty();
        page.Find("[data-testid=fqr-technical-raw-toggle]").Click();

        var rows = page.Find("[data-testid=fqr-technical-evidence]").QuerySelectorAll("li")
            .Where(li => li.TextContent.Contains(shared)).ToList();

        // Three findings cited the same record; it is printed once, naming all three.
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("Large application JavaScript payload").And.Contain("Large CSS payload");
    }

    // ── Post-run cleanup: counts, derived indicators, compact key issues, contribution, overlap ────────────────────

    // §33, §71. QA Readiness is a derived assessment, not another engine domain, and names indicators, never source findings.
    [Fact]
    public void QaReadinessOffersIndicatorsNotSourceFindings()
    {
        var page = Result(RealisticReport());
        page.Find("[data-testid=fqr-domain-results] .fqr-domain-grid").QuerySelectorAll("[data-testid=fqr-domain-result]")
            .Should().NotContain(d => d.GetAttribute("data-category") == "Readiness");

        var derived = page.Find("[data-testid=fqr-derived-assessment]");
        derived.QuerySelector("h3")!.TextContent.Should().Be("Derived assessment");
        derived.TextContent.Should().Contain("No dedicated engine ran");
        var readiness = derived.QuerySelector("[data-testid=fqr-domain-result][data-category=Readiness]")!;
        readiness.QuerySelector("[data-testid=fqr-domain-result-count]")!.TextContent.Should().Be("3 indicators");
        readiness.TextContent.Should().NotContain("source finding");
    }

    // §60 / §7 Source findings and derived indicators counted apart; the total only as "recorded".
    [Fact]
    public void SourceFindingsAndDerivedIndicatorsAreCountedApart()
    {
        var report = RealisticReport();
        var page = Result(report);
        var source = report.Findings.Count(f => f.Origin == FrontendQualityFindingOrigin.Source);
        var derived = report.Findings.Count(f => f.Origin == FrontendQualityFindingOrigin.Derived);

        page.Find("[data-testid=fqr-metric-source]").TextContent.Trim().Should().Be(source.ToString());
        page.Find("[data-testid=fqr-metric-derived]").TextContent.Trim().Should().Be(derived.ToString());
        var hint = page.Find("[data-testid=fqr-result-details] .fqr-section-note").TextContent.Trim();
        hint.Should().StartWith($"{source} source findings · {derived} derived indicators · {source + derived} recorded");
        hint.Should().NotContain($"{source + derived} source findings").And.NotContain("observations from every engine");
    }

    // §61 / §49 Each kind under its own name: the Issues view counts actionable issues, derived indicators are counted in Evidence.
    [Fact]
    public void ReviewItemsCountEachKindByItsOwnName()
    {
        var report = RealisticReport();
        var page = Result(report);
        var view = FrontendQualityResultPresentation.Build(report);

        page.Find("[data-testid=fqr-metric-logical]").TextContent.Trim().Should().Be(view.LogicalIssueCount.ToString());
        page.Find("[data-testid=fqr-tab-issues]").TextContent.Trim().Should().Be($"Issues ({view.LogicalIssueCount})");
        page.Find("#fqr-issues-heading").TextContent.Trim().Should().Be($"Logical issues ({view.LogicalIssueCount})");
        page.Find("[data-testid=fqr-tabpanel-issues]").QuerySelectorAll("[data-testid=fqr-issue-derived]").Should().BeEmpty();
        page.Find("[data-testid=fqr-review-items-derived]").TextContent.Trim().Should().Be($"Derived indicators ({view.DerivedIndicatorCount})");
    }

    // §5 The metric note says why the logical-issue and source-finding counts differ.
    [Fact]
    public void MetricsExplainWhyLogicalAndSourceCountsDiffer()
    {
        var page = Result(RealisticReport());
        page.Find("[data-testid=fqr-result-metrics-note]").TextContent.Should().Contain("may map to the same logical issue")
            .And.Contain("not expected to match");
        // §26 The critical/high count is of logical issues, beside the logical-issue count it belongs to.
        page.Find("[data-testid=fqr-metric-critical-high-issues]").ParentElement!.QuerySelector("dt")!.TextContent.Should().Be("Critical/high issues");
        page.Find("[data-testid=fqr-metric-critical-high-issues]").TextContent.Trim().Should().Be("2", "contrast and the missing CSP");
    }

    // §62 / §10 Key issue cards are compact; details render only on request, without raw evidence.
    [Fact]
    public void KeyIssueCardsAreCompactUntilOpened()
    {
        var page = Result(RealisticReport());
        var key = page.Find("[data-testid=fqr-key-issues]");

        key.QuerySelectorAll("[data-testid=fqr-logical-issue]").Should().NotBeEmpty();
        key.QuerySelectorAll(".disclosure").Should().BeEmpty("no evidence disclosures are built into the key issue cards");
        key.QuerySelectorAll("[data-testid=fqr-issue-details], [data-testid=fqr-issue-page-list], code").Should().BeEmpty();
        key.QuerySelectorAll("[data-testid=fqr-issue-action]").Should().NotBeEmpty();

        var contrast = key.QuerySelectorAll("[data-testid=fqr-logical-issue]").First(i => i.TextContent.Contains("Contrast (Minimum)"));
        var toggle = contrast.QuerySelector("[data-testid=fqr-issue-details-toggle]")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();

        var details = page.Find("[data-testid=fqr-key-issues] [data-testid=fqr-issue-details]");
        details.TextContent.Should().Contain("Primary domain").And.Contain("Observed by").And.Contain("Affected pages").And.Contain("/admin/operations");
        page.Find("[data-testid=fqr-key-issues] [data-testid=fqr-issue-details-toggle][aria-expanded=true]").TextContent.Should().Be("Hide evidence");
        details.QuerySelectorAll("[data-testid=fqr-issue-source-list] code").Should().BeEmpty("sanitized raw evidence stays in the Evidence view");
    }

    // §63 / §14 CSP: one logical issue owned by Security; Standards says where its source finding went.
    [Fact]
    public void StandardsExplainsFindingsGroupedUnderSecurity()
    {
        var report = RealisticReport();
        var csp = report.LogicalIssues.Where(i => i.FindingInstances.Any(f => f.SourceFindingId is "sec-csp" or "std-csp")).ToList();
        csp.Should().ContainSingle("the CSP observations of two domains are one logical issue");
        csp[0].Category.Should().Be(FrontendQualityCategory.Security);
        csp[0].RelatedCategories.Should().Contain(FrontendQualityCategory.Standards);
        csp[0].FindingInstances.Should().HaveCount(2, "source evidence of both observations is preserved");

        var page = Result(report);
        var standards = page.FindAll("[data-testid=fqr-domain-result]").Single(d => d.GetAttribute("data-category") == "Standards");
        standards.QuerySelector("[data-testid=fqr-domain-result-count]")!.TextContent.Should().Be("0 logical issues · 1 source finding");
        standards.QuerySelector("[data-testid=fqr-domain-result-contribution]")!.TextContent.Trim()
            .Should().Be("1 source finding contributed to logical issues grouped under Security, where it is counted once.");
    }

    // A derived finding filed under a non-derived domain is not one of its source findings.
    [Fact]
    public void DomainSourceCountsExcludeDerivedFindings()
    {
        var report = RealisticReport();
        report.Findings.Add(Finding("api-r001", "OpenAPI description not published", FrontendQualityCategory.Standards,
            FrontendQualitySeverity.Low, engine: FrontendQualityEngineId.StaticSecurity, ruleId: "API-R001", origin: FrontendQualityFindingOrigin.Derived));
        var regrouped = FrontendQualityLogicalIssueGrouper.Group(report.Findings);
        report.LogicalIssues.Clear();
        report.LogicalIssues.AddRange(regrouped);

        var standards = FrontendQualityResultPresentation.Build(report).Domains.Single(d => d.Category == FrontendQualityCategory.Standards);
        standards.FindingCount.Should().Be(1, "only the CSP source finding");
        standards.CountLabel.Should().EndWith("· 1 derived indicator");
    }

    // §64 / §18 The accessibility overlap is stated where the numbers are, not only in a disclosure.
    [Fact]
    public void AccessibilityOverlapIsVisibleBesideTheCounts()
    {
        var page = Result(RealisticReport());
        var note = page.Find("[data-testid=fqr-a11y-overlap-note]");
        note.HasAttribute("hidden").Should().BeFalse();
        note.TextContent.Should().Contain("a criterion may have automated evidence and still require human assessment");
        page.Find("[data-testid=fqr-accessibility-summary]").TextContent.Should().NotContainAny("criteria failed", "of 48 failed");
        // §20 Automated failure EVIDENCE, never "recorded as failed": partial automation is not a criterion outcome.
        var accessibility = page.FindAll("[data-testid=fqr-domain-result]").Single(d => d.GetAttribute("data-category") == "Accessibility");
        accessibility.QuerySelector("[data-testid=fqr-domain-result-summary]")!.TextContent.Should()
            .Be("1 criterion has automated failure evidence. Manual assessment is still required.");
    }

    // §67 Every evidence disclosure starts collapsed.
    [Fact]
    public void ReviewDetailsStartCollapsed()
    {
        var page = Result(RealisticReport());
        var toggles = page.Find("[data-testid=fqr-tabpanel-evidence]").QuerySelectorAll(".disclosure > .disclosure-toggle");
        toggles.Should().NotBeEmpty();
        toggles.Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false");
    }

    // §73 The export uses the page's count model and the four completeness dimensions.
    [Fact]
    public void ExportCountsLogicalSourceDerivedAndInformationalApart()
    {
        var report = RealisticReport();
        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        var view = FrontendQualityResultPresentation.Build(report);

        html.Should().Contain($"<strong>Logical issues:</strong> {view.LogicalIssueCount}")
            .And.Contain($"<strong>Source findings:</strong> {view.SourceFindingCount}")
            .And.Contain($"<strong>Derived indicators:</strong> {view.DerivedIndicatorCount}")
            .And.Contain("may map to the same logical issue")
            .And.Contain("Required coverage:").And.Contain("Manual assessment:");
        html.Should().NotContain($"<strong>Source findings:</strong> {report.Findings.Count}<",
            "derived indicators are not source findings");
    }

    // §42 A public target does not need an authenticated context; "Not available" beside "Not required" read as a problem.
    [Fact]
    public void PublicTargetsSayTheAuthenticatedContextIsNotNeeded()
    {
        FrontendQualityTargetAccess.ApiContextLabel(new FrontendQualityTargetAccessContext
            { RequiresAuthentication = false, Method = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy })
            .Should().Be("Not used — review scope is public only");
        FrontendQualityTargetAccess.ApiContextLabel(new FrontendQualityTargetAccessContext
            { RequiresAuthentication = true, Method = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy })
            .Should().Be("Not available", "an authenticated target without a context is still reported as missing");
        FrontendQualityTargetAccess.ApiContextLabel(new FrontendQualityTargetAccessContext
            { RequiresAuthentication = false, Method = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy, ApiContextStatus = BirkNext.LocalHttpsProxy.AuthenticatedApiContextStatus.Available })
            .Should().Be("Available — memory only", "an available context is reported as it is");
    }
}
