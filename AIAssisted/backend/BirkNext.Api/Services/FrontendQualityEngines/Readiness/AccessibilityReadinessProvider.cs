using BirkNext.Api.Services.FrontendAccessibility;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class AccessibilityReadinessProvider : IFrontendQualityEngineReadinessProvider
{
    private readonly IFrontendAccessibilityReviewService _service;
    private readonly ILogger<AccessibilityReadinessProvider> _logger;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.Accessibility;

    public AccessibilityReadinessProvider(
        IFrontendAccessibilityReviewService service,
        ILogger<AccessibilityReadinessProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var result = await _service.CheckReadinessAsync(linked.Token);

            if (!result.Available)
                _logger.LogWarning("Accessibility readiness unavailable ({State}): {Diagnostic}", result.State, result.Error);

            return new(
                EngineId,
                result.Available,
                SafeReason(result.State),
                DateTime.UtcNow,
                MapReason(result.State));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Accessibility readiness check timed out");
            return new(EngineId, false, "Accessibility readiness check timed out.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.CheckTimedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Accessibility readiness check failed");
            return new(EngineId, false, "Accessibility readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }

    private static FrontendQualityEngineReadinessReason MapReason(AccessibilityReadinessState state) => state switch
    {
        AccessibilityReadinessState.Ready => FrontendQualityEngineReadinessReason.None,
        AccessibilityReadinessState.Disabled => FrontendQualityEngineReadinessReason.DisabledInSystemSettings,
        AccessibilityReadinessState.ChromiumUnavailable => FrontendQualityEngineReadinessReason.ExecutableUnavailable,
        AccessibilityReadinessState.AxeUnavailable => FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable,
        _ => FrontendQualityEngineReadinessReason.EngineUnavailable
    };

    private static string? SafeReason(AccessibilityReadinessState state) => state switch
    {
        AccessibilityReadinessState.Ready => null,
        AccessibilityReadinessState.Disabled => "Accessibility is disabled in System Settings.",
        AccessibilityReadinessState.ChromiumUnavailable => "Chromium runtime is unavailable.",
        AccessibilityReadinessState.AxeUnavailable => "axe-core runtime is unavailable.",
        _ => "Accessibility runtime could not be started."
    };
}
