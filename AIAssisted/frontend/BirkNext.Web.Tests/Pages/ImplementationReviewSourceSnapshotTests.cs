using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Analysis snapshots are workspace-level (Project Import stores them without a Target Environment), so Implementation Evidence
/// Review must list them without an environment ID. The old gate disabled the button until an ID was typed — an imported project's
/// snapshot could never be bound.
/// </summary>
public sealed class ImplementationReviewSourceSnapshotTests : BunitContext
{
    private readonly Mock<ISddEvidenceApiService> _api = new();

    public ImplementationReviewSourceSnapshotTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(new Mock<IWorkspaceAutoSaveService>().Object);
        Services.AddSingleton(_api.Object);
        Services.AddScoped<SddEvidenceGraphService>();
    }

    [Fact]
    public void Load_is_enabled_without_an_environment_and_lists_the_workspace_snapshots()
    {
        var snapshot = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new SourceArchive("project.zip", new string('a', 64), 10), Status = SourceAnalysisStatus.Ready };
        _api.Setup(x => x.SourceSnapshotsAsync("", It.IsAny<CancellationToken>())).ReturnsAsync([snapshot]);
        var cut = Render<ImplementationReview>();

        var load = cut.Find("[data-testid=ier-load-snapshots]");
        load.HasAttribute("disabled").Should().BeFalse();
        cut.Markup.Should().Contain("environment ID (optional)");
        load.Click();

        _api.Verify(x => x.SourceSnapshotsAsync("", It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=ier-source-snapshot]").InnerHtml.Should().Contain(snapshot.Id.ToString());
        cut.Find("[data-testid=ier-source-message]").TextContent.Should().Contain("Loaded 1 metadata snapshot");
    }

    [Fact]
    public void An_empty_workspace_says_so_instead_of_blaming_the_environment()
    {
        _api.Setup(x => x.SourceSnapshotsAsync("", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var cut = Render<ImplementationReview>();

        cut.Find("[data-testid=ier-load-snapshots]").Click();

        cut.Find("[data-testid=ier-source-message]").TextContent.Should().Be("No Source Analysis snapshots are available for this workspace.");
        cut.FindAll("[data-testid=ier-source-snapshot]").Should().BeEmpty();
    }
}
