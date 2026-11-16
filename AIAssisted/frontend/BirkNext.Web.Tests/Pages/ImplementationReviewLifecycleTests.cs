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
        var sddApi = new Mock<ISddEvidenceApiService>();
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(repository);
        Services.AddSingleton<IWorkspaceAutoSaveService>(autosave.Object);
        Services.AddSingleton(sddApi.Object);
        Services.AddScoped<SddEvidenceGraphService>();
    }

    [Fact]
    public void DocumentOnlyWorkspace_ReportsSourceNotAssessedAndDoesNotAcceptPastedCode()
    {
        var cut = Render<ImplementationReview>();

        cut.Markup.Should().Contain("Source snapshot unavailable");
        cut.Markup.Should().Contain("implementation is not assessed");
        cut.Markup.Should().NotContain("NoSourceAvailable");
        cut.Find("#test-result-import").Should().NotBeNull();
        cut.Markup.Should().Contain("Designed scenarios remain separate");
        cut.Markup.Should().NotContain("Reviewing...");
    }

    [Fact] // R3: one implementation verdict — the feature readiness (gap evaluation); the requirement table is supporting evidence only
    public void The_requirement_table_is_supporting_evidence_and_names_the_one_verdict()
    {
        var cut = Render<ImplementationReview>();

        cut.Find("[data-testid=ier-verdict-source]").TextContent.Should().Contain("The implementation verdict is the feature readiness above")
            .And.Contain("same verdict Quality Review shows").And.Contain("not a second verdict");
        cut.Markup.Should().Contain("Supporting evidence per requirement").And.NotContain("<h2>Requirements and evidence</h2>");
    }
}
