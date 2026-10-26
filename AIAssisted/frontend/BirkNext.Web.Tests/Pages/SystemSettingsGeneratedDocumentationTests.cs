using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public partial class SystemSettingsEnvironmentDiagnosticsTests
{
    [Fact]
    public void GeneratedDocumentationHealth_IsNoLongerADeveloperSettingsSection()
    {
        var cut = Render<SystemSettings>();

        cut.WaitForAssertion(() => FindButton(cut, "Environment Diagnostics").Should().NotBeNull());
        FindButton(cut, "Generated Documentation Health").Should().BeNull("target-project analysis is not a BirkNext developer setting");
        DocumentationCatalog.SettingsSections.Should().NotContain("Generated Documentation Health");
    }

    [Fact]
    public void Old_settings_link_redirects_to_the_canonical_analysis_page()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("admin/system-settings?section=" + SystemSettings.LegacyGeneratedDocumentationHealthSection);

        Render<SystemSettings>();

        navigation.Uri.Should().EndWith("/" + NavigationCatalog.GeneratedDocumentationHealthRoute);
    }
}
