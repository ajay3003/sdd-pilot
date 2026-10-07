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
    private readonly Mock<IExtractionSessionService> _session = new();
    private readonly Mock<ISaveReviewedCandidatesMutation> _saveReviewed = new();

    public SpecificationExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);
        Services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(_documentResolver);
        Services.AddSingleton<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>(new BirkNext.Web.Services.Explorers.ArtifactExplorerContext(_workspace, _documentResolver, _documentResolver));
        Services.AddSingleton(_extractionService.Object);
        Services.AddSingleton<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
        Services.AddSingleton(new FeatureVisibilityService());
        _session.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        _session.Setup(s => s.GetSpecificationAnalysisAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SpecificationAnalysisCacheEntry?)null);
        _session.Setup(s => s.SaveSpecificationAnalysisAsync(It.IsAny<SpecificationAnalysisCacheEntry>())).Returns(Task.CompletedTask);
        _session.Setup(s => s.SaveAsync(It.IsAny<ExtractionSessionSnapshot>())).Returns(Task.CompletedTask);
        _session.Setup(s => s.ClearAsync()).Returns(Task.CompletedTask);
        _session.Setup(s => s.IsExpired(It.IsAny<ExtractionSessionSnapshot>())).Returns(false);
        Services.AddSingleton(_session.Object);

        var createScenarios = new Mock<ICreateScenariosMutation>();
        _saveReviewed
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
        Services.AddSingleton(_saveReviewed.Object);
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
            markup.Should().Contain("No Specification artifact was detected in");
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
            cut.Markup.Should().Contain("Sample Project project a"));

        // Deselect project
        _documentResolver.SetSelectedProject(null);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Header should be gone when no project is selected
            markup.Should().NotContain("Sample Project project a");
        });
    }

    [Fact]
    public void SpecificationExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project project a");
            markup.Should().NotContain("No Specification artifact was detected in");
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
        _extractionService.Verify(s => s.ExtractAsync(projectSpec, ExtractionProfile.Speckit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SpecificationExplorer_AutomaticallyAnalyzesSelectedSampleProjectSpecification()
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

        cut.WaitForAssertion(() =>
        {
            analyzedText.Should().Be(sampleProjectSpec);
            analyzedText.Should().NotBe(workspaceSpec);
            cut.Find("[data-testid='requirements-metric']").TextContent.Should().Contain("1");
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("1");
            cut.Find("[data-testid='requirements-metric']").TextContent.Should().Contain("Requirements analyzed");
            cut.Markup.Should().Contain("FR-001: The system shall approve requests.");
        });
    }

    [Fact]
    public void SpecificationExplorer_AutomaticallyAnalyzesWithoutAssessingTraceability()
    {
        const string spec = "# School attendance requirements\n\n## Requirements\n\n- FR-001: The system shall record absence.\n\n## Tests\n\n- Given an absence, when saved, then it is recorded.";
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "requirements-person.md", null, null, "File", select: true);
        _extractionService.Setup(s => s.ExtractAsync(spec, ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult([], spec));

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("requirements-person.md");
            cut.Find("[data-testid='se-traceability-state']").TextContent.Should().Contain("Not assessed for this Specification");
            cut.Find("[data-testid='se-traceability-state'] a[href='artifact-traceability']").TextContent.Should().Contain("Open Requirements Traceability");
            cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed");
            cut.Find("[data-testid='requirements-metric']").TextContent.Should().Contain("Requirements analyzed");
            cut.Markup.Should().NotContain("HEALTHY");
            cut.Markup.Should().NotContain("coverage attention");
            cut.Markup.Should().NotContain("No traceability information available");
        });
        _extractionService.Verify(s => s.ExtractAsync(spec, ExtractionProfile.Speckit, It.IsAny<CancellationToken>()), Times.Once);
        _saveReviewed.Verify(m => m.ExecuteAsync(It.IsAny<SaveReviewedCandidatesInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SpecificationExplorer_DistinguishesCompletedAnalysisWithZeroCandidatesFromNotRun()
    {
        const string spec = "# Empty review spec\n\n## Notes\nNo candidate patterns.";
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "custom-spec.txt", null, null, "File", select: true);
        _extractionService.Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExtractionPipelineResult.NonSuccess(PipelineStatus.NoResults, spec.Length, spec.Split('\n').Length, 1, ExtractionProfile.Speckit));

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed");
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("0");
            cut.Find("[data-testid='spec-no-review-candidates']").TextContent.Should().Contain("No review candidates were found");
            cut.FindAll("[data-testid='extract-pre-state']").Should().BeEmpty();
        });
        _session.Verify(s => s.SaveSpecificationAnalysisAsync(It.IsAny<SpecificationAnalysisCacheEntry>()), Times.Once);
    }

    [Fact]
    public void SpecificationExplorer_ReusesCachedResultForCurrentArtifact()
    {
        const string spec = "# Cached specification";
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "requirements-person.md", null, null, "File", select: true);
        var candidate = new CandidateSnapshot(
            Guid.NewGuid(), "FR-007: Cached candidate", ScenarioKind.Requirement,
            ClassificationSignal.Rfc2119Lowercase, null, BlockType.UnorderedListItem,
            null, false, CandidateReviewStatus.AutoAccepted, CandidateSaveState.Pending, null, null);
        _session.Setup(s => s.GetSpecificationAnalysisAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new SpecificationAnalysisCacheEntry("cached-key", "speckit-deterministic-v1", DateTimeOffset.UtcNow, spec.Length, 1, 2, [candidate]));

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed");
            cut.Find("[data-testid='spec-review-candidates']").TextContent.Should().Contain("FR-007: Cached candidate");
        });
        _extractionService.Verify(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()), Times.Never);
        _session.Verify(s => s.SaveSpecificationAnalysisAsync(It.IsAny<SpecificationAnalysisCacheEntry>()), Times.Never);
    }

    [Fact]
    public void SpecificationExplorer_AnalysisFailureDoesNotRetryUntilExplicitRetry()
    {
        const string spec = "# Retry specification";
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "retry-spec.md", null, null, "File", select: true);
        _extractionService.SetupSequence(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private internal detail"))
            .ReturnsAsync(MakeResult([], spec));

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Failed"));
        cut.Render();
        _extractionService.Verify(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()), Times.Once);
        cut.Markup.Should().NotContain("private internal detail");

        cut.Find("[data-testid='spec-explorer-analyze']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed"));
        _extractionService.Verify(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void SpecificationExplorer_ProjectSwitchMarksPreviousAnalysisStaleAndHidesItsCandidates()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";

        _documentResolver.SetProjectSpecification(projectASlug, "# Project A Specification\n\n- FR-001: The system shall approve requests.");
        _documentResolver.SetProjectSpecification(projectBSlug, "# Project B Specification");
        _documentResolver.SetSelectedProject(projectASlug);
        _extractionService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .Returns<string, ExtractionProfile, CancellationToken>((text, _, _) => Task.FromResult(text.Contains("Project A", StringComparison.Ordinal)
                ? MakeResult([
            new ExtractionCandidate
            {
                Title = "FR-001: Project A only",
                    Classification = ScenarioKind.Requirement,
                    ClassificationSignal = ClassificationSignal.Rfc2119Lowercase,
                    SourceBlockType = BlockType.UnorderedListItem
                }
            ], text)
                : MakeResult([], text)));

        var cut = Render<SpecificationExplorer>();
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("1"));

        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B Specification");
            cut.Markup.Should().NotContain("FR-001: Project A only");
            cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed");
            cut.Find("[data-testid='candidates-metric']").TextContent.Should().Contain("0");
        });
    }

    [Fact]
    public void SpecificationExplorer_RapidArtifactSwitchDoesNotRenderLatePreviousResult()
    {
        const string projectA = "project-a";
        const string projectB = "project-b";
        const string specA = "# Project A Specification";
        const string specB = "# Project B Specification";
        var pendingA = new TaskCompletionSource<ExtractionPipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _documentResolver.SetProjectSpecification(projectA, specA);
        _documentResolver.SetProjectSpecification(projectB, specB);
        _documentResolver.SetSelectedProject(projectA);
        _extractionService.Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .Returns<string, ExtractionProfile, CancellationToken>((text, _, _) => text == specA
                ? pendingA.Task
                : Task.FromResult(MakeResult([
                    new ExtractionCandidate
                    {
                        Title = "FR-002: Project B candidate",
                        Classification = ScenarioKind.Requirement,
                        ClassificationSignal = ClassificationSignal.Rfc2119Lowercase,
                        SourceBlockType = BlockType.UnorderedListItem,
                    }
                ], text)));

        var cut = Render<SpecificationExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzing"));

        _documentResolver.SetSelectedProject(projectB);
        cut.Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-review-candidates']").TextContent.Should().Contain("Project B candidate"));

        pendingA.SetResult(MakeResult([
            new ExtractionCandidate
            {
                Title = "FR-001: Project A candidate",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Lowercase,
                SourceBlockType = BlockType.UnorderedListItem,
            }
        ], specA));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='spec-review-state']").TextContent.Should().Be("Analyzed");
            cut.Find("[data-testid='spec-review-candidates']").TextContent.Should().Contain("Project B candidate");
            cut.Markup.Should().NotContain("Project A candidate");
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
            cut.Markup.Should().Contain("No Specification artifact was detected in");
            cut.Markup.Should().NotContain("OLD WORKSPACE SPEC");
            cut.FindAll("[data-testid='spec-explorer-analyze']").Should().BeEmpty();
        });
    }

    [Fact]
    public void SpecificationExplorer_NoSampleProject_OpensManuallyImportedCustomFileName()
    {
        const string spec = "# School attendance requirements\n\n- FR-001: The system shall record absence.";
        string? analyzedText = null;
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "requirements.md", null, null, "File", select: true);
        _extractionService
            .Setup(s => s.ExtractAsync(It.IsAny<string>(), ExtractionProfile.Speckit, It.IsAny<CancellationToken>()))
            .Callback<string, ExtractionProfile, CancellationToken>((text, _, _) => analyzedText = text)
            .ReturnsAsync(MakeResult([], spec));

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("School attendance requirements");
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("requirements.md");
            cut.Markup.Should().NotContain("No Sample Project selected");
        });
        cut.WaitForAssertion(() => analyzedText.Should().Be(spec));
    }

    [Fact]
    public void SpecificationExplorer_EmptyWorkspace_OffersImportAndSampleProjects()
    {
        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-empty'] h2").TextContent.Should().Be("No Specification artifact is loaded");
            cut.Find("[data-testid='artifact-explorer-import-toggle']").TextContent.Trim().Should().Be("Import specification");
            cut.Find("[data-testid='artifact-explorer-empty'] a[href='sample-projects']").TextContent.Should().Be("Open Sample Projects");
            cut.Markup.Should().NotContain("No Sample Project selected");
            cut.Markup.Should().NotContain("specification.md files");
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
