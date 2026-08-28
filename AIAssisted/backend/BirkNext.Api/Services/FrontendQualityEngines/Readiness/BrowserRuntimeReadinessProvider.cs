using BirkNext.Api.Services.FrontendBrowserRuntime;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class BrowserRuntimeReadinessProvider : IFrontendQualityEngineReadinessProvider
{
    private readonly IFrontendBrowserRuntimeReviewService _service;
    private readonly ILogger<BrowserRuntimeReadinessProvider> _logger;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.BrowserRuntime;

    public BrowserRuntimeReadinessProvider(
        IFrontendBrowserRuntimeReviewService service,
        ILogger<BrowserRuntimeReadinessProvider> logger)
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
                result.IsAvailable,
                result.ErrorMessage,
                DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Browser Runtime readiness check timed out");
            return new(EngineId, false, "Runtime status unknown: check timed out", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser Runtime readiness check failed");
            return new(EngineId, false, $"Runtime status unknown: {ex.Message}", DateTime.UtcNow);
        }
    }
}
