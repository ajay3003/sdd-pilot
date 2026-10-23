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
/// Phase 4: once a review has run, the page stops being a configuration surface. The result, the profile it was produced
/// under and what each domain found come first; engine diagnostics, coverage and access details move below them and stay
/// available. These tests assert document order and disclosure state — the information architecture, not the pixels.
/// </summary>
public sealed class FrontendQualityResultLayoutTests : BunitContext
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

    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed,
        FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required,
        FrontendQualityEngineOutcomeReason reason = FrontendQualityEngineOutcomeReason.None) => new()
        {
            EngineId = id, DisplayName = id.ToString(), Enabled = true,
            Requirement = requirement, ExecutionState = state, OutcomeReason = reason,
        };

    private static WcagAssessment Assessment(string profileId, params (string CriterionId, WcagStatus Status)[] results) => new()
    {
        Profile = WcagProfiles.Resolve(profileId),
        PagesWithEvidence = 1,
        Results = results.Select(r => new WcagCriterionResult
        {
            Definition = WcagRegistry.All.Single(d => d.CriterionId == r.CriterionId),
            Page = "/", Status = r.Status, LastTested = DateTimeOffset.UtcNow,
        }).ToList(),
    };

    /// <summary>A completed review with every engine assessed, one finding per domain, and a WCAG assessment.</summary>
    private static FrontendQualityReviewReport CompletedReport(
        WcagAssessment? wcag = null,
        IEnumerable<FrontendQualityEngineOutcome>? outcomes = null) => new()
        {
            TargetUrl = Url,
            TargetEnvironment = new FrontendReviewTargetIdentity("dev", "M2LB DEV", "Development", Url, DateTime.UtcNow),
            GeneratedAt = DateTime.UtcNow,
            CompletedAt = new DateTime(2026, 9, 18, 10, 30, 0, DateTimeKind.Utc),
            EngineOutcomes = (outcomes ?? Enum.GetValues<FrontendQualityEngineId>().Select(id => Outcome(id))).ToList(),
            Findings =
            [
                new() { Category = FrontendQualityCategory.Security, Severity = FrontendQualitySeverity.High, Title = "Missing Content-Security-Policy", Description = "No CSP header was served." },
                new() { Category = FrontendQualityCategory.Performance, Severity = FrontendQualitySeverity.Medium, Title = "Uncompressed bundle", Description = "The main bundle was served without compression." },
            ],
            Wcag = wcag ?? Assessment(WcagProfiles.NorwegianId, ("1.1.1", WcagStatus.Pass)),
            ActiveEngines = FrontendQualityActiveEngines.Resolve(Context()),
        };

    private IRenderedComponent<FrontendQualityReview> Result(FrontendQualityReviewReport? report = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var context = Context();
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        var orchestrator = new Mock<IFrontendQualityReviewOrchestrator>();
        orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityReviewOrchestrationResult(QualityReport: report ?? CompletedReport()));
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

    /// <summary>The result blocks in document order — the reading order and the focus order.</summary>
    private static IReadOnlyList<string> Order(IRenderedComponent<FrontendQualityReview> page) =>
        page.FindAll("[data-testid=fqr-result-summary], [data-testid=fqr-key-issues], [data-testid=fqr-domain-results], [data-testid=fqr-recommendation-themes], [data-testid=fqr-result-details]")
            .Select(e => e.GetAttribute("data-testid")!).ToList();

    private static IElement Domain(IRenderedComponent<FrontendQualityReview> page, FrontendQualityCategory category) =>
        page.Find($"[data-testid=fqr-domain-result][data-category='{category}']");

    /// <summary>Document position: true when <paramref name="first"/> precedes <paramref name="second"/>.</summary>
    private static bool Precedes(IRenderedComponent<FrontendQualityReview> page, string first, string second)
    {
        var markup = page.Markup;
        return markup.IndexOf(first, StringComparison.Ordinal) >= 0
            && markup.IndexOf(second, StringComparison.Ordinal) > markup.IndexOf(first, StringComparison.Ordinal);
    }

    // ── §44. The result comes first ───────────────────────────────────────────────────────────────────────────────

    // 10, 12, 13.
    [Fact]
    public void ReviewResultIsTheFirstThingTheFinishedPageSays()
    {
        var page = Result();

        // 47, §25. Result and what to act on first (Summary), then Issues, Coverage and Evidence — one view each, in tab order.
        Order(page).Should().Equal(
            "fqr-result-summary", "fqr-key-issues", "fqr-recommendation-themes", "fqr-domain-results", "fqr-result-details");
        page.Find("#fqr-result-heading").TextContent.Should().Be("Review result");
        page.Find("[data-testid=fqr-result-automation]").TextContent.Should().Be("Automation complete");
        page.Find("[data-testid=fqr-result-manual]").TextContent.Should().Be("Manual assessment required");
        page.Find("[data-testid=fqr-result-headline]").TextContent.Trim().Should().Be("Automated review complete — manual assessment required");
        page.Find("[data-testid=fqr-run-again]").TextContent.Trim().Should().Be("Run again");
    }

    // 11. Pre-run readiness answered a different question and is not reused as the verdict.
    [Fact]
    public void PreRunReadinessIsNotReusedAsTheResultVerdict()
    {
        var page = Result();

        page.FindAll("[data-testid=fqr-readiness]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-landing]").Should().BeEmpty();
        page.Markup.Should().NotContain("Ready to review").And.NotContain("Review can run with limitations");
    }

    // 14. No claim the report cannot support.
    [Fact]
    public void NoUnsupportedPassOrComplianceWordingAppears()
    {
        var page = Result();

        // The claim must be absent where the page asserts something. Disclaimers that DENY such a claim ("cannot prove
        // that an application is secure") are the opposite of the problem, so the whole markup is the wrong thing to scan.
        var asserted = page.Find("[data-testid=fqr-result-summary]").TextContent
            + string.Join(" ", page.FindAll("[data-testid=fqr-domain-result-state], [data-testid=fqr-domain-result-summary]").Select(e => e.TextContent));

        foreach (var claim in new[] { "Passed", "Compliant", "Secure", "Healthy", "Fully compatible", "No accessibility issues" })
            asserted.Should().NotContain(claim);
        page.Markup.Should().NotContain("WCAG compliant");
    }

    // 4 (§43), §9. Zero automated failures reads as an absence of detected violations.
    [Fact]
    public void ZeroAutomatedWcagFailuresIsStatedAsAnAbsenceOfDetectedViolations()
    {
        var page = Result();

        var accessibility = Domain(page, FrontendQualityCategory.Accessibility);
        accessibility.QuerySelector("[data-testid=fqr-domain-result-summary]")!.TextContent
            .Should().Be("No automated WCAG violations were detected in the available evidence. Manual assessment is still required.");
    }

    // ── §45. Result order ─────────────────────────────────────────────────────────────────────────────────────────

    // 15, 16, 17, 18, 19.
    [Fact]
    public void EveryResultDomainPrecedesTheDiagnostics()
    {
        var page = Result();

        page.FindAll("[data-testid=fqr-domain-result]").Select(d => d.GetAttribute("data-category"))
            .Should().Equal("Accessibility", "Performance", "Security", "Standards", "BlazorWasm", "Readiness");

        foreach (var category in new[] { "Accessibility", "Performance", "Security" })
            Precedes(page, $"data-category=\"{category}\"", "fqr-result-details").Should().BeTrue($"{category} is a result, the engine matrix is not");

        // The domain headings are h2, at the same level as the result and its details — a flat, scannable result page.
        page.FindAll("[data-testid=fqr-domain-result] h2").Should().HaveCount(6);
    }

    // ── §46. Diagnostics below the result ─────────────────────────────────────────────────────────────────────────

    // 20, 22, 24, 25, §35. Engine diagnostics and target access live in Coverage, below the engine coverage table.
    [Fact]
    public void EngineInventoryAndCoverageLiveInCoverage()
    {
        var page = Result();

        var coverage = page.Find("[data-testid=fqr-tabpanel-coverage]");
        coverage.QuerySelector("[data-testid=fqr-result-technical]").Should().NotBeNull("target access at review start");
        coverage.QuerySelector("[data-testid=fqr-engine-coverage-table]").Should().NotBeNull();
        coverage.QuerySelector("[data-testid=fqr-engine-coverage-body] [data-testid=fqr-coverage-label]").Should().NotBeNull("raw engine diagnostics are behind View engine diagnostics");
        // 24. Enabled (configuration) and the execution state stay separate columns.
        coverage.QuerySelectorAll("[data-testid=fqr-enabled]").Should().NotBeEmpty();
        coverage.QuerySelectorAll("[data-testid=fqr-state]").Should().NotBeEmpty();
        // 20. None of it sits in the Summary.
        page.Find("[data-testid=fqr-tabpanel-summary]").QuerySelector("[data-testid=fqr-coverage-label]").Should().BeNull();
    }

    // 21, 23. Pairing diagnostics and the pre-run check inventory belong to the decision page, not the result page.
    [Fact]
    public void BrowserCompanionSessionAndCheckInventoryAreNotOnTheResultPage()
    {
        var page = Result();

        page.FindAll("[data-testid=fqr-companion]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-checks-disclosure]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-capabilities-disclosure]").Should().BeEmpty();
        page.Markup.Should().NotContain("Needs pairing").And.NotContain("Browser Companion session");
    }

    // ── §47. Domain results ───────────────────────────────────────────────────────────────────────────────────────

    // 26, 20, 72. An unavailable optional engine is a limitation on the EVIDENCE, not the domain's headline — and the
    // limitation names it. "1 evidence source did not contribute to this domain" kept the engine out of the card and
    // sent the reader to the engine matrix to find out which one it was.
    [Fact]
    public void AnUnavailableOptionalEngineDoesNotBecomeTheDomainHeadline()
    {
        var outcomes = Enum.GetValues<FrontendQualityEngineId>().Select(id => id == FrontendQualityEngineId.Lighthouse
            ? Outcome(id, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineRequirement.Optional,
                FrontendQualityEngineOutcomeReason.ReadinessUnavailable)
            : Outcome(id));

        var page = Result(CompletedReport(outcomes: outcomes));

        var performance = Domain(page, FrontendQualityCategory.Performance);
        performance.QuerySelector("[data-testid=fqr-domain-result-state]")!.TextContent.Trim().Should().Be("Automated checks completed — limited evidence");
        // 33. Two different questions, answered separately: how many things to fix, and how much evidence there is.
        performance.QuerySelector("[data-testid=fqr-domain-result-count]")!.TextContent.Should().Be("1 logical issue · 1 source finding");

        // The headline — state and summary — stays about the domain's result.
        performance.QuerySelector("[data-testid=fqr-domain-result-summary]")!.TextContent.Should().NotContain("Lighthouse");
        // The limitation line, and only the limitation line, names what did not contribute.
        performance.QuerySelector("[data-testid=fqr-domain-result-limitation]")!.TextContent
            .Should().Be("Lighthouse did not contribute to this domain.");
    }

    // 28. A domain with no evidence shows no count — and a domain that HAS findings never denies them.
    [Fact]
    public void ADomainWithoutEvidenceShowsNoFindingCount_ButOneWithFindingsDoesNotDenyThem()
    {
        var outcomes = Enum.GetValues<FrontendQualityEngineId>()
            .Select(id => Outcome(id, FrontendQualityEngineExecutionState.Unavailable));

        var page = Result(CompletedReport(outcomes: outcomes));

        // Security's own engines did not run, but the report carries its missing-CSP finding. Saying "no evidence"
        // here denied a finding listed further down the same page.
        var security = Domain(page, FrontendQualityCategory.Security);
        security.QuerySelector("[data-testid=fqr-domain-result-count]").Should().NotBeNull("the domain has a finding");
        security.TextContent.Should().NotContain("nothing can be concluded about it");

        // A domain with neither an engine nor a finding still establishes no number.
        var blazor = Domain(page, FrontendQualityCategory.BlazorWasm);
        blazor.QuerySelector("[data-testid=fqr-domain-result-count]").Should().BeNull("nothing established a number");
        blazor.TextContent.Should().Contain("Not assessed in this run", "an engine that never ran is Not assessed, never \"No evidence\"");
    }

    // 29. An execution failure is reported as one.
    [Fact]
    public void AFailedRunIsReportedAsAnExecutionProblem_NotAsFindings()
    {
        var report = CompletedReport();
        var failed = new FrontendQualityReviewReport
        {
            TargetUrl = Url, TargetEnvironment = report.TargetEnvironment, GeneratedAt = report.GeneratedAt,
            EngineOutcomes = report.EngineOutcomes, ErrorMessage = "The review could not start.",
        };

        var page = Result(failed);

        // An execution problem: automation failed, and nothing that did not run is ever called covered.
        page.Find("[data-testid=fqr-result-automation]").TextContent.Trim().Should().Be("Automation failed");
        page.Find("[data-testid=fqr-result-coverage]").TextContent.Trim().Should().Be("Coverage not assessed");
        page.Find("[data-testid=fqr-result-headline]").TextContent.Trim().Should().Be("Automated review failed to run");
        page.Find("[data-testid=fqr-result-summary-text]").TextContent.Should().Be("The review could not start.");
    }

    // ── §48. Density ──────────────────────────────────────────────────────────────────────────────────────────────

    // 31, 32, 34.
    [Fact]
    public void ResultSummariesAreVisibleWhileDetailedTablesStayClosed()
    {
        var page = Result();

        // 31. Every domain states its result without the reader expanding anything.
        page.FindAll("[data-testid=fqr-domain-result-summary]").Should().HaveCount(6);
        page.FindAll("[data-testid=fqr-domain-result-summary]").Should().OnlyContain(s => s.Closest(".disclosure-body") == null);
        page.FindAll("[data-testid=fqr-domain-result-state]").Should().HaveCount(6);

        // 32. The full WCAG criteria matrix is not rendered until asked for.
        page.Find("[data-testid=wcag-toggle-criteria]").GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll("[data-testid=wcag-criterion-row]").Should().BeEmpty();

        // 34. The engine matrix opens on request; domain findings are not repeated on the cards at all.
        page.Find("[data-testid=fqr-engine-coverage-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.FindAll("[data-testid=fqr-domain-finding-list]").Should().BeEmpty();
    }

    // 33, §68. A domain's one action shows that domain's issues, and only those.
    [Fact]
    public void ReviewingADomainShowsItsOwnIssuesOnly()
    {
        var page = Result();

        page.Find("[data-testid=fqr-domain-review-issues][data-category=Security]").Click();

        var issues = page.Find("[data-testid=fqr-tabpanel-issues]");
        issues.HasAttribute("hidden").Should().BeFalse();
        issues.QuerySelectorAll("[data-testid=fqr-logical-issue]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Missing Content-Security-Policy").And.Contain("High");
        issues.QuerySelector("[data-testid=fqr-all-logical-issues]")!.TextContent.Should().NotContain("Uncompressed bundle", "that issue belongs to Performance");
    }

    // ── §6, §33. The profile the result was produced under ────────────────────────────────────────────────────────

    [Fact]
    public void TheProfileThatProducedTheResultIsVisibleWithoutOpeningAnything()
    {
        var page = Result();

        page.Find("[data-testid=fqr-result-profile]").TextContent.Should().Contain(WcagProfiles.Norwegian.Label);
        page.Find("[data-testid=fqr-result-profile-scope]").TextContent.Should().Be($"{WcagProfiles.Norwegian.CriteriaInScope} criteria in selected profile");
        Domain(page, FrontendQualityCategory.Accessibility).QuerySelector("[data-testid=fqr-domain-result-scope]")!.TextContent
            .Should().Contain(WcagProfiles.Norwegian.Label).And.Contain("criteria in selected profile");
    }

    // 8, 9 (§43). Changing the profile after a run asks for a rerun; it never relabels the finished result.
    [Fact]
    public void ChangingTheProfileAfterARunAsksForARerunInsteadOfRelabellingTheResult()
    {
        var page = Result();
        page.FindAll("[data-testid=fqr-profile-drift]").Should().BeEmpty("nothing has changed yet");

        // The selector inside the WCAG result surface chooses the profile for the NEXT run.
        page.Find("[data-testid=wcag-profile]").Change(WcagProfiles.ExtendedId);

        var drift = page.Find("[data-testid=fqr-profile-drift]");
        drift.TextContent.Should().Contain(WcagProfiles.Norwegian.Label)
            .And.Contain(WcagProfiles.Extended.Label)
            .And.Contain("Run the review again");
        // The result itself is untouched: same profile, same scope, same counts.
        page.Find("[data-testid=fqr-result-profile]").TextContent.Should().Contain(WcagProfiles.Norwegian.Label);
        page.Find("[data-testid=fqr-result-profile-scope]").TextContent.Should().Be($"{WcagProfiles.Norwegian.CriteriaInScope} criteria in selected profile");
        page.Find("[data-testid=wcag-assessment-title]").TextContent.Should().Be(WcagProfiles.Norwegian.Label);
    }

    // ── §49. The pre-run hierarchy is unchanged ───────────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeARunThePhase3BHierarchyStillHolds()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context());
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();

        var page = Render<FrontendQualityReview>();

        page.FindAll("[data-testid=fqr-target-summary], [data-testid=fqr-profile], [data-testid=fqr-readiness], [data-testid=fqr-run-bar], [data-testid=fqr-dimensions], [data-testid=fqr-details]")
            .Select(e => e.GetAttribute("data-testid"))
            .Should().Equal("fqr-target-summary", "fqr-profile", "fqr-readiness", "fqr-run-bar", "fqr-dimensions", "fqr-details");
        page.FindAll("[data-testid=fqr-result-summary]").Should().BeEmpty("results appear only after a run");
        page.FindAll("[data-testid=fqr-domain-result]").Should().BeEmpty();
    }
}
