namespace BirkNext.Api.Services.FrontendQualityEngines;

public interface IFrontendQualityEngineReadinessAggregator
{
    Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAllAsync(CancellationToken ct);
    Task<FrontendQualityEngineReadiness> RevalidateAsync(FrontendQualityEngineId id, CancellationToken ct);
}

public sealed class FrontendQualityEngineReadinessAggregator : IFrontendQualityEngineReadinessAggregator
{
    private readonly IReadOnlyDictionary<FrontendQualityEngineId, IFrontendQualityEngineReadinessProvider> _providers;
    private readonly ILogger<FrontendQualityEngineReadinessAggregator> _logger;

    public FrontendQualityEngineReadinessAggregator(
        IEnumerable<IFrontendQualityEngineReadinessProvider> providers,
        ILogger<FrontendQualityEngineReadinessAggregator> logger)
    {
        _providers = providers.ToDictionary(p => p.EngineId);
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAllAsync(CancellationToken ct)
    {
        var tasks = _providers.Values.Select(p => CheckSafeAsync(p, ct));
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.EngineId);
    }

    public async Task<FrontendQualityEngineReadiness> RevalidateAsync(FrontendQualityEngineId id, CancellationToken ct)
    {
        if (!_providers.TryGetValue(id, out var provider))
        {
            _logger.LogWarning("No readiness provider found for engine {EngineId}", id);
            return new(id, false, "Engine provider not found", DateTime.UtcNow);
        }

        return await CheckSafeAsync(provider, ct);
    }

    private async Task<FrontendQualityEngineReadiness> CheckSafeAsync(
        IFrontendQualityEngineReadinessProvider provider,
        CancellationToken ct)
    {
        try
        {
            return await provider.CheckAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frontend quality readiness provider failed for {EngineId}", provider.EngineId);
            return new(provider.EngineId, false, "Engine readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }
}
