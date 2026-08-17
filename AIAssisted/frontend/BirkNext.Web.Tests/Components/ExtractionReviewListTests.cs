using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
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
    private readonly Mock<ISaveReviewedCandidatesMutation> _mockSaveReview = new();

    public ExtractionReviewListTests()
    {
        Services.AddSingleton(_mockMutation.Object);

        _mockSaveReview
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveReviewedCandidatesInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveReviewedCandidatesResult>>());
        Services.AddSingleton(_mockSaveReview.Object);

        var mockSaveLinks = new Mock<ISaveCandidateLinksMutation>();
        mockSaveLinks
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveCandidateLinksInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveCandidateLinksResult>>());
        Services.AddSingleton(mockSaveLinks.Object);

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);

        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut)
    {
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();
    }

    private static void SelectAndAcceptFirst(IRenderedComponent<ExtractionReviewList> cut)
    {
        cut.Find("[data-testid='candidate-checkbox']").Change(true);
        cut.Find(".bulk-review-accept").Click();
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
    public void SpecificationExplorer_Tabs_AreInQaPriorityOrder()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        var tabs = cut.FindAll(".view-mode-tab").Select(t => t.TextContent.Trim()).ToList();

        tabs.Should().HaveCountGreaterThanOrEqualTo(5);
        tabs[0].Should().Contain("Traceability & Coverage");
        tabs[1].Should().Contain("Flow View");
        tabs[2].Should().Contain("Spec Explorer");
        tabs[3].Should().Contain("Extraction Review");
        tabs[4].Should().Contain("Architecture View");
    }

    [Fact]
    public void SpecificationExplorer_HidesAdvancedTabsWhenFeatureFlagsAreDisabled()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = false,
            EnableArchitectureView = false
        });

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        var tabs = cut.FindAll(".view-mode-tab").Select(t => t.TextContent.Trim()).ToList();

        tabs.Should().HaveCount(3);
        tabs[0].Should().Contain("Traceability & Coverage");
        tabs[1].Should().Contain("Flow View");
        tabs[2].Should().Contain("Spec Explorer");
        cut.Markup.Should().NotContain("Extraction Review");
        cut.Markup.Should().NotContain("Architecture View");
        cut.Find("[data-testid='analysis-workflow-hint']").TextContent
            .Should().NotContain("extraction quality");
    }

    [Fact]
    public void ExtractionReview_UsesNewName()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        var text = cut.Markup;
        text.Should().Contain("Extraction Review");
        text.Should().NotContain("Document" + " View");
    }

    [Fact]
    public void SpecificationExplorer_DefaultTabAfterAnalysis_IsTraceabilityCoverage()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        var activeTab = cut.FindAll(".view-mode-tab").Single(t => t.ClassList.Contains("is-active"));
        activeTab.TextContent.Should().Contain("Traceability & Coverage");
        cut.Markup.Should().Contain("tv-root");
    }

    [Fact]
    public void ExtractionReview_ShowsPurposeBanner()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        var banner = cut.Find("[data-testid='extraction-review-banner']").TextContent;
        banner.Should().Contain("Extraction Review");
        banner.Should().Contain("Artifacts have already been extracted");
        banner.Should().Contain("Review extraction quality");
        banner.Should().Contain("This step is optional for normal testing workflows");
    }

    [Fact]
    public void ExtractionReview_ExplainsReviewStatuses()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        var help = cut.Find("[data-testid='review-status-help']").TextContent;
        help.Should().Contain("AutoAccepted");
        help.Should().Contain("Automatically extracted and included");
        help.Should().Contain("Manually Accepted");
        help.Should().Contain("Reviewed and confirmed");
        help.Should().Contain("Needs Review");
        help.Should().Contain("Potential issue requiring attention");
        help.Should().Contain("Rejected");
        help.Should().Contain("Excluded from Traceability calculations");
    }

    [Fact]
    public void ExtractionReview_ShowsExtractionHealthSummary()
    {
        var rejected = MakeCandidate("FR-002: rejected", ScenarioKind.Requirement);
        rejected.ReviewStatus = CandidateReviewStatus.Rejected;
        var needsReview = MakeCandidate("FR-003: ambiguous", ScenarioKind.Requirement);
        needsReview.ReviewStatus = CandidateReviewStatus.NeedsReview;
        var accepted = MakeCandidate("FR-004: accepted", ScenarioKind.Requirement);
        accepted.ReviewStatus = CandidateReviewStatus.Accepted;

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([MakeCandidate(), rejected, needsReview, accepted])));
        OpenDocumentView(cut);

        var summary = cut.Find("[data-testid='extraction-health-summary']").TextContent;
        summary.Should().Contain("AutoAccepted");
        summary.Should().Contain("Needs Review");
        summary.Should().Contain("Rejected");
        summary.Should().Contain("Link Suggestions");
        summary.Should().Contain("Coverage Impact");
        summary.Should().Contain("artifacts currently excluded");
    }

    [Fact]
    public void ExtractionReview_EmptyStatesRender()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(status: PipelineStatus.NoResults)));

        cut.Find("[data-testid='empty-state']").TextContent.Should().Contain("No traceability data available yet");

        var panel = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, MakeCandidate())
            .Add(c => c.LinkableCandidates, Array.Empty<ExtractionCandidate>()));

        panel.Markup.Should().Contain("No suggested links available");
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
        OpenDocumentView(cut);

        var summary = cut.Find("[data-testid='candidate-summary']").TextContent;
        summary.Should().Contain("4 artifacts extracted");
        summary.Should().Contain("2 requirements");
        summary.Should().Contain("1 test");
        summary.Should().Contain("1 clarification");
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
        OpenDocumentView(cut);

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().HaveCount(3);
        rows.Select(r => r.TextContent).Should().Contain(t => t.Contains("REQUIREMENT"));
        rows.Select(r => r.TextContent).Should().Contain(t => t.Contains("TEST"));
        rows.Select(r => r.TextContent).Should().Contain(t => t.Contains("CLARIFICATION"));
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
        OpenDocumentView(cut);

        var checkboxes = cut.FindAll("[data-testid='candidate-checkbox']");
        checkboxes.Should().AllSatisfy(cb => cb.HasAttribute("checked").Should().BeFalse());
    }

    [Fact]
    public void ConfirmSaveButton_DisabledWhenNoCandidatesSelected()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmSaveButton_EnabledWhenAtLeastOneCandidateSelected()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        SelectAndAcceptFirst(cut);

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
        OpenDocumentView(cut);

        SelectAndAcceptFirst(cut);

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
        OpenDocumentView(cut);

        SelectAndAcceptFirst(cut);

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
        OpenDocumentView(cut);

        SelectAndAcceptFirst(cut);

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
        OpenDocumentView(cut);

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
        OpenDocumentView(cut);

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
        OpenDocumentView(cut);

        AddFirstAvailableLinkFromFirstRow(cut);

        cut.FindAll("[data-testid='link-indicator']")
            .Select(i => i.TextContent)
            .Should().Contain(t => t.Contains("1 link"));

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
        OpenDocumentView(cut);

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
        OpenDocumentView(cut);

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
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find("[data-testid='link-section-clarifications'] [data-testid='link-add-btn']").Click();

        ClickTraceabilityFilter(cut, "Clarifications without requirements");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("retry policy");
        cut.Find(".traceability-filter").TextContent.Should().Contain("Clarifications without requirements 1");
    }

    private static void AddFirstAvailableLinkFromFirstRow(IRenderedComponent<ExtractionReviewList> cut)
    {
        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find("[data-testid='link-drawer'] [data-testid='link-add-btn']").Click();
    }

    private static void AddClarificationLinkFromRow(IRenderedComponent<ExtractionReviewList> cut, string rowText)
    {
        cut.FindAll("[data-testid='candidate-row']")
            .Single(r => r.TextContent.Contains(rowText))
            .QuerySelector("[data-testid='link-indicator']")!.Click();
        cut.Find("[data-testid='link-section-clarifications'] [data-testid='link-add-btn']").Click();
    }

    private static void ClickTraceabilityFilter(IRenderedComponent<ExtractionReviewList> cut, string label)
    {
        cut.FindAll(".traceability-filter .filter-chip")
            .Single(b => b.TextContent.Contains(label))
            .Click();
    }

    [Fact]
    public void LinkIndicator_Click_OpensDrawer()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();

        cut.Find("[data-testid='link-drawer']").Should().NotBeNull();
    }

    [Fact]
    public void CloseButton_Click_ClosesDrawer()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find("[data-testid='link-drawer-close']").Click();

        cut.FindAll("[data-testid='link-drawer']").Should().BeEmpty();
    }

    [Fact]
    public void Backdrop_Click_ClosesDrawer()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find(".link-drawer-overlay").Click();

        cut.FindAll("[data-testid='link-drawer']").Should().BeEmpty();
    }

    [Fact]
    public void EscapeKey_ClosesDrawer()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();
        cut.Find("[data-testid='link-drawer']").KeyDown("Escape");

        cut.FindAll("[data-testid='link-drawer']").Should().BeEmpty();
    }

    [Fact]
    public void Drawer_ShowsCorrectCandidateName()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("FR-007: The system MUST restrict access", ScenarioKind.Requirement),
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult(candidates)));
        OpenDocumentView(cut);

        cut.Find("[data-testid='link-indicator']").Click();

        cut.Find("[data-testid='link-drawer']").TextContent
            .Should().Contain("FR-007: The system MUST restrict access");
    }

    // =========================================================================
    // T008 — Auto-persist calls mutation when PipelineResult first arrives
    // =========================================================================

    [Fact]
    public async Task AnalyzeSpec_AutoPersistsNormalizedArtifacts()
    {
        var candidate = MakeCandidate();

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([candidate])));

        await Task.Delay(50); // allow fire-and-forget to complete

        _mockSaveReview.Verify(
            m => m.ExecuteAsync(
                It.Is<SaveReviewedCandidatesInput>(i =>
                    i.Items.Count == 1 &&
                    i.Items[0].ReviewStatus == CandidateReviewStatus.AutoAccepted),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "auto-persist must call SaveReviewedCandidates with AutoAccepted status on first PipelineResult");
    }

    // =========================================================================
    // T009 — Auto-persist preserves existing manual review statuses
    // =========================================================================

    [Fact]
    public async Task AutoPersist_DoesNotDuplicateExistingArtifacts()
    {
        var candidateWithKnownId = new ExtractionCandidate
        {
            Title = "The system shall validate input",
            Classification = ScenarioKind.Requirement,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
            ReviewStatus = CandidateReviewStatus.Rejected,
        };

        var snapshot = new ExtractionSessionSnapshot
        {
            SessionId = "prior-session-id",
            Timestamp = DateTimeOffset.UtcNow,
            Profile = ExtractionProfile.Default,
            PipelineStatus = PipelineStatus.Success,
            Candidates = [new CandidateSnapshot(
                CandidateId: candidateWithKnownId.CandidateId,
                Title: candidateWithKnownId.Title,
                Classification: ScenarioKind.Requirement,
                ClassificationSignal: ClassificationSignal.Rfc2119Uppercase,
                ContextHeading: null,
                SourceBlockType: BlockType.UnorderedListItem,
                Confidence: null,
                IsSelected: false,
                ReviewStatus: CandidateReviewStatus.Rejected,
                SaveState: CandidateSaveState.Saved,
                SaveError: null,
                SavedScenarioId: null)],
        };

        var cut = Render<ExtractionReviewList>(p =>
        {
            p.Add(c => c.PipelineResult, MakeResult([candidateWithKnownId]));
            p.Add(c => c.InitialSession, snapshot);
        });

        await Task.Delay(50);

        _mockSaveReview.Verify(
            m => m.ExecuteAsync(
                It.Is<SaveReviewedCandidatesInput>(i =>
                    i.Items.Any(item => item.ReviewStatus == CandidateReviewStatus.Rejected)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "auto-persist must preserve Rejected status for candidates from restored session");
    }

    // =========================================================================
    // T015 — Traceability works without any save action
    // =========================================================================

    [Fact]
    public void SaveAcceptedArtifacts_NotRequiredForTraceability()
    {
        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult()));

        var activeTab = cut.FindAll(".view-mode-tab").Single(t => t.ClassList.Contains("is-active"));
        activeTab.TextContent.Should().Contain("Traceability & Coverage");
        cut.Markup.Should().Contain("tv-root", "TraceabilityView must render without any Accept/Save action");
    }

    // =========================================================================
    // T019 — Existing Accepted candidates still appear in Traceability
    // =========================================================================

    [Fact]
    public void ExistingAcceptedArtifacts_StillWorkAfterMigration()
    {
        var legacyAccepted = new ExtractionCandidate
        {
            Title = "FR-001: The system MUST allow login",
            Classification = ScenarioKind.Requirement,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
            ReviewStatus = CandidateReviewStatus.Accepted,
        };

        var cut = Render<ExtractionReviewList>(p =>
            p.Add(c => c.PipelineResult, MakeResult([legacyAccepted])));

        cut.Markup.Should().Contain("tv-root",
            "legacy Accepted candidates must still appear in Traceability after migration");
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

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut)
    {
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();
    }

    private static void SelectAndAcceptFirst(IRenderedComponent<ExtractionReviewList> cut)
    {
        OpenDocumentView(cut);
        cut.Find("[data-testid='candidate-checkbox']").Change(true);
        cut.Find(".bulk-review-accept").Click();
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

        SelectAndAcceptFirst(cut);
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

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();

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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut) =>
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();




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
        OpenDocumentView(cut);

        cut.Find(".search-input").Input("admin");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().HaveCount(1);
        rows[0].TextContent.Should().Contain("admin");
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

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();

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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut) =>
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();





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
        OpenDocumentView(cut);

        cut.Find(".search-input").Input("retry");

        cut.FindAll("[data-testid='candidate-row']").Should().HaveCount(1);
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

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();

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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut) =>
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();





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
        OpenDocumentView(cut);

        cut.Find(".search-input").Input("rate");

        var rows = cut.FindAll("[data-testid='candidate-row']");
        rows.Should().HaveCount(1);
        rows[0].TextContent.Should().Contain("rate");
    }


}

