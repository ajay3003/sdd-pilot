using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Components;

public class SpecComparisonPanelSaveTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _extractionService = new();
    private readonly Mock<ISaveQaDeltaReviewMutation> _saveMutation = new();
    private readonly Mock<IBirkNextClient> _client = new();

    public SpecComparisonPanelSaveTests()
    {
        var config = new Mock<IExtractionConfiguration>();
        config.Setup(c => c.MaxInputLengthChars).Returns(50_000);

        _client.Setup(c => c.SaveQaDeltaReview).Returns(_saveMutation.Object);

        Services.AddSingleton(config.Object);
        Services.AddSingleton(_extractionService.Object);
        Services.AddSingleton<ISpecComparisonService, SpecComparisonService>();
        Services.AddSingleton(_client.Object);
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    [Fact]
    public void SaveSection_NotPresent_BeforeComparison()
    {
        var cut = Render<SpecComparisonPanel>();

        cut.FindAll("[data-testid='save-review-section']").Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSection_AppearsAfterComparison()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='save-review-section']").Should().NotBeNull();
    }

    [Fact]
    public async Task ReviewTitleInput_HasDefaultValueWithDeltaReviewPrefix()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        var titleInput = cut.Find("[data-testid='review-title-input']");
        titleInput.GetAttribute("value").Should().StartWith("Delta Review –");
    }

    [Fact]
    public async Task SaveButton_EmptyTitle_ShowsValidationError_AndDoesNotCallMutation()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='review-title-input']").Input(string.Empty);
        cut.Find("[data-testid='save-review-button']").Click();

        cut.Find("[role='alert']").TextContent.Should().Contain("Title is required");
        _saveMutation.Verify(
            m => m.ExecuteAsync(It.IsAny<SaveQaDeltaReviewInput>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveButton_SuccessfulSave_ShowsSuccessMessage()
    {
        SetupModifiedRequirement();
        SetupSuccessfulSave();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='save-review-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-review-success']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='save-review-success']").TextContent
            .Should().Contain("Saved");
        cut.Find("[data-testid='save-review-success'] a")
            .GetAttribute("href").Should().Contain("compare/reviews");
    }

    [Fact]
    public async Task SaveButton_SuccessfulSave_CallsMutationOnce()
    {
        SetupModifiedRequirement();
        SetupSuccessfulSave();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='save-review-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-review-success']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        _saveMutation.Verify(
            m => m.ExecuteAsync(It.IsAny<SaveQaDeltaReviewInput>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveButton_ApiError_ShowsInlineError()
    {
        SetupModifiedRequirement();
        SetupFailedSave("Server error");
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='save-review-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-review-error']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='save-review-error']").TextContent.Should().Contain("Save failed");
    }

    [Fact]
    public async Task SaveButton_NetworkException_ShowsInlineError()
    {
        SetupModifiedRequirement();
        _saveMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveQaDeltaReviewInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network unavailable"));
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='save-review-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-review-error']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='save-review-error']").TextContent.Should().Contain("Save failed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupModifiedRequirement()
    {
        _extractionService
            .Setup(s => s.ExtractAsync("old", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([Candidate("FR-001: The system MUST allow password login", ScenarioKind.Requirement)]));
        _extractionService
            .Setup(s => s.ExtractAsync("new", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([Candidate("FR-001: The system MUST allow passwordless login", ScenarioKind.Requirement)]));
    }

    private void SetupSuccessfulSave()
    {
        var mockReview = new Mock<ISaveQaDeltaReview_SaveQaDeltaReview_Review>();
        mockReview.Setup(r => r.Id).Returns("rev-1");
        mockReview.Setup(r => r.Title).Returns("Test Review");
        mockReview.Setup(r => r.CreatedAt).Returns(DateTimeOffset.UtcNow);

        var mockPayload = new Mock<ISaveQaDeltaReview_SaveQaDeltaReview>();
        mockPayload.Setup(p => p.Review).Returns(mockReview.Object);
        mockPayload.Setup(p => p.Errors).Returns([]);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-1");

        var mockData = new Mock<ISaveQaDeltaReviewResult>();
        mockData.Setup(d => d.SaveQaDeltaReview).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ISaveQaDeltaReviewResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);

        _saveMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveQaDeltaReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);
    }

    private void SetupFailedSave(string errorMessage)
    {
        var mockPayload = new Mock<ISaveQaDeltaReview_SaveQaDeltaReview>();
        mockPayload.Setup(p => p.Review).Returns((ISaveQaDeltaReview_SaveQaDeltaReview_Review?)null);
        mockPayload.Setup(p => p.Errors).Returns([]);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-1");

        var mockData = new Mock<ISaveQaDeltaReviewResult>();
        mockData.Setup(d => d.SaveQaDeltaReview).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ISaveQaDeltaReviewResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);

        _saveMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveQaDeltaReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);
    }

    private async Task<IRenderedComponent<SpecComparisonPanel>> RenderWithResultAsync()
    {
        var cut = Render<SpecComparisonPanel>();
        cut.Find("[data-testid='old-spec-textarea']").Input("old");
        cut.Find("[data-testid='new-spec-textarea']").Input("new");
        cut.Find("[data-testid='run-comparison-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='delta-dashboard']").Count == 1,
            timeout: TimeSpan.FromSeconds(1));

        return cut;
    }

    private static ExtractionCandidate Candidate(string title, ScenarioKind kind) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    private static ExtractionPipelineResult MakeResult(IReadOnlyList<ExtractionCandidate> candidates) =>
        ExtractionPipelineResult.Success(
            candidates,
            inputLengthChars: 100,
            inputLineCount: 5,
            durationMs: 10,
            requirementCount: candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            testCount: candidates.Count(c => c.Classification == ScenarioKind.Test),
            needsClarificationCount: candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification));
}
