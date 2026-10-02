using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class ImplementationReviewLifecycleTests : BunitContext
{
    public ImplementationReviewLifecycleTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        var repository = new WorkspaceArtifactRepository();
        var autosave = new Mock<IWorkspaceAutoSaveService>();
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(repository);
        Services.AddSingleton<IWorkspaceAutoSaveService>(autosave.Object);
    }

    [Fact]
    public void DocumentOnlyWorkspace_ReportsSourceNotAssessedAndDoesNotAcceptPastedCode()
    {
        var cut = Render<ImplementationReview>();

        cut.Markup.Should().Contain("Not available");
        cut.Markup.Should().Contain("implementation is not assessed");
        cut.FindAll("textarea").Should().BeEmpty();
        cut.Markup.Should().NotContain("Reviewing...");
    }
}
