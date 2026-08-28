using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Diagnostic test for Layer 2 persistence across save/reload cycle.</summary>
public sealed class PersistenceDiagnosticTest
{
    [Fact(DisplayName = "Persistence Level 2: Configuration reads saved value after Reload()")]
    public async Task Level2_ConfigurationReadsSavedValueAfterReload()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled(removeLocalJson: false);
        using var scope = factory.Services.CreateScope();

        var adminService = scope.ServiceProvider.GetRequiredService<AdminService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // LEVEL 1: Save
        var saveRequest = new SaveSettingsRequest
        {
            FrontendQualityEngines = new SaveFrontendQualityEngineSettings
            {
                BrowserRuntimeEnabled = true,
            }
        };

        var (success, message) = await adminService.SaveSettingsAsync(saveRequest);
        success.Should().BeTrue($"save should succeed: {message}");

        // LEVEL 2: Verify IConfiguration reads the saved value after reload
        var key = "FrontendQualityEnginePreferences:BrowserRuntimeEnabled";
        var valueAfterReload = config.GetValue<bool>(key, false);

        valueAfterReload.Should().BeTrue(because: "configuration should read persisted value after Reload()");

        // LEVEL 3: Verify interpreter reads the saved value
        var interpreter = scope.ServiceProvider.GetRequiredService<FrontendQualityEngineLegacyConfigInterpreter>();
        var (_, enabled) = interpreter.ResolveLayer1And2(FrontendQualityEngineId.BrowserRuntime);

        enabled.Should().BeTrue(because: "interpreter should read persisted value through configuration");
    }
}
