namespace BirkNext.Api.Services.FrontendQualityEngines;

public sealed record FrontendQualityEngineReadiness(
    FrontendQualityEngineId EngineId,
    bool IsAvailable,
    string? StatusReason,
    DateTime CheckedAtUtc,
    FrontendQualityEngineReadinessReason Reason = FrontendQualityEngineReadinessReason.None,
    IReadOnlyList<FrontendQualityRuntimeComponent>? RuntimeComponents = null);

/// <summary>
/// One runtime prerequisite of an engine, reported separately so a consumer can tell WHICH one is missing (Lighthouse: the Node execution
/// runtime and the Chromium/CDP browser target are distinct). <paramref name="Available"/> null = not determined by this check.
/// Kinds: "NodeRuntime", "CdpTarget". Provider and detail are non-secret (product/version), never a path, port or endpoint.
/// </summary>
public sealed record FrontendQualityRuntimeComponent(string Kind, bool? Available, string Provider, string? Detail = null);

public enum FrontendQualityEngineReadinessReason
{
    None,
    DisabledInSystemSettings,
    RuntimePrerequisiteUnavailable,
    ExecutableUnavailable,
    ContainerRuntimeUnavailable,
    EngineUnavailable,
    CheckTimedOut,
    ProviderError,
    /// <summary>Readiness was deliberately not probed because the engine is not active for the requesting review.</summary>
    NotChecked
}

public interface IFrontendQualityEngineReadinessProvider
{
    FrontendQualityEngineId EngineId { get; }
    Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct);
}
