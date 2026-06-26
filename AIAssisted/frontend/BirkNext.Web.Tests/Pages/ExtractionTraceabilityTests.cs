using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public class ExtractionTraceabilityTests : BunitContext
{
    public ExtractionTraceabilityTests()
    {
        Services.AddSingleton(new Mock<ICreateScenariosMutation>().Object);
        Services.AddSingleton(new Mock<ISaveReviewedCandidatesMutation>().Object);
        Services.AddSingleton(new Mock<ISaveCandidateLinksMutation>().Object);
        Services.AddSingleton(new Mock<IGetReviewedCandidatesQuery>().Object);
        Services.AddSingleton(new Mock<IExtractionSessionService>().Object);
        Services.AddSingleton<ILogger<ExtractionReviewList>>(NullLogger<ExtractionReviewList>.Instance);
        Services.AddSingleton<FeatureVisibilityService>();
    }

    private static ExtractionPipelineResult NoResults() =>
        ExtractionPipelineResult.NonSuccess(PipelineStatus.NoResults, 0, 0, 0);

    private static ExtractionCandidate Requirement(string title = "FR-001 The system shall allow login") =>
        new ExtractionCandidate
        {
            Title = title,
            Classification = ScenarioKind.Requirement,
            ClassificationSignal = ClassificationSignal.FrPrefix,
            SourceBlockType = BlockType.ParagraphLine,
        };

    [Fact]
    public void TraceabilityPage_DoesNotRenderExtractionControls()
    {
        var cut = Render<ExtractionReviewList>(p => p
            .Add(x => x.PipelineResult, NoResults()));

        cut.FindAll("[data-testid='extraction-health-summary']").Should().BeEmpty();
        cut.FindAll("[data-testid='save-review-button']").Should().BeEmpty();
        cut.FindAll("[data-testid='confirm-save-button']").Should().BeEmpty();
    }

    [Fact]
    public void TraceabilityPage_UsesOnlyCoverageModel()
    {
        var cut = Render<TraceabilityView>(p => p
            .Add(x => x.SpecMarkdown, string.Empty)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>())
            .Add(x => x.Links, Array.Empty<CandidateLinkEntry>()));

        cut.FindAll("[data-testid='extraction-health-summary']").Should().BeEmpty();
        cut.FindAll("[data-testid='save-review-button']").Should().BeEmpty();
        cut.Markup.Should().NotContain("AutoAccepted");
        cut.Markup.Should().NotContain("Manually Accepted");
    }

    [Fact]
    public void ExtractionReview_IsAdvancedViewMode()
    {
        var cut = Render<ExtractionReviewList>(p => p
            .Add(x => x.PipelineResult, NoResults()));

        cut.Markup.Should().NotContain("Optional Extraction Review");
        ExtractionViewMode.Traceability.Should().NotBe(ExtractionViewMode.Extraction);
    }

    [Fact]
    public void LinkPanel_IsSimplifiedSuggestionsOnly()
    {
        var cut = Render<CandidateLinkPanel>(p => p
            .Add(x => x.Candidate, Requirement())
            .Add(x => x.Links, Array.Empty<CandidateLinkEntry>())
            .Add(x => x.LinkableCandidates, Array.Empty<ExtractionCandidate>()));

        cut.Find(".link-panel-title").TextContent.Trim().Should().Be("Suggested Traceability Links");
        cut.Markup.Should().NotContain("Link Related Candidates");
    }

    [Fact]
    public void NoAutoAcceptedStateInTraceabilityUI()
    {
        var cut = Render<TraceabilityView>(p => p
            .Add(x => x.SpecMarkdown, string.Empty)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>())
            .Add(x => x.Links, Array.Empty<CandidateLinkEntry>()));

        cut.Markup.Should().NotContain("AutoAccepted");
        cut.Markup.Should().NotContain("Manually Accepted");
        cut.Markup.Should().NotContain("Rejected");
    }
}
