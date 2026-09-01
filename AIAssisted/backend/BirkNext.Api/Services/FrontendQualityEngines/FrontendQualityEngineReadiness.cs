namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed record FrontendQualityEngineReadiness(
    FrontendQualityEngineId EngineId,
    bool IsAvailable,
    string? StatusReason,
    DateTime CheckedAtUtc,
    FrontendQualityEngineReadinessReason Reason = FrontendQualityEngineReadinessReason.None);

public enum FrontendQualityEngineReadinessReason
{
    None,
    DisabledInSystemSettings,
    RuntimePrerequisiteUnavailable,
    ExecutableUnavailable,
    ContainerRuntimeUnavailable,
    EngineUnavailable,
    CheckTimedOut,
    ProviderError
}

public interface IFrontendQualityEngineReadinessProvider
{
    FrontendQualityEngineId EngineId { get; }
    Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct);
}
