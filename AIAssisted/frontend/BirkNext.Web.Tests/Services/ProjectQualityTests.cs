using BirkNext.ExecutionEvidence;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Project Quality semantics: execution evidence is only recorded runs (never document review, never zero when unreadable), specification
/// quality is not "passed" while findings remain, optional document types do not enter the denominator, and every "Learn how" target and
/// stage help topic is a real Help Center article.
/// </summary>
public sealed class ProjectQualityTests
{
    private static readonly IReadOnlyList<DashboardReadinessRow> Rows =
    [
        new("Specification", "Specifications", DashboardStatus.Available, "Available", ""),
        new("Plan", "Plans", DashboardStatus.Available, "Available", ""),
        new("Tasks", "Tasks", DashboardStatus.NeedsSetup, "Missing", ""),
        new("Constitution", "Constitutions", DashboardStatus.NotApplicable, "Not found", ""),
    ];

    private static DashboardKpi Card(DashboardReviewFacts facts, string key) => DashboardQualitySummary.Cards(Rows, [], facts).Single(c => c.Key == key);

    private static ExecutionRunSummary Run(ExecutionState state, int minutesAgo) => new() { RunId = Guid.NewGuid(), State = state, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo) };

    [Fact]
    public void Execution_evidence_counts_only_recorded_runs_and_never_turns_unreadable_into_zero()
    {
        Card(new DashboardReviewFacts { QaAssessed = true, QaScore = 95 }, "execution").Should().Match<DashboardKpi>(k => k.Value == "Not read" && k.Status == DashboardStatus.NotAssessed,
            "a document review score is not test execution");
        Card(new DashboardReviewFacts { Execution = ExecutionEvidenceFacts.From(null, "QA") }, "execution").Should().Match<DashboardKpi>(k => k.Value == "Unavailable" && k.Status == DashboardStatus.Unavailable);
        Card(new DashboardReviewFacts { Execution = ExecutionEvidenceFacts.From([], "QA") }, "execution").Should().Match<DashboardKpi>(k => k.Value == "No recorded runs"
            && k.Status == DashboardStatus.NotAssessed && k.Detail.Contains("Generated tests") && k.Detail.Contains("QA"));
        var facts = ExecutionEvidenceFacts.From([Run(ExecutionState.Passed, 5), Run(ExecutionState.Failed, 10), Run(ExecutionState.Error, 20), Run(ExecutionState.TimedOut, 30), Run(ExecutionState.Blocked, 40), Run(ExecutionState.NotAssessed, 50)], "QA");
        facts.Should().Match<ExecutionEvidenceFacts>(f => f.Runs == 6 && f.Passed == 1 && f.Failed == 3);
        var card = Card(new DashboardReviewFacts { Execution = facts }, "execution");
        card.Value.Should().Be("6 recorded runs");
        card.Detail.Should().StartWith("1 passed · 3 failed or error · 2 blocked, not assessed or other");
        card.Status.Should().Be(DashboardStatus.AttentionNeeded);
        Card(new DashboardReviewFacts { Execution = ExecutionEvidenceFacts.From([Run(ExecutionState.Passed, 1)], "QA") }, "execution").Status.Should().Be(DashboardStatus.Available);
    }

    [Fact]
    public void Specification_quality_is_not_passed_while_findings_remain_and_optional_types_are_not_counted()
    {
        Card(new DashboardReviewFacts { DocumentReview = AnalysisSessionStatus.Completed, DocumentReviewFindings = 4, DocumentReviewSevere = 0 }, "specification").Status.Should().Be(DashboardStatus.Partial);
        Card(new DashboardReviewFacts { DocumentReview = AnalysisSessionStatus.Completed, DocumentReviewFindings = 0 }, "specification").Status.Should().Be(DashboardStatus.Available);
        Card(new DashboardReviewFacts { DocumentReview = AnalysisSessionStatus.Completed, DocumentReviewFindings = 2, DocumentReviewSevere = 1 }, "specification").Status.Should().Be(DashboardStatus.AttentionNeeded);
        DashboardQualitySummary.Cards(Rows, [], new()).Should().NotContain(k => k.Status == DashboardStatus.Passed, "no KPI is a pass verdict");
        Card(new DashboardReviewFacts(), "evidence").Value.Should().Be("2 of 3 core document types", "the Constitution does not apply here");
    }

    [Theory]
    [InlineData("none", "getting-started")]
    [InlineData(WorkflowReadinessService.LoadWorkspaceKey, "project-import")]
    [InlineData(WorkflowReadinessService.ConfigureProjectKey, "admin/system-settings")]
    [InlineData(WorkflowReadinessService.EstablishSourceBindingKey, "source-analysis")]
    [InlineData(WorkflowReadinessService.CompleteTargetKey, "admin/system-settings")]
    [InlineData("OpenReview:dqr", "quality/document")]
    [InlineData("SpecificationExplorer", "specification-explorer")]
    [InlineData("ArtifactTraceability", "artifact-traceability")]
    [InlineData("OpenReview:x", "api-quality-review/contracts")]
    [InlineData("OpenReview:y", "performance-test-review")]
    public void Every_learn_how_target_is_a_real_help_center_article(string key, string route)
    {
        var topic = RecommendedActionHelp.TopicFor(key, route);
        topic.Should().NotBeNull();
        UserGuideContent.TopicFor(topic).Should().NotBeNull($"{key} → {topic} must be a guide article");
    }

    [Fact]
    public void The_project_quality_article_exists_and_the_dashboard_feature_points_to_it()
    {
        UserGuideContent.TopicFor("project-quality").Should().NotBeNull();
        DocumentationCatalog.Features.Single(f => f.Route == "/dashboard").UserGuideAnchor.Should().Be("project-quality");
        foreach (var topic in new[] { "getting-started", "traceability", "quality-reviews", "execution-results", "contract-testing", "readiness", "source-analysis" })
            UserGuideContent.TopicFor(topic).Should().NotBeNull($"stage help topic {topic}");
    }
}
