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

    [Fact]
    public void TraceabilityFilters_RenderCoverageCountsBeforeLinksExist()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("FR-001: The system MUST validate credentials", ScenarioKind.Requirement),
            MakeCandidate("FR-002: The system MUST log sign-ins", ScenarioKind.Requirement),
            MakeCandidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test),
            MakeCandidate("What happens when the identity provider is down?", ScenarioKind.NeedsClarification),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var traceability = cut.Find(".traceability-filter").TextContent;

        traceability.Should().Contain("Requirements without tests");
        traceability.Should().Contain("2");
        traceability.Should().Contain("Tests without requirements");
        traceability.Should().Contain("1");
        traceability.Should().Contain("Clarifications without requirements");
        traceability.Should().Contain("1");
    }

    [Fact]
    public void ExtractionReviewList_DoesNotRenderDashboardWidgets()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        cut.FindAll("[data-testid='coverage-dashboard']").Should().BeEmpty();
        cut.FindAll("[data-testid='candidate-row']").Should().NotBeEmpty();
        cut.Find("[data-testid='confirm-save-button']").Should().NotBeNull();
    }

    [Fact]
    public void RequirementsWithTests_FilterAndIndicator_UpdateAfterManualLink()
    {
        var requirement = MakeCandidate("FR-001: The system MUST validate credentials", ScenarioKind.Requirement);
        var test = MakeCandidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test);

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([requirement, test])));

        AddFirstAvailableLinkFromFirstRow(cut);

        cut.FindAll("[data-testid='link-indicator']")
            .Select(i => i.TextContent)
            .Should().Contain(t => t.Contains("1 test"));

        ClickTraceabilityFilter(cut, "Requirements with tests");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("FR-001");
        cut.Find(".traceability-filter").TextContent.Should().Contain("Requirements with tests 1");
    }

    [Fact]
    public void RequirementsWithoutTests_FilterExcludesLinkedRequirements()
    {
        var linkedRequirement = MakeCandidate("FR-001: The system MUST validate credentials", ScenarioKind.Requirement);
        var unlinkedRequirement = MakeCandidate("FR-002: The system MUST log sign-ins", ScenarioKind.Requirement);
        var test = MakeCandidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test);

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([linkedRequirement, unlinkedRequirement, test])));

        AddFirstAvailableLinkFromFirstRow(cut);
        ClickTraceabilityFilter(cut, "Requirements without tests");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("FR-002");
        rows[0].TextContent.Should().NotContain("FR-001");
        cut.Find(".traceability-filter").TextContent.Should().Contain("Requirements without tests 1");
    }

    [Fact]
    public void TestsWithoutRequirements_FilterExcludesTestsLinkedToRequirements()
    {
        var requirement = MakeCandidate("FR-001: The system MUST validate credentials", ScenarioKind.Requirement);
        var linkedTest = MakeCandidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test);
        var unlinkedTest = MakeCandidate("Given invalid credentials When submitted Then error appears", ScenarioKind.Test);

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([requirement, linkedTest, unlinkedTest])));

        AddFirstAvailableLinkFromFirstRow(cut);
        ClickTraceabilityFilter(cut, "Tests without requirements");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("invalid credentials");
        cut.Find(".traceability-filter").TextContent.Should().Contain("Tests without requirements 1");
    }

    [Fact]
    public void ClarificationsWithoutRequirements_FilterExcludesClarificationsLinkedToRequirements()
    {
        var requirement = MakeCandidate("FR-001: The system MUST validate credentials", ScenarioKind.Requirement);
        var linkedClarification = MakeCandidate("What happens when the identity provider is down?", ScenarioKind.NeedsClarification);
        var unlinkedClarification = MakeCandidate("Who owns retry policy?", ScenarioKind.NeedsClarification);

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([requirement, linkedClarification, unlinkedClarification])));

        cut.Find("[data-testid='link-indicator']").Click();
        var clarificationSection = cut.FindAll(".candidate-link-panel .link-section")
            .Single(s => s.TextContent.Contains("Clarifications"));
        clarificationSection.QuerySelector(".link-add-btn")!.Click();
        cut.FindAll(".candidate-link-panel .link-section")
            .Single(s => s.TextContent.Contains("Clarifications"))
            .QuerySelector(".link-picker-item")!
            .Click();

        ClickTraceabilityFilter(cut, "Clarifications without requirements");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("retry policy");
        cut.Find(".traceability-filter").TextContent.Should().Contain("Clarifications without requirements 1");
    }

    private static void AddFirstAvailableLinkFromFirstRow(IRenderedComponent<ExtractionReviewList> cut)
    {
        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find(".candidate-link-panel .link-add-btn").Click();
        cut.Find(".candidate-link-panel .link-picker-item").Click();
    }

    private static void AddClarificationLinkFromRow(IRenderedComponent<ExtractionReviewList> cut, string rowText)
    {
        var row = cut.FindAll("[data-testid='candidate-row']")
            .Single(r => r.TextContent.Contains(rowText));
        row.QuerySelector("[data-testid='link-indicator']")!.Click();
        var clarificationSection = cut.FindAll(".candidate-link-panel .link-section")
            .Last(s => s.TextContent.Contains("Clarifications"));
        clarificationSection.QuerySelector(".link-add-btn")!.Click();
        cut.FindAll(".candidate-link-panel .link-section")
            .Last(s => s.TextContent.Contains("Clarifications"))
            .QuerySelector(".link-picker-item")!
            .Click();
    }

    private static void ClickTraceabilityFilter(IRenderedComponent<ExtractionReviewList> cut, string label)
    {
        cut.FindAll(".traceability-filter .filter-chip")
            .Single(b => b.TextContent.Contains(label))
            .Click();
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

// ── T090 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests that TEST candidates are grouped by ContextHeading into collapsible subsections,
/// while REQUIREMENT and NEEDS_CLARIFICATION sections remain flat.
/// </summary>
public class TestSubsectionGroupingTests : BunitContext
{
    private readonly Mock<ICreateScenariosMutation> _mockCreateMutation = new();

    public TestSubsectionGroupingTests()
    {
        Services.AddSingleton(_mockCreateMutation.Object);

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

    private static ExtractionCandidate MakeTest(string title, string? heading = null) => new()
    {
        Title = title,
        Classification = ScenarioKind.Test,
        ClassificationSignal = ClassificationSignal.BddPattern,
        SourceBlockType = BlockType.UnorderedListItem,
        ContextHeading = heading,
    };

    private static ExtractionPipelineResult MakeResult(IReadOnlyList<ExtractionCandidate> candidates)
    {
        var req  = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        var test = candidates.Count(c => c.Classification == ScenarioKind.Test);
        var nc   = candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification);
        return ExtractionPipelineResult.Success(candidates, 100, 5, 10, req, test, nc);
    }

    [Fact]
    public void SameContextHeading_RenderedInSameSubsection()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeTest("Given user logs in when credentials are valid", "User Story 1"),
            MakeTest("Given admin views the dashboard", "User Story 2"),
            MakeTest("Then user sees the home screen", "User Story 1"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2, "two unique headings → two subsection groups");

        var us1 = subsections.First(s => s.TextContent.Contains("User Story 1"));
        us1.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);

        var us2 = subsections.First(s => s.TextContent.Contains("User Story 2"));
        us2.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(1);
    }

    [Fact]
    public void NullContextHeading_GroupedUnderOtherTests()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeTest("Given user logs in", null),
            MakeTest("Then system validates token", null),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1);
        subsections[0].TextContent.Should().Contain("Other Tests");
        subsections[0].QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);
    }

    [Fact]
    public void RequirementSection_HasOwnSubgroups_WhenContextHeadingsPresent()
    {
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                Title = "System must validate input",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "User Story 1",
            },
            new()
            {
                Title = "System must log errors",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "User Story 2",
            },
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='group-requirement']").Should().NotBeNull();
        cut.FindAll("[data-testid='test-subsection-group']").Should().HaveCount(2,
            "requirement candidates with context headings are grouped into subsections");
    }

    [Fact]
    public void SearchFilter_AppliesWithinSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeTest("Given user logs in with valid credentials", "User Story 1"),
            MakeTest("Given admin resets the password", "User Story 1"),
            MakeTest("Then system shows confirmation dialog", "User Story 2"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("admin");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().HaveCount(1);
        rows[0].TextContent.Should().Contain("admin");
    }

    [Fact]
    public void CheckboxSelection_WorksInSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeTest("Given user opens the app", "User Story 1"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void EmptyGroup_NotRendered_WhenSearchHidesAllCandidatesInThatGroup()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeTest("Given user logs in", "User Story 1"),
            MakeTest("Given admin configures system", "User Story 2"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("admin");

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1, "group with zero visible results should not be rendered");
        subsections[0].TextContent.Should().Contain("User Story 2");
    }
}

