using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// FQR hardening invariant: missing optional engines never reduce readiness, coverage or the decision unless policy makes them required;
/// available evidence is never executed assessment; authenticated API evidence is never authenticated browser or DOM evidence. One canonical
/// model (FrontendQualityRunSemantics) feeds summary, coverage and export, so they agree.
/// </summary>
public sealed class FrontendQualityHardeningScenarioTests
{
    private static FrontendQualityTargetAccessContext ProxyApiOnly() => new()
    {
        ReviewScope = FrontendReviewAccessScope.PublicAndAuthenticated, RequiresAuthentication = true, Method = AuthenticatedTestingMethod.LocalHttpsProxy,
        AuthenticatedApiAvailable = true, AuthenticatedBrowserSessionAvailable = false, AuthenticatedBrowserDomAvailable = false, TargetUrl = "https://m2lbdev.example.test/",
    };

    private static FrontendQualityCapabilityRow Row(FrontendQualityEngineId id, FrontendQualityEngineRequirement policy, FrontendQualityCapabilityState state) =>
        new(id, id.ToString(), policy, state, null, null, Enabled: true);

    // A — required available, optional unavailable: neutral, no penalty, no finding.
    [Fact]
    public void A_optional_unavailable_is_neutral_before_and_after_the_run()
    {
        var rows = new[]
        {
            Row(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled),
            Row(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled),
            Row(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, FrontendQualityCapabilityState.Unavailable),
            Row(FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineRequirement.Optional, FrontendQualityCapabilityState.Unavailable),
        };
        var summary = new FrontendQualityPreRunEngineSummary(rows);
        summary.RequiredLine.Should().Be("Required: 2/2 available");
        summary.OptionalLine.Should().Be("Optional: 0 available · 2 unavailable (non-blocking)");
        summary.RequiredLimitations.Should().BeEmpty();

        var report = FrontendQualityRunSemanticsTests.Report(FrontendReviewAccessScope.PublicOnly);
        var run = FrontendQualityRunSemanticsProjection.From(report);
        run.Automation.Should().Be(FrontendQualityAutomationState.Complete);
        run.CoverageReasons.Should().NotContain(r => r.Contains("Optional", StringComparison.OrdinalIgnoreCase));
        run.DecisionReasons.Should().NotContain(r => r.Contains("Optional", StringComparison.OrdinalIgnoreCase));
        run.OptionalExecutionDetail.Should().EndWith("non-blocking");
        FrontendQualityResultPresentation.Build(report).Release!.Reasons.Should().NotContain(r => r.Contains("Optional evidence"));
        report.Findings.Should().NotContain(f => f.Title.Contains("unavailable", StringComparison.OrdinalIgnoreCase), "unavailability is never a finding");
    }

    // B — authenticated API only.
    [Fact]
    public void B_authenticated_api_only_never_reads_as_authenticated_frontend()
    {
        var report = FrontendQualityRunSemanticsTests.Report(authenticatedApiChecks: 2) with { TargetAccess = ProxyApiOnly() };
        var run = FrontendQualityRunSemanticsProjection.From(report);
        run.Authenticated!.Api.Should().Be("Observed");
        run.Authenticated.BrowserSession.Should().Be("Not recorded");
        run.Authenticated.UiDom.Should().Be("Not assessed");
        run.Authenticated.UiReason.Should().Contain("API context only").And.Contain("Managed Edge (CDP)");
        run.ReviewScopeSummary.Should().Be("Public frontend assessed · authenticated API evidence observed · authenticated UI not assessed");
    }

    // C — Companion DOM with unknown sign-in state is not an authenticated session.
    [Fact]
    public void C_companion_evidence_is_never_an_authenticated_browser_session()
    {
        var report = FrontendQualityRunSemanticsTests.Report() with { TargetAccess = ProxyApiOnly() with { Method = AuthenticatedTestingMethod.ManagedEdgeCdp, AuthenticatedApiAvailable = false } };
        var run = FrontendQualityRunSemanticsProjection.From(report);
        run.Authenticated!.BrowserSession.Should().Be("Not recorded");
        run.Authenticated.UiDom.Should().Be("Not assessed");
        FrontendQualityReviewScopes.PathFor(FrontendQualityEngineAccessKind.BrowserCompanion).Should().Be(FrontendQualityEngineAccessPath.CompanionEvidence);
    }

    // D — an optional engine that executed stays non-blocking.
    [Fact]
    public void D_optional_engine_completed_is_counted_and_still_non_blocking()
    {
        var run = FrontendQualityRunSemanticsProjection.From(FrontendQualityRunSemanticsTests.Report(FrontendReviewAccessScope.PublicOnly, optionalCompleted: true));
        run.OptionalAssessed.Should().Be(1);
        run.OptionalExecutionDetail.Should().Be("Optional execution: 1 of 1 completed · non-blocking");
    }

