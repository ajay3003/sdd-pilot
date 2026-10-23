using BirkNext.AiCodeReview;
using BirkNext.SourceEvidence;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Code Risk Review result triage: summary, deterministic actions (one per group), grouped findings with every finding kept, review coverage
/// (gaps and unsupported analyzers are not findings), the prominent baseline limitation and an explicit, never automatic, baseline choice.
/// </summary>
public sealed class CodeRiskReviewTriageTests : BunitContext
{
    private static readonly Guid Current = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Previous = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static AiCodeFinding Finding(string ruleId, AiCodeReviewCategory category, AiFindingSeverity severity, string file, int line, string title) => new()
    {
        LogicalId = $"{ruleId}|{file}|{line}", RuleId = ruleId, Category = category, Severity = severity, Title = title, Description = "Observed in source.",
        Rationale = "Why it matters.", EvidenceSources = [AiEvidenceSource.Source], SourceSnapshotId = Current, Locations = [new(file, line)],
        Limitation = "Syntax-level only.", Recommendation = "Review it.",
    };

    private static AiRuleExecution Rule(string id, AiCodeReviewCategory category, AiRuleExecutionState state, int findings = 0, string? reason = null) =>
        new(id, category, id, state, reason, findings, [AiEvidenceSource.Source]);

    /// <summary>2 permissive CORS, 9 empty catches, 148 generated-documentation drift candidates; no baseline, no executed tests, no Specification.</summary>
    private static AiCodeReviewResult Fixture() => new()
    {
        Mode = AiReviewMode.CurrentSnapshot, CompletedAt = DateTimeOffset.UtcNow, Current = new(Current, "M2LB", "M2LB.zip", new string('c', 64), DateTimeOffset.UtcNow, true),
        Findings =
        [
            .. Enumerable.Range(1, 2).Select(i => Finding("AIC-SEC-003", AiCodeReviewCategory.Security, AiFindingSeverity.Medium, $"Api/Program{i}.cs", 10 + i, "Permissive CORS policy")),
            .. Enumerable.Range(1, 9).Select(i => Finding("AIC-VAL-001", AiCodeReviewCategory.ValidationErrorHandling, AiFindingSeverity.Low, $"Core/Service{i}.cs", i, "Empty catch block")),
            .. Enumerable.Range(1, 148).Select(i => Finding("AIC-DOC-002", AiCodeReviewCategory.GeneratedDocumentation, AiFindingSeverity.Info, $"autodoc/page{i}.md", 1, "Generated documentation drift candidate")),
        ],
        Rules =
        [
            Rule("AIC-SEC-003", AiCodeReviewCategory.Security, AiRuleExecutionState.Executed, 2),
            Rule("AIC-VAL-001", AiCodeReviewCategory.ValidationErrorHandling, AiRuleExecutionState.Executed, 9),
            Rule("AIC-DOC-002", AiCodeReviewCategory.GeneratedDocumentation, AiRuleExecutionState.Executed, 148),
            Rule("AIC-SEC-004", AiCodeReviewCategory.Security, AiRuleExecutionState.Executed),
            Rule("AIC-REQ-001", AiCodeReviewCategory.RequirementsAlignment, AiRuleExecutionState.NotAssessed, reason: "No Specification is loaded in the current workspace."),
            Rule("AIC-TEST-005", AiCodeReviewCategory.Tests, AiRuleExecutionState.NotAssessed, reason: "No executed test evidence."),
            Rule("AIC-REF-003", AiCodeReviewCategory.UnresolvedReferences, AiRuleExecutionState.NotAssessed, reason: "Client-side endpoint references are not extracted."),
            Rule("AIC-TEST-006", AiCodeReviewCategory.Tests, AiRuleExecutionState.Unsupported, reason: "No AST comparison."),
            Rule("AIC-CONTRACT-001", AiCodeReviewCategory.ContractDrift, AiRuleExecutionState.NotApplicable, reason: "Needs a baseline snapshot."),
        ],
        Readiness =
        [
            new("source", "Source snapshot", true, "M2LB.zip"), new("code-risk", "Code-risk observations", true, "415 C# file(s) analyzed", "Source-level rules"),
            new("baseline", "Baseline snapshot", false, "Not selected — change rules are not applicable.", "Change, contract and configuration drift rules"),
            new("specification", "Specification", false, "Not loaded", "Requirements alignment"), new("constitution", "Constitution", false, "Not loaded", "Constitution rules"),
            new("tasks", "Tasks", false, "Not loaded", "Completed-task checks"), new("execution", "Build / test execution evidence", false, "None imported (NotVerified)", "Execution rules"),
            new("dependency-review", "Dependency Review", true, "Run reviewed this snapshot", "Dependency health and update-policy context", "Dependency Review run 2026-10-23"),
        ],
        Languages = [new("C#", "csharp-syntax", true, 415), new("Python", "none", false, 14, "No Python code-risk analyzer."), new("JavaScript", "none", false, 1, "No JavaScript code-risk analyzer.")],
        Limitations = ["Python: 14 file(s) present — No Python code-risk analyzer.", "JavaScript: 1 file(s) present — No JavaScript code-risk analyzer."],
        Categories = [new(AiCodeReviewCategory.Security, AiCategoryStatus.Findings, 2, "415 production C# file(s)", 0, "2 of 2 rule(s) executed.")],
    };

