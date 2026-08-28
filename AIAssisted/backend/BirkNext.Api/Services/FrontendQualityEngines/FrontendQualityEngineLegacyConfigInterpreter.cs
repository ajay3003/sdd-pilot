using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed class FrontendQualityEngineLegacyConfigInterpreter
{
    private readonly ILogger<FrontendQualityEngineLegacyConfigInterpreter> _logger;
    private readonly IConfiguration _config;
    private readonly IOptions<FrontendBrowserRuntimeOptions> _browserRuntimeOptions;
    private readonly IOptions<FrontendAccessibilityOptions> _accessibilityOptions;
    private readonly IOptions<FrontendLighthouseOptions> _lighthouseOptions;

    public FrontendQualityEngineLegacyConfigInterpreter(
        ILogger<FrontendQualityEngineLegacyConfigInterpreter> logger,
        IConfiguration config,
        IOptions<FrontendBrowserRuntimeOptions> browserRuntimeOptions,
        IOptions<FrontendAccessibilityOptions> accessibilityOptions,
        IOptions<FrontendLighthouseOptions> lighthouseOptions)
    {
        _logger = logger;
        _config = config;
        _browserRuntimeOptions = browserRuntimeOptions;
        _accessibilityOptions = accessibilityOptions;
        _lighthouseOptions = lighthouseOptions;
    }

    public (bool Allowed, bool Enabled) ResolveLayer1And2(FrontendQualityEngineId engineId)
    {
        var (layer1Suffix, legacySection) = GetConfigSectionNames(engineId);

        var layer1Key = $"FrontendQualityCapabilities:{layer1Suffix}";
        var layer2Key = $"FrontendQualityEnginePreferences:{GetLayer2Suffix(engineId)}";

        var layer1ValueStr = _config.GetValue<string>(layer1Key);
        var layer2ValueStr = _config.GetValue<string>(layer2Key);
        var hasNewLayer1 = layer1ValueStr != null;
        var hasNewLayer2 = layer2ValueStr != null;
        var hasLegacy = _config.GetSection(legacySection).Exists();

        var allowed = false;
        var enabled = false;

        if (hasNewLayer1)
        {
            allowed = _config.GetValue(layer1Key, false);
        }

        if (hasNewLayer2)
        {
            enabled = _config.GetValue(layer2Key, false);
        }
        else if (hasLegacy && !hasNewLayer2)
        {
            enabled = GetLegacyEnabledValue(engineId);
        }

        if (hasNewLayer1 || hasNewLayer2)
        {
            _logger.LogInformation(
                "Engine {EngineId} Layer1/2: explicit config found. Allowed={Allowed}, Enabled={Enabled}",
                engineId, allowed, enabled);
            return (allowed, enabled);
        }

        if (hasLegacy)
        {
            _logger.LogInformation(
                "Engine {EngineId} Layer1/2: using legacy fallback. Allowed={Allowed}, Enabled={Enabled}",
                engineId, allowed, enabled);
            return (allowed, enabled);
        }

        _logger.LogInformation(
            "Engine {EngineId} Layer1/2: no config, using defaults",
            engineId);
        return (false, false);
    }

    private static string GetLayer2Suffix(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => "BrowserRuntimeEnabled",
        FrontendQualityEngineId.Accessibility => "AccessibilityEnabled",
        FrontendQualityEngineId.Lighthouse => "LighthouseEnabled",
        FrontendQualityEngineId.PassiveSecurity => "PassiveSecurityEnabled",
        _ => string.Empty,
    };

    private bool GetLegacyEnabledValue(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => _config.GetValue<bool>("FrontendBrowserRuntime:Enabled"),
        FrontendQualityEngineId.Accessibility => _config.GetValue<bool>("FrontendAccessibility:Enabled"),
        FrontendQualityEngineId.Lighthouse => _config.GetValue<bool>("FrontendLighthouse:Enabled"),
        FrontendQualityEngineId.PassiveSecurity => _config.GetValue<bool>("FrontendPassiveSecurity:Enabled"),
        _ => false,
    };

    private static (string newLayerKey, string legacySection) GetConfigSectionNames(FrontendQualityEngineId engineId) => engineId switch
    {
        FrontendQualityEngineId.BrowserRuntime => ("BrowserRuntimeAllowed", FrontendBrowserRuntimeOptions.SectionName),
        FrontendQualityEngineId.Accessibility => ("AccessibilityAllowed", FrontendAccessibilityOptions.SectionName),
        FrontendQualityEngineId.Lighthouse => ("LighthouseAllowed", FrontendLighthouseOptions.SectionName),
        FrontendQualityEngineId.PassiveSecurity => ("PassiveSecurityAllowed", "FrontendPassiveSecurity"),
        _ => (string.Empty, string.Empty),
    };
}
