using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Project Test Setup was removed (2026-11-19). Old /project-scopes links still land somewhere useful: the systems and connections views open
/// Integration Topology, every other view project selection; it is in no navigation group and no catalog, and no replacement page exists.
/// </summary>
public sealed class ProjectTestSetupRetirementTests : BunitContext
{
    [Theory]
    [InlineData(null, null, null, "project-import")]
    [InlineData("areas", null, null, "project-import")]
    [InlineData("setup", null, null, "project-import")]
    [InlineData(null, "scopes", null, "project-import")]
    [InlineData("systems", null, null, "integration-topology")]
    [InlineData("connections", null, "orders-api", "integration-topology")]
    [InlineData(null, "connections", null, "integration-topology")]
    [InlineData(null, null, "orders-api", "integration-topology")]
    public void Old_links_redirect_to_an_existing_page(string? view, string? tab, string? system, string expected) =>
        ProjectTestSetupRetirement.RedirectFor(view, tab, system).Route.Should().Be(expected);

    [Fact]
    public void Opening_the_old_route_replaces_it_with_the_destination()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("project-scopes?view=connections&system=orders-api");
        Render<ProjectScopes>();
        nav.Uri.Should().EndWith("/integration-topology");
        nav.NavigateTo("project-scopes?scope=" + Guid.NewGuid());
        Render<ProjectScopes>();
        nav.Uri.Should().EndWith("/project-import");
    }

    [Fact]
    public void Project_Test_Setup_is_in_no_navigation_group_or_catalog()
    {
        var items = NavigationCatalog.Sections.SelectMany(s => s.Items).ToList();
        items.Should().NotContain(i => i.Route == "project-scopes" || i.Label == "Project Test Setup");
        items.Should().NotContain(i => i.Label.Contains("Test area", StringComparison.OrdinalIgnoreCase), "no replacement page was added");
        WorkspaceViews.All.SelectMany(v => v.Modules.Concat(v.Groups?.SelectMany(g => g.Routes) ?? [])).Should().NotContain("project-scopes");
    }
}
