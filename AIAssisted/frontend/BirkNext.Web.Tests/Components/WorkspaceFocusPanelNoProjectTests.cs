using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// With nothing imported (first start, or after Reset Local Data) the Dashboard's "Your next steps" never lists blocked steps or the built-in
/// system guide's scenario candidates as if they were the user's: it says no project is imported and links to Import Project.
/// </summary>
public sealed class WorkspaceFocusPanelNoProjectTests : BunitContext
{
    private readonly NavigationSectionState _state = new();
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();

    public WorkspaceFocusPanelNoProjectTests()
    {
        Services.AddSingleton(_state);
        Services.AddSingleton(_plans.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void Projection(CurrentWorkspaceState state, string? slug)
    {
        var projection = new Mock<ICurrentWorkspaceProjection>();
        projection.Setup(p => p.GetAsync()).ReturnsAsync(new CurrentWorkspaceSnapshot(state, null, "", slug, slug, slug is not null, [], "", null, 1));
        Services.AddSingleton(projection.Object);
    }

    [Fact]
    public void Without_an_imported_project_the_panel_says_so_once_without_counts_or_its_own_import_link()
    {
        Projection(CurrentWorkspaceState.NoWorkspace, null);
        _state.SetView(WorkspaceView.ManualTesting);

        var cut = Render<WorkspaceFocusPanel>(p => p.Add(x => x.Capabilities, []));

        cut.Find("[data-testid=wfp-no-project]").TextContent.Should().Contain("once a project is imported");
        cut.FindAll("a[href=project-import]").Should().BeEmpty("the page's empty state offers Import Project once");
        cut.FindAll("[data-testid=wfp-ready], [data-testid=wfp-blocked], [data-testid=wfp-done]").Should().BeEmpty("\"1 blocked\" before anything exists is noise");
        cut.FindAll("[data-testid=wfp-action]").Should().BeEmpty();
        cut.FindAll("[data-testid=wfp-test-plan]").Should().BeEmpty();
        _plans.Verify(a => a.SummaryAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never(),
            "the built-in guide's candidates are not this user's evidence");
    }
}
