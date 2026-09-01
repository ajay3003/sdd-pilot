using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.FrontendQualityEngines.Readiness;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

[Collection("Frontend quality engine local settings")]
public sealed class FrontendQualityEngineConfigurationIntegrationTests
{
    [Fact]
    public void LocalTesterConfiguration_DeclaresExplicitFailClosedPolicy()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        config.GetValue<bool>("FrontendQualityCapabilities:BrowserRuntimeAllowed").Should().BeTrue();
        config.GetValue<bool>("FrontendQualityCapabilities:AccessibilityAllowed").Should().BeTrue();
        config.GetValue<bool>("FrontendQualityCapabilities:LighthouseAllowed").Should().BeTrue();
        config.GetValue<bool>("FrontendQualityCapabilities:PassiveSecurityAllowed").Should().BeFalse();
    }

    [Fact]
    public async Task Layer2Preferences_ControlRuntimeOptionsAndReachDependencyProbes()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IOptions<FrontendBrowserRuntimeOptions>>().Value.Enabled.Should().BeTrue();
        services.GetRequiredService<IOptions<FrontendLighthouseOptions>>().Value.Enabled.Should().BeTrue();

        var browser = await services.GetRequiredService<BrowserRuntimeReadinessProvider>()
            .CheckAsync(CancellationToken.None);
        var lighthouse = await services.GetRequiredService<LighthouseReadinessProvider>()
            .CheckAsync(CancellationToken.None);

        browser.StatusReason.Should().NotBe("Browser Runtime engine is disabled.");
        lighthouse.StatusReason.Should().NotBe("Lighthouse review engine is disabled.");
    }

    [Theory]
    [InlineData("BrowserRuntimeEnabled", FrontendQualityEngineId.BrowserRuntime)]
    [InlineData("AccessibilityEnabled", FrontendQualityEngineId.Accessibility)]
    [InlineData("LighthouseEnabled", FrontendQualityEngineId.Lighthouse)]
    [InlineData("PassiveSecurityEnabled", FrontendQualityEngineId.PassiveSecurity)]
    public void NewLayer2Preference_OverridesConflictingLegacyFalse(string preference, FrontendQualityEngineId engine)
    {
        var values = new Dictionary<string, string?>
        {
            [$"FrontendQualityEnginePreferences:{preference}"] = "true",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        FrontendQualityEngineEnablement.Resolve(config, engine, legacyEnabled: false).Should().BeTrue();
    }
}
