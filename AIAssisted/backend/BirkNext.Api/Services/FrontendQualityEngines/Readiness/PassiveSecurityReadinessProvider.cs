using BirkNext.Api.Services.FrontendPassiveSecurity;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class PassiveSecurityReadinessProvider : IFrontendQualityEngineReadinessProvider
{
    private readonly IFrontendZapPassiveReviewService _service;
    private readonly ILogger<PassiveSecurityReadinessProvider> _logger;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.PassiveSecurity;

    /// <summary>
    /// Readiness includes a real <c>zap.sh -version</c> container start (JVM), after the runtime and image checks (15 s + 15 s + 30 s
    /// budgets in the service). The previous 5 s budget timed out before the JVM started, so a working installation reported
    /// "status unknown". Bounded, never infinite.
    /// </summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(75);

    public PassiveSecurityReadinessProvider(
        IFrontendZapPassiveReviewService service,
        ILogger<PassiveSecurityReadinessProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadinessTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var result = await _service.CheckReadinessAsync(linked.Token);

            if (!result.Available)
                _logger.LogWarning("Passive Security readiness unavailable ({State}): {Diagnostic}", result.State, result.Error);

            return new(
                EngineId,
                result.Available,
                SafeReason(result.State),
                DateTime.UtcNow,
                MapReason(result.State));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Passive Security readiness check timed out");
            return new(EngineId, false, "Passive Security readiness check timed out.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.CheckTimedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passive Security readiness check failed");
            return new(EngineId, false, "Passive Security readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }

    private static FrontendQualityEngineReadinessReason MapReason(PassiveSecurityReadinessState state) => state switch
    {
        PassiveSecurityReadinessState.Ready => FrontendQualityEngineReadinessReason.None,
        PassiveSecurityReadinessState.Disabled => FrontendQualityEngineReadinessReason.DisabledInSystemSettings,
        PassiveSecurityReadinessState.DockerUnavailable => FrontendQualityEngineReadinessReason.ContainerRuntimeUnavailable,
        PassiveSecurityReadinessState.ZapImageUnavailable => FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable,
        _ => FrontendQualityEngineReadinessReason.EngineUnavailable
    };

    private static string? SafeReason(PassiveSecurityReadinessState state) => state switch
    {
        PassiveSecurityReadinessState.Ready => null,
        PassiveSecurityReadinessState.Disabled => "Passive Security is disabled in System Settings.",
        PassiveSecurityReadinessState.DockerUnavailable => "Container runtime is unavailable.",
        PassiveSecurityReadinessState.ZapImageUnavailable => "Passive Security runtime image is unavailable.",
        PassiveSecurityReadinessState.ZapLaunchFailed => "Passive Security runtime could not be started.",
        _ => "Passive Security runtime is unavailable."
    };
}
