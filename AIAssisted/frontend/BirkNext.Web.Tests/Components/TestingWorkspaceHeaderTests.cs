using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using BirkNext.Web.Tests.Services;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// While the saved project selection is still being read, the shared testing-workspace header says so instead of "No project selected"
/// (measured: about 0.6 s on every fresh start); once read, a missing project is stated as before.
/// </summary>
public sealed class TestingWorkspaceHeaderTests : BunitContext
{
    private IRenderedComponent<TestingWorkspaceHeader> Header(string? project) => Render<TestingWorkspaceHeader>(p => p
        .Add(x => x.Workspace, "Manual Testing").Add(x => x.HomeRoute, "manual-testing").Add(x => x.Stages, [])
        .Add(x => x.Title, "Manual Testing").Add(x => x.ProjectName, project));

    [Fact]
    public void While_the_saved_project_is_read_the_header_says_it_is_loading_not_that_no_project_is_selected()
    {
        var projection = new Mock<ICurrentWorkspaceProjection>();
        projection.SetupGet(p => p.Current).Returns((CurrentWorkspaceSnapshot?)null);
        Services.AddSingleton(projection.Object);

        var cut = Header(null);

        cut.Find("[data-testid=twh-project-restoring]").TextContent.Should().Be("Loading the selected project…");
        cut.Find("[data-testid=twh-project]").TextContent.Should().NotContain("No project selected");
    }

    [Fact]
    public void Once_read_a_missing_project_is_stated_and_a_project_is_named()
    {
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);

        Header(null).Find("[data-testid=twh-project]").TextContent.Should().Contain("No project selected");
        Header("LendoPlus").Find("[data-testid=twh-project]").TextContent.Should().Contain("LendoPlus");
    }
}