// ── T092 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests that NEEDS_CLARIFICATION candidates are grouped by ContextHeading into collapsible
/// subsections, while REQUIREMENT and TEST sections remain unaffected.
/// </summary>
public class ClarificationSubsectionGroupingTests : BunitContext
{
    private readonly Mock<ICreateScenariosMutation> _mockCreateMutation = new();

    public ClarificationSubsectionGroupingTests()
    {
        Services.AddSingleton(_mockCreateMutation.Object);

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

    private static ExtractionCandidate MakeClarification(string title, string? heading = null) => new()
    {
        Title = title,
        Classification = ScenarioKind.NeedsClarification,
        ClassificationSignal = ClassificationSignal.ClarificationSignal,
        SourceBlockType = BlockType.UnorderedListItem,
        ContextHeading = heading,
    };

    private static ExtractionPipelineResult MakeResult(IReadOnlyList<ExtractionCandidate> candidates)
    {
        var req  = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        var test = candidates.Count(c => c.Classification == ScenarioKind.Test);
        var nc   = candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification);
        return ExtractionPipelineResult.Success(candidates, 100, 5, 10, req, test, nc);
    }

    [Fact]
    public void SameContextHeading_RenderedInSameSubsection()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeClarification("What happens when the token expires?", "Open Questions"),
            MakeClarification("Who owns the retry logic?", "Business Rules"),
            MakeClarification("Is this behaviour required for guest users?", "Open Questions"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2, "two unique headings → two subsection groups");

