using BirkNext.Web.Components;
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

public class ScenarioExtractionSessionTests : BunitContext
{
    private readonly Mock<IExtractionSessionService> _mockSession = new();
    private readonly Mock<IScenarioExtractionService> _mockExtraction = new();
    private readonly WorkspaceArtifactRepository _workspace = new();

    public ScenarioExtractionSessionTests()
    {
        var mockConfig = new Mock<IExtractionConfiguration>();
        mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);

        Services.AddSingleton(_mockExtraction.Object);
        Services.AddSingleton(mockConfig.Object);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton(new Mock<ICreateScenariosMutation>().Object);
        Services.AddSingleton<ISpecComparisonService, SpecComparisonService>();

        var mockSaveReview = new Mock<ISaveReviewedCandidatesMutation>();
        mockSaveReview
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveReviewedCandidatesInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveReviewedCandidatesResult>>());
        Services.AddSingleton(mockSaveReview.Object);

        var mockSaveLinks = new Mock<ISaveCandidateLinksMutation>();
        mockSaveLinks
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveCandidateLinksInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveCandidateLinksResult>>());
        Services.AddSingleton(mockSaveLinks.Object);

        _mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        _mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(_mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        Services.AddLogging();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true).SetVoidResult();
    }

    private static ExtractionSessionSnapshot MakeActiveSession(string specMarkdown = "") => new()
    {
        SessionId = "page-test-session",
        Timestamp = DateTimeOffset.UtcNow,
        Profile = ExtractionProfile.Speckit,
        PipelineStatus = PipelineStatus.Success,
        InputLengthChars = 100,
        InputLineCount = 5,
        DurationMs = 10,
        SpecMarkdown = specMarkdown,
        Candidates =
        [
            new CandidateSnapshot(
                CandidateId: Guid.NewGuid(),
                Title: "The system shall allow login",
                Classification: ScenarioKind.Requirement,
                ClassificationSignal: ClassificationSignal.Rfc2119Uppercase,
                ContextHeading: null,
                SourceBlockType: BlockType.UnorderedListItem,
                Confidence: null,
                IsSelected: false,
                ReviewStatus: CandidateReviewStatus.New,
                SaveState: CandidateSaveState.Pending,
                SaveError: null,
                SavedScenarioId: null)
        ],
    };

    [Fact]
    public async Task OnInit_WithNoSession_ShowsPreState()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);

        var cut = Render<ScenarioExtraction>();
        await cut.WaitForStateAsync(() => true);

        cut.Find("[data-testid='extract-pre-state']").Should().NotBeNull();
    }

    [Fact]
    public async Task OnInit_WithActiveSession_HidesPreState()
    {
        var session = MakeActiveSession();
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(session);

        var cut = Render<ScenarioExtraction>();
        await cut.WaitForStateAsync(() => cut.FindAll("[data-testid='extract-pre-state']").Count == 0);

        cut.FindAll("[data-testid='extract-pre-state']").Should().BeEmpty(
            "active session should restore pipeline result and hide the pre-state div");
    }

    [Fact]
    public async Task OnInit_WithActiveSession_ShowsCandidateSummary()
    {
        var session = MakeActiveSession();
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(session);

        var cut = Render<ScenarioExtraction>();
        await cut.WaitForStateAsync(() => cut.FindAll("[data-testid='extract-pre-state']").Count == 0);

        cut.Markup.Should().Contain("Analysis Results");
        cut.Markup.Should().Contain("The system shall allow login");
    }

    [Fact]
    public async Task OnInit_WithExpiredSession_ShowsPreState()
    {
        var expiredSession = new ExtractionSessionSnapshot
        {
            SessionId = "page-test-session",
            Timestamp = DateTimeOffset.UtcNow.AddHours(-3),
            Profile = ExtractionProfile.Speckit,
            PipelineStatus = PipelineStatus.Success,
            InputLengthChars = 100,
            InputLineCount = 5,
            DurationMs = 10,
            Candidates = MakeActiveSession().Candidates,
        };
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(expiredSession);
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(true);

        var cut = Render<ScenarioExtraction>();
        await cut.WaitForStateAsync(() => true);

        cut.Find("[data-testid='extract-pre-state']").Should().NotBeNull(
            "expired session must not restore the pipeline result");
    }

    [Fact]
    public async Task HandleExtractionCompleted_ShowsReviewList()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);

        var result = ExtractionPipelineResult.Success(
            [new ExtractionCandidate
            {
                Title = "The system shall log in",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                SourceBlockType = BlockType.UnorderedListItem,
            }],
            inputLengthChars: 50, inputLineCount: 1, durationMs: 5,
            requirementCount: 1, testCount: 0, needsClarificationCount: 0);

        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var cut = Render<ScenarioExtraction>();
        cut.Find("[data-testid='spec-textarea']").Input("some text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("The system shall log in"));

        cut.FindAll("[data-testid='extract-pre-state']").Should().BeEmpty();
    }

    [Fact]
    public async Task OnInit_WithWorkspaceSpec_DoesNotRestoreSessionFromDifferentSpec()
    {
        _workspace.Set(WorkspaceArtifactKind.Specification, "PROJECT B SPEC", "spec.md", "SampleData/proxy");
        var session = MakeActiveSession("PROJECT A SPEC");
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(session);

        var cut = Render<ScenarioExtraction>();
        await cut.WaitForStateAsync(() => true);

        cut.FindAll("[data-testid='candidate-summary']").Should().BeEmpty(
            "a global extraction session from another spec must not appear over the active workspace spec");
        cut.Find("[data-testid='spec-textarea']").GetAttribute("value")
            .Should().Be("PROJECT B SPEC");
    }
}
