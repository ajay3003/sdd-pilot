using BirkNext.BuildInfo;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Runtime &amp; Environment: shows the profile the backend reports (never a guess), its database and k6 runner state, offers no
/// switch, and stays readable when the backend cannot answer.
/// </summary>
public sealed class RuntimeEnvironmentCardTests : BunitContext
{
    private sealed class Api(RuntimeEnvironmentInfo? info, string? error = null) : IBuildInfoApiService
    {
        public Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default) => Task.FromResult<(BuildInfoResponse?, string?)>((null, "n/a"));
        public Task<(RuntimeEnvironmentInfo? Info, string? Error)> GetRuntimeAsync(CancellationToken ct = default) => Task.FromResult((info, error));
    }

    private IRenderedComponent<RuntimeEnvironmentCard> Render(IBuildInfoApiService? api)
    {
        if (api is not null) Services.AddSingleton(api);
        var card = Render<RuntimeEnvironmentCard>();
        card.WaitForAssertion(() => card.Find("[data-testid=rte]").GetAttribute("aria-busy").Should().Be("false"));
        return card;
    }

    private static RuntimeEnvironmentInfo Tester(string k6 = "Available") => new()
    {
        Profile = RuntimeProfile.TesterNative, ProfileName = RuntimeProfiles.DisplayName(RuntimeProfile.TesterNative),
        ProfileSource = "Set by the launcher (fixed until BirkNext is restarted with another launcher)", Distribution = "Windows Tester Package",
        DatabaseMode = "Bundled PostgreSQL, managed by the BirkNext launcher (this user only, local connections only)", DatabaseStatus = "Connected", DatabaseServerVersion = "16.13",
        PerformanceRunner = "Bundled k6.exe (native Windows process)", PerformanceRunnerAvailability = k6, K6Version = k6 == "Available" ? "1.0.0" : null,
        PerformanceRunnerDetail = "Performance testing is unavailable because the bundled k6 executable is missing. Download the Tester Package again.",
    };

    [Fact]
    public void Shows_the_reported_profile_database_and_k6_runner_without_a_switch()
    {
        var card = Render(new Api(Tester()));
        card.Find("[data-testid=rte]").GetAttribute("data-profile").Should().Be("TesterNative");
        card.Find("[data-testid=rte-profile]").TextContent.Should().Be("Tester (native Windows)");
        card.Find("[data-testid=rte-distribution]").TextContent.Should().Be("Windows Tester Package");
        card.Find("[data-testid=rte-source]").TextContent.Should().Contain("cannot be changed while BirkNext runs");
        card.Find("[data-testid=rte-database-status]").TextContent.Should().Contain("Connected").And.Contain("server 16.13");
        card.Find("[data-testid=rte-k6]").TextContent.Should().Contain("Bundled k6.exe").And.Contain("k6 1.0.0");
        card.FindAll("button, input, select").Should().BeEmpty("the profile is chosen by the launcher, never switched from the browser");
        card.Markup.Should().NotContainEquivalentOf("password").And.NotContain("Host=");
    }

    [Fact]
    public void An_unavailable_k6_runner_says_why_and_never_suggests_containers_to_a_tester()
    {
        var card = Render(new Api(Tester("Unavailable")));
        card.Find("[data-testid=rte-k6-availability]").TextContent.Should().Be("Unavailable");
        card.Find("[data-testid=rte-k6-detail]").TextContent.Should().Contain("bundled k6 executable is missing").And.NotContainEquivalentOf("podman");
    }

    [Fact]
    public void An_unreachable_backend_or_host_without_the_client_is_not_presented_as_a_profile()
    {
        Render(new Api(null, "The backend's runtime information is not reachable.")).Find("[data-testid=rte-unavailable]").TextContent.Should().Contain("not reachable");
        var bare = new BunitContext();
        var card = bare.Render<RuntimeEnvironmentCard>();
        card.WaitForAssertion(() => card.Find("[data-testid=rte-unavailable]").TextContent.Should().Contain("not available"));
        card.FindAll("[data-testid=rte-profile]").Should().BeEmpty();
    }
}
