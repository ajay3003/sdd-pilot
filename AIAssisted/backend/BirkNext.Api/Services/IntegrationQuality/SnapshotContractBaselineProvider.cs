using BirkNext.Api.Services.ContractAnalysis;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 5: resolves the previous contract for drift from persisted snapshot
/// history.
///
/// Lookup is by environment plus baseline key only. Display name and IntegrationId are never
/// used, and a lookup never crosses environments, so a Dev baseline cannot leak into QA.
/// </summary>
public sealed class SnapshotContractBaselineProvider : IContractBaselineProvider
{
    private readonly IIntegrationQualitySnapshotRepository _repository;
    private readonly ILogger<SnapshotContractBaselineProvider> _logger;

    public SnapshotContractBaselineProvider(
        IIntegrationQualitySnapshotRepository repository,
        ILogger<SnapshotContractBaselineProvider> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// The environment and baseline key for the current resolution. Set per review by the
    /// orchestrating service, because IContractBaselineProvider is addressed per integration.
    /// </summary>
    public string? EnvironmentId { get; set; }

    public async Task<ContractBaseline?> GetBaselineAsync(
        string integrationId,
        string contractName,
        CancellationToken ct = default)
    {
        // integrationId is not a usable history key; callers supply the baseline key instead.
        return await GetBaselineByKeyAsync(EnvironmentId, integrationId, ct);
    }

    /// <summary>
    /// Resolves the previous normalized contract for one integration in one environment.
    /// Returns null when there is no history, when the stored entry predates contract capture,
    /// or when the stored baseline identity version no longer matches the current algorithm.
    /// </summary>
    public async Task<ContractBaseline?> GetBaselineByKeyAsync(
        string? environmentId,
        string baselineKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(baselineKey))
            return null;

        var entry = await _repository.GetLatestForIntegrationAsync(environmentId, baselineKey, ct);

        if (entry?.NormalizedContract is null)
            return null;

        var snapshot = await _repository.GetLatestAsync(environmentId, ct);

        return new ContractBaseline
        {
            Contract = entry.NormalizedContract,
            CapturedAt = snapshot?.CapturedAt.UtcDateTime
        };
    }
}