    // ── Rules ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvidenceGapsNotAssessedUnsupportedAndNotApplicableStayDistinct()
    {
        var r = Fixture();
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-SEC-003"), r).Should().Be(CodeRiskResultKind.Finding);
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-SEC-004"), r).Should().Be(CodeRiskResultKind.ExecutedClean);
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-REQ-001"), r).Should().Be(CodeRiskResultKind.EvidenceGap, "a Specification can be loaded");
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-TEST-005"), r).Should().Be(CodeRiskResultKind.EvidenceGap, "executed test evidence can be imported");
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-REF-003"), r).Should().Be(CodeRiskResultKind.NotAssessed);
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-TEST-006"), r).Should().Be(CodeRiskResultKind.Unsupported);
        CodeRiskTriage.Kind(r.Rules.Single(x => x.RuleId == "AIC-CONTRACT-001"), r).Should().Be(CodeRiskResultKind.NotApplicable);
        CodeRiskTriage.Help(CodeRiskResultKind.NotAssessed).Should().Be("Required evidence was unavailable; this is not a pass or failure.");
    }

    [Fact]
    public void TheSummaryCountsAndSaysWhichModeRan()
    {
        var summary = CodeRiskTriage.Summary(Fixture());
        (summary.Findings, summary.Medium, summary.Low, summary.Info).Should().Be((159, 2, 9, 148));
        (summary.Executed, summary.EvidenceGaps, summary.NotAssessed, summary.Unsupported, summary.NotApplicable).Should().Be((4, 2, 1, 1, 1));
        summary.CurrentState.Should().Be("Partial", "Python/JavaScript are not analyzed and evidence gaps remain");
        summary.ChangeRisk.Should().Be("Limited — no baseline snapshot selected");
        summary.Headline.Should().Be("Current-state review completed. Change-risk checks requiring a baseline were not assessed.");

        var full = CodeRiskTriage.Summary(Fixture() with { Baseline = new(Previous, "M2LB", "M2LB.zip", new string('d', 64), DateTimeOffset.UtcNow.AddDays(-1), true) });
        full.Headline.Should().Be("Current-state and change-risk review completed.");
    }

    [Fact]
    public void RepeatedFindingsAreGroupedWithEveryFindingKept_InADeterministicOrder()
    {
        var groups = CodeRiskTriage.Groups(Fixture().Findings);
        groups.Select(g => g.Category).Should().Equal(AiCodeReviewCategory.Security, AiCodeReviewCategory.ValidationErrorHandling, AiCodeReviewCategory.GeneratedDocumentation);
        var catches = groups[1].Rules.Single();
        (catches.DisplayId, catches.Title, catches.Findings.Count).Should().Be(("CRR-VAL-001", "Empty catch block", 9));
        groups[2].Rules.Single().Findings.Should().HaveCount(148, "grouping is presentation only");
        CodeRiskTriage.Groups(Fixture().Findings.AsEnumerable().Reverse()).Select(g => g.Rules[0].RuleId).Should().Equal(groups.Select(g => g.Rules[0].RuleId));
    }

    [Fact]
    public void RecommendedActionsAreOnePerGroupThenTheSetupThatUnlocksRules()
    {
        CodeRiskTriage.Actions(Fixture()).Select(a => a.Text).Should().Equal(
            "Review 2 permissive CORS policy findings",
            "Review 9 empty catch block findings",
            "Review 148 generated documentation drift candidate findings",
            "Select a baseline snapshot to enable change and drift rules",
            "Attach executed test evidence (TRX) — test results are not verified",
            "Load Specification / Constitution / Tasks for alignment checks",
            "14 Python files are not analyzed (unsupported analyzer)",
            "1 JavaScript file is not analyzed (unsupported analyzer)");
        CodeRiskTriage.Actions(Fixture())[0].Href.Should().Be("#crr-group-crr-sec-003");
    }

