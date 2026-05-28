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

public class ExtractionReviewListSessionTests : BunitContext
{
    private readonly Mock<IExtractionSessionService> _mockSession = new();

    public ExtractionReviewListSessionTests()
    {
        Services.AddSingleton(_mockSession.Object);

        _mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        _mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        _mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        _mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);

        var mockMutation = new Mock<ICreateScenariosMutation>();
        Services.AddSingleton(mockMutation.Object);

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

        Services.AddLogging();
    }

    private static CandidateSnapshot MakeCandidateSnapshot(
        Guid? id = null,
        string title = "The system shall allow login",
        ScenarioKind kind = ScenarioKind.Requirement,
        bool isSelected = false,
        CandidateReviewStatus reviewStatus = CandidateReviewStatus.New) => new(
            CandidateId: id ?? Guid.NewGuid(),
            Title: title,
            Classification: kind,
            ClassificationSignal: ClassificationSignal.Rfc2119Uppercase,
            ContextHeading: null,
            SourceBlockType: BlockType.UnorderedListItem,
            Confidence: null,
            IsSelected: isSelected,
            ReviewStatus: reviewStatus,
            SaveState: CandidateSaveState.Pending,
            SaveError: null,
            SavedScenarioId: null);

    private static ExtractionSessionSnapshot MakeSnapshot(
        IEnumerable<CandidateSnapshot>? candidates = null,
        ScenarioKind? activeFilter = null,
        string searchTerm = "",
        List<Guid>? selectedIds = null) => new()
        {
            SessionId = "review-list-test-session",
            Timestamp = DateTimeOffset.UtcNow,
            Profile = ExtractionProfile.Speckit,
            PipelineStatus = PipelineStatus.Success,
            InputLengthChars = 100,
            InputLineCount = 5,
            DurationMs = 10,
            Candidates = candidates?.ToList() ?? [MakeCandidateSnapshot()],
            ActiveFilter = activeFilter,
            SearchTerm = searchTerm,
            SelectedIds = selectedIds ?? [],
        };

    // ── Restore notice ───────────────────────────────────────────────────────

    [Fact]
    public void WithInitialSession_ShowsRestoreNotice()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("[data-testid='session-restore-notice']").Should().NotBeNull(
            "restore notice must be visible when InitialSession is provided");
    }

    [Fact]
    public void WithoutInitialSession_NoRestoreNotice()
    {
        var candidate = new ExtractionCandidate
        {
            Title = "The system shall allow login",
            Classification = ScenarioKind.Requirement,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
        };
        var pipelineResult = ExtractionPipelineResult.Success(
            [candidate], inputLengthChars: 100, inputLineCount: 5, durationMs: 10,
            requirementCount: 1, testCount: 0, needsClarificationCount: 0);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult));

        cut.FindAll("[data-testid='session-restore-notice']").Should().BeEmpty(
            "restore notice must not appear when no session was restored");
    }

    [Fact]
    public void DismissRestoreNotice_HidesNotice()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("[data-testid='session-restore-notice']").Should().NotBeNull();
        cut.Find("[data-testid='session-restore-dismiss']").Click();

        cut.FindAll("[data-testid='session-restore-notice']").Should().BeEmpty(
            "clicking the dismiss button must remove the restore notice");
    }

    // ── Restore state ────────────────────────────────────────────────────────

    [Fact]
    public void WithInitialSession_RestoresTypeFilter()
    {
        var candidates = new[]
        {
            MakeCandidateSnapshot(title: "System MUST validate", kind: ScenarioKind.Requirement),
            MakeCandidateSnapshot(title: "Given login When valid Then redirect", kind: ScenarioKind.Test),
        };
        var snapshot = MakeSnapshot(candidates: candidates, activeFilter: ScenarioKind.Requirement);
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find(".filter-chip-requirement").ClassList
            .Should().Contain("is-active", "REQUIREMENT filter chip must be active after session restore");
        cut.FindAll("[data-testid='candidate-row']")
            .Should().HaveCount(1, "only the requirement candidate should be visible when filter is active");
    }

    [Fact]
    public void WithInitialSession_RestoresSearchTerm()
    {
        var snapshot = MakeSnapshot(searchTerm: "login");
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("input[type='search']").GetAttribute("value")
            .Should().Be("login", "search term must be restored from session snapshot");
    }

    [Fact]
    public void WithInitialSession_SelectedCandidateCheckboxIsChecked()
    {
        var candidateId = Guid.NewGuid();
        var candidate = MakeCandidateSnapshot(id: candidateId, isSelected: true);
        var snapshot = MakeSnapshot(candidates: [candidate], selectedIds: [candidateId]);
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("[data-testid='candidate-checkbox']").HasAttribute("checked")
            .Should().BeTrue("restored candidate with IsSelected=true must render with checkbox checked");
    }

    [Fact]
    public void WithInitialSession_RestoresAcceptedReviewStatus()
    {
        var candidate = MakeCandidateSnapshot(reviewStatus: CandidateReviewStatus.Accepted);
        var snapshot = MakeSnapshot(candidates: [candidate]);
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("[data-testid='candidate-row']").ClassList
            .Should().Contain("is-review-accepted",
                "candidate row must reflect Accepted review status restored from session");
    }

    // ── Session save on state mutations ──────────────────────────────────────

    [Fact]
    public async Task TypeFilterClick_SavesSession()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find(".filter-chip-requirement").Click();

        await cut.WaitForStateAsync(
            () => _mockSession.Invocations.Any(i => i.Method.Name == "SaveAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockSession.Verify(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>()), Times.AtLeastOnce,
            "SaveAsync must be called when the type filter changes");
    }

    [Fact]
    public async Task SearchInput_SavesSession()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("input[type='search']").Input("login");

        await cut.WaitForStateAsync(
            () => _mockSession.Invocations.Any(i => i.Method.Name == "SaveAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockSession.Verify(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>()), Times.AtLeastOnce,
            "SaveAsync must be called when the search term changes");
    }

    [Fact]
    public async Task SelectionToggle_SavesSession()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(
            () => _mockSession.Invocations.Any(i => i.Method.Name == "SaveAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockSession.Verify(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>()), Times.AtLeastOnce,
            "SaveAsync must be called when a candidate is selected");
    }

    [Fact]
    public async Task ReviewStatusChange_SavesSession()
    {
        var snapshot = MakeSnapshot();
        var pipelineResult = ExtractionPipelineResult.Restore(snapshot);

        var cut = Render<ExtractionReviewList>(p => p
            .Add(c => c.PipelineResult, pipelineResult)
            .Add(c => c.InitialSession, snapshot));

        cut.Find(".review-action-accept").Click();

        await cut.WaitForStateAsync(
            () => _mockSession.Invocations.Any(i => i.Method.Name == "SaveAsync"),
            timeout: TimeSpan.FromSeconds(2));

        _mockSession.Verify(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>()), Times.AtLeastOnce,
            "SaveAsync must be called when a candidate review status changes");
    }
}
