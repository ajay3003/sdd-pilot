using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class CompareSpecsPageTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _extractionService = new();

    public CompareSpecsPageTests()
    {
        var config = new Mock<IExtractionConfiguration>();
        config.Setup(c => c.MaxInputLengthChars).Returns(50_000);

        var mockSaveMutation = new Mock<ISaveQaDeltaReviewMutation>();
        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.SaveQaDeltaReview).Returns(mockSaveMutation.Object);

        Services.AddSingleton(config.Object);
        Services.AddSingleton(_extractionService.Object);
        Services.AddSingleton<ISpecComparisonService, SpecComparisonService>();
        Services.AddSingleton(mockClient.Object);
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    [Fact]
    public void CompareRoute_Renders()
    {
        var cut = Render<CompareSpecs>();

        cut.Markup.Should().Contain("Compare Specs");
        cut.Find("[data-testid='spec-comparison-panel']").Should().NotBeNull();
    }

    [Fact]
    public void CompareRoute_RendersOldAndNewSpecInputs()
    {
        var cut = Render<CompareSpecs>();

        cut.Find("[data-testid='old-spec-textarea']").Should().NotBeNull();
        cut.Find("[data-testid='new-spec-textarea']").Should().NotBeNull();
        cut.Markup.Should().Contain("Baseline Specification");
        cut.Markup.Should().Contain("Updated Specification");
    }

    [Fact]
    public async Task ComparisonRuns_WithValidText()
    {
        _extractionService
            .Setup(s => s.ExtractAsync("old spec", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result([Candidate("FR-001: The system MUST allow password login", ScenarioKind.Requirement)]));
        _extractionService
            .Setup(s => s.ExtractAsync("new spec", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result([Candidate("FR-001: The system MUST allow passwordless login", ScenarioKind.Requirement)]));

        var cut = Render<CompareSpecs>();

        cut.Find("[data-testid='old-spec-textarea']").Input("old spec");
        cut.Find("[data-testid='new-spec-textarea']").Input("new spec");
        cut.Find("[data-testid='run-comparison-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='delta-dashboard']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        var dashboard = cut.Find("[data-testid='delta-dashboard']").TextContent;
        dashboard.Should().Contain("Changed");
        dashboard.Should().Contain("1");
    }

    [Fact]
    public void EmptyInputValidation_Works()
    {
        var cut = Render<CompareSpecs>();

        cut.Find("[data-testid='run-comparison-button']").Click();

        cut.Find("[data-testid='comparison-validation']")
            .TextContent.Should().Contain("Provide both old and new specification text");
        _extractionService.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ComparisonErrors_ShowInlineError()
    {
        _extractionService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Extraction failed"));

        var cut = Render<CompareSpecs>();

        cut.Find("[data-testid='old-spec-textarea']").Input("old spec");
        cut.Find("[data-testid='new-spec-textarea']").Input("new spec");
        cut.Find("[data-testid='run-comparison-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='comparison-error']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='comparison-error']")
            .TextContent.Should().Contain("Comparison failed");
        cut.FindAll("[data-testid='delta-dashboard']").Should().BeEmpty();
    }

    private static ExtractionCandidate Candidate(string title, ScenarioKind kind) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    private static ExtractionPipelineResult Result(IReadOnlyList<ExtractionCandidate> candidates)
    {
        return ExtractionPipelineResult.Success(
            candidates,
            inputLengthChars: 100,
            inputLineCount: 5,
            durationMs: 10,
            requirementCount: candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            testCount: candidates.Count(c => c.Classification == ScenarioKind.Test),
            needsClarificationCount: candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification));
    }
}
