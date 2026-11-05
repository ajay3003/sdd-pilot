using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// NEXT50 root cause: ITestDataApiService was never registered in the real host. Tests register a mock, so the XML fixture save ("Fixtures cannot be
/// saved in this host.") and the automatic fixture draft after an import (silently no draft) only failed in the running application. The host now
/// registers the typed client, and no surface refuses to save because of how the service was obtained.
/// </summary>
public sealed class TestDataHostCompositionTests
{
    private static string Source(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web"))) dir = dir.Parent;
        dir.Should().NotBeNull("the frontend sources are next to the test project");
        return File.ReadAllText(Path.Combine([dir!.FullName, "BirkNext.Web", .. relative]));
    }

    [Fact]
    public void The_host_registers_the_Test_Data_client()
    {
        Source("Program.cs").Should().Contain("AddHttpClient<ITestDataApiService, TestDataApiService>");
    }

    [Fact]
    public void No_surface_refuses_to_save_a_fixture_because_of_the_host()
    {
        Source("Components", "XmlFixturePanel.razor").Should().NotContain("cannot be saved in this host");
        Source("Services", "XmlFixtureAutoDraft.cs").Should().Contain("ResolveTestData(services)");
    }
}
