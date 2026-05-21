using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Components;

public class ExtractionReviewListTests : BunitContext
{
    private readonly Mock<ICreateScenariosMutation> _mockMutation = new();

    public ExtractionReviewListTests()
    {
        Services.AddSingleton(_mockMutation.Object);
        Services.AddLogging();
    }

    private static ExtractionCandidate MakeCandidate(
        string title = "The system shall allow login",
        ScenarioKind kind = ScenarioKind.Requirement) => new()
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
        };

    private static ExtractionPipelineResult MakeResult(
        IReadOnlyList<ExtractionCandidate>? candidates = null,
        PipelineStatus status = PipelineStatus.Success)
    {
        candidates ??= [MakeCandidate()];

        if (status != PipelineStatus.Success)
            return ExtractionPipelineResult.NonSuccess(status, 0, 0, 0);

        var req = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        var test = candidates.Count(c => c.Classification == ScenarioKind.Test);
        var nc = candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification);

        return ExtractionPipelineResult.Success(
            candidates: candidates,
            inputLengthChars: 100,
            inputLineCount: 5,
            durationMs: 10,
            requirementCount: req,
            testCount: test,
            needsClarificationCount: nc);
    }

    private static IOperationResult<ICreateScenariosResult> MakeSuccessOperationResult(string scenarioId = "sc-1")
    {
        var mockScenario = new Mock<ICreateScenarios_CreateScenarios_Results_Scenario>();
        mockScenario.Setup(s => s.Id).Returns(scenarioId);
        mockScenario.Setup(s => s.Title).Returns("saved title");
        mockScenario.Setup(s => s.Kind).Returns(ScenarioKind.Requirement);

        var mockSuccess = new Mock<ICreateScenarios_CreateScenarios_Results_CreateScenarioSuccess>();
        mockSuccess.Setup(s => s.Scenario).Returns(mockScenario.Object);

        var mockPayload = new Mock<ICreateScenarios_CreateScenarios>();
        mockPayload.Setup(p => p.Results)
            .Returns(new List<ICreateScenarios_CreateScenarios_Results> { mockSuccess.Object });
        mockPayload.Setup(p => p.SuccessCount).Returns(1);
        mockPayload.Setup(p => p.FailureCount).Returns(0);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-1");

        var mockData = new Mock<ICreateScenariosResult>();
        mockData.Setup(d => d.CreateScenarios).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ICreateScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);

        return mockResult.Object;
    }

    private static IOperationResult<ICreateScenariosResult> MakeErrorOperationResult(string message = "Title too long")
    {
        var mockError = new Mock<ICreateScenarios_CreateScenarios_Results_CreateScenarioError>();
        mockError.Setup(e => e.Code).Returns("TITLE_TOO_LONG");
        mockError.Setup(e => e.Message).Returns(message);
        mockError.Setup(e => e.Field).Returns("title");

        var mockPayload = new Mock<ICreateScenarios_CreateScenarios>();
        mockPayload.Setup(p => p.Results)
            .Returns(new List<ICreateScenarios_CreateScenarios_Results> { mockError.Object });
        mockPayload.Setup(p => p.SuccessCount).Returns(0);
        mockPayload.Setup(p => p.FailureCount).Returns(1);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-err");

        var mockData = new Mock<ICreateScenariosResult>();
        mockData.Setup(d => d.CreateScenarios).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ICreateScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);

        return mockResult.Object;
    }

    [Fact]
    public void NullPipelineResult_RendersNothing()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, null));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void NoResults_ShowsEmptyStateMessage()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(status: PipelineStatus.NoResults)));

        cut.Find("[data-testid='empty-state']").Should().NotBeNull();
    }

    [Fact]
    public void CountSummaryHeader_ShowsCorrectTotals()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate(kind: ScenarioKind.Requirement),
            MakeCandidate(kind: ScenarioKind.Requirement),
            MakeCandidate(kind: ScenarioKind.Test),
            MakeCandidate(kind: ScenarioKind.NeedsClarification),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var summary = cut.Find("[data-testid='candidate-summary']").TextContent;
        summary.Should().Contain("4 candidates");
        summary.Should().Contain("2 REQUIREMENT");
        summary.Should().Contain("1 TEST");
        summary.Should().Contain("1 NEEDS_CLARIFICATION");
    }

    [Fact]
    public void Candidates_RenderedInThreeGroupsByClassification()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate(kind: ScenarioKind.Requirement),
            MakeCandidate(kind: ScenarioKind.Test),
            MakeCandidate(kind: ScenarioKind.NeedsClarification),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='group-requirement']").Should().NotBeNull();
        cut.Find("[data-testid='group-test']").Should().NotBeNull();
        cut.Find("[data-testid='group-needs-clarification']").Should().NotBeNull();
    }

    [Fact]
    public void NoCheckboxCheckedByDefault()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate(),
            MakeCandidate(),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var checkboxes = cut.FindAll("[data-testid='candidate-checkbox']");
        checkboxes.Should().AllSatisfy(cb => cb.HasAttribute("checked").Should().BeFalse());
    }

    [Fact]
    public void ConfirmSaveButton_DisabledWhenNoCandidatesSelected()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmSaveButton_EnabledWhenAtLeastOneCandidateSelected()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task OnSuccessfulSave_CandidateRowShowsSavedIndicator()
    {
        _mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessOperationResult());

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='confirm-save-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-saved']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.FindAll("[data-testid='save-saved']").Should().NotBeEmpty();
    }

    [Fact]
    public async Task OnErrorSave_CandidateRowShowsErrorMessage()
    {
        _mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeErrorOperationResult("Title too long"));

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='confirm-save-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-error']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='save-error']").TextContent.Should().Contain("Title too long");
    }

    [Fact]
    public async Task AfterCompleteSave_ReviewSavePhaseCompleteStateReached()
    {
        _mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessOperationResult());

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='confirm-save-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-complete']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='save-complete']").Should().NotBeNull();
    }
}

