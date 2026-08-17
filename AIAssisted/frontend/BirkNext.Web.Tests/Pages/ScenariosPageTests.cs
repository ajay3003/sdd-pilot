using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class ScenariosPageTests : BunitContext
{
    [Fact]
    public void ScenariosPage_SuccessfulScenarioLoad_RendersScenariosAndPreservesSearchFilter()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", "Users can sign in", ScenarioKind.Requirement),
                MakeScenario("sc-2", "Checkout test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        // Switch to "All types" to see all scenarios before searching
        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("select[aria-label='Filter by type']").Change(string.Empty);

        cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(2);
        cut.FindAll("[data-testid='coverage-dashboard']").Should().BeEmpty();

        cut.Find("input[aria-label='Search scenarios']").Input("checkout");

        cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle();
        cut.Markup.Should().Contain("Checkout test");
        cut.FindAll("[data-testid='scenario-row']")[0].TextContent.Should().NotContain("Login requirement");
    }

    [Fact]
    public void ScenariosPage_EmptyScenarioLoad_RendersEmptyState()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult([]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='scenario-row']").Should().BeEmpty();
            cut.Find("[data-testid='empty-state']").TextContent.Should().NotBeNullOrEmpty();
            cut.Markup.Should().NotContain("Loading scenarios");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_FailedScenarioLoad_ShowsInlineErrorInsteadOfThrowing()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='scenario-load-error']")
                .TextContent.Should().Contain("couldn't load scenarios");
            cut.Markup.Should().NotContain("Loading scenarios");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_SuccessfulDelete_RemovesScenarioFromList()
    {
        var mockGetQuery = new Mock<IGetScenariosQuery>();
        mockGetQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Keep me", null, ScenarioKind.Test),
                MakeScenario("sc-2", "Delete me", null, ScenarioKind.Test),
            ]));

        var mockDeletePayload = new Mock<IDeleteScenario_DeleteScenario>();
        mockDeletePayload.Setup(p => p.Success).Returns(true);
        mockDeletePayload.Setup(p => p.DeletedId).Returns("sc-2");
        mockDeletePayload.Setup(p => p.Errors).Returns([]);

        var mockDeleteData = new Mock<IDeleteScenarioResult>();
        mockDeleteData.Setup(d => d.DeleteScenario).Returns(mockDeletePayload.Object);

        var mockDeleteResult = new Mock<IOperationResult<IDeleteScenarioResult>>();
        mockDeleteResult.Setup(r => r.Data).Returns(mockDeleteData.Object);

        var mockDeleteMutation = new Mock<IDeleteScenarioMutation>();
        mockDeleteMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDeleteResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockGetQuery.Object);
        mockClient.Setup(c => c.DeleteScenario).Returns(mockDeleteMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(2),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='delete-btn-sc-2']").Click();
        cut.Find("[data-testid='delete-confirm-btn']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle();
            cut.Markup.Should().Contain("Keep me");
            cut.Markup.Should().NotContain("Delete me");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_FailedDelete_ShowsInlineError()
    {
        var mockGetQuery = new Mock<IGetScenariosQuery>();
        mockGetQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "My scenario", null, ScenarioKind.Test),
            ]));

        var mockDeletePayload = new Mock<IDeleteScenario_DeleteScenario>();
        mockDeletePayload.Setup(p => p.Success).Returns(false);
        mockDeletePayload.Setup(p => p.DeletedId).Returns((string?)null);

        var mockError = new Mock<IDeleteScenario_DeleteScenario_Errors>();
        mockError.Setup(e => e.Message).Returns("Scenario not found");
        mockDeletePayload.Setup(p => p.Errors).Returns([mockError.Object]);

        var mockDeleteData = new Mock<IDeleteScenarioResult>();
        mockDeleteData.Setup(d => d.DeleteScenario).Returns(mockDeletePayload.Object);

        var mockDeleteResult = new Mock<IOperationResult<IDeleteScenarioResult>>();
        mockDeleteResult.Setup(r => r.Data).Returns(mockDeleteData.Object);

        var mockDeleteMutation = new Mock<IDeleteScenarioMutation>();
        mockDeleteMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDeleteResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockGetQuery.Object);
        mockClient.Setup(c => c.DeleteScenario).Returns(mockDeleteMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle(),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("[data-testid='delete-btn-sc-1']").Click();
        cut.Find("[data-testid='delete-confirm-btn']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle();
            cut.Find("[data-testid='delete-error-sc-1']")
                .TextContent.Should().Contain("Scenario not found");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_DoesNotHaveNewScenarioButton()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult([]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.FindAll("a.btn-primary[href='scenarios/new']").Should().BeEmpty();
    }

    // ── New behavior tests ───────────────────────────────────────────────────

    [Fact]
    public void ScenariosPage_PageTitle_IsQaArtifactLibrary()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult([]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.Find("h1").TextContent.Should().Be("QA Artifact Library");
    }

    [Fact]
    public void ScenariosPage_DefaultFilter_ShowsAllArtifactTypes()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", null, ScenarioKind.Requirement),
                MakeScenario("sc-2", "Checkout test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        // Default view is the knowledge base: all artifact groups are visible.
        cut.FindAll("[data-testid='group-tests']").Should().HaveCount(1,
            "Tests section must be shown by default");
        cut.FindAll("[data-testid='group-requirements']").Should().HaveCount(1,
            "Requirements section must be shown by default");
        cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(2,
            "all saved artifacts should be visible by default");
    }

    [Fact]
    public void ScenariosPage_AllTypesFilter_ShowsGroupedSections()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", null, ScenarioKind.Requirement),
                MakeScenario("sc-2", "Checkout test", null, ScenarioKind.Test),
                MakeScenario("sc-3", "Clarify timeout", null, ScenarioKind.NeedsClarification),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        // Change to "All types"
        cut.Find("select[aria-label='Filter by type']").Change(string.Empty);

        cut.FindAll("[data-testid='group-tests']").Should().HaveCount(1);
        cut.FindAll("[data-testid='group-requirements']").Should().HaveCount(1);
        cut.FindAll("[data-testid='group-clarifications']").Should().HaveCount(1);
        cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(3);
    }

    [Fact]
    public void ScenariosPage_GroupedDisplay_TestsAndRequirementsSeparated()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "A requirement", null, ScenarioKind.Requirement),
                MakeScenario("sc-2", "A test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("select[aria-label='Filter by type']").Change(string.Empty);

        var testsSection = cut.Find("[data-testid='group-tests']");
        var reqSection = cut.Find("[data-testid='group-requirements']");

        testsSection.TextContent.Should().Contain("A test").And.NotContain("A requirement");
        reqSection.TextContent.Should().Contain("A requirement").And.NotContain("A test");
    }

    // ── Drag ordering tests ──────────────────────────────────────────────────

    [Fact]
    public void ScenariosPage_TestItems_RenderedInDisplayOrder()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "First test",  null, ScenarioKind.Test, displayOrder: 0),
                MakeScenario("sc-2", "Second test", null, ScenarioKind.Test, displayOrder: 1),
                MakeScenario("sc-3", "Third test",  null, ScenarioKind.Test, displayOrder: 2),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid='scenario-row']");
            rows.Should().HaveCount(3);
            rows[0].TextContent.Should().Contain("First test");
            rows[1].TextContent.Should().Contain("Second test");
            rows[2].TextContent.Should().Contain("Third test");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_TestFilterNoSearch_ShowsDragHintAndHandles()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "A test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("select[aria-label='Filter by type']").Change("Test");

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='drag-hint']").Should().NotBeNull();
            cut.Find("[data-testid='drag-handle-sc-1']").Should().NotBeNull();
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_RequirementFilter_NoDragHandles()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "A req", null, ScenarioKind.Requirement),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("select[aria-label='Filter by type']").Change("Requirement");

        cut.FindAll("[data-testid='drag-hint']").Should().BeEmpty();
        cut.FindAll("[aria-label^='Drag to reorder']").Should().BeEmpty();
    }

    [Fact]
    public void ScenariosPage_WithSearchText_NoDragHandles()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle(),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("input[aria-label='Search scenarios']").Input("login");

        cut.FindAll("[data-testid='drag-hint']").Should().BeEmpty();
        cut.FindAll("[aria-label^='Drag to reorder']").Should().BeEmpty();
    }

    [Fact]
    public async Task ScenariosPage_MoveUp_CallsReorderMutation()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "First test",  null, ScenarioKind.Test, displayOrder: 0),
                MakeScenario("sc-2", "Second test", null, ScenarioKind.Test, displayOrder: 1),
            ]));

        var mockPayload = new Mock<IReorderTestScenarios_ReorderTestScenarios>();
        mockPayload.Setup(p => p.Success).Returns(true);
        mockPayload.Setup(p => p.Errors).Returns([]);
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-1");

        var mockData = new Mock<IReorderTestScenariosResult>();
        mockData.Setup(d => d.ReorderTestScenarios).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<IReorderTestScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        var mockReorderMutation = new Mock<IReorderTestScenariosMutation>();
        mockReorderMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<ReorderTestScenariosInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        mockClient.Setup(c => c.ReorderTestScenarios).Returns(mockReorderMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(2),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("select[aria-label='Filter by type']").Change("Test");

        cut.Find("[data-testid='move-up-btn-sc-2']").Click();

        await Task.Delay(100);

        mockReorderMutation.Verify(
            m => m.ExecuteAsync(It.IsAny<ReorderTestScenariosInput>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var rows = cut.FindAll("[data-testid='scenario-row']");
        rows[0].TextContent.Should().Contain("Second test");
        rows[1].TextContent.Should().Contain("First test");
    }

    // ── QA Library repository positioning ────────────────────────────────────

    [Fact]
    public void ScenariosPage_DoesNotShowCoverageMetricsDashboard()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", null, ScenarioKind.Requirement),
                MakeScenario("sc-2", "Login test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        // Coverage dashboard sections must not exist in the library
        cut.Markup.Should().NotContain("Coverage Map",              "Coverage Map belongs in Traceability & Coverage");
        cut.Markup.Should().NotContain("Traceability Health",       "Traceability Health strip belongs in Traceability & Coverage");
        cut.Markup.Should().NotContain("Release Readiness",         "Release Readiness belongs in Traceability & Coverage");
        cut.Markup.Should().NotContain("Recommended Actions",       "Coverage-oriented recommended actions belong in Traceability & Coverage");
        cut.Markup.Should().NotContain("Requirement coverage",      "Coverage % metric belongs in Traceability & Coverage");
        cut.Markup.Should().NotContain("Requirements Not Imported", "Import workflow belongs in Specification Explorer, not the library");
        cut.Markup.Should().NotContain("Import Requirements",       "Import Requirements CTA belongs in Specification Explorer");
    }

    [Fact]
    public void ScenariosPage_ShowsTraceabilityRedirectNote()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult([]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        // The page must have a note directing coverage analysis to Traceability & Coverage
        var note = cut.Find("[data-testid='ql-traceability-redirect']");
        note.Should().NotBeNull("library must show a redirect note pointing coverage analysis to Traceability & Coverage");
        note.TextContent.Should().Contain("Traceability",
            "redirect note must name Traceability & Coverage as the coverage workspace");
        note.TextContent.Should().Contain("reusable",
            "redirect note must describe the library as a reuse repository");
    }

    [Fact]
    public void ScenariosPage_DoesNotShowCoverageGapsFilter()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", null, ScenarioKind.Requirement),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        // Coverage Gaps filter chip must not exist — it implies the library is a coverage tool
        cut.Markup.Should().NotContain("Coverage Gaps",
            "Coverage Gaps filter belongs in Traceability & Coverage, not the library");
    }

    [Fact]
    public void ScenariosPage_ShowsRepositoryPurposeAssetCounts()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", null, ScenarioKind.Requirement),
                MakeScenario("sc-2", "Login test", null, ScenarioKind.Test),
                MakeScenario("sc-3", "Another test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().NotContain("Loading scenarios"),
            timeout: TimeSpan.FromSeconds(1));

        // Hero must frame the library as an asset store (not a coverage tool)
        cut.Find("h1").TextContent.Should().Be("QA Artifact Library");
        cut.Markup.Should().Contain("Published Assets",
            "hero must use asset-repository language, not coverage language");
        cut.Markup.Should().NotContain("Requirement coverage",
            "coverage percentage metric must not appear in the library hero");
        cut.Markup.Should().NotContain("Quality risks",
            "risk-indicator metric belongs in Traceability & Coverage");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IOperationResult<IGetScenariosResult> MakeGetScenariosResult(
        IReadOnlyList<IGetScenarios_Scenarios> scenarios)
    {
        var mockData = new Mock<IGetScenariosResult>();
        mockData.Setup(d => d.Scenarios).Returns(scenarios);

        var mockResult = new Mock<IOperationResult<IGetScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        return mockResult.Object;
    }

    private static IGetScenarios_Scenarios MakeScenario(
        string id,
        string title,
        string? description,
        ScenarioKind kind,
        int displayOrder = 0)
    {
        var mockScenario = new Mock<IGetScenarios_Scenarios>();
        mockScenario.Setup(s => s.Id).Returns(id);
        mockScenario.Setup(s => s.Title).Returns(title);
        mockScenario.Setup(s => s.Description).Returns(description);
        mockScenario.Setup(s => s.Kind).Returns(kind);
        mockScenario.Setup(s => s.CreatedAt).Returns(DateTimeOffset.UtcNow);
        mockScenario.Setup(s => s.DisplayOrder).Returns(displayOrder);

        return mockScenario.Object;
    }
}

