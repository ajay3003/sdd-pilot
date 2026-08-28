namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed class FrontendQualityEnginePreferences
{
    public const string SectionName = "FrontendQualityEnginePreferences";

    public bool BrowserRuntimeEnabled { get; set; }
    public bool AccessibilityEnabled { get; set; }
    public bool LighthouseEnabled { get; set; }
    public bool PassiveSecurityEnabled { get; set; }

    public bool IsEnabled(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => BrowserRuntimeEnabled,
        FrontendQualityEngineId.Accessibility => AccessibilityEnabled,
        FrontendQualityEngineId.Lighthouse => LighthouseEnabled,
        FrontendQualityEngineId.PassiveSecurity => PassiveSecurityEnabled,
        _ => false,
    };
}
