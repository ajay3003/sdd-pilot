using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
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
    public void A_resolved_view_is_shown_without_another_read()
    {
        var resolved = new ProjectEvidenceView("dev", "Development", new EnvironmentProject { ProjectName = "M2LB" }, null, [], [], [], []);
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(resolved);
        Services.AddSingleton(evidence.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());

        var cut = Render<ProjectContextStrip>();

        cut.Find("[data-testid=project-context-project]").TextContent.Should().Be("M2LB");
        evidence.Verify(e => e.GetAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
