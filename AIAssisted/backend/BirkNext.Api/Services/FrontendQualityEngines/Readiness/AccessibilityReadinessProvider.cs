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

            return new(
                EngineId,
                result.Available,
                result.Error,
                DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Accessibility readiness check timed out");
            return new(EngineId, false, "Runtime status unknown: check timed out", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Accessibility readiness check failed");
            return new(EngineId, false, $"Runtime status unknown: {ex.Message}", DateTime.UtcNow);
        }
    }
}