    [Fact]
    public void CoverageSeparatesMissingEvidenceFromUnsupportedAnalyzers()
    {
        var rows = CodeRiskTriage.Coverage(Fixture()).ToDictionary(r => r.Key);
        rows["source"].Status.Should().Be(CodeRiskCoverageStatus.Partial);
        rows["source"].Detail.Should().Be("Analyzed source files: 415 / 430");
        rows["baseline"].Status.Should().Be(CodeRiskCoverageStatus.NotSelected);
        rows["specification"].Should().Match<CodeRiskCoverageRow>(r => r.Status == CodeRiskCoverageStatus.Missing && r.Action == "Open Specification Explorer");
        rows["execution"].Status.Should().Be(CodeRiskCoverageStatus.Missing);
        rows["dependency-review"].Should().Match<CodeRiskCoverageRow>(r => r.Status == CodeRiskCoverageStatus.Ready && r.Provenance == "Dependency Review run 2026-10-23");
        rows["language:Python"].Should().Match<CodeRiskCoverageRow>(r => r.Status == CodeRiskCoverageStatus.Unsupported && r.Detail == "14 files not analyzed");
        rows["language:JavaScript"].Detail.Should().Be("1 file not analyzed");
        CodeRiskTriage.AnalyzedFiles(Fixture()).Should().Be((415, 430), "unsupported files are never counted as analyzed");
    }

    [Fact]
    public void SearchMatchesRuleIdsInBothFormsTitlesFilesAndCategories()
    {
        var f = Fixture().Findings[0];
        CodeRiskTriage.Matches(f, "crr-sec-003").Should().BeTrue();
        CodeRiskTriage.Matches(f, "AIC-SEC").Should().BeTrue();
        CodeRiskTriage.Matches(f, "program1").Should().BeTrue();
        CodeRiskTriage.Matches(f, "security").Should().BeTrue();
        CodeRiskTriage.Matches(f, "catch").Should().BeFalse();
    }

    // ── Page ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<IAiCodeReviewApiService> _api = new();

