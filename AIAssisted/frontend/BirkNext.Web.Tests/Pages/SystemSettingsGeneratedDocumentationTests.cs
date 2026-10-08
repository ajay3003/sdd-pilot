using BirkNext.GeneratedDocumentation;
using BirkNext.Web.Pages.Admin;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public partial class SystemSettingsEnvironmentDiagnosticsTests
{
    [Fact]
    public void GeneratedDocumentationHealth_IsADeveloperDiagnosticSectionWithARunButton()
    {
        var cut = Render<SystemSettings>();

        cut.WaitForAssertion(() => FindButton(cut, "Generated Documentation Health").Should().NotBeNull());
        FindButton(cut, "Generated Documentation Health")!.Click();

        cut.Find(".ss-nav-item.is-active").TextContent.Should().Contain("Generated Documentation Health");
        cut.Find("[data-testid=gdh-description]").TextContent.Should().Be(GeneratedDocumentationText.Description);
        FindButton(cut, "Run diagnostics").Should().NotBeNull();
        cut.FindAll("button").Any(button => button.TextContent.Contains("Edit Settings")).Should().BeFalse();
    }
}
