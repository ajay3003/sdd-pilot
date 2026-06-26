using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class HomePageTests : BunitContext
{
    [Fact]
    public void RootRoute_RedirectsToDashboard()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();

        Render<Home>();

        navigation.Uri.Should().EndWith("/dashboard");
    }
}
