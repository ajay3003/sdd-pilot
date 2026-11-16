using BirkNext.Api.Services.FrontendPassiveSecurity;

namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

public sealed class PassiveSecurityReadinessProvider : IFrontendQualityEngineReadinessProvider, IFreshReadinessProvider
{
    private readonly IFrontendZapPassiveReviewService _service;
    private readonly ILogger<PassiveSecurityReadinessProvider> _logger;
    private readonly EngineReadinessReuse? _reuse;
    private readonly IConfiguration? _configuration;

    public FrontendQualityEngineId EngineId => FrontendQualityEngineId.PassiveSecurity;

    /// <summary>
    /// Readiness includes a real <c>zap.sh -version</c> container start (JVM), after the runtime and image checks (15 s + 15 s + 30 s
    /// budgets in the service plus bounded probe cleanup). The previous 5 s budget timed out before the JVM started, so a working
    /// installation reported "status unknown". Bounded, never infinite.
    /// </summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);

    /// <param name="reuse">R13: when registered, status displays reuse a recent probe result for the same configuration (see
    /// <see cref="EngineReadinessReuse"/>); without it every call probes, as before.</param>
    public PassiveSecurityReadinessProvider(
        IFrontendZapPassiveReviewService service,
        ILogger<PassiveSecurityReadinessProvider> logger,
        EngineReadinessReuse? reuse = null,
        IConfiguration? configuration = null)
    {
        _service = service;
        _logger = logger;
        _reuse = reuse;
        _configuration = configuration;
    }

    public Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct) => Check(fresh: false, ct);

    /// <summary>"Check again": always probes (and refreshes the reused result).</summary>
    public Task<FrontendQualityEngineReadiness> CheckFreshAsync(CancellationToken ct) => Check(fresh: true, ct);

    private Task<FrontendQualityEngineReadiness> Check(bool fresh, CancellationToken ct) =>
        _reuse is null || _configuration is null
            ? ProbeAsync(ct)
            // The shared probe is bounded by ReadinessTimeout, not by one caller's request: leaving the page cancels only that caller's wait.
            : _reuse.GetAsync(ConfigurationKey(_configuration), () => ProbeAsync(CancellationToken.None), fresh, ct);

    /// <summary>Everything the probe's answer depends on besides the machine itself: a change in any of it is a different key.</summary>
    internal static string ConfigurationKey(IConfiguration configuration) => string.Join("|",
        "passive-security",
        FrontendQualityEngineEnablement.Resolve(configuration, FrontendQualityEngineId.PassiveSecurity, configuration.GetValue<bool>("FrontendPassiveSecurity:Enabled")),
        configuration["FrontendQualityCapabilities:PassiveSecurityAllowed"] ?? "",
        configuration.GetValue<string>("FrontendPassiveSecurity:ContainerRuntime") ?? "podman",
        configuration.GetValue<string>("FrontendPassiveSecurity:ContainerNetwork") ?? "",
        FrontendZapPassiveReviewService.Image);

    private async Task<FrontendQualityEngineReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadinessTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var result = await _service.CheckReadinessAsync(linked.Token);

            if (!result.Available)
                _logger.LogWarning("Passive Web Security readiness unavailable ({State}): {Diagnostic}", result.State, result.Error);

            return new(
                EngineId,
                result.Available,
                SafeReason(result.State) ?? (result.Limitations is { Count: > 0 } ? string.Join(" ", result.Limitations) : null),
                DateTime.UtcNow,
                MapReason(result.State));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Passive Web Security readiness check timed out");
            return new(EngineId, false, "Passive Web Security readiness check timed out.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.CheckTimedOut);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passive Web Security readiness check failed");
            return new(EngineId, false, "Passive Web Security readiness check failed.", DateTime.UtcNow,
                FrontendQualityEngineReadinessReason.ProviderError);
        }
    }

    private static FrontendQualityEngineReadinessReason MapReason(PassiveSecurityReadinessState state) => state switch
    {
        PassiveSecurityReadinessState.Ready => FrontendQualityEngineReadinessReason.None,
        PassiveSecurityReadinessState.InstallationPolicyBlocked => FrontendQualityEngineReadinessReason.EngineUnavailable,
        PassiveSecurityReadinessState.Disabled => FrontendQualityEngineReadinessReason.DisabledInSystemSettings,
        PassiveSecurityReadinessState.ContainerRuntimeUnavailable => FrontendQualityEngineReadinessReason.ContainerRuntimeUnavailable,
        PassiveSecurityReadinessState.ZapImageUnavailable => FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable,
        _ => FrontendQualityEngineReadinessReason.EngineUnavailable
    };

    private static string? SafeReason(PassiveSecurityReadinessState state) => state switch
    {
        PassiveSecurityReadinessState.Ready => null,
        PassiveSecurityReadinessState.InstallationPolicyBlocked => "Installation policy does not allow container security engines.",
        PassiveSecurityReadinessState.Disabled => "Passive Web Security is disabled in System Settings.",
        PassiveSecurityReadinessState.ContainerRuntimeUnavailable => "Container runtime is unavailable.",
        PassiveSecurityReadinessState.ZapImageUnavailable => "Passive Web Security runtime image is unavailable.",
        PassiveSecurityReadinessState.ZapLaunchFailed => "Passive Web Security runtime could not be started.",
        _ => "Passive Web Security runtime is unavailable."
    };
}
