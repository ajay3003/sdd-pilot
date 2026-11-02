using BirkNext.Web.Services;
using BirkNext.Web.Services.Operations;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>SDD Quality &amp; Readiness summary: values only from shared results (no score), finding groups by stable key, project-isolated activity.</summary>
public sealed class DashboardQualitySummaryTests
{
    private static readonly DashboardReadinessRow[] Rows =
    [
        new("Specification", "Specifications", DashboardStatus.Available, "Available", ""),
        new("Plan", "Plans", DashboardStatus.Partial, "Partial", ""),
        new("Tasks", "Tasks", DashboardStatus.Available, "Available", ""),
        new("Constitution", "Constitutions", DashboardStatus.NotApplicable, "Not found", ""),
        new("source", "Source", DashboardStatus.Available, "Analyzed", ""),
        new("target", "Target Environment", DashboardStatus.NeedsSetup, "Not configured", ""),
    ];

    private static DashboardKpi Card(IReadOnlyList<DashboardKpi> cards, string key) => cards.Single(c => c.Key == key);

    [Fact]
    public void AvailableEvidence_IsNotReviewed_AndUnrunReviewsAreNotAssessed()
    {
        var cards = DashboardQualitySummary.Cards(Rows, [], new DashboardReviewFacts());
        Card(cards, "evidence").Value.Should().Be("2 of 4 document types");
        Card(cards, "evidence").Detail.Should().Contain("1 partial").And.Contain("presence only, not reviewed");
        Card(cards, "specification").Value.Should().Be("Not assessed");
        Card(cards, "traceability").Value.Should().Be("Not assessed");
        Card(cards, "qa").Value.Should().Be("Not verified");
        Card(cards, "readiness").Value.Should().Be("1 of 2 inputs ready");
        cards.Should().OnlyContain(c => !c.Value.Contains('%'), "no invented percentage");
    }

    [Fact]
    public void CompletedReviews_ShowTheirOwnResults()
    {
        var facts = new DashboardReviewFacts
        {
            DocumentReview = AnalysisSessionStatus.Completed, DocumentReviewFindings = 7, DocumentReviewSevere = 2,
            TraceabilityRun = true, TraceabilityTransitionsAssessed = 2, TraceabilityTransitionsTotal = 3, TraceabilityReferenceIssues = 1,
        };
        var cards = DashboardQualitySummary.Cards(Rows, [], facts);
        Card(cards, "specification").Value.Should().Be("7 findings");
        Card(cards, "specification").Status.Should().Be(DashboardStatus.AttentionNeeded);
        Card(cards, "traceability").Value.Should().Be("2 of 3 transitions assessed");
        Card(cards, "attention").Value.Should().Be("0");
    }

    [Fact]
    public void Findings_AreGroupedByStableKey_NeverBySimilarText()
    {
        DashboardAttentionItem[] items =
        [
            new("source", "Source missing", null, "Import", "x"),
            new("source-binding", "Binding not established", null, "Bind", "x"),
            new("severe-findings", "Severe findings", null, "Open", "x"),
            new("stale", "Results outdated", null, "Re-run", "x"),
            new("custom", "Source missing", null, "Open", "x"),
            new("info", "Note", null, "Open", "x", Informational: true),
        ];
        DashboardQualitySummary.Group("source").Should().Be("Missing required evidence");
        DashboardQualitySummary.Group("custom").Should().Be("Other", "same text, different key: not merged");
        DashboardQualitySummary.Groups(items).Should().BeEquivalentTo(["Missing required evidence", "Specification quality", "Outdated results", "Other"]);
        DashboardQualitySummary.Cards(Rows, items, new DashboardReviewFacts()).Single(c => c.Key == "attention").Value.Should().Be("5", "informational notes are not counted");
    }

}
