namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Supplies the previously recorded contract for an integration so drift can be computed.
/// Checkpoint 4 defines the abstraction only; baseline persistence is owned by Checkpoint 5.
/// </summary>
public interface IContractBaselineProvider
{
    Task<ContractBaseline?> GetBaselineAsync(
        string integrationId,
        string contractName,
        CancellationToken ct = default);
}

public sealed class ContractBaseline
{
    public required NormalizedContract Contract { get; init; }
    public DateTime? CapturedAt { get; init; }
}

/// <summary>
/// Default provider used until baseline persistence exists. Always reports "no baseline", which
/// surfaces as DriftState.BaselineUnavailable rather than a fabricated history.
/// </summary>
public sealed class NoBaselineProvider : IContractBaselineProvider
{
    public Task<ContractBaseline?> GetBaselineAsync(
        string integrationId,
        string contractName,
        CancellationToken ct = default) => Task.FromResult<ContractBaseline?>(null);
}
