using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class HomePageTests : BunitContext
{
    [Fact]
    public void RootRoute_without_a_remembered_workspace_opens_Testoversikt()
    {
        // V6 (2026-11-26): Manual Testing is the default workspace; its start page is Testoversikt.
        var navigation = Services.GetRequiredService<NavigationManager>();

        Render<Home>();

        navigation.Uri.Should().EndWith("/manual-testing");
    }

    [Theory]
    [InlineData(BirkNext.Web.Services.WorkspaceView.TestLead)]
    [InlineData(BirkNext.Web.Services.WorkspaceView.AllModules)]
    [InlineData(BirkNext.Web.Services.WorkspaceView.Developer)]
    public void RootRoute_in_another_remembered_workspace_still_opens_the_dashboard(BirkNext.Web.Services.WorkspaceView view)
    {
        Services.AddScoped<BirkNext.Web.Services.NavigationSectionState>();
        Services.GetRequiredService<BirkNext.Web.Services.NavigationSectionState>().SetView(view);
        var navigation = Services.GetRequiredService<NavigationManager>();

        Render<Home>();

        navigation.Uri.Should().EndWith("/dashboard", "the other workspaces keep their start page");
    }
}
