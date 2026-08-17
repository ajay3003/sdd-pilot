using BirkNext.Web.Models;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public sealed class SpecificationExplorerSampleProjectTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();
    private readonly Mock<IScenarioExtractionService> _extractionService = new();

    public SpecificationExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);
        Services.AddSingleton(_extractionService.Object);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
        Services.AddSingleton(new FeatureVisibilityService());
        var session = new Mock<IExtractionSessionService>();
        session.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        session.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        session.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        session.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(session.Object);

        var createScenarios = new Mock<ICreateScenariosMutation>();
        var saveReviewed = new Mock<ISaveReviewedCandidatesMutation>();
        saveReviewed
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveReviewedCandidatesInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveReviewedCandidatesResult>>());
        var saveLinks = new Mock<ISaveCandidateLinksMutation>();
        saveLinks
            .Setup(m => m.ExecuteAsync(It.IsAny<SaveCandidateLinksInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<ISaveCandidateLinksResult>>());
        var reviewed = new Mock<IGetReviewedCandidatesQuery>();
        reviewed
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IOperationResult<IGetReviewedCandidatesResult>>());
        Services.AddSingleton(createScenarios.Object);
        Services.AddSingleton(saveReviewed.Object);
        Services.AddSingleton(saveLinks.Object);
        Services.AddSingleton(reviewed.Object);

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void SpecificationExplorer_LoadsFromSelectedSampleProject()
    {
        const string projectASlug = "project-a";
        const string projectASpec = "# Project A Specification\n\n## Feature\nTest feature";

        _documentResolver.SetProjectSpecification(projectASlug, projectASpec);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project a");
            markup.Should().Contain("Project A Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_SwitchesProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectASpec = "# Project A Specification";
        const string projectBSpec = "# Project B Specification";

        _documentResolver.SetProjectSpecification(projectASlug, projectASpec);
        _documentResolver.SetProjectSpecification(projectBSlug, projectBSpec);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Specification"));

        // Switch to project B
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project b");
            markup.Should().Contain("Project B Specification");
            markup.Should().NotContain("Project A Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_ShowsMissingStateWhenSpecNotFound()
    {
        const string projectSlug = "project-without-spec";

        // Register project but don't set a specification
        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("spec.md is not available");
        });
    }

    [Fact]
    public void SpecificationExplorer_DoesNotUseWorkspaceAsAutomaticSource()
    {
        const string projectSlug = "project-a";
        const string workspaceSpec = "# Workspace Specification";
        const string sampleProjectSpec = "# Sample Project Specification";

        _documentResolver.SetProjectSpecification(projectSlug, sampleProjectSpec);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Specification, workspaceSpec);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Sample Project Specification");
            markup.Should().NotContain("Workspace Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_ClearsProjectHeaderOnProjectDeselection()
    {
        const string projectSlug = "project-a";
        const string projectSpec = "# Project A Specification";

        _documentResolver.SetProjectSpecification(projectSlug, projectSpec);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Sample Project:"));

        // Deselect project
        _documentResolver.SetSelectedProject(null);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Header should be gone when no project is selected
            markup.Should().NotContain("Sample Project:");
        });
    }

    [Fact]
    public void SpecificationExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project:");
            markup.Should().NotContain("spec.md is not available");
        });
    }

    [Fact]
    public void SpecificationExplorer_ReloadsSameProjectWithoutDuplication()
    {
        const string projectSlug = "project-a";
        const string projectSpec = "# Project A Specification\n\n## Feature\nTest feature";

        _documentResolver.SetProjectSpecification(projectSlug, projectSpec);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Specification"));

        var firstRender = cut.Markup;
        var featureCount = firstRender.Split("Feature").Length - 1;

        // Re-render same project
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var secondRender = cut.Markup;
            var secondFeatureCount = secondRender.Split("Feature").Length - 1;
            secondFeatureCount.Should().Be(featureCount);
        });
    }

    [Fact]
    public void SpecificationExplorer_AnalyzeUsesSelectedSampleProjectSpecification()
    {
        const string projectSlug = "project-a";
        const string workspaceSpec = "# OLD WORKSPACE SPEC";
        const string sampleProjectSpec = "# Project A Specification\n\n- FR-001: The system shall approve requests.";
        string? analyzedText = null;

        _documentResolver.SetProjectSpecification(projectSlug, sampleProjectSpec);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Specification, workspaceSpec);
        _extractionService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .Callback<string, ExtractionProfile, CancellationToken>((text, _, _) => analyzedText = text)
            .ReturnsAsync(MakeResult([
                new ExtractionCandidate
                {
                    Title = "FR-001: The system shall approve requests.",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Lowercase,
                    SourceBlockType = BlockType.UnorderedListItem
                }
            ], sampleProjectSpec));

        var cut = Render<SpecificationExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-explorer-analyze']").Should().NotBeNull());

        cut.Find("[data-testid='spec-explorer-analyze']").Click();

        cut.WaitForAssertion(() =>
        {
            analyzedText.Should().Be(sampleProjectSpec);
            analyzedText.Should().NotBe(workspaceSpec);
            cut.Find("[data-testid='requirements-metric']").TextContent.Should().Contain("1");
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("1");
            cut.Markup.Should().Contain("FR-001: The system shall approve requests.");
        });
    }

    [Fact]
    public void SpecificationExplorer_ProjectSwitchClearsPreviousAnalysisResult()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";

        _documentResolver.SetProjectSpecification(projectASlug, "# Project A Specification\n\n- FR-001: The system shall approve requests.");
        _documentResolver.SetProjectSpecification(projectBSlug, "# Project B Specification");
        _documentResolver.SetSelectedProject(projectASlug);
        _extractionService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([
                new ExtractionCandidate
                {
                    Title = "FR-001: Project A only",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Lowercase,
                    SourceBlockType = BlockType.UnorderedListItem
                }
            ], "# Project A Specification"));

        var cut = Render<SpecificationExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-explorer-analyze']").Should().NotBeNull());
        cut.Find("[data-testid='spec-explorer-analyze']").Click();
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("1"));

        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B Specification");
            cut.Markup.Should().NotContain("FR-001: Project A only");
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("0");
        });
    }

    [Fact]
    public void SpecificationExplorer_MissingSpecHasNoAnalyzeFallback()
    {
        const string projectSlug = "project-without-spec";

        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Specification, "# OLD WORKSPACE SPEC");

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("spec.md is not available");
            cut.Markup.Should().NotContain("OLD WORKSPACE SPEC");
            cut.FindAll("[data-testid='spec-explorer-analyze']").Should().BeEmpty();
        });
    }

    private static ExtractionPipelineResult MakeResult(
        IReadOnlyList<ExtractionCandidate> candidates,
        string specMarkdown)
    {
        return ExtractionPipelineResult.Success(
            candidates,
            specMarkdown.Length,
            specMarkdown.Split('\n').Length,
            1,
            candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            candidates.Count(c => c.Classification == ScenarioKind.Test),
            candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification),
            ExtractionProfile.Speckit,
            specMarkdown);
    }
}
