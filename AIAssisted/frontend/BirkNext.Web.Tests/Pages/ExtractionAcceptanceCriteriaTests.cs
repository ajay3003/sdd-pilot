// T091 — US2 Acceptance Criteria verification
// Maps to spec.md §US2 §Acceptance Criteria, ACs 1–6.
// Each test name encodes the AC number so failures trace directly to the spec.

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

/// <summary>
/// End-to-end bUnit coverage of the six US2 acceptance criteria from spec.md §US2.
/// Tests render the full <c>ScenarioExtraction</c> page, which hosts both
/// <c>ExtractionInput</c> and <c>ExtractionReviewList</c>, exercising the
/// complete component wire-up without a live server.
/// </summary>
public class ExtractionAcceptanceCriteriaTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _mockExtraction = new();
    private readonly Mock<ICreateScenariosMutation> _mockMutation = new();

    public ExtractionAcceptanceCriteriaTests()
    {
        var mockConfig = new Mock<IExtractionConfiguration>();
        mockConfig.Setup(c => c.MaxInputLengthChars).Returns(50_000);
        mockConfig.Setup(c => c.MinCandidateLengthChars).Returns(3);
        mockConfig.Setup(c => c.MaxLineLengthForPatternMatching).Returns(2_000);

        Services.AddSingleton(_mockExtraction.Object);
        Services.AddSingleton(mockConfig.Object);
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
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ExtractionCandidate MakeCandidate(
        string title,
        ScenarioKind kind = ScenarioKind.Requirement) => new()
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
        };

    private static ExtractionPipelineResult MakeSuccessResult(
        IReadOnlyList<ExtractionCandidate> candidates)
    {
        int req = candidates.Count(c => c.Classification == ScenarioKind.Requirement);
        int test = candidates.Count(c => c.Classification == ScenarioKind.Test);
        int nc = candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification);
        return ExtractionPipelineResult.Success(
            candidates: candidates,
            inputLengthChars: 500,
            inputLineCount: 10,
            durationMs: 5,
            requirementCount: req,
            testCount: test,
            needsClarificationCount: nc);
    }

    private static IOperationResult<ICreateScenariosResult> MakeSuccessOperationResult(
        IEnumerable<(string id, string title, ScenarioKind kind)> items)
    {
        var results = items.Select(item =>
        {
            var mockScenario = new Mock<ICreateScenarios_CreateScenarios_Results_Scenario>();
            mockScenario.Setup(s => s.Id).Returns(item.id);
            mockScenario.Setup(s => s.Title).Returns(item.title);
            mockScenario.Setup(s => s.Kind).Returns(item.kind);

            var mockSuccess = new Mock<ICreateScenarios_CreateScenarios_Results_CreateScenarioSuccess>();
            mockSuccess.Setup(s => s.Scenario).Returns(mockScenario.Object);
            return (ICreateScenarios_CreateScenarios_Results)mockSuccess.Object;
        }).ToList();

        var mockPayload = new Mock<ICreateScenarios_CreateScenarios>();
        mockPayload.Setup(p => p.Results).Returns(results);
        mockPayload.Setup(p => p.SuccessCount).Returns(results.Count);
        mockPayload.Setup(p => p.FailureCount).Returns(0);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-ac");

        var mockData = new Mock<ICreateScenariosResult>();
        mockData.Setup(d => d.CreateScenarios).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ICreateScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        return mockResult.Object;
    }

    // ── AC1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC1 — Given pasted spec text with bullet points, When extraction is triggered,
    /// Then all bullet points are extracted and displayed as candidate rows.
    /// </summary>
    [Fact]
    public async Task AC1_BulletPointsExtracted_AllDisplayedAsCandidateRows()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("The system MUST validate credentials"),
            MakeCandidate("The system SHALL store hashed passwords"),
            MakeCandidate("The system MUST enforce rate limits"),
        };

        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult(candidates));

        var cut = Render<ScenarioExtraction>();

        cut.Find("[data-testid='spec-textarea']").Input("- some bullet text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='candidate-row']").Count == 3,
            timeout: TimeSpan.FromSeconds(2));

        cut.FindAll("[data-testid='candidate-row']").Should().HaveCount(3);
    }

    // ── AC2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC2 — Given extracted candidates are displayed, When the user views the list,
    /// Then each candidate shows its classification label (REQUIREMENT / TEST / NEEDS_CLARIFICATION).
    /// </summary>
    [Fact]
    public async Task AC2_EachCandidateDisplaysClassificationLabel()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("System MUST validate", ScenarioKind.Requirement),
            MakeCandidate("Given login When valid Then redirect", ScenarioKind.Test),
            MakeCandidate("Session timeout policy?", ScenarioKind.NeedsClarification),
        };

        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult(candidates));

        var cut = Render<ScenarioExtraction>();
        cut.Find("[data-testid='spec-textarea']").Input("spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='classification-badge']").Count == 3,
            timeout: TimeSpan.FromSeconds(2));

        var badges = cut.FindAll("[data-testid='classification-badge']")
            .Select(b => b.TextContent.Trim())
            .ToList();

        badges.Should().Contain("REQUIREMENT");
        badges.Should().Contain("TEST");
        badges.Should().Contain("NEEDS_CLARIFICATION");
    }

    // ── AC3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC3 — Given extracted candidates are displayed, When the user has not performed
    /// a save action, Then no candidates are persisted (createScenarios mutation is not called).
    /// </summary>
    [Fact]
    public async Task AC3_CandidatesDisplayed_BeforeSaveAction_MutationNotCalled()
    {
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("System MUST log all errors"),
        };

        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult(candidates));

        var cut = Render<ScenarioExtraction>();
        cut.Find("[data-testid='spec-textarea']").Input("spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='candidate-row']").Count == 1,
            timeout: TimeSpan.FromSeconds(2));

        // Candidates are displayed but save has NOT been triggered
        _mockMutation.Verify(
            m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "createScenarios must not be called before the user explicitly triggers save");
    }

    // ── AC4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC4 — Given pasted text with no extractable candidates, When extraction is triggered,
    /// Then the system displays a message indicating no candidates were found.
    /// </summary>
    [Fact]
    public async Task AC4_NoExtractableCandidates_EmptyStateMessageDisplayed()
    {
        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExtractionPipelineResult.NonSuccess(PipelineStatus.NoResults, 0, 0, 0));

        var cut = Render<ScenarioExtraction>();
        cut.Find("[data-testid='spec-textarea']").Input("# Only a heading — no bullets");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='empty-state']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        cut.Find("[data-testid='empty-state']").TextContent
            .Should().NotBeNullOrEmpty("empty-state message must be shown when no candidates found");
    }

    // ── AC5 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC5 — Given a user selects a subset of extracted candidates and confirms save,
    /// When the save action is processed, Then only the selected candidates are sent
    /// to createScenarios; unselected candidates are discarded.
    /// </summary>
    [Fact]
    public async Task AC5_SubsetSelected_OnlySelectedCandidatesSentToMutation()
    {
        var candidateA = MakeCandidate("System MUST store credentials");
        var candidateB = MakeCandidate("Given login When valid Then redirect", ScenarioKind.Test);
        var candidateC = MakeCandidate("Session timeout policy is TBD", ScenarioKind.NeedsClarification);

        _mockExtraction
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult([candidateA, candidateB, candidateC]));

        _mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessOperationResult([("sc-1", candidateA.Title, ScenarioKind.Requirement)]));

        var cut = Render<ScenarioExtraction>();
        cut.Find("[data-testid='spec-textarea']").Input("spec text");
        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='candidate-checkbox']").Count == 3,
            timeout: TimeSpan.FromSeconds(2));

        // Select only the first candidate (candidateA)
        cut.FindAll("[data-testid='candidate-checkbox']")[0].Change(true);

        await cut.WaitForStateAsync(
            () => !cut.Find("[data-testid='confirm-save-button']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='confirm-save-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='save-saved']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        // Verify mutation was called with exactly 1 item (the selected one only)
        _mockMutation.Verify(
            m => m.ExecuteAsync(
                It.Is<CreateScenariosInput>(i =>
                    i.Items.Count == 1 &&
                    i.Items[0].Title == candidateA.Title),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "only the selected candidate must be sent; unselected candidates must be discarded");
    }

    // ── AC6 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC6 — Given a user triggers extraction on an empty text area,
    /// When the input is evaluated, Then the system shows a validation message
    /// and does not attempt extraction.
    /// </summary>
    [Fact]
    public void AC6_EmptyTextArea_ValidationMessageShown_ExtractionNotAttempted()
    {
        var cut = Render<ScenarioExtraction>();

        // Text area is empty — click extract without entering any text
        cut.Find("[data-testid='extract-button']").Click();

        cut.Find("[data-testid='validation-message']").TextContent
            .Should().NotBeNullOrEmpty("a validation message must appear for empty input");

        _mockExtraction.Verify(
            s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "extraction service must not be called when input is empty");
    }

    /// <summary>
    /// AC7 — Given a specification file is imported via the import zone,
    /// When the user clicks Extract, Then candidates are displayed from the imported content.
    /// </summary>
    [Fact]
    public async Task AC7_ImportedFile_ExtractsAndDisplaysCandidates()
    {
        const string importedText = "The system shall allow imported users to log in.";
        var candidates = new[] { MakeCandidate("The system shall allow imported users to log in.") };
        _mockExtraction
            .Setup(s => s.ExtractAsync(importedText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSuccessResult(candidates));

        var cut = Render<ScenarioExtraction>();

        var importChild = cut.FindComponent<SpecificationImport>();
        await cut.InvokeAsync(() => importChild.Instance.OnFileDrop("spec.md", 512, importedText));

        cut.Find("[data-testid='extract-button']").Click();

        await cut.WaitForStateAsync(
            () => cut.FindAll("[data-testid='candidate-row']").Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        _mockExtraction.Verify(
            s => s.ExtractAsync(importedText, It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "extraction service must be called with the imported file content");
        cut.FindAll("[data-testid='candidate-row']").Should().HaveCount(1);
    }
}

