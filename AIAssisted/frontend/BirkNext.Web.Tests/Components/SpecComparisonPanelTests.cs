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

public class SpecComparisonPanelTests : BunitContext
{
    private readonly Mock<IScenarioExtractionService> _extractionService = new();

    public SpecComparisonPanelTests()
    {
        var config = new Mock<IExtractionConfiguration>();
        config.Setup(c => c.MaxInputLengthChars).Returns(50_000);

        var mockSaveMutation = new Mock<ISaveQaDeltaReviewMutation>();
        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.SaveQaDeltaReview).Returns(mockSaveMutation.Object);

        var mockWorkspace = new Mock<IWorkspaceSessionService>();
        mockWorkspace.Setup(w => w.CurrentProject).Returns("test-project");

        Services.AddSingleton(config.Object);
        Services.AddSingleton(_extractionService.Object);
        Services.AddSingleton<ISpecComparisonService, SpecComparisonService>();
        Services.AddSingleton(mockClient.Object);
        Services.AddSingleton(mockWorkspace.Object);
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    // ── Section structure ────────────────────────────────────────────────────

    [Fact]
    public void SpecInputsSection_RendersWithoutRunningComparison()
    {
        var cut = Render<SpecComparisonPanel>();

        cut.Find("[data-testid='spec-inputs-section']").Should().NotBeNull();
        cut.FindAll("[data-testid='delta-summary-section']").Should().BeEmpty();
        cut.FindAll("[data-testid='change-explorer-section']").Should().BeEmpty();
    }

    [Fact]
    public void EmptyDeltaResultsAndProfileCards_RenderBeforeComparison()
    {
        var cut = Render<SpecComparisonPanel>();

        cut.Find("[data-testid='delta-empty-state']").TextContent.Should().Contain("No comparison yet");
        cut.Markup.Should().Contain("Speckit Structured Spec");
        cut.Markup.Should().Contain("Generic Document");
        cut.Markup.Should().Contain("Comparison does not modify saved QA artifacts");
    }

