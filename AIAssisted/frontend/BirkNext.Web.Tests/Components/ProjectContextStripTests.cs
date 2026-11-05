using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Hardening (NEXT41, IMP-01): every page's context strip shows the same Project Evidence. A page that had not resolved it yet (Import)
/// said "Not configured" while Recommended Workflow showed the project; the strip now resolves once through the shared service.</summary>
public sealed class ProjectContextStripTests : BunitContext
{
    [Fact]
    public void An_unresolved_view_is_resolved_once_through_the_shared_service_instead_of_reading_Not_configured()
    {
        var unresolved = new ProjectEvidenceView("dev", "Development", null, null, [], [], [], []);
        var resolved = unresolved with { Project = new EnvironmentProject { ProjectName = "M2LB" } };
        var current = unresolved;
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(() => current);
        evidence.Setup(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(() => { current = resolved; return resolved; });
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());

        var cut = Render<ProjectContextStrip>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=project-context-project]").TextContent.Should().Be("M2LB"));
        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void A_resolved_and_classified_view_is_shown_without_another_read()
    {
        var resolved = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, [], [], [], []) { Classified = true };
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(resolved);
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());

        var cut = Render<ProjectContextStrip>();

        cut.Find("[data-testid=project-context-project]").TextContent.Should().Be("M2LB");
        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void A_configured_view_without_Source_Analysis_classification_is_resolved_once_so_role_counts_match_other_pages()
    {
        var cached = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, [], [], [], []);
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(cached);
        evidence.Setup(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(cached with { Classified = true });
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());

        Render<ProjectContextStrip>();

        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void After_another_page_invalidates_the_cache_the_strip_resolves_again_and_never_loops_when_classification_is_unavailable()
    {
        var classified = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, [], [], [], []) { Classified = true };
        var current = classified;
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(() => current);
        evidence.Setup(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(() => current with { Classified = false });
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Render<ProjectContextStrip>();
        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Never());

        current = classified with { Classified = false };                 // Source Analysis invalidated the cache
        evidence.Raise(e => e.Changed += null);
        evidence.Raise(e => e.Changed += null);
        evidence.Raise(e => e.Changed += null);

        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once(),
            "resolved again after the invalidation; an unclassifiable view is not requested on every change");
    }

    [Fact]
    public void A_page_that_never_read_the_workspace_reads_it_once_so_the_role_count_matches_other_pages()
    {
        var empty = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, [], [], [], []) { Classified = true };
        var current = empty;
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(() => current);
        var workspace = new Mock<ICurrentWorkspaceProjection>();
        workspace.SetupGet(w => w.Current).Returns((CurrentWorkspaceSnapshot?)null);
        workspace.Setup(w => w.GetAsync()).ReturnsAsync(() => { current = empty with { Packages = [] }; return WorkspaceSnapshots.AllRoles(); });
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(workspace.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());

        Render<ProjectContextStrip>();

        workspace.Verify(w => w.GetAsync(), Times.Once(), "the workspace (its imported packages) is read before counting roles");
        evidence.Verify(e => e.Peek(It.IsAny<string?>()), Times.AtLeast(2), "the strip recomputes after the workspace is read");
    }
}