    private IRenderedComponent<AiGeneratedCodeReview> Page(AiCodeReviewResult? result = null, params ReviewSourceSnapshot[] snapshots)
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(_api.Object);
        Services.AddScoped<SddEvidenceGraphService>();
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "M2LB", WorkspaceArtifactType.Specification)).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions { Snapshots = [.. snapshots] }, (string?)null));
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result is null ? [] : [new AiCodeReviewRunSummary(result.RunId, result.CompletedAt, result.Mode, Current, null, "M2LB · current snapshot · 159 finding(s)", 159)]);
        if (result is not null) _api.Setup(x => x.GetAsync(result.RunId, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return Render<AiGeneratedCodeReview>();
    }

    private static ReviewSourceSnapshot Snap(Guid id, int daysAgo, bool latest) => new()
    {
        SnapshotId = id, RepositoryKey = "m2lb", Repository = "M2LB", ArchiveName = $"M2LB-{daysAgo}.zip", Fingerprint = new string(daysAgo == 0 ? 'c' : 'd', 64),
        AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo), SourceStatus = "Ready", Latest = latest, HasConsumerEvidence = true,
    };

    [Fact]
    public void ThePageIsCodeRiskReview_UnderBothRoutes()
    {
        var routes = typeof(AiGeneratedCodeReview).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(a => a.Template);
        routes.Should().BeEquivalentTo(["/code-risk-review", "/ai-generated-code-review"], "old bookmarks keep working");
        var cut = Page(null, Snap(Current, 0, true));
        cut.Find("h1").TextContent.Should().Contain("Code Risk Review");
        cut.Find("[data-testid=aic-run]").TextContent.Trim().Should().Be("Run Code Risk Review");
        cut.Markup.Should().NotContain("AI-Generated").And.NotContain("AI-assisted");
        cut.Find("[data-testid=aic-disclaimer]").TextContent.Should().Contain("It does not determine whether code was written by AI");
    }

    [Fact]
    public void ABaselineIsSuggestedButOnlySelectedOnRequest()
    {
        var cut = Page(null, Snap(Current, 0, true), Snap(Previous, 3, false));

        cut.Find("[data-testid=aic-baseline]").GetAttribute("value").Should().BeEmpty("a baseline is never chosen automatically");
        cut.Find("[data-testid=aic-baseline-suggestion]").TextContent.Should().Contain("Suggested baseline: previous snapshot of the same repository").And.Contain("Not selected");
        cut.Find("[data-testid=aic-scope-changed]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=aic-scope-changed-help]").TextContent.Should().StartWith("Changed files only needs a baseline");

        cut.Find("[data-testid=aic-baseline-use]").Click();

        cut.Find("[data-testid=aic-baseline]").GetAttribute("value").Should().Be(Previous.ToString());
        cut.Find("[data-testid=aic-scope-changed]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=aic-baseline-suggestion]").Should().BeEmpty();
    }

    [Fact]
    public void AResultReadsSummaryActionsCoverageGroupsChangeRiskRulesAndLimitations()
    {
        var result = Fixture();
        var cut = Page(result, Snap(Current, 0, true));
        cut.Find("[data-testid=aic-history-open]").Click();

        cut.Find("#aic-result-heading").TextContent.Should().Be("Code Risk Review completed");
        cut.Find("[data-testid=aic-headline]").TextContent.Should().Be("Current-state review completed. Change-risk checks requiring a baseline were not assessed.");
        cut.Find("[data-testid=aic-summary-findings]").TextContent.Should().Be("159");
        cut.Find("[data-testid=aic-summary-gaps]").TextContent.Should().Be("2");
        cut.Find("[data-testid=aic-change-state]").TextContent.Should().Be("Limited — no baseline snapshot selected");
        cut.FindAll("[data-testid=aic-action]").Should().HaveCount(8);
        cut.Find("[data-testid=aic-coverage-row][data-key='language:Python']").TextContent.Should().Contain("Unsupported").And.Contain("14 files not analyzed");
        cut.Find("[data-testid=aic-change-limited]").TextContent.Should().Contain("Change-risk coverage: Limited").And.Contain("No baseline snapshot selected");

        var catches = cut.Find("[data-testid=aic-group][data-rule=AIC-VAL-001]");
        catches.QuerySelector("summary")!.TextContent.Should().Contain("Empty catch block · 9").And.Contain("CRR-VAL-001");
        catches.QuerySelectorAll("[data-testid=aic-finding]").Should().HaveCount(9);
        catches.HasAttribute("open").Should().BeFalse("groups start collapsed");

        cut.FindAll("[data-testid=aic-action] button")[1].Click();
        cut.Find("[data-testid=aic-group][data-rule=AIC-VAL-001]").HasAttribute("open").Should().BeTrue("the action opens its finding group");

        // The rule table defaults to findings and evidence gaps; Not applicable rules are one filter away.
        cut.FindAll("[data-testid=aic-rule]").Select(r => r.GetAttribute("data-kind")).Should().OnlyContain(k => k == "Finding" || k == "EvidenceGap");
        cut.FindAll("[data-testid=aic-rule][data-rule=AIC-CONTRACT-001]").Should().BeEmpty();
        cut.Find("[data-testid=aic-rule-filter]").Change("NotApplicable");
        cut.Find("[data-testid=aic-rule][data-rule=AIC-CONTRACT-001] code").TextContent.Should().Be("CRR-CONTRACT-001");
        cut.Find("[data-testid=aic-limitations]").TextContent.Should().Contain("Python");
        cut.Markup.Should().NotMatchRegex(@"\d+\s*%", "there is no score");
    }

    [Fact]
    public void SearchNarrowsTheGroups()
    {
        var result = Fixture();
        var cut = Page(result, Snap(Current, 0, true));
        cut.Find("[data-testid=aic-history-open]").Click();

        cut.Find("[data-testid=aic-search]").Input("CRR-SEC");

        cut.FindAll("[data-testid=aic-group]").Select(g => g.GetAttribute("data-rule")).Should().Equal("AIC-SEC-003");
    }

    [Fact]
    public void RecommendedWorkflowOffersItOnceSourceEvidenceExists_WithoutRequiringABaseline()
    {
        var ready = BirkNext.Technology.ApplicabilityEvaluator.Evaluate("code-risk-review", new BirkNext.Technology.ProjectApplicabilityInput { HasSourceSnapshot = true });
        ready.Status.Should().Be(BirkNext.Applicability.ApplicabilityStatus.Applicable);
        ready.Reason.Should().Contain("select a baseline later for change-risk analysis");
        var none = BirkNext.Technology.ApplicabilityEvaluator.Evaluate("code-risk-review", new BirkNext.Technology.ProjectApplicabilityInput());
        none.Status.Should().Be(BirkNext.Applicability.ApplicabilityStatus.NotEnoughEvidence);
        none.Reason.Should().StartWith("Source evidence required");
        NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == "code-risk-review").ReviewId.Should().Be("code-risk-review");
    }
}
