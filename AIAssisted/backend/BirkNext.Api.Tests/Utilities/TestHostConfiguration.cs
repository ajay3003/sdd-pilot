using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>Helpers for creating test hosts with explicit external-engine configuration.</summary>
public static class TestHostConfiguration
{
    /// <summary>Create a WebApplicationFactory with all heavy/external engines disabled by default.</summary>
    public static WebApplicationFactory<Program> CreateDefaultHostWithEnginesDisabled()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FrontendBrowserRuntime:Enabled", "false" },
                        { "FrontendAccessibility:Enabled", "false" },
                        { "FrontendLighthouse:Enabled", "false" },
                        { "FrontendPassiveSecurity:Enabled", "false" },
                        { "AuthenticatedReview:Enabled", "false" }
                    });
                });
            });
    }

    /// <summary>Create a WebApplicationFactory with a specific engine enabled for testing.</summary>
    public static WebApplicationFactory<Program> CreateHostWithEngineEnabled(string engineConfigKey)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FrontendBrowserRuntime:Enabled", "false" },
                        { "FrontendAccessibility:Enabled", "false" },
                        { "FrontendLighthouse:Enabled", "false" },
                        { "FrontendPassiveSecurity:Enabled", "false" },
                        { "AuthenticatedReview:Enabled", "false" },
                        { engineConfigKey, "true" }
                    });
                });
            });
    }
}
