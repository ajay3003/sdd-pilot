using System.Xml.Linq;
using FluentAssertions;

namespace BirkNext.Web.Tests.Packaging;

/// <summary>
/// The development copy of the generated scoped CSS bundle (written to wwwroot by setup-dev-scoped-css.ps1) must never become build
/// content: as a second static web asset with the generated bundle's path it crashed the SDK ("Sequence contains more than one element"),
/// and it could ship stale styles. The copy is excluded and the guard warns instead of failing every later build.
/// </summary>
public sealed class ScopedCssBuildConfigurationTests
{
    private const string BundlePath = "wwwroot/BirkNext.Web.styles.css";

    private static XDocument WebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIAssisted", "frontend", "BirkNext.Web", "BirkNext.Web.csproj"))) directory = directory.Parent;
        return XDocument.Load(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("BirkNext.Web.csproj"), "AIAssisted", "frontend", "BirkNext.Web", "BirkNext.Web.csproj"));
    }

    [Fact]
    public void TheDevelopmentCopyIsNeverBuildContent()
    {
        WebProject().Descendants("Content").Where(c => (string?)c.Attribute("Remove") == BundlePath)
            .Should().ContainSingle("the generated bundle must be the only static web asset with this path");
    }

    [Fact]
    public void TheGuardNamesTheCopyAsAWarning_AndNoTargetFailsTheBuildOverIt()
    {
        var targets = WebProject().Descendants("Target").Where(t => ((string?)t.Attribute("Condition"))?.Contains(BundlePath) == true).ToList();
        targets.Should().ContainSingle();
        targets[0].Elements("Warning").Should().ContainSingle().Which.Attribute("Code")!.Value.Should().Be("BNWEB001");
        targets[0].Elements("Error").Should().BeEmpty("a development copy is excluded, so it no longer breaks a build");
    }
}
