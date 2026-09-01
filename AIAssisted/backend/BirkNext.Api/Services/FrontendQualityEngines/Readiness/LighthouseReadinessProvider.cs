using BirkNext.Api.Services.FrontendLighthouse;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class LighthouseReadinessProvider : IFrontendQualityEngineReadinessProvider
{
    private readonly IFrontendLighthouseReviewService _service;
    private readonly ILogger<LighthouseReadinessProvider> _logger;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.Lighthouse;

    public LighthouseReadinessProvider(
        IFrontendLighthouseReviewService service,
        ILogger<LighthouseReadinessProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var result = await _service.CheckReadinessAsync(linked.Token);

            if (!result.Available)
                _logger.LogWarning("Lighthouse readiness unavailable ({State}): {Diagnostic}", result.State, result.Error);

            return new(
                EngineId,
                result.Available,
                SafeReason(result.State, result.Available),
                DateTime.UtcNow,
                MapReason(result.State, result.Available));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Lighthouse readiness check timed out");
            return new(EngineId, false, "Lighthouse readiness check timed out.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.CheckTimedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lighthouse readiness check failed");
            return new(EngineId, false, "Lighthouse readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }

    private static FrontendQualityEngineReadinessReason MapReason(LighthouseReadinessState state, bool available) => (state, available) switch
    {
        (LighthouseReadinessState.Ready, true) => FrontendQualityEngineReadinessReason.None,
        (LighthouseReadinessState.Disabled, _) => FrontendQualityEngineReadinessReason.DisabledInSystemSettings,
        (LighthouseReadinessState.NodeUnavailable, _) => FrontendQualityEngineReadinessReason.ExecutableUnavailable,
        (LighthouseReadinessState.LighthouseUnavailable, _) => FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable,
        (LighthouseReadinessState.ChromiumUnavailable, _) => FrontendQualityEngineReadinessReason.ExecutableUnavailable,
        _ => FrontendQualityEngineReadinessReason.EngineUnavailable
    };

    private static string? SafeReason(LighthouseReadinessState state, bool available) => (state, available) switch
    {
        (LighthouseReadinessState.Ready, true) => null,
        (LighthouseReadinessState.Disabled, _) => "Lighthouse is disabled in System Settings.",
        (LighthouseReadinessState.NodeUnavailable, _) => "Node.js runtime is unavailable.",
        (LighthouseReadinessState.LighthouseUnavailable, _) => "Lighthouse runner is unavailable.",
        (LighthouseReadinessState.ChromiumUnavailable, _) => "Chromium runtime is unavailable.",
        (LighthouseReadinessState.ConfigurationInvalid, _) => "Lighthouse readiness response was invalid.",
        _ => "Lighthouse runtime could not be started."
    };
}
