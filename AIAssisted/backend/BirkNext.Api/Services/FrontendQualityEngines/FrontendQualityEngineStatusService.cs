namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed class FrontendQualityEngineStatusService : IFrontendQualityEngineStatusService
{
    private readonly IFrontendQualityEngineReadinessAggregator _readinessAggregator;
    private readonly FrontendQualityEngineLegacyConfigInterpreter _legacyInterpreter;
    private readonly ILogger<FrontendQualityEngineStatusService> _logger;

    private static readonly FrontendQualityEngineId[] AllEngines = [
        FrontendQualityEngineId.BrowserRuntime,
        FrontendQualityEngineId.Accessibility,
        FrontendQualityEngineId.Lighthouse,
        FrontendQualityEngineId.PassiveSecurity,
    ];

    private static readonly Dictionary<FrontendQualityEngineId, string> DisplayNames = new()
    {
        { FrontendQualityEngineId.BrowserRuntime, "Browser Runtime" },
        { FrontendQualityEngineId.Accessibility, "Accessibility" },
        { FrontendQualityEngineId.Lighthouse, "Lighthouse" },
        { FrontendQualityEngineId.PassiveSecurity, "Passive Security" },
    };

    public FrontendQualityEngineStatusService(
        IFrontendQualityEngineReadinessAggregator readinessAggregator,
        FrontendQualityEngineLegacyConfigInterpreter legacyInterpreter,
        ILogger<FrontendQualityEngineStatusService> logger)
    {
        _readinessAggregator = readinessAggregator;
        _legacyInterpreter = legacyInterpreter;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineStatusReport> GetStatusAsync(FrontendQualityEngineStatusQuery? query = null, CancellationToken ct = default)
    {
        query ??= new();
        var readinessResults = await _readinessAggregator.CheckAllAsync(ct);

        var statuses = AllEngines.Select(engineId => ComputeEngineStatus(
            engineId,
            query,
            readinessResults[engineId])).ToList();

        return new(statuses.AsReadOnly(), DateTime.UtcNow);
    }

    public FrontendQualityEngineCapabilitySnapshot CaptureSnapshot(ReviewAuthenticationMode authMode)
    {
        var allowed = new Dictionary<FrontendQualityEngineId, bool>();
        var enabled = new Dictionary<FrontendQualityEngineId, bool>();
        var authSupported = new Dictionary<FrontendQualityEngineId, bool>();

        foreach (var engineId in AllEngines)
        {
            var (layer1, layer2) = _legacyInterpreter.ResolveLayer1And2(engineId);
            allowed[engineId] = layer1;
            enabled[engineId] = layer2;
            authSupported[engineId] = FrontendQualityEngineAuthenticationSupport.Supports(engineId, authMode);
        }

        return new(
            allowed.AsReadOnly(),
            enabled.AsReadOnly(),
            authMode,
            authSupported.AsReadOnly(),
            DateTime.UtcNow);
    }

    private FrontendQualityEngineStatus ComputeEngineStatus(
        FrontendQualityEngineId engineId,
        FrontendQualityEngineStatusQuery query,
        FrontendQualityEngineReadiness readiness)
    {
        var reasons = new List<FrontendQualityEngineUnavailableReason>();
        var (layer1Allowed, layer2Enabled) = _legacyInterpreter.ResolveLayer1And2(engineId);
        var authSupported = FrontendQualityEngineAuthenticationSupport.Supports(engineId, query.AuthMode);
        var layer3Available = readiness.IsAvailable;

        if (!layer1Allowed)
            reasons.Add(FrontendQualityEngineUnavailableReason.BlockedByDeploymentPolicy);

        if (!layer2Enabled)
            reasons.Add(FrontendQualityEngineUnavailableReason.DisabledInSystemSettings);

        if (!layer3Available)
        {
            if (readiness.StatusReason?.Contains("check timed out") == true ||
                readiness.StatusReason?.Contains("Runtime status unknown") == true)
                reasons.Add(FrontendQualityEngineUnavailableReason.RuntimeStatusUnknown);
            else
                reasons.Add(FrontendQualityEngineUnavailableReason.RuntimeUnavailable);
        }

        if (!authSupported)
            reasons.Add(FrontendQualityEngineUnavailableReason.AuthenticationModeUnsupported);

        var available = layer1Allowed && layer2Enabled && layer3Available && authSupported;

        bool? selected = null;
        if (query.Selection?.Selected.TryGetValue(engineId, out var selectedValue) == true)
            selected = selectedValue;

        var eligibleToExecute = available && (selected ?? true);

        if (reasons.Count == 0)
            reasons.Add(FrontendQualityEngineUnavailableReason.None);

        return new(
            engineId,
            DisplayNames[engineId],
            layer1Allowed,
            layer2Enabled,
            readiness,
            true,
            selected,
            authSupported,
            available,
            eligibleToExecute,
            reasons.AsReadOnly());
    }
}