        var oq = subsections.First(s => s.TextContent.Contains("Open Questions"));
        oq.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);

        var br = subsections.First(s => s.TextContent.Contains("Business Rules"));
        br.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(1);
    }

    [Fact]
    public void NullContextHeading_GroupedUnderOtherClarifications()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeClarification("TBD: confirm error message wording", null),
            MakeClarification("TBD: confirm timeout value", null),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1);
        subsections[0].TextContent.Should().Contain("Other Clarifications");
        subsections[0].QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);
    }

    [Fact]
    public void TestSection_NotAffectedByClarificationGrouping()
    {
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                Title = "Given user submits form when all fields are valid",
                Classification = ScenarioKind.Test,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "User Story 1",
            },
            MakeClarification("What validation rules apply?", "Edge Cases"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        // Both TEST and NEEDS_CLARIFICATION have one subsection each
        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2);
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("User Story 1"));
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("Edge Cases"));
    }

    [Fact]
    public void RequirementSection_HasOwnSubgroups_WhenContextHeadingPresent()
    {
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                Title = "System must validate all required fields",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "Open Questions",
            },
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='group-requirement']").Should().NotBeNull();
        cut.FindAll("[data-testid='test-subsection-group']").Should().HaveCount(1,
            "requirement candidates with context headings are grouped into subsections");
    }

    [Fact]
    public void SearchFilter_AppliesWithinClarificationSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeClarification("What is the retry timeout?", "Edge Cases"),
            MakeClarification("Who approves the workflow?", "Business Rules"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("retry");

        cut.FindAll("[data-testid='candidate-row']").Should().HaveCount(1);
    }

    [Fact]
    public void CheckboxSelection_WorksInClarificationSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeClarification("TBD: define error recovery strategy", "Edge Cases"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void EmptyGroup_NotRendered_WhenSearchFiltersAllCandidates()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeClarification("What is the timeout?", "Edge Cases"),
            MakeClarification("Who owns the business rule?", "Business Rules"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("timeout");

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1, "the Business Rules group has no visible candidates");
        subsections[0].TextContent.Should().Contain("Edge Cases");
    }
}

