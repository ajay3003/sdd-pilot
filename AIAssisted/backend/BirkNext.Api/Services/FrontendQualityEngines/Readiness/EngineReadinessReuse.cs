namespace BirkNext.Api.Services.FrontendQualityEngines.Readiness;

/// <summary>
/// A provider whose readiness probe is expensive and can be checked fresh on request ("Check again" — the readiness endpoint), while status
/// displays reuse a recent result (<see cref="EngineReadinessReuse"/>).
/// </summary>
public interface IFreshReadinessProvider
{
    Task<FrontendQualityEngineReadiness> CheckFreshAsync(CancellationToken ct);
}

/// <summary>
/// Roadmap R13 (feature-portfolio audit): Passive Web Security readiness starts a real ZAP container (a JVM printing its version) after the runtime
/// and image checks — about 13 s on every Frontend Quality Review and System Settings status request, warm or cold, measured on the Release build.
/// Status displays reuse the last probe result for its configuration, and concurrent requests share one probe in flight:
/// <list type="bullet">
/// <item>The key is the engine's configuration (enablement, installation policy, container runtime, network, image), so a settings change is never
/// answered from an older result.</item>
/// <item>A ready result is reused for <see cref="ReadyFor"/>, a not-ready one for <see cref="NotReadyFor"/> (a fix shows soon); a timed-out or failed
/// check is never reused.</item>
/// <item>The result keeps its own <see cref="FrontendQualityEngineReadiness.CheckedAtUtc"/>: a reused result says when it was checked.</item>
/// <item>"Check again" probes fresh; a review run never uses this (the engine re-checks its readiness when it runs).</item>
/// <item>The shared probe is bounded by the provider's own timeout and is not cancelled when one caller leaves (navigation away cancels only that
/// caller's wait).</item>
/// </list>
/// </summary>
public sealed class EngineReadinessReuse(TimeProvider clock)
{
    public static readonly TimeSpan ReadyFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NotReadyFor = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, (FrontendQualityEngineReadiness Result, DateTimeOffset At)> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<FrontendQualityEngineReadiness>> _inFlight = new(StringComparer.Ordinal);

    public async Task<FrontendQualityEngineReadiness> GetAsync(string key, Func<Task<FrontendQualityEngineReadiness>> probe, bool fresh, CancellationToken ct)
    {
        Task<FrontendQualityEngineReadiness> task;
        lock (_gate)
        {
            if (!fresh && _last.TryGetValue(key, out var last) && clock.GetUtcNow() - last.At < (last.Result.IsAvailable ? ReadyFor : NotReadyFor))
                return last.Result;
            if (!_inFlight.TryGetValue(key, out task!))
            {
                task = RunAsync(key, probe);
                _inFlight[key] = task;
            }
        }
        return await task.WaitAsync(ct);
    }

    private async Task<FrontendQualityEngineReadiness> RunAsync(string key, Func<Task<FrontendQualityEngineReadiness>> probe)
    {
        await Task.Yield();
        try
        {
            var result = await probe();
            lock (_gate)
            {
                if (result.Reason is FrontendQualityEngineReadinessReason.CheckTimedOut or FrontendQualityEngineReadinessReason.ProviderError) _last.Remove(key);
                else _last[key] = (result, clock.GetUtcNow());
            }
            return result;
        }
        finally
        {
            lock (_gate) _inFlight.Remove(key);
        }
    }
}
