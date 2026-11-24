using BirkNext.Api.Services;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Infrastructure;

/// <summary>No test host may write into the developer's App_Data (a full suite once rotated the running BirkNext's launch code).</summary>
public sealed class TestAppDataIsolationTests
{
    [Fact]
    public void Test_hosts_use_a_temporary_App_Data_never_the_API_projects_own()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var root = Path.GetFullPath(AppDataLocation.Root(environment));

        TestAppDataIsolation.ApiProjectFolder.Should().NotBeNull("the tests run from the source tree");
        Path.GetFullPath(environment.ContentRootPath).TrimEnd('\\', '/').Should().BeEquivalentTo(Path.GetFullPath(TestAppDataIsolation.ApiProjectFolder!).TrimEnd('\\', '/'));
        root.Should().NotStartWith(Path.GetFullPath(Path.Combine(TestAppDataIsolation.ApiProjectFolder!, "App_Data")));
        root.Should().StartWith(Path.GetFullPath(TestAppDataIsolation.Redirected!));
        var session = factory.Services.GetRequiredService<BirkNext.Api.Services.LocalSecurity.LocalSessionService>();
        Path.GetFullPath(session.CodePath).Should().StartWith(root, "the launch code of a test host is never the developer's");
    }

    [Fact]
    public void A_test_with_its_own_content_root_keeps_its_own_App_Data()
    {
        var own = Path.Combine(Path.GetTempPath(), "birknext-own-root-" + Guid.NewGuid().ToString("N"));
        AppDataLocation.Root(own).Should().Be(Path.Combine(own, "App_Data"));
    }
}
