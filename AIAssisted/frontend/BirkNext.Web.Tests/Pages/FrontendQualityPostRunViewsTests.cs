using AngleSharp.Dom;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The five post-run views — Summary, Issues, Coverage, Evidence, History — over a Public + authenticated run whose authenticated surface
/// could not be assessed: HTTP Security and HTTP Performance (Required, Public HTTP) completed on the public surface, Accessibility
/// (Optional, Browser DOM) was Not assessed, and the release decision follows only from the exact blockers the report carries.
/// </summary>
public sealed class FrontendQualityPostRunViewsTests : BunitContext
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

    private static FrontendQualityFinding Finding(string id, string title, FrontendQualityCategory category, FrontendQualitySeverity severity,
        string? ruleId = null) => new()
        {
            Id = id, Title = title, Category = category, Severity = severity, Description = title,
            Recommendation = $"Resolve {title}.", SourceSystem = "StaticSecurity", EngineId = FrontendQualityEngineId.StaticSecurity,
            SourceRuleId = ruleId, Status = CheckExecutionStatus.Failed,
        };

    private static FrontendQualitySurfacePlan Surface(FrontendQualityReviewSurface surface, FrontendQualityPlanDecision decision, string prerequisite,
        bool primary, string reason = "") => new(surface, decision, prerequisite, decision == FrontendQualityPlanDecision.Run ? "Backend HTTP" : null, reason, primary);

    private static FrontendQualityEnginePlan Plan(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, string prerequisite,
        params FrontendQualitySurfacePlan[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Policy = policy, Scope = FrontendReviewAccessScope.PublicAndAuthenticated,
            Prerequisite = prerequisite, Surfaces = surfaces, Applicable = true,
            Decision = surfaces.Any(s => s.Decision == FrontendQualityPlanDecision.Run) ? FrontendQualityPlanDecision.Run : FrontendQualityPlanDecision.NotAssessed,
        };

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy,
        FrontendQualityEngineExecutionState state, params FrontendQualitySurfaceResult[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Requirement = policy, ExecutionState = state, Surfaces = [.. surfaces],
        };

    private const string DomNotAvailable = "Browser DOM unavailable for this run.";

    /// <param name="requiredCompleted">False: HTTP Security did not complete on the public surface — a genuine coverage blocker.</param>
    /// <param name="policyBlocksCsp">The Target Environment release policy names the CSP issue as release-blocking.</param>
    /// <param name="accessibilityPlannedToRun">A public DOM was resolved, so Accessibility was planned to run on Public — and then did not complete.</param>
    private static FrontendQualityReviewReport Report(bool requiredCompleted = true, bool policyBlocksCsp = false, DateTime? completedAt = null,
        bool accessibilityPlannedToRun = false)
    {
        var findings = new List<FrontendQualityFinding>
        {
            Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security, FrontendQualitySeverity.High, "HDR-MISSING-CONTENT-SECURITY-POLICY"),
            Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards, FrontendQualitySeverity.Critical, "std-csp-missing"),
            Finding("perf-js", "Large application JavaScript payload", FrontendQualityCategory.Performance, FrontendQualitySeverity.Medium),
        };
        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);
        var csp = issues.Single(i => i.Category == FrontendQualityCategory.Security);

        var publicHttp = "Public HTTP";
        var plan = new FrontendQualityExecutionPlan
        {
            Scope = FrontendReviewAccessScope.PublicAndAuthenticated,
            Engines =
            [
                Plan(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, publicHttp,
                    Surface(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, publicHttp, true),
                    Surface(FrontendQualityReviewSurface.Authenticated, FrontendQualityPlanDecision.NotAssessed, publicHttp, false, "No authenticated context.")),
                Plan(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required, publicHttp,
                    Surface(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, publicHttp, true)),
                Plan(FrontendQualityEngineId.Accessibility, "Accessibility", FrontendQualityEngineRequirement.Optional, "Browser DOM",
                    Surface(FrontendQualityReviewSurface.Public, accessibilityPlannedToRun ? FrontendQualityPlanDecision.Run : FrontendQualityPlanDecision.NotAssessed,
                        "Browser DOM", true, accessibilityPlannedToRun ? "" : DomNotAvailable)),
            ],
        };

        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required,
                requiredCompleted ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable,
                new FrontendQualitySurfaceResult(FrontendQualityReviewSurface.Public, requiredCompleted ? "Completed" : "Not assessed", requiredCompleted, "Backend HTTP",
                    requiredCompleted ? "" : "Target unreachable over public HTTP.", true)),
            Outcome(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required, FrontendQualityEngineExecutionState.Assessed,
                new FrontendQualitySurfaceResult(FrontendQualityReviewSurface.Public, "Completed", true, "Backend HTTP", "", true)),
            Outcome(FrontendQualityEngineId.Accessibility, "Accessibility", FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable,
                new FrontendQualitySurfaceResult(FrontendQualityReviewSurface.Public, "Not assessed", false, null, DomNotAvailable, true)),
        };
        var coverage = FrontendQualityCoverage.Evaluate(outcomes, plan);

        var blockers = new List<FrontendQualityReleaseBlocker>();
        if (!requiredCompleted)
            blockers.Add(new(FrontendQualityBlockerType.Coverage, "HTTP Security (Public)", "Target unreachable over public HTTP.", "Required engines must complete."));
        if (policyBlocksCsp)
            blockers.Add(new(FrontendQualityBlockerType.IssuePolicy, csp.CanonicalTitle, "High logical issue matched the release policy.", FrontendQualityDecisionSupportService.IssuePolicyRule));

        return new FrontendQualityReviewReport
        {
            TargetUrl = Url,
            TargetEnvironment = new FrontendReviewTargetIdentity("dev", "M2LB DEV", "Development", Url, DateTime.UtcNow),
            GeneratedAt = completedAt ?? DateTime.UtcNow, CompletedAt = completedAt ?? DateTime.UtcNow,
            EngineOutcomes = outcomes, Coverage = coverage, ExecutionPlan = plan, ReleaseBlockers = blockers,
            ReleaseDisposition = blockers.Count > 0 ? FrontendQualityReleaseDisposition.Blocked : FrontendQualityReleaseDisposition.NoAutomatedBlockDetected,
            Findings = findings, LogicalIssues = issues,
            ActiveEngines = FrontendQualityActiveEngines.Resolve(Context()),
        };
    }

    private Mock<IFrontendQualityReviewOrchestrator> Register(bool withHistory = false)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context());
        var orchestrator = new Mock<IFrontendQualityReviewOrchestrator>();
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(orchestrator.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        if (withHistory) Services.AddSingleton<FrontendQualityReviewHistory>();
        return orchestrator;
    }

    private static void Returns(Mock<IFrontendQualityReviewOrchestrator> orchestrator, FrontendQualityReviewReport report) =>
        orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityReviewOrchestrationResult(QualityReport: report));

    private IRenderedComponent<FrontendQualityReview> Result(FrontendQualityReviewReport report)
    {
        Returns(Register(), report);
        var page = Render<FrontendQualityReview>();
        page.Find("[data-testid=fqr-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-result-summary]"));
        return page;
    }

    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    // ── Summary: the decision and its exact causal chain ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACompletedPublicSurface_SatisfiesRequiredCoverage_AndAMissingOptionalDomIsNonBlocking()
    {
        var page = Result(Report());

        page.Find("[data-testid=fqr-release-label]").TextContent.Trim().Should().Be("No automated block detected");
        page.FindAll("[data-testid=fqr-release-blockers]").Should().BeEmpty();
        page.Find("[data-testid=fqr-completeness-required]").TextContent.Should().Contain("2 of 2 completed");
        page.Find("[data-testid=fqr-completeness-required-meaning]").TextContent.Trim().Should().Be("Satisfied");
        page.Find("[data-testid=fqr-completeness-optional]").TextContent.Should().Contain("0 of 1 completed");
        page.Find("[data-testid=fqr-completeness-optional-meaning]").TextContent.Trim().Should().Be("Non-blocking");

        var surfaces = page.FindAll("[data-testid=fqr-result-summary] [data-testid=fqr-surface-status]");
        surfaces.Select(s => s.GetAttribute("data-surface")).Should().Equal("Public", "Authenticated");
        surfaces[0].TextContent.Should().Contain("Completed").And.Contain("2 of 2 engines completed");
        surfaces[1].TextContent.Should().Contain("Not assessed");
    }

    [Fact]
    public void ARequiredEngineThatDidNotComplete_IsNamedAsACoverageBlocker_WithItsReason()
    {
        var page = Result(Report(requiredCompleted: false));
        var release = page.Find("[data-testid=fqr-release]");

        release.QuerySelector("[data-testid=fqr-release-label]")!.TextContent.Trim().Should().Be("Blocked");
        release.QuerySelector("[data-testid=fqr-release-primary]")!.TextContent.Should().Contain("Required coverage incomplete");
        release.QuerySelectorAll("[data-testid=fqr-blocker-type]").Select(t => t.TextContent.Trim()).Should().Equal("Coverage blocker");
        release.QuerySelector("[data-testid=fqr-blocker]")!.TextContent.Should()
            .Contain("HTTP Security (Public)").And.Contain("Target unreachable over public HTTP.").And.Contain("Required engines must complete.");
        page.Find("[data-testid=fqr-completeness-required-meaning]").TextContent.Trim().Should().Be("Blocking");
        // Critical/high issues exist but the policy names none of them: severity is said not to block on its own.
        release.QuerySelector("[data-testid=fqr-release-severity-note]")!.TextContent.Should().Be(FrontendQualityDecisionSupportService.SeverityNotBlocking);
    }

    [Fact]
    public void AnIssuePolicyBlocker_IsTheOnlyWayAnIssueBlocks_AndItsCardSaysSo()
    {
        var page = Result(Report(policyBlocksCsp: true));

        page.Find("[data-testid=fqr-release-primary]").TextContent.Should().Contain("Release policy blocks a logical issue");
        page.FindAll("[data-testid=fqr-blocker-type]").Select(t => t.GetAttribute("data-type")).Should().Equal("IssuePolicy");
        page.FindAll("[data-testid=fqr-release-severity-note]").Should().BeEmpty();

        var cards = page.Find("[data-testid=fqr-tabpanel-issues]").QuerySelectorAll("[data-testid=fqr-logical-issue]");
        cards.Single(c => c.TextContent.Contains("Content-Security-Policy")).QuerySelector("[data-testid=fqr-issue-impact]")!
            .TextContent.Trim().Should().Be("Blocks release (policy)");
        cards.Single(c => c.TextContent.Contains("JavaScript payload")).QuerySelector("[data-testid=fqr-issue-impact]")!
            .TextContent.Trim().Should().Be("Does not block release");

        // Release impact filter.
        page.Find("[data-testid=fqr-issue-filter-impact]").Change(nameof(FrontendQualityReleaseImpact.Blocking));
        page.Find("[data-testid=fqr-tabpanel-issues]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Should().ContainSingle().Which.TextContent.Should().Contain("Content-Security-Policy");
    }

    // ── Coverage: plan-driven engine table, surfaces, access ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EngineCoverageTable_ReadsPolicySurfacePrerequisiteResultAndReleaseImpactFromThePlan()
    {
        var page = Result(Report());
        var rows = page.FindAll("[data-testid=fqr-engine-coverage-row]");

        rows.Select(r => r.GetAttribute("data-engine")).Should().Equal("HTTP Security", "HTTP Performance", "Accessibility");
        string Cell(IElement row, string label) => row.QuerySelector($"[data-label='{label}']")!.TextContent.Trim();

        var staticSecurity = rows[0];
        Cell(staticSecurity, "Policy").Should().Be("Required");
        Cell(staticSecurity, "Surface").Should().Be("Public", "a Required Public HTTP engine runs on the public surface only");
        Cell(staticSecurity, "Prerequisite").Should().StartWith("Public HTTP");
        Cell(staticSecurity, "Result").Should().Be("Completed");
        Cell(staticSecurity, "Release impact").Should().Be("Satisfied");

        var accessibility = rows[2];
        Cell(accessibility, "Policy").Should().Be("Optional");
        Cell(accessibility, "Prerequisite").Should().Be("Browser DOM");
        Cell(accessibility, "Result").Should().Contain("Not assessed").And.Contain(DomNotAvailable).And.NotContain("No evidence");
        Cell(accessibility, "Release impact").Should().Be("Non-blocking");

        // Table headers are real column headers; row headers name the engine.
        page.FindAll("[data-testid=fqr-engine-coverage-table] thead th[scope=col]").Select(h => h.TextContent)
            .Should().Equal("Engine", "Policy", "Surface", "Prerequisite", "Result", "Release impact");
    }

    [Fact]
    public void CoverageView_StatesScopeAndEverySurface_AndKeepsDiagnosticsCollapsed()
    {
        var page = Result(Report());
        var coverage = page.Find("[data-testid=fqr-tabpanel-coverage]");

        coverage.QuerySelector("[data-testid=fqr-review-scope]")!.TextContent.Should().Be(FrontendQualityReviewScopes.Label(FrontendReviewAccessScope.PublicAndAuthenticated));
        coverage.QuerySelectorAll("[data-testid=fqr-coverage-surface]").Select(s => s.QuerySelector("strong")!.TextContent)
            .Should().Equal("Completed", "Not assessed");
        page.Find("[data-testid=fqr-engine-coverage-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    // ── Duplication: one canonical issue, referenced elsewhere ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCspIssue_IsOnePreviewInSummary_OneCardInIssues_AndItsSourceFindingsNameItInEvidence()
    {
        var report = Report();
        var csp = report.LogicalIssues.Single(i => i.Category == FrontendQualityCategory.Security);
        var page = Result(report);

        page.Find("[data-testid=fqr-tabpanel-summary]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Count(i => i.GetAttribute("data-logical-id") == csp.LogicalId).Should().Be(1);
        page.Find("[data-testid=fqr-tabpanel-issues]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Count(i => i.GetAttribute("data-logical-id") == csp.LogicalId).Should().Be(1);
        page.Find("[data-testid=fqr-tabpanel-coverage]").QuerySelectorAll("[data-testid=fqr-logical-issue]").Should().BeEmpty();
        page.Find("[data-testid=fqr-tabpanel-evidence]").QuerySelectorAll("[data-testid=fqr-logical-issue]")
            .Should().NotContain(i => i.GetAttribute("data-logical-id") == csp.LogicalId);

        // Both CSP source findings (Security and Standards) are grouped into the one issue: 3 findings, 2 issues.
        var grouped = page.FindAll("[data-testid=fqr-source-finding]")
            .Where(r => r.QuerySelector("[data-testid=fqr-finding-grouped-into]")!.TextContent == csp.CanonicalTitle).ToList();
        grouped.Should().HaveCount(2);
        report.LogicalIssues.Count(i => i.IsActionable).Should().Be(2);

        // Recommendations reference the issue by title; they never repeat its card.
        page.Find("[data-testid=fqr-recommendation-themes]").QuerySelectorAll("[data-testid=fqr-issue-action]").Should().BeEmpty();
    }

    // ── Buttons, export, tabs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RunAgainIsTheOnlyPrimaryAction_BackIsSecondary_AndExportIsOneMenu()
    {
        var page = Result(Report());
        var header = page.Find(".fqr-page").FirstElementChild!;

        header.QuerySelectorAll(".btn-primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Run again");
        page.Find("[data-testid=fqr-back]").ClassList.Should().Contain("btn-secondary");
        page.FindAll("button").Where(b => b.TextContent.Trim() is "Export HTML" or "Export PDF").Should().BeEmpty();

        var toggle = page.Find("[data-testid=fqr-export-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        page.FindAll("[data-testid=fqr-export] .export-menu-item").Select(i => i.TextContent.Trim()).Should().Equal("HTML", "PDF (print)");
    }

    [Fact]
    public void Tabs_FollowTheAriaTabsPattern_AndArrowKeysMoveTheSelection()
    {
        var page = Result(Report());
        var summary = page.Find("[data-testid=fqr-tab-summary]");

        summary.GetAttribute("aria-selected").Should().Be("true");
        summary.GetAttribute("tabindex").Should().Be("0");
        page.Find("[data-testid=fqr-tab-issues]").GetAttribute("tabindex").Should().Be("-1");
        page.Find("#fqr-tabpanel-summary").GetAttribute("aria-labelledby").Should().Be("fqr-tab-summary");

        summary.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        page.Find("[data-testid=fqr-tab-issues]").GetAttribute("aria-selected").Should().Be("true");
        page.Find("#fqr-tabpanel-issues").HasAttribute("hidden").Should().BeFalse();
        page.Find("#fqr-tabpanel-summary").HasAttribute("hidden").Should().BeTrue();

        page.Find("[data-testid=fqr-tab-issues]").KeyDown(new KeyboardEventArgs { Key = "End" });
        page.Find("[data-testid=fqr-tab-history]").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public void ThePrimarySummary_DoesNotShowRawScoresTargetUrlOrSourceFindingsByDefault()
    {
        var page = Result(Report());
        var visible = VisibleText(page.Find(".fqr-page"));

        visible.Should().NotContain(Url, "run details are one disclosure away");
        visible.Should().NotContain("Large application JavaScript payload — ").And.NotContain("Grouped into");
        page.Find("[data-testid=fqr-run-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void Export_CarriesTheSameBlockersCoverageMeaningAndEngineTable()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(Report(requiredCompleted: false), "Test");

        html.Should().Contain("<strong>Primary blocker:</strong> Required coverage incomplete")
            .And.Contain("<h3>Coverage blocker</h3>")
            .And.Contain("<strong>HTTP Security (Public)</strong> — Target unreachable over public HTTP.")
            .And.Contain("1 of 2 completed — Blocking").And.Contain("0 of 1 completed — Non-blocking")
            .And.Contain("<th>Release impact</th>")
            .And.Contain(FrontendQualityDecisionSupportService.SeverityNotBlocking);
    }

    [Fact]
    public void ASurfaceWhoseRequiredEnginesCompleted_SaysSo_WhenAnOptionalPlannedEngineDidNot()
    {
        var statuses = FrontendQualityPostRunPresentation.SurfaceStatuses(Report(accessibilityPlannedToRun: true));

        statuses[0].Status.Should().Be("Required engines completed");
        statuses[0].Detail.Should().Be("2 of 3 engines completed; optional engines not completed do not block");
        FrontendQualityPostRunPresentation.SurfaceStatuses(Report(requiredCompleted: false))[0].Status.Should().Be("Partially completed");
    }

    [Fact]
    public void EngineRows_UseThePrimarySurfacesOwnPrerequisite_AndTheFirstNonEmptyReason()
    {
        var report = Report();
        var plan = report.ExecutionPlan!;
        var authOnly = new FrontendQualityReviewReport
        {
            TargetUrl = Url, EngineOutcomes = [Outcome(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required,
                FrontendQualityEngineExecutionState.Unavailable,
                new FrontendQualitySurfaceResult(FrontendQualityReviewSurface.Authenticated, "Not assessed", false, null, "", true))],
            ExecutionPlan = plan with
            {
                Scope = FrontendReviewAccessScope.AuthenticatedOnly,
                Engines = [Plan(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, "Public HTTP",
                    Surface(FrontendQualityReviewSurface.Authenticated, FrontendQualityPlanDecision.NotAssessed, "Authenticated HTTP/API context", true, "No authenticated context."))],
            },
        };

        var row = FrontendQualityPostRunPresentation.EngineRows(authOnly).Single();
        row.Prerequisite.Should().Be("Authenticated HTTP/API context");
        row.Reason.Should().Be("No authenticated context.", "an empty surface result reason never hides the plan's");
        row.ReleaseImpactLabel.Should().Be("Blocking");

        // With no reason text anywhere, the typed outcome reason still explains a planned engine that did not complete.
        var silent = new FrontendQualityReviewReport
        {
            TargetUrl = Url, ExecutionPlan = plan,
            EngineOutcomes = [new FrontendQualityEngineOutcome
            {
                EngineId = FrontendQualityEngineId.StaticSecurity, DisplayName = "HTTP Security", Enabled = true,
                Requirement = FrontendQualityEngineRequirement.Required, ExecutionState = FrontendQualityEngineExecutionState.Unavailable,
                OutcomeReason = FrontendQualityEngineOutcomeReason.ReadinessUnavailable,
                Surfaces = [new FrontendQualitySurfaceResult(FrontendQualityReviewSurface.Public, "Not assessed", false, null, "", true)],
            }],
        };
        FrontendQualityPostRunPresentation.EngineRows(silent).First().Reason.Should()
            .Be(FrontendQualityEngineOutcomePresentation.GetLabel(FrontendQualityEngineOutcomeReason.ReadinessUnavailable));
    }

    // ── History ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_ListsEachRun_AndOpeningOneShowsItsStoredResultUnchanged()
    {
        var orchestrator = Register(withHistory: true);
        var blocked = Report(requiredCompleted: false, completedAt: new DateTime(2026, 10, 21, 9, 59, 0, DateTimeKind.Utc));
        var clean = Report(completedAt: new DateTime(2026, 10, 21, 11, 0, 0, DateTimeKind.Utc));

        Returns(orchestrator, blocked);
        var page = Render<FrontendQualityReview>();
        page.Find("[data-testid=fqr-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-result-summary]"));
        Returns(orchestrator, clean);
        await page.InvokeAsync(() => page.Find("[data-testid=fqr-run-again]").Click());
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-release-label]").TextContent.Trim().Should().Be("No automated block detected"));

        page.Find("[data-testid=fqr-tab-history]").Click();
        var runs = page.FindAll("[data-testid=fqr-history-run]");
        runs.Should().HaveCount(2);
        runs[0].QuerySelector("[data-testid=fqr-history-decision]")!.TextContent.Should().Be("No automated block detected");
        runs[0].QuerySelector("[data-testid=fqr-history-showing]").Should().NotBeNull("the newest run is the one on screen");
        runs[1].QuerySelector("[data-testid=fqr-history-scope]")!.TextContent.Should().Be(FrontendQualityReviewScopes.Label(FrontendReviewAccessScope.PublicAndAuthenticated));
        runs[1].QuerySelector("[data-testid=fqr-history-decision]")!.TextContent.Should().Be("Blocked");
        runs[1].QuerySelector("[data-testid=fqr-history-issues]")!.TextContent.Should().Be("2 issues");
        runs[1].QuerySelector("[data-testid=fqr-history-required]")!.TextContent.Should().Be("1/2 required");

        runs[1].QuerySelector("[data-testid=fqr-history-open]")!.Click();
        page.Find("[data-testid=fqr-history-opened]").TextContent.Should().Contain("Previous run").And.Contain("not recomputed");
        page.Find("[data-testid=fqr-tab-summary]").GetAttribute("aria-selected").Should().Be("true");
        page.Find("[data-testid=fqr-release-label]").TextContent.Trim().Should().Be("Blocked");
        page.Find("[data-testid=fqr-blocker]").TextContent.Should().Contain("HTTP Security (Public)");

        page.Find("[data-testid=fqr-history-return]").Click();
        page.Find("[data-testid=fqr-release-label]").TextContent.Trim().Should().Be("No automated block detected");
        page.FindAll("[data-testid=fqr-history-opened]").Should().BeEmpty();
    }

    [Fact]
    public async Task HistoryStore_KeepsTheNewestTenRunsPerTargetEnvironment_AndPersistsThemLocally()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var history = new FrontendQualityReviewHistory(JSInterop.JSRuntime);
        for (var i = 0; i < 12; i++)
            await history.AddAsync("dev", Report(completedAt: new DateTime(2026, 10, 21, 8, i, 0, DateTimeKind.Utc)));
        await history.AddAsync("qa", Report());

        history.For("dev").Should().HaveCount(FrontendQualityReviewHistory.MaxRuns);
        history.For("dev")[0].CompletedAt.Minute.Should().Be(11, "newest first");
        history.For("dev")[^1].CompletedAt.Minute.Should().Be(2, "the two oldest were dropped");
        history.For("qa").Should().ContainSingle("history is per Target Environment");
        JSInterop.Invocations.Where(c => c.Identifier == "birkNextStorage.setItem")
            .Select(c => c.Arguments[0]).Should().Contain("birknext:fqr-history:dev").And.Contain("birknext:fqr-history:qa");
        ApplicationRuntimeResetService.ProjectStoragePrefixes.Should().Contain("birknext:fqr-history:", "Local Data Reset clears it");

        history.ResetForLocalDataReset();
        history.For("dev").Should().BeEmpty();
    }
}
