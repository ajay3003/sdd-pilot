using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>Helpers for creating test hosts with explicit external-engine configuration.</summary>
public static class TestHostConfiguration
{
    /// <summary>Create a WebApplicationFactory with Layer 1 deployment policy safely disabled (deterministic test execution).</summary>
    /// <param name="removeLocalJson">When true, removes appsettings.Local.json which has hardcoded Layer2 values for development.
    /// Set to true for status/snapshot tests, false for persistence tests that need to save/load.</param>
    public static WebApplicationFactory<Program> CreateDefaultHostWithEnginesDisabled(bool removeLocalJson = true)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");

                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    if (removeLocalJson)
                    {
                        // Remove Local.json which contains Layer2 hardcoded values meant for development only
                        var fileSourcesToRemove = config.Sources
                            .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
                            .Where(s => s.Path?.Contains("Local") ?? false)
                            .ToList();

                        foreach (var source in fileSourcesToRemove)
                        {
                            config.Sources.Remove(source);
                        }
                    }

                    // Add test-specific overrides to disable Layer 1 and Layer 2 for deterministic testing
                    // Layer 2 keys NOT set here - they should be absent unless explicitly set by tests
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FrontendBrowserRuntime:Enabled", "false" },
                        { "FrontendAccessibility:Enabled", "false" },
                        { "FrontendLighthouse:Enabled", "false" },
                        { "FrontendPassiveSecurity:Enabled", "false" },
                        { "AuthenticatedReview:Enabled", "false" },
                        { "FrontendQualityCapabilities:BrowserRuntimeAllowed", "false" },
                        { "FrontendQualityCapabilities:AccessibilityAllowed", "false" },
                        { "FrontendQualityCapabilities:LighthouseAllowed", "false" },
                        { "FrontendQualityCapabilities:PassiveSecurityAllowed", "false" }
                        // Layer 2 keys intentionally absent - only loaded from persistence when tests save them
                    });
                });
            });
    }

    /// <summary>Create a WebApplicationFactory with a specific Layer 1 engine enabled for testing. Layer 2 defaults to absent (defaults false).</summary>
    public static WebApplicationFactory<Program> CreateHostWithEngineEnabled(string engineConfigKey)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");

                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    var localSources = config.Sources
                        .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
                        .Where(source => source.Path?.Contains("Local") ?? false)
                        .ToList();

                    foreach (var source in localSources)
                    {
                        config.Sources.Remove(source);
                    }

                    var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "FrontendBrowserRuntime:Enabled", "false" },
                        { "FrontendAccessibility:Enabled", "false" },
                        { "FrontendLighthouse:Enabled", "false" },
                        { "FrontendPassiveSecurity:Enabled", "false" },
                        { "AuthenticatedReview:Enabled", "false" }
                    };

                    // Add Layer 1 keys only when explicitly enabling a Layer 1 key
                    if (engineConfigKey.StartsWith("FrontendQualityCapabilities:"))
                    {
                        overrides["FrontendQualityCapabilities:BrowserRuntimeAllowed"] = "false";
                        overrides["FrontendQualityCapabilities:AccessibilityAllowed"] = "false";
                        overrides["FrontendQualityCapabilities:LighthouseAllowed"] = "false";
                        overrides["FrontendQualityCapabilities:PassiveSecurityAllowed"] = "false";
                    }

                    overrides[engineConfigKey] = "true";

                    config.AddInMemoryCollection(overrides);
                });
            });
    }

    /// <summary>Create a WebApplicationFactory with a minimal legacy-only configuration for migration tests.
    /// This simulates an OLD installation that only has legacy configuration keys, no new Layer1/Layer2 keys.
    /// Explicitly removes Local.json which contains new Layer2 keys to ensure a clean migration test.
    /// </summary>
    public static WebApplicationFactory<Program> CreateHostWithLegacyConfigOnly(Dictionary<string, string?> legacyConfig)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // First, remove Local settings that contain new Layer2 keys
                // This must happen BEFORE ConfigureAppConfiguration to remove the Local file provider
                builder.UseEnvironment("Test");

                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    // Remove any file provider that loads Local.json (which has Layer2 keys)
                    var fileSourcesToRemove = config.Sources
                        .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
                        .Where(s => s.Path?.Contains("Local") ?? false)
                        .ToList();

                    foreach (var source in fileSourcesToRemove)
                    {
                        config.Sources.Remove(source);
                    }

                    // Now add the legacy-only configuration with highest precedence (last = highest)
                    config.AddInMemoryCollection(legacyConfig);
                });
            });
    }
}
