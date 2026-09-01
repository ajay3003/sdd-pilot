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

        // TRI-STATE: Distinguish MISSING from EXPLICIT FALSE
        var layer1Present = _config.GetSection(layer1Key).Exists();
        var layer2Present = _config.GetSection(layer2Key).Exists();
        var hasLegacy = _config.GetSection(legacySection).Exists();

        // DIAGNOSTIC: log configuration provider details
        _logger.LogInformation("=== CONFIGURATION STATE FOR {EngineId} ===", engineId);
        _logger.LogInformation("Layer1Key: {Layer1Key}, Exists: {Layer1Present}", layer1Key, layer1Present);
        if (layer1Present)
        {
            var layer1Value = _config.GetValue(layer1Key, false);
            _logger.LogInformation("  Layer1 raw value: {Layer1Value}", layer1Value);
        }
        _logger.LogInformation("Layer2Key: {Layer2Key}, Exists: {Layer2Present}", layer2Key, layer2Present);
        if (layer2Present)
        {
            var layer2Value = _config.GetValue(layer2Key, false);
            _logger.LogInformation("  Layer2 raw value: {Layer2Value}", layer2Value);
        }
        _logger.LogInformation("LegacySection: {LegacySection}, Exists: {HasLegacy}", legacySection, hasLegacy);
        if (hasLegacy)
        {
            var legacyValue = GetLegacyEnabledValue(engineId);
            _logger.LogInformation("  Legacy raw value: {LegacyValue}", legacyValue);
        }

        // Log all configuration providers
        if (_config is IConfigurationRoot root)
        {
            _logger.LogInformation("Configuration providers (in precedence order):");
            var providerCount = 0;
            foreach (var provider in root.Providers)
            {
                providerCount++;
                _logger.LogInformation("  [{Count}] {ProviderType}", providerCount, provider.GetType().Name);
            }
        }

        var legacyEnabled = hasLegacy && GetLegacyEnabledValue(engineId);

        // Layer 1 remains fail-closed once either new layer is configured. A
        // legacy-only installation migrates its old value into both layers.
        var allowed = layer1Present
            ? _config.GetValue(layer1Key, false)
            : !layer2Present && legacyEnabled;

        // Layer 2 has one authority: the System Settings preference when it is
        // present, with the old per-engine flag retained only for migration.
        var enabled = FrontendQualityEngineEnablement.Resolve(
            _config,
            engineId,
            legacyEnabled);

        _logger.LogInformation(
            "Engine {EngineId}: Allowed={Allowed} Enabled={Enabled} (layer1Present={Layer1Present} layer2Present={Layer2Present} hasLegacy={HasLegacy})",
            engineId, allowed, enabled, layer1Present, layer2Present, hasLegacy);

        return (allowed, enabled);
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