// ── T086 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Observability tests for ExtractionReviewList: verifies CandidateReviewAbandoned is emitted
/// when the user navigates away with unsaved candidates, is NOT emitted after a complete save,
/// and never contains candidate title text.
/// </summary>
public class ExtractionReviewListObservabilityTests : BunitContext
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    private readonly Mock<ICreateScenariosMutation> _mockMutation = new();
    private readonly CapturingLogger<ExtractionReviewList> _logger = new();

    public ExtractionReviewListObservabilityTests()
    {
        Services.AddSingleton(_mockMutation.Object);
        Services.AddSingleton<ILogger<ExtractionReviewList>>(_logger);
    }

    private static ExtractionCandidate MakeCandidate(string title = "sentinel-candidate-title") => new()
    {
        Title = title,
        Classification = ScenarioKind.Requirement,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    private static ExtractionPipelineResult MakeResult(IReadOnlyList<ExtractionCandidate>? candidates = null)
    {
        candidates ??= [MakeCandidate()];
        var req = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        return ExtractionPipelineResult.Success(candidates, 100, 5, 10, req, 0, 0);
    }

    private static IOperationResult<ICreateScenariosResult> MakeSuccessOperationResult()
    {
        var mockScenario = new Mock<ICreateScenarios_CreateScenarios_Results_Scenario>();
        mockScenario.Setup(s => s.Id).Returns("sc-obs-1");
        mockScenario.Setup(s => s.Title).Returns("saved");
        mockScenario.Setup(s => s.Kind).Returns(ScenarioKind.Requirement);

        var mockSuccess = new Mock<ICreateScenarios_CreateScenarios_Results_CreateScenarioSuccess>();
        mockSuccess.Setup(s => s.Scenario).Returns(mockScenario.Object);

        var mockPayload = new Mock<ICreateScenarios_CreateScenarios>();
        mockPayload.Setup(p => p.Results)
            .Returns(new List<ICreateScenarios_CreateScenarios_Results> { mockSuccess.Object });
        mockPayload.Setup(p => p.SuccessCount).Returns(1);
        mockPayload.Setup(p => p.FailureCount).Returns(0);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-obs");

        var mockData = new Mock<ICreateScenariosResult>();
        mockData.Setup(d => d.CreateScenarios).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ICreateScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        return mockResult.Object;
    }

    [Fact]
    public void Dispose_WithUnsavedCandidates_LogsCandidateReviewAbandoned()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        // Call Dispose() on the component instance directly; bUnit's cut.Dispose() only
        // disposes the test wrapper, not the Blazor component's IDisposable lifecycle.
        cut.Instance.Dispose();

        _logger.Messages.Should().Contain(m => m.Contains("CandidateReviewAbandoned"),
            "navigating away with unsaved candidates should log CandidateReviewAbandoned");
    }

    [Fact]
    public async Task Dispose_WhenSavePhaseComplete_DoesNotLogCandidateReviewAbandoned()
    {
        _mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessOperationResult());

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);
        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));
        cut.Find("[data-testid='confirm-save-button']").Click();
        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-complete']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Instance.Dispose();

        _logger.Messages.Should().NotContain(m => m.Contains("CandidateReviewAbandoned"),
            "after a complete save, disposing should not log CandidateReviewAbandoned");
    }

    [Fact]
    public void Dispose_CandidateReviewAbandoned_DoesNotContainCandidateTitle()
    {
        const string sentinelTitle = "unique-sentinel-candidate-title-should-not-appear";
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([MakeCandidate(sentinelTitle)])));

        cut.Instance.Dispose();

        // no raw text: verify the candidate title is absent from the abandon log message
        _logger.Messages
            .Where(m => m.Contains("CandidateReviewAbandoned"))
            .Should().NotContain(m => m.Contains(sentinelTitle),
                "CandidateReviewAbandoned must log only counts, never candidate title text");
    }

    [Fact]
    public void Dispose_WithNullPipelineResult_DoesNotLog()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, (ExtractionPipelineResult?)null));

        cut.Instance.Dispose();

        _logger.Messages.Should().NotContain(m => m.Contains("CandidateReviewAbandoned"));
    }
}
