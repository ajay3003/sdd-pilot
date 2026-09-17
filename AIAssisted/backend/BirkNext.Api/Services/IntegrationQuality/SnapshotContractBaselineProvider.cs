using BirkNext.Api.Services.ContractAnalysis;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 5: resolves the previous contract for drift from a snapshot that was
/// already loaded before the current review began.
///
/// The provider is deliberately scoped to one already-resolved snapshot rather than querying the
/// repository per integration. Because it can only ever see history that existed before the
/// current run, a review physically cannot compare itself against its own snapshot no matter how
/// the save is later ordered.
///
/// Lookup is by baseline key within that snapshot's environment. Display name and IntegrationId
/// are never used, and a snapshot belongs to exactly one environment, so a Dev baseline cannot
/// resolve for QA.
/// </summary>
public sealed class SnapshotScopedBaselineProvider : IContractBaselineProvider
{
    private readonly IntegrationQualitySnapshot? _previous;
    private readonly Dictionary<string, IntegrationSnapshotEntry> _byBaselineKey;

    public SnapshotScopedBaselineProvider(IntegrationQualitySnapshot? previousSnapshot)
    {
        _previous = previousSnapshot;

        _byBaselineKey = previousSnapshot is null
            ? new Dictionary<string, IntegrationSnapshotEntry>(StringComparer.Ordinal)
            : previousSnapshot.Integrations
                .GroupBy(e => e.BaselineKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IntegrationQualitySnapshot? PreviousSnapshot => _previous;

    public bool HasBaseline => _previous is not null;

    /// <summary>
    /// Resolves the previous normalized contract for one integration. The key argument is a
    /// baseline key, not an IntegrationId; IntegrationId is not a stable history key.
    /// </summary>
    public Task<ContractBaseline?> GetBaselineAsync(
        string baselineKey,
        string contractName,
        CancellationToken ct = default)
    {
        if (_previous is null || string.IsNullOrWhiteSpace(baselineKey))
            return Task.FromResult<ContractBaseline?>(null);

        if (!_byBaselineKey.TryGetValue(baselineKey, out var entry) || entry.NormalizedContract is null)
            return Task.FromResult<ContractBaseline?>(null);

        return Task.FromResult<ContractBaseline?>(new ContractBaseline
        {
            Contract = entry.NormalizedContract,
            CapturedAt = _previous.CapturedAt.UtcDateTime
        });
    }

    public IntegrationSnapshotEntry? FindEntry(string baselineKey) =>
        _byBaselineKey.TryGetValue(baselineKey, out var entry) ? entry : null;
}
