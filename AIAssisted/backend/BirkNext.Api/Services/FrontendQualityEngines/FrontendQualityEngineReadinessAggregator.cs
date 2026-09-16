namespace BirkNext.Api.Services.FrontendQualityEngines;

public interface IFrontendQualityEngineReadinessAggregator
{
    Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAllAsync(CancellationToken ct);

    /// <summary>
    /// Probes readiness for the given engines only (in parallel). Engines not listed are not contacted at all and are reported
    /// as <see cref="FrontendQualityEngineReadinessReason.NotChecked"/>, so inactive engines never delay a review.
    /// </summary>
    Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAsync(IReadOnlyCollection<FrontendQualityEngineId> engines, CancellationToken ct);

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

    public Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAllAsync(CancellationToken ct) =>
        CheckAsync(_providers.Keys.ToList(), ct);

    public async Task<IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineReadiness>> CheckAsync(
        IReadOnlyCollection<FrontendQualityEngineId> engines, CancellationToken ct)
    {
        var requested = engines.ToHashSet();
        var probed = await Task.WhenAll(_providers.Values.Where(p => requested.Contains(p.EngineId)).Select(p => CheckSafeAsync(p, ct)));
        var results = probed.ToDictionary(r => r.EngineId);
        foreach (var id in _providers.Keys.Where(id => !results.ContainsKey(id)))
            results[id] = new(id, false, "Readiness not checked: the engine is not active for this review.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.NotChecked);
        return results;
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
