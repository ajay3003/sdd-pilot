using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One post-run model, five separate answers: automated execution (required engines only), coverage (per surface, with the stored
/// reason), findings, manual assessment and the decision. Projected from the stored report alone, so a history run never changes.
/// </summary>
public sealed class FrontendQualityRunSemanticsTests
{
    private const string Url = "https://m2lbdev.example.test/";
    private const string HttpReq = "Public HTTP";
    private const string NoAuthDom = "Authenticated browser DOM unavailable: no signed-in browser context for this run.";

    private static FrontendQualityFinding Finding(string id, string title, FrontendQualityCategory category, FrontendQualitySeverity severity,
        FrontendQualityEngineId engine = FrontendQualityEngineId.StaticSecurity, string? rule = null,
        FrontendQualityFindingOrigin origin = FrontendQualityFindingOrigin.Source) => new()
        {
            Id = id, Title = title, Category = category, Severity = severity, Description = title, Recommendation = $"Resolve {title}.",
            SourceSystem = engine.ToString(), EngineId = engine, SourceRuleId = rule, Status = CheckExecutionStatus.Failed, Origin = origin,
        };

    private static FrontendQualitySurfacePlan SurfacePlan(FrontendQualityReviewSurface surface, FrontendQualityPlanDecision decision, bool primary, string reason = "") =>
        new(surface, decision, HttpReq, decision == FrontendQualityPlanDecision.Run ? "Backend HTTP" : null, reason, primary);

    /// <param name="scope">The scope the run was started with.</param>
    /// <param name="authenticatedDomCompleted">An engine completed on the authenticated surface (signed-in pages reviewed).</param>
    /// <param name="authenticatedApiChecks">Authenticated API checks executed during the run.</param>
    /// <param name="requiredCompleted">Both required engines completed.</param>
    /// <param name="optionalCompleted">The optional Accessibility engine completed.</param>
    private static FrontendQualityReviewReport Report(
        FrontendReviewAccessScope scope = FrontendReviewAccessScope.PublicAndAuthenticated,
        bool authenticatedDomCompleted = false, int authenticatedApiChecks = 0, bool requiredCompleted = true, bool optionalCompleted = false,
        IReadOnlyList<FrontendQualityFinding>? findings = null, IReadOnlyList<FrontendQualityReleaseBlocker>? blockers = null)
    {
        findings ??=
        [
            Finding("csp", "Content Security Policy header missing", FrontendQualityCategory.Security, FrontendQualitySeverity.Critical, rule: "HDR-MISSING-CONTENT-SECURITY-POLICY"),
            Finding("js", "Large application JavaScript payload", FrontendQualityCategory.Performance, FrontendQualitySeverity.Medium),
            Finding("qa-1", "Missing security headers raise release risk", FrontendQualityCategory.Readiness, FrontendQualitySeverity.Low, origin: FrontendQualityFindingOrigin.Derived),
        ];
        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);
        var authenticated = scope != FrontendReviewAccessScope.PublicOnly;
        var publicIncluded = scope != FrontendReviewAccessScope.AuthenticatedOnly;

        FrontendQualityEnginePlan Engine(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, params FrontendQualitySurfacePlan[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Policy = policy, Scope = scope, Prerequisite = HttpReq, Surfaces = surfaces, Applicable = true,
            Decision = surfaces.Any(s => s.Decision == FrontendQualityPlanDecision.Run) ? FrontendQualityPlanDecision.Run : FrontendQualityPlanDecision.NotAssessed,
        };
        var accessibilitySurfaces = new List<FrontendQualitySurfacePlan>();
        if (publicIncluded) accessibilitySurfaces.Add(SurfacePlan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true));
        if (authenticated) accessibilitySurfaces.Add(SurfacePlan(FrontendQualityReviewSurface.Authenticated,
            authenticatedDomCompleted ? FrontendQualityPlanDecision.Run : FrontendQualityPlanDecision.NotAssessed, !publicIncluded, authenticatedDomCompleted ? "" : NoAuthDom));

        var plan = new FrontendQualityExecutionPlan
        {
            Scope = scope,
            Engines =
            [
                Engine(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, SurfacePlan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true)),
                Engine(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required, SurfacePlan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true)),
                Engine(FrontendQualityEngineId.Accessibility, "Accessibility", FrontendQualityEngineRequirement.Optional, [.. accessibilitySurfaces]),
            ],
        };

        FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, bool completed, params FrontendQualitySurfaceResult[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Requirement = policy, Surfaces = [.. surfaces],
            ExecutionState = completed ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable,
        };
        FrontendQualitySurfaceResult Result(FrontendQualityReviewSurface surface, bool completed, bool primary, string reason = "") =>
            new(surface, completed ? "Completed" : "Not assessed", completed, completed ? "Backend HTTP" : null, reason, primary);

        var accessibilityResults = new List<FrontendQualitySurfaceResult>();
        if (publicIncluded) accessibilityResults.Add(Result(FrontendQualityReviewSurface.Public, optionalCompleted, true, optionalCompleted ? "" : "Browser DOM unavailable."));
        if (authenticated) accessibilityResults.Add(Result(FrontendQualityReviewSurface.Authenticated, authenticatedDomCompleted, !publicIncluded, authenticatedDomCompleted ? "" : NoAuthDom));
        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, requiredCompleted,
                Result(FrontendQualityReviewSurface.Public, requiredCompleted, true, requiredCompleted ? "" : "Target unreachable over public HTTP.")),
            Outcome(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required, true,
                Result(FrontendQualityReviewSurface.Public, true, true)),
            Outcome(FrontendQualityEngineId.Accessibility, "Accessibility", FrontendQualityEngineRequirement.Optional, optionalCompleted || authenticatedDomCompleted, [.. accessibilityResults]),
        };
        var actualBlockers = blockers?.ToList() ?? [];
        if (!requiredCompleted)
            actualBlockers.Add(new(FrontendQualityBlockerType.Coverage, "HTTP Security (Public)", "Target unreachable over public HTTP.", "Required engines must complete."));
        return new FrontendQualityReviewReport
        {
            TargetUrl = Url, GeneratedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            TargetEnvironment = new FrontendReviewTargetIdentity("dev", "M2LB DEV", "Development", Url, DateTime.UtcNow),
            EngineOutcomes = outcomes, Coverage = FrontendQualityCoverage.Evaluate(outcomes, plan), ExecutionPlan = plan,
            ReleaseBlockers = actualBlockers,
            ReleaseDisposition = actualBlockers.Count > 0 ? FrontendQualityReleaseDisposition.Blocked : FrontendQualityReleaseDisposition.ReviewRequired,
            Findings = [.. findings], LogicalIssues = issues,
            AuthenticatedApiSurface = authenticatedApiChecks == 0 ? null : new FrontendAuthenticatedApiSurfaceResult
            {
                ContextAvailable = true,
                Checks = [.. Enumerable.Range(0, authenticatedApiChecks).Select(i => new FrontendAuthenticatedApiCheck
                    { Label = $"check {i}", Url = $"https://api.example.test/{i}", Status = AuthenticatedExecutionStatus.Executed, StatusCode = 200 })],
            },
        };
    }

    private static FrontendQualityRunSemantics Run(FrontendQualityReviewReport report) => FrontendQualityRunSemanticsProjection.From(report);
    private static FrontendQualitySurfaceCoverage Surface(FrontendQualityRunSemantics run, FrontendQualityReviewSurface surface) => run.Surfaces.Single(s => s.Surface == surface);

    /// <summary>The reported run: 2/2 required, an optional engine missing, authenticated scope selected but not assessed.</summary>
    [Fact]
    public void RequiredComplete_OptionalAndAuthenticatedGaps_AreCoverage_NotIncompleteExecution()
    {
        var report = Report();
        var view = FrontendQualityResultPresentation.Build(report);
        var run = FrontendQualityRunSemanticsProjection.From(report, view);

        view.State.Should().NotBe(FrontendQualityResultState.Incomplete, "a coverage-pass gap is never execution state");
        run.Automation.Should().Be(FrontendQualityAutomationState.Complete);
        run.AutomationDetail.Should().StartWith("2 of 2 required engines completed");
        run.Coverage.Should().Be(FrontendQualityCoverageLevel.Partial);
        run.FindingsLabel.Should().Be("Issues identified");
        run.DecisionLabel.Should().Be("Review required");
        run.Headline.Should().StartWith("Automated review complete");
        Surface(run, FrontendQualityReviewSurface.Public).State.Should().Be(FrontendQualitySurfaceCoverageState.Assessed);
        Surface(run, FrontendQualityReviewSurface.Authenticated).State.Should().Be(FrontendQualitySurfaceCoverageState.NotAssessed);
        Surface(run, FrontendQualityReviewSurface.Authenticated).Reason.Should().Be(NoAuthDom, "the stored reason, never an invented one");
        run.CoverageReasons.Should().Contain(r => r.StartsWith("Optional automation: 1 of 1 optional engine did not complete (Accessibility)"));
    }

    [Fact]
    public void PublicOnlyScope_AuthenticatedSurface_IsNotInSelectedScope()
    {
        var run = Run(Report(FrontendReviewAccessScope.PublicOnly, optionalCompleted: true));
        Surface(run, FrontendQualityReviewSurface.Authenticated).State.Should().Be(FrontendQualitySurfaceCoverageState.NotInSelectedScope);
        Surface(run, FrontendQualityReviewSurface.Authenticated).StateLabel.Should().Be("Not in selected scope");
        run.CoverageReasons.Should().NotContain(r => r.StartsWith("Authenticated"), "an excluded surface is not a coverage gap");
    }

    [Fact]
    public void AuthenticatedApiEvidenceWithoutSignedInPages_IsPartiallyAssessed()
    {
        var run = Run(Report(authenticatedApiChecks: 2));
        var authenticated = Surface(run, FrontendQualityReviewSurface.Authenticated);
        authenticated.State.Should().Be(FrontendQualitySurfaceCoverageState.PartiallyAssessed);
        authenticated.Reason.Should().StartWith("Authenticated API checks executed (2); signed-in pages were not assessed");
    }

    [Fact]
    public void AllSelectedAuthenticatedCapabilitiesRan_IsAssessed()
    {
        var run = Run(Report(authenticatedDomCompleted: true, optionalCompleted: true));
        Surface(run, FrontendQualityReviewSurface.Authenticated).State.Should().Be(FrontendQualitySurfaceCoverageState.Assessed);
    }

    [Fact]
    public void RequiredEngineFailed_IsAutomationPartial_AndCoverageNeverComplete()
    {
        var run = Run(Report(requiredCompleted: false));
        run.Automation.Should().Be(FrontendQualityAutomationState.Partial);
        run.Headline.Should().Be("Automated review incomplete — 1 of 2 required engines completed");
        run.Coverage.Should().NotBe(FrontendQualityCoverageLevel.Complete);
        run.CoverageReasons[0].Should().StartWith("Required automation incomplete");
    }

    /// <summary>Severity, the automatic policy and the human decision are three different facts.</summary>
    [Fact]
    public void CriticalIssue_NotBlockedByPolicy_StillNeedsHumanReview()
    {
        var report = Report();
        var critical = report.LogicalIssues.Single(i => i.PrimarySeverity == FrontendQualitySeverity.Critical);
        var impact = FrontendQualityPostRunPresentation.IssueImpact(report, critical);

        impact.Should().Be(FrontendQualityReleaseImpact.NonBlocking);
        FrontendQualityPostRunPresentation.IssueImpactLabel(impact).Should().Be("Not an automatic release blocker");
        FrontendQualityPostRunPresentation.RequiresHumanReview(critical, impact).Should().BeTrue();
        Run(report).DecisionReasons.Should().Contain(r => r.Contains("critical/high logical issue") && r.Contains("not an automatic release blocker"));
    }

    [Fact]
    public void Counts_SourceAndDerived_AreRecordedObservations_GroupedIntoLogicalIssues()
    {
        var run = Run(Report());
        (run.SourceFindings, run.DerivedIndicators, run.RecordedObservations).Should().Be((2, 1, 3));
        run.CountRelationship.Should().Be($"2 source findings and 1 derived indicator were recorded (3 observations); related evidence was grouped into {run.LogicalIssues} logical issues.");
    }

    [Fact]
    public void EvidenceSource_NamesProviderAndHowItWasObserved()
    {
        FrontendQualityPostRunPresentation.EvidenceSource(Finding("cfg", "Development configuration file publicly accessible", FrontendQualityCategory.Security,
                FrontendQualitySeverity.High, FrontendQualityEngineId.StaticSecurity, "WASM-DEV-CONFIG"))
            .Should().StartWith("HTTP Security · HTTP responses and assets fetched from the deployed application").And.Contain("rule WASM-DEV-CONFIG");
        FrontendQualityPostRunPresentation.EvidenceSource(Finding("bq", "Contrast (Minimum)", FrontendQualityCategory.Accessibility,
                FrontendQualitySeverity.High, FrontendQualityEngineId.BrowserQuality, "text-contrast"))
            .Should().StartWith("Browser Companion");
    }

    /// <summary>axe color-contrast and the Companion's text-contrast check are one WCAG 1.4.3 issue with both observations.</summary>
    [Fact]
    public void ContrastFromAxeAndCompanion_IsOneLogicalIssue_RelatedToWcag143()
    {
        var issues = FrontendQualityLogicalIssueGrouper.Group(
        [
            Finding("axe", "Elements must meet minimum color contrast ratio thresholds", FrontendQualityCategory.Accessibility, FrontendQualitySeverity.High, FrontendQualityEngineId.Accessibility, "color-contrast"),
            Finding("bq", "Contrast (Minimum) — /start", FrontendQualityCategory.Accessibility, FrontendQualitySeverity.High, FrontendQualityEngineId.BrowserQuality, "text-contrast"),
        ]);
        var contrast = issues.Should().ContainSingle().Subject;
        contrast.FindingInstances.Should().HaveCount(2);
        FrontendQualityPostRunPresentation.WcagCriteria(contrast).Should().Equal("1.4.3 Contrast (Minimum)");
    }

    /// <summary>A stored run reads the same after a JSON round trip (as history stores it): nothing is recomputed from current state.</summary>
    [Fact]
    public void HistoryRun_ProjectsIdentically_FromItsStoredReport()
    {
        var report = Report();
        var stored = JsonSerializer.Deserialize<FrontendQualityReviewReport>(JsonSerializer.Serialize(report))!;
        var before = Run(report);
        var after = Run(stored);
        (after.Automation, after.Coverage, after.Headline, after.LogicalIssues).Should().Be((before.Automation, before.Coverage, before.Headline, before.LogicalIssues));
        Surface(after, FrontendQualityReviewSurface.Authenticated).Should().BeEquivalentTo(Surface(before, FrontendQualityReviewSurface.Authenticated));
    }

    [Fact]
    public void Export_UsesTheSameSemantics_NeverIncomplete()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(Report(), null);
        html.Should().Contain("Automation complete").And.Contain("Coverage partial").And.Contain("Authenticated surface:").And.Contain(NoAuthDom)
            .And.Contain("Not an automatic release blocker").And.NotContain(">Incomplete<");
    }
}
