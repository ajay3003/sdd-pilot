namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed record FrontendQualityEngineReadiness(
    FrontendQualityEngineId EngineId,
    bool IsAvailable,
    string? StatusReason,
    DateTime CheckedAtUtc);

public interface IFrontendQualityEngineReadinessProvider
{
    FrontendQualityEngineId EngineId { get; }
    Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct);
}