// ── T093 ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests that top-level sections are expanded and subgroups are collapsed by default
/// after a new extraction, and that Expand All / Collapse All operate on both levels.
/// </summary>
public class ExtractionReviewListDefaultExpansionTests : BunitContext
{
    public ExtractionReviewListDefaultExpansionTests()
    {
        Services.AddSingleton(new Mock<ICreateScenariosMutation>().Object);

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

        var mockSession = new Mock<IExtractionSessionService>();
        mockSession.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        mockSession.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        mockSession.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(mockSession.Object);

        var mockGetReviewed = new Mock<IGetReviewedCandidatesQuery>();
        mockGetReviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(mockGetReviewed.Object);

        var featureVisibility = new FeatureVisibilityService();
        featureVisibility.ApplyLocalFlags(new FeatureVisibilityDto
        {
            EnableExtractionReview = true,
            EnableArchitectureView = true
        });
        Services.AddSingleton(featureVisibility);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();

        Services.AddLogging();
    }

    private static ExtractionCandidate Make(string title, ScenarioKind kind, string? heading = null) => new()
    {
        Title = title,
        Classification = kind,
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

    private static void OpenDocumentView(IRenderedComponent<ExtractionReviewList> cut) =>
        cut.FindAll(".view-mode-tab").First(t => t.TextContent.Contains("Extraction Review")).Click();






}

