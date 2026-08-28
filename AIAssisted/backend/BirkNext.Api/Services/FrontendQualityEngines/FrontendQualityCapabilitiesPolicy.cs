namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed class FrontendQualityCapabilitiesPolicy
{
    public const string SectionName = "FrontendQualityCapabilities";

    public bool BrowserRuntimeAllowed { get; set; }
    public bool AccessibilityAllowed { get; set; }
    public bool LighthouseAllowed { get; set; }
    public bool PassiveSecurityAllowed { get; set; }

    public bool IsAllowed(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => BrowserRuntimeAllowed,
        FrontendQualityEngineId.Accessibility => AccessibilityAllowed,
        FrontendQualityEngineId.Lighthouse => LighthouseAllowed,
        FrontendQualityEngineId.PassiveSecurity => PassiveSecurityAllowed,
        _ => false,
    };
}
