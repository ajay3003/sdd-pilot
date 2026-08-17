using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class CompareReviewsPageTests : BunitContext
{
    [Fact]
    public void CompareReviews_RedirectsToSpecDrift()
    {
        var mockQuery = new Mock<IGetQaDeltaReviewsQuery>();
        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetQaDeltaReviews).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var mockWorkspace = new Mock<IWorkspaceSessionService>();
        mockWorkspace.Setup(w => w.CurrentProject).Returns("test-project");
        Services.AddSingleton(mockWorkspace.Object);

        Render<CompareReviews>();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain("/spec-drift");
        nav.Uri.Should().Contain("tab=changes");
    }
}
