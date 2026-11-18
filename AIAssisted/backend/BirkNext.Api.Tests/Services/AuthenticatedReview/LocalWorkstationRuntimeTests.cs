using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

/// <summary>
/// Defect 2026-11-18: a pipeline artifact (Production, shipped appsettings.json only) reported the HTTPS inspection certificate as not
/// installed and not trusted, and refused to install it, while development worked — because only the developer's appsettings.Local.json
/// set AuthenticatedReview to the LocalWorkstation runtime. Without an explicit AuthenticatedReview Enabled/Runtime the runtime now follows
/// Local Tester Mode, including its loopback-only listener check; an explicit configuration still wins.
/// </summary>
public sealed class LocalWorkstationRuntimeTests
{
    [Theory]
    [InlineData(false, true, true)]   // not configured, mode active → local workstation (artifact, tester package, local Release run)
    [InlineData(false, false, false)] // not configured, mode off or a non-loopback listener → not local
    public void WithoutExplicitConfiguration_LocalTesterModeDecides(bool configured, bool modeActive, bool expected) =>
        new AuthenticatedReviewOptions { RuntimeConfigured = configured, LocalTesterModeActive = () => modeActive }.IsLocalWorkstation.Should().Be(expected);

    [Theory]
    [InlineData(true, "LocalWorkstation", true)]
    [InlineData(true, "Unsupported", false)]
    [InlineData(false, "LocalWorkstation", false)]
    public void AnExplicitConfiguration_WinsOverLocalTesterMode(bool enabled, string runtime, bool expected) =>
        new AuthenticatedReviewOptions { Enabled = enabled, Runtime = runtime, LocalTesterModeActive = () => true }.IsLocalWorkstation.Should().Be(expected);

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void TheShippedConfiguration_IsALocalWorkstationInEveryHostingEnvironment(string environment)
    {
        using var factory = Host(environment);
        var options = factory.Services.GetRequiredService<IOptions<AuthenticatedReviewOptions>>().Value;
        options.RuntimeConfigured.Should().BeFalse("the shipped appsettings.json no longer pins AuthenticatedReview to Unsupported");
        options.IsLocalWorkstation.Should().BeTrue();
        options.EffectiveRuntime.Should().Be("LocalWorkstation");
    }

    [Fact]
    public void LocalTesterModeOff_IsNotALocalWorkstation()
    {
        using var factory = Host("Production", mode: false);
        factory.Services.GetRequiredService<IOptions<AuthenticatedReviewOptions>>().Value.IsLocalWorkstation.Should().BeFalse();
    }

    [Fact]
    public void ANonLoopbackListener_TurnsTheLocalWorkstationRuntimeOff()
    {
        using var factory = Host("Production");
        var options = factory.Services.GetRequiredService<IOptions<AuthenticatedReviewOptions>>().Value;
        factory.Services.GetRequiredService<LocalTrustedExecution>().ObserveListeners(["http://0.0.0.0:5000"]);
        options.IsLocalWorkstation.Should().BeFalse("BirkNext reachable from other machines never opens a browser or changes certificate trust");
    }

    [Fact]
    public void AnExplicitUnsupportedRuntime_StaysOff()
    {
        using var factory = Host("Production", runtime: "Unsupported");
        factory.Services.GetRequiredService<IOptions<AuthenticatedReviewOptions>>().Value.IsLocalWorkstation.Should().BeFalse();
    }

    private static WebApplicationFactory<Program> Host(string environment, bool mode = true, string? runtime = null) =>
        TestHostConfiguration.CreateDefaultHostWithEnginesDisabled().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting(LocalTrustedExecution.ModeSetting, mode ? "true" : "false");
            // As shipped: no AuthenticatedReview Enabled/Runtime (the test host's explicit "false" is cleared; appsettings.Local.json is removed
            // by TestHostConfiguration). A later in-memory source with null values hides the earlier ones.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthenticatedReview:Enabled"] = null,
                ["AuthenticatedReview:Runtime"] = runtime,
            }));
        });
}
