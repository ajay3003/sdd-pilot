using BirkNext.Api.Services.FrontendLighthouse;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class LighthouseReadinessProvider : IFrontendQualityEngineReadinessProvider
{
    private readonly IFrontendLighthouseReviewService _service;
    private readonly ILogger<LighthouseReadinessProvider> _logger;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.Lighthouse;

    public LighthouseReadinessProvider(
        IFrontendLighthouseReviewService service,
        ILogger<LighthouseReadinessProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var result = await _service.CheckReadinessAsync(linked.Token);

            if (!result.Available)
                _logger.LogWarning("Lighthouse readiness unavailable ({State}): {Diagnostic}", result.State, result.Error);

            return new(
                EngineId,
                result.Available,
                SafeReason(result.State, result.Available),
                DateTime.UtcNow,
                MapReason(result.State, result.Available),
                Components(result));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Lighthouse readiness check timed out");
            return new(EngineId, false, "Lighthouse readiness check timed out.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.CheckTimedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lighthouse readiness check failed");
            return new(EngineId, false, "Lighthouse readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }

    public const string NodeComponent = "NodeRuntime";
    public const string CdpComponent = "CdpTarget";
    public const string BundledChromium = "Bundled Chromium (headless, launched by the Lighthouse runner)";

    /// <summary>
    /// Node and the Chromium/CDP target, apart. NodeUnavailable means the runner never started, so the browser target is undetermined;
    /// a runner that started but could not launch its browser proves Node and disproves the target.
    /// </summary>
    public static IReadOnlyList<FrontendQualityRuntimeComponent>? Components(LighthouseReadinessResult result) => result.State switch
    {
        LighthouseReadinessState.Ready when result.Available =>
        [
            new(NodeComponent, true, "Node.js", result.NodeVersion is { Length: > 0 } node ? $"Node.js {node}" : null),
            new(CdpComponent, true, BundledChromium, string.Join(" ", new[] { result.BrowserName, result.BrowserVersion }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } browser ? browser : null),
        ],
        LighthouseReadinessState.NodeUnavailable => [new(NodeComponent, false, "Node.js"), new(CdpComponent, null, BundledChromium)],
        LighthouseReadinessState.LaunchFailed or LighthouseReadinessState.ChromiumUnavailable =>
            [new(NodeComponent, true, "Node.js"), new(CdpComponent, false, BundledChromium, "The bundled Chromium could not be started.")],
        _ => null,
    };

    private static FrontendQualityEngineReadinessReason MapReason(LighthouseReadinessState state, bool available) => (state, available) switch
    {
        (LighthouseReadinessState.Ready, true) => FrontendQualityEngineReadinessReason.None,
        (LighthouseReadinessState.Disabled, _) => FrontendQualityEngineReadinessReason.DisabledInSystemSettings,
        (LighthouseReadinessState.NodeUnavailable, _) => FrontendQualityEngineReadinessReason.ExecutableUnavailable,
        (LighthouseReadinessState.LighthouseUnavailable, _) => FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable,
        (LighthouseReadinessState.ChromiumUnavailable, _) => FrontendQualityEngineReadinessReason.ExecutableUnavailable,
        _ => FrontendQualityEngineReadinessReason.EngineUnavailable
    };

    private static string? SafeReason(LighthouseReadinessState state, bool available) => (state, available) switch
    {
        (LighthouseReadinessState.Ready, true) => null,
        (LighthouseReadinessState.Disabled, _) => "Lighthouse is disabled in System Settings.",
        (LighthouseReadinessState.NodeUnavailable, _) => "Node.js runtime is unavailable.",
        (LighthouseReadinessState.LighthouseUnavailable, _) => "Lighthouse runner is unavailable.",
        (LighthouseReadinessState.ChromiumUnavailable, _) => "Chromium runtime is unavailable.",
        (LighthouseReadinessState.ConfigurationInvalid, _) => "Lighthouse readiness response was invalid.",
        _ => "Lighthouse runtime could not be started."
    };
}
