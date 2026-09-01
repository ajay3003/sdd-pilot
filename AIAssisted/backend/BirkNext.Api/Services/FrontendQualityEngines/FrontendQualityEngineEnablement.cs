namespace BirkNext.Api.Services.FrontendQualityEngines;

/// <summary>
/// Resolves the single effective Layer 2 value. New System Settings preferences
/// take precedence; legacy engine flags are compatibility input only.
/// </summary>
public static class FrontendQualityEngineEnablement
{
    public static bool Resolve(
        IConfiguration configuration,
        FrontendQualityEngineId engineId,
        bool legacyEnabled)
    {
        var key = $"{FrontendQualityEnginePreferences.SectionName}:{GetPreferenceKey(engineId)}";
        return configuration.GetSection(key).Exists()
            ? configuration.GetValue(key, false)
            : legacyEnabled;
    }

    private static string GetPreferenceKey(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => nameof(FrontendQualityEnginePreferences.BrowserRuntimeEnabled),
        FrontendQualityEngineId.Accessibility => nameof(FrontendQualityEnginePreferences.AccessibilityEnabled),
        FrontendQualityEngineId.Lighthouse => nameof(FrontendQualityEnginePreferences.LighthouseEnabled),
        FrontendQualityEngineId.PassiveSecurity => nameof(FrontendQualityEnginePreferences.PassiveSecurityEnabled),
        _ => string.Empty,
    };
}