    [Fact]
    public async Task DeltaSummaryAndChangeExplorer_AppearAfterComparison()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='delta-summary-section']").Should().NotBeNull();
        cut.Find("[data-testid='change-explorer-section']").Should().NotBeNull();
    }

    // ── Filter chip active states ────────────────────────────────────────────

    [Fact]
    public async Task FilterChip_AllTypes_IsActiveByDefault()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-all-types']").ClassList.Should().Contain("is-active");
        cut.Find("[data-testid='filter-requirements']").ClassList.Should().NotContain("is-active");
        cut.Find("[data-testid='filter-tests']").ClassList.Should().NotContain("is-active");
        cut.Find("[data-testid='filter-clarifications']").ClassList.Should().NotContain("is-active");
    }

    [Fact]
    public async Task FilterChip_AllDeltas_IsActiveByDefault()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-all-deltas']").ClassList.Should().Contain("is-active");
        cut.Find("[data-testid='filter-added']").ClassList.Should().NotContain("is-active");
        cut.Find("[data-testid='filter-modified']").ClassList.Should().NotContain("is-active");
        cut.Find("[data-testid='filter-removed']").ClassList.Should().NotContain("is-active");
    }

    [Fact]
    public async Task FilterChip_Requirements_BecomesActiveWhenClicked()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-requirements']").Click();

        cut.Find("[data-testid='filter-requirements']").ClassList.Should().Contain("is-active");
        cut.Find("[data-testid='filter-all-types']").ClassList.Should().NotContain("is-active");
    }

    [Fact]
    public async Task FilterChip_Added_BecomesActiveWhenClicked()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-added']").Click();

        cut.Find("[data-testid='filter-added']").ClassList.Should().Contain("is-active");
        cut.Find("[data-testid='filter-all-deltas']").ClassList.Should().NotContain("is-active");
    }

    // ── Empty state ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeExplorer_ShowsEmptyState_WhenDeltaFilterHasNoMatches()
    {
        // Result has only a Modified item; clicking "Added" filter yields zero results
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-added']").Click();

        cut.Find("[data-testid='change-explorer-empty']").Should().NotBeNull();
        cut.Find("[data-testid='change-explorer-empty']").TextContent
            .Should().Contain("No changes match");
    }

    [Fact]
    public async Task ChangeExplorer_ShowsEmptyState_WhenKindFilterHasNoMatches()
    {
        // Result has only Requirement items; clicking "Tests" filter yields zero results
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-tests']").Click();

        cut.Find("[data-testid='change-explorer-empty']").Should().NotBeNull();
    }

    [Fact]
    public async Task ChangeExplorer_HidesEmptyState_WhenResultsExist()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        // Default "All" filters: results present, no empty state
        cut.FindAll("[data-testid='change-explorer-empty']").Should().BeEmpty();
    }

    // ── Delta card rendering ─────────────────────────────────────────────────

    [Fact]
    public async Task DeltaCards_AreRenderedAfterComparison()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.FindAll("[data-testid='delta-card']").Should().HaveCount(1);
    }

    [Fact]
    public async Task DeltaCard_Modified_ShowsDiffGrid()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='delta-card']").QuerySelector(".delta-diff-grid")
            .Should().NotBeNull("Modified delta cards must show side-by-side diff");
    }

    [Fact]
    public async Task DeltaCard_Added_ShowsSingleTextWithoutDiffGrid()
    {
        SetupAddedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='delta-card']").QuerySelectorAll(".delta-diff-grid")
            .Should().BeEmpty("Added items show single text, not a diff grid");
        cut.Find("[data-testid='delta-card']").QuerySelector(".delta-single-text")
            .Should().NotBeNull();
    }

    [Fact]
    public async Task FilterChip_KindFilter_HidesOtherKindCards()
    {
        // Result has one Requirement (Added) and one Test (Added)
        SetupMixedAdded();
        var cut = await RenderWithResultAsync();

        cut.FindAll("[data-testid='delta-card']").Should().HaveCount(2);

        cut.Find("[data-testid='filter-tests']").Click();

        cut.FindAll("[data-testid='delta-card']").Should().ContainSingle(
            "only the Test card should be visible when Tests filter is active");
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    [Fact]
    public async Task FilterChips_HaveAriaPressed()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='filter-all-types']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-requirements']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-tests']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-all-deltas']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-added']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-modified']").HasAttribute("aria-pressed").Should().BeTrue();
        cut.Find("[data-testid='filter-removed']").HasAttribute("aria-pressed").Should().BeTrue();
    }

    [Fact]
    public async Task DeltaDashboard_HasAriaLabel()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[data-testid='delta-dashboard']").GetAttribute("aria-label")
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SpecInputTextareas_HaveAriaLabels()
    {
        var cut = Render<SpecComparisonPanel>();

        cut.Find("[data-testid='old-spec-textarea']").GetAttribute("aria-label")
            .Should().NotBeNullOrEmpty();
        cut.Find("[data-testid='new-spec-textarea']").GetAttribute("aria-label")
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FilterToolbar_HasAriaLabel()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        cut.Find("[role='toolbar']").GetAttribute("aria-label")
            .Should().NotBeNullOrEmpty();
    }

    // ── Badge and heading structure ──────────────────────────────────────────

    [Fact]
    public async Task DeltaCard_StatusAndKindBadges_AreRenderedAsSeparateElements()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        var card = cut.Find("[data-testid='delta-card']");
        card.QuerySelector("[data-testid='delta-status-badge']").Should().NotBeNull();
        card.QuerySelector("[data-testid='delta-kind-badge']").Should().NotBeNull();
    }

    [Fact]
    public async Task DeltaCard_Markup_DoesNotContainConcatenatedBadgeText()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        // None of the "concatenated" patterns that appear when CSS isolation fails
        cut.Markup.Should().NotContain("ModifiedREQUIREMENT");
        cut.Markup.Should().NotContain("AddedREQUIREMENT");
        cut.Markup.Should().NotContain("RemovedREQUIREMENT");
        cut.Markup.Should().NotContain("MODIFIEDREQUIREMENT");
    }

    [Fact]
    public async Task ChangeExplorer_SectionHeading_HasSeparateCountElement()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        // The heading and count are separate elements (h3 text + span)
        var section = cut.Find(".delta-section");
        section.QuerySelector(".delta-section-count").Should().NotBeNull(
            "section count must be in a separate element for proper styling");
    }

    [Fact]
    public async Task ChangeExplorer_SectionHeading_ContainsRequirementsText()
    {
        SetupModifiedRequirement();
        var cut = await RenderWithResultAsync();

        var heading = cut.Find(".delta-section-heading");
        heading.TextContent.Should().Contain("Requirements");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupModifiedRequirement()
    {
        _extractionService
            .Setup(s => s.ExtractAsync("old", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([
                Candidate("FR-001: The system MUST allow password login", ScenarioKind.Requirement),
            ]));
        _extractionService
            .Setup(s => s.ExtractAsync("new", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([
                Candidate("FR-001: The system MUST allow passwordless login", ScenarioKind.Requirement),
            ]));
    }

    private void SetupAddedRequirement()
    {
        _extractionService
            .Setup(s => s.ExtractAsync("old", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([]));
        _extractionService
            .Setup(s => s.ExtractAsync("new", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([
                Candidate("FR-002: The system MUST support MFA", ScenarioKind.Requirement),
            ]));
    }

    private void SetupMixedAdded()
    {
        _extractionService
            .Setup(s => s.ExtractAsync("old", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([]));
        _extractionService
            .Setup(s => s.ExtractAsync("new", It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([
                Candidate("FR-001: New requirement", ScenarioKind.Requirement),
                Candidate("TC-001: New test", ScenarioKind.Test),
            ]));
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
