using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class TraceabilityPageTests : BunitContext
{
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
    }

    [Fact]
    public void TraceabilityWithoutSpecificationIsNotPresentedAsFailure()
    {
        var cut = Render<Traceability>();
        cut.Markup.Should().Contain("Requirements are not available in this workspace");
        cut.Markup.Should().Contain("Missing evidence is a traceability gap, not an automatic failure");
        cut.Markup.Should().NotContain("Load Artifacts First");
    }

    [Fact]
    public void TraceabilityUsesTheSharedMatrixAndNoLegacyAnalysisActions()
    {
        var cut = Render<Traceability>();
        cut.Markup.Should().Contain("Shared SDD evidence graph");
        cut.FindAll("button").Should().BeEmpty();
    }
}
