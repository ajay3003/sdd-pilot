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
            _logger.LogWarning("Lighthouse readiness check timed out");
            return new(EngineId, false, "Runtime status unknown: check timed out", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lighthouse readiness check failed");
            return new(EngineId, false, $"Runtime status unknown: {ex.Message}", DateTime.UtcNow);
        }
    }
}
