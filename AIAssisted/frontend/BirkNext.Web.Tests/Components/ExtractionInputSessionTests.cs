using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

public class ExtractionInputSessionTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _mockExtraction = new();
    private readonly Mock<IExtractionSessionService> _mockSession = new();

    public ExtractionInputSessionTests()
    {
        var mockConfig = new Mock<IExtractionConfiguration>();
        mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);

        Services.AddSingleton(_mockExtraction.Object);
        Services.AddSingleton(mockConfig.Object);
        Services.AddSingleton(_mockSession.Object);
        Services.AddLogging();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    private static ExtractionSessionSnapshot MakeActiveSession() => new()
    {
        SessionId = "input-test-session",
        Timestamp = DateTimeOffset.UtcNow,
        Profile = ExtractionProfile.Speckit,
        PipelineStatus = PipelineStatus.Success,
        InputLengthChars = 100,
        InputLineCount = 5,
        DurationMs = 10,
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

    private static ExtractionPipelineResult MakeSuccessResult() =>
        ExtractionPipelineResult.Success(
            candidates:
            [
                new ExtractionCandidate
                {
                    Title = "The system shall do something",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                    SourceBlockType = BlockType.UnorderedListItem,
                }
            ],
            inputLengthChars: 50, inputLineCount: 1, durationMs: 5,
            requirementCount: 1, testCount: 0, needsClarificationCount: 0);

    [Fact]
    public async Task Extract_WhenNoActiveSession_RunsImmediatelyWithoutDialog()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _mockExtraction.Invocations.Any(i => i.Method.Name == "ExtractAsync"),
            timeout: TimeSpan.FromSeconds(2));

        cut.FindAll("[data-testid='replace-session-dialog']").Should().BeEmpty(
            "replace dialog must not appear when there is no active session");
        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "extraction must run immediately when no session is active");
    }

    [Fact]
    public async Task Extract_WhenActiveSession_ShowsReplaceDialog_NotExtracting()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(MakeActiveSession());
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='replace-session-dialog']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='replace-session-dialog']").Should().NotBeNull(
            "replace dialog must appear when an active session exists");
        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "extraction must not run until the user confirms the replace dialog");
    }

    [Fact]
    public async Task ReplaceDialog_Cancel_DismissesDialogWithoutExtracting()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(MakeActiveSession());
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='replace-session-dialog']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='replace-cancel-btn']").Click();

        cut.FindAll("[data-testid='replace-session-dialog']").Should().BeEmpty(
            "clicking cancel must dismiss the replace dialog");
        cut.FindAll("[data-testid='extract-button']").Should().HaveCount(1,
            "extract button must be visible again after cancelling");
        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "extraction must not run after cancel");
    }

    [Fact]
    public async Task ReplaceDialog_Confirm_ClearsSessionAndRunsExtraction()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(MakeActiveSession());
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        _mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='replace-session-dialog']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='replace-confirm-btn']").Click();

        await cut.WaitForStateAsync(
            () => _mockExtraction.Invocations.Any(i => i.Method.Name == "ExtractAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockSession.Verify(s => s.ClearAsync(), Times.Once,
            "session must be cleared before starting a replacement extraction");
        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "extraction must run after the user confirms replace");
        cut.FindAll("[data-testid='replace-session-dialog']").Should().BeEmpty(
            "dialog must be dismissed after confirming replace");
    }

    [Fact]
    public async Task Extract_WhenExpiredSession_RunsImmediatelyWithoutDialog()
    {
        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync(MakeActiveSession());
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(true);
        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult());

        var cut = Render<ExtractionInput>();
        cut.Find("[data-testid='spec-textarea']").Input("some spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => _mockExtraction.Invocations.Any(i => i.Method.Name == "ExtractAsync"),
            timeout: TimeSpan.FromSeconds(2));

        cut.FindAll("[data-testid='replace-session-dialog']").Should().BeEmpty(
            "replace dialog must not appear for an expired session");
        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "extraction must run immediately past an expired session");
    }
}