    // E, F — pipeline gates never infer failure from configuration.
    [Fact]
    public void E_F_pipeline_gates_are_not_assessed_or_configured_never_not_passed()
    {
        DeliveryGateStates.For(snapshotSelected: false, configured: true, executionResult: null).Should().Be("Not assessed — select a source snapshot");
        DeliveryGateStates.For(snapshotSelected: true, configured: true, executionResult: null).Should().Be("Configured in source · execution result not available");
        DeliveryGateStates.For(snapshotSelected: true, configured: true, executionResult: "Passed").Should().Be("Passed", "only execution evidence states a result");
        new[] { DeliveryGateStates.NoSnapshot, DeliveryGateStates.ConfiguredNoExecution }.Should().NotContain(s => s.Contains("not passed", StringComparison.OrdinalIgnoreCase));
    }

    // G — a critical finding is technical severity, not an automatic block.
    [Fact]
    public void G_critical_severity_is_not_release_blocked_without_policy()
    {
        var report = FrontendQualityRunSemanticsTests.Report(FrontendReviewAccessScope.PublicOnly);
        var view = FrontendQualityResultPresentation.Build(report);
        var critical = view.ActionableIssues.First(i => i.PrimarySeverity == FrontendQualitySeverity.Critical);
        FrontendQualityPostRunPresentation.IssueImpactLabel(FrontendQualityPostRunPresentation.IssueImpact(report, critical)).Should().Be("Not an automatic release blocker");
        report.ReleaseDisposition.Should().NotBe(FrontendQualityReleaseDisposition.Blocked);
        FrontendQualityDecisionSupportService.SeverityNotBlocking.Should().Contain("does not by itself determine release status");
    }

    // I — identical runtime console errors on two pages are one logical issue.
    [Fact]
    public void I_identical_coop_findings_on_two_pages_group_into_one_logical_issue()
    {
        static BrowserRuntimeResultDto Page(string url) => new(BrowserRuntimeEngineStatusDto.Assessed, FinalUrl: url, Findings:
            [new($"console-error-{Guid.NewGuid()}", "Cross-Origin-Opener-Policy policy would block the window.closed call", BrowserRuntimeFindingSeverityDto.Medium,
                "ConsoleError", "COOP", "Set a compatible Cross-Origin-Opener-Policy.", [])]);
        var findings = FrontendQualityEngineOutcomeNormalizer.NormalizeBrowserRuntimeFindings(Page("https://app.example.test/a"))
            .Concat(FrontendQualityEngineOutcomeNormalizer.NormalizeBrowserRuntimeFindings(Page("https://app.example.test/b"))).ToList();
        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);
        var issue = issues.Should().ContainSingle().Subject;
        issue.SourceFindingCount.Should().Be(2);
        issue.AffectedPages.Should().HaveCount(2);
        FrontendQualityRecommendationGrouper.Build(issues).Should().ContainSingle("one remediation, not one per page");
    }

    // J — a derived indicator is not a logical issue, finding or release input.
    [Fact]
    public void J_derived_indicator_is_separate_and_non_defect()
    {
        var report = FrontendQualityRunSemanticsTests.Report(FrontendReviewAccessScope.PublicOnly);
        var run = FrontendQualityRunSemanticsProjection.From(report);
        var view = FrontendQualityResultPresentation.Build(report);
        view.DerivedIssues.Should().NotBeEmpty();
        view.ActionableIssues.Should().NotContain(i => i.Derived);
        run.DerivedIndicators.Should().BeGreaterThan(0);
        run.LogicalIssues.Should().Be(view.ActionableIssues.Count, "derived indicators never add to the logical issue count");
    }

    // K — explicit policy can make a normally optional capability required.
    [Fact]
    public void K_policy_required_capability_unavailable_is_a_real_limitation()
    {
        var rows = new[]
        {
            Row(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled),
            Row(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Unavailable),
        };
        var summary = new FrontendQualityPreRunEngineSummary(rows);
        summary.RequiredLimitations.Select(r => r.EngineId).Should().Equal(FrontendQualityEngineId.Lighthouse);
        summary.AllRequiredAvailable.Should().BeFalse();
        summary.RequiredLine.Should().Be("Required: 1/2 available · 1 unavailable");
    }

    // L — export parity.
    [Fact]
    public void L_export_states_the_same_scope_surfaces_and_severity_semantics()
    {
        var report = FrontendQualityRunSemanticsTests.Report(authenticatedApiChecks: 2) with { TargetAccess = ProxyApiOnly() };
        var run = FrontendQualityRunSemanticsProjection.From(report);
        var html = new ReportExportService().ExportFrontendQualityReview(report, "Fixture");
        html.Should().Contain($"<dt><strong>Assessed:</strong></dt><dd>{run.ReviewScopeSummary}</dd>")
            .And.Contain("<dt>Authenticated UI/DOM:</dt><dd>Not assessed")
            .And.Contain("<dt>Authenticated API context:</dt><dd>Observed</dd>")
            .And.Contain(FrontendQualityDecisionSupportService.SeverityNotBlocking)
            .And.Contain("Technical severity:").And.Contain("Release impact:")
            .And.Contain("Derived indicators and observations")
            .And.NotContain("not passed").And.NotContain("NoAutomatedBlockDetected");
    }
}