// ── T093 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests that REQUIREMENT candidates are grouped by ContextHeading into collapsible subsections,
/// while TEST and NEEDS_CLARIFICATION sections remain unaffected.
/// </summary>
public class RequirementSubsectionGroupingTests : BunitContext
{
    private readonly Mock<ICreateScenariosMutation> _mockCreateMutation = new();

    public RequirementSubsectionGroupingTests()
    {
        Services.AddSingleton(_mockCreateMutation.Object);

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

    private static ExtractionCandidate MakeRequirement(string title, string? heading = null) => new()
    {
        Title = title,
        Classification = ScenarioKind.Requirement,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
        ContextHeading = heading,
    };

    private static ExtractionPipelineResult MakeResult(IReadOnlyList<ExtractionCandidate> candidates)
    {
        var req  = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        var test = candidates.Count(c => c.Classification == ScenarioKind.Test);
        var nc   = candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification);
        return ExtractionPipelineResult.Success(candidates, 100, 5, 10, req, test, nc);
    }

    [Fact]
    public void SameContextHeading_RenderedInSameSubsection()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST validate credentials", "Functional Requirements"),
            MakeRequirement("The system MUST enforce rate limits", "Non-Functional Requirements"),
            MakeRequirement("The system SHALL store hashed passwords", "Functional Requirements"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2, "two unique headings → two subsection groups");

        var func = subsections.First(s => s.TextContent.Contains("Functional Requirements"));
        func.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);

        var nonfunc = subsections.First(s => s.TextContent.Contains("Non-Functional Requirements"));
        nonfunc.QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(1);
    }

    [Fact]
    public void NullContextHeading_GroupedUnderOtherRequirements()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST respond within 200ms", null),
            MakeRequirement("The system SHALL support 1000 concurrent users", null),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1);
        subsections[0].TextContent.Should().Contain("Other Requirements");
        subsections[0].QuerySelectorAll("[data-testid='candidate-row']").Should().HaveCount(2);
    }

    [Fact]
    public void TestSection_NotAffectedByRequirementGrouping()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST validate input", "Functional Requirements"),
            new()
            {
                Title = "Given user submits form when all fields are valid",
                Classification = ScenarioKind.Test,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "User Story 1",
            },
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        // Both REQUIREMENT and TEST have one subsection each — grouping is independent
        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2);
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("Functional Requirements"));
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("User Story 1"));
    }

    [Fact]
    public void NeedsClarificationSection_NotAffectedByRequirementGrouping()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST log all errors", "Observability"),
            new()
            {
                Title = "TBD: confirm retry policy",
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.ClarificationSignal,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "Open Questions",
            },
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(2);
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("Observability"));
        subsections.Select(s => s.TextContent).Should().Contain(t => t.Contains("Open Questions"));
    }

    [Fact]
    public void SearchFilter_AppliesWithinRequirementSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST validate credentials", "Functional Requirements"),
            MakeRequirement("The system MUST enforce rate limits", "Functional Requirements"),
            MakeRequirement("The system SHALL respond within 200ms", "Performance"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("rate");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().HaveCount(1);
        rows[0].TextContent.Should().Contain("rate");
    }

    [Fact]
    public void CheckboxSelection_WorksInRequirementSubgroups()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST validate input", "Functional Requirements"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void EmptyGroup_NotRendered_WhenSearchHidesAllCandidatesInThatGroup()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeRequirement("The system MUST validate credentials", "Functional Requirements"),
            MakeRequirement("The system SHALL respond within 200ms", "Performance"),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));

        cut.Find(".search-input").Input("200ms");

        var subsections = cut.FindAll("[data-testid='test-subsection-group']");
        subsections.Should().HaveCount(1, "Functional Requirements group has no visible candidates");
        subsections[0].TextContent.Should().Contain("Performance");
    }
}
