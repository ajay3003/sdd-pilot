using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class CompareSpecsPageTests : BunitContext
{
    [Fact]
    public void CompareSpecs_RedirectsToSpecDrift()
    {
        var nav = Services.GetRequiredService<NavigationManager>();

        Render<CompareSpecs>();

        nav.Uri.Should().Contain("/spec-drift");
        nav.Uri.Should().Contain("tab=changes");
    }
}
