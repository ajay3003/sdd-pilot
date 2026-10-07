using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class TraceabilityPageTests : BunitContext
{
    private CurrentWorkspaceSnapshot _snapshot = CurrentWorkspaceSnapshot.None();

    public TraceabilityPageTests()
    {
        var workspace = new WorkspaceArtifactRepository();
        Services.AddSingleton<IWorkspaceSessionService>(workspace);
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new BirkNext.Web.Models.ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddScoped<SddEvidenceGraphService>();
        var autosave = new Mock<IWorkspaceAutoSaveService>();
        autosave.Setup(x => x.SaveNowAsync()).ReturnsAsync(true);
        Services.AddSingleton(autosave.Object);
        var artifactContext = new Mock<IArtifactExplorerContext>();
        artifactContext.Setup(x => x.GetStateAsync(It.IsAny<WorkspaceArtifactType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkspaceArtifactType role, CancellationToken _) => new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty,
                null, null, []));
        Services.AddSingleton(artifactContext.Object);
        var projection = new Mock<ICurrentWorkspaceProjection>();
        projection.Setup(x => x.GetAsync()).Returns(() => Task.FromResult(_snapshot));
        projection.SetupGet(x => x.Current).Returns(() => _snapshot);
        Services.AddSingleton<ICurrentWorkspaceProjection>(projection.Object);
    }

    [Fact]
    public void TraceabilityWithoutSpecificationIsNotPresentedAsFailure()
    {
        var cut = Render<Traceability>();
        cut.Markup.Should().Contain("Specification unavailable");
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.Markup.Should().NotContain("Load Artifacts First");
    }

    [Fact]
    public void TraceabilityUsesTheSharedMatrixAndNoLegacyAnalysisActions()
    {
        _snapshot = WorkspaceSnapshots.Loaded("Saved workspace", null, null, WorkspaceArtifactType.Specification);
        var artifactContext = Services.GetRequiredService<IArtifactExplorerContext>();
        Mock.Get(artifactContext).Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArtifactExplorerState(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Loaded, null, null,
                [WorkspaceSnapshots.Available(WorkspaceArtifactType.Specification).SelectedArtifact!],
                WorkspaceSnapshots.Available(WorkspaceArtifactType.Specification).SelectedArtifact, ExplorerSelectionReason.OnlyArtifact,
                "# Feature Specification\n\n## Requirements\n\n### FR-01 Test\nText"));
        var cut = Render<Traceability>();
        cut.Markup.Should().Contain("Shared SDD evidence graph");
        cut.FindAll("button").Should().BeEmpty();
    }
}
