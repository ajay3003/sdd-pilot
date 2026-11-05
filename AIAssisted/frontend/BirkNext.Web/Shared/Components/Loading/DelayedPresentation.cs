namespace BirkNext.Web.Shared.Components.Loading;

/// <summary>
/// The shared loading presentation thresholds (one place for the whole application):
/// below <see cref="Inline"/> nothing is visible; from <see cref="Inline"/> a spinner, skeleton or compact progress line; from
/// <see cref="Detail"/> the full step list of a multi-step operation; from <see cref="Slow"/> "Still working — waiting for …".
/// Long-running work that must survive navigation runs as a BackgroundOperationService operation (Activity Center) instead.
/// </summary>
public static class LoadingThresholds
{
    public static TimeSpan Inline { get; set; } = TimeSpan.FromMilliseconds(300);
    public static TimeSpan Detail { get; set; } = TimeSpan.FromSeconds(2);
    public static TimeSpan Slow { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Wall-clock phases of a loading element, measured from when it first rendered: 0 = hidden, 1 = shown, 2 = detail, 3 = slow (as many
/// phases as thresholds given). A CSS animation delay is not used for this: while WebAssembly keeps the main thread busy, the browser's
/// animation clock lags and a 300 ms reveal can fire after far less real time — a fast load could flash. The timer only reveals an element
/// that exists; the caller removes the element when the operation ends, which disposes this and stops it, so nothing can be left stuck.
/// </summary>
public sealed class DelayedPresentation : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public DelayedPresentation(Func<Task> changed, params TimeSpan[] thresholds) => _ = RunAsync(changed, thresholds, _cts.Token);

    public int Phase { get; private set; }

    private async Task RunAsync(Func<Task> changed, TimeSpan[] thresholds, CancellationToken token)
    {
        var elapsed = TimeSpan.Zero;
        foreach (var threshold in thresholds)
        {
            try { await Task.Delay(threshold > elapsed ? threshold - elapsed : TimeSpan.Zero, token); }
            catch (OperationCanceledException) { return; }
            elapsed = threshold;
            if (token.IsCancellationRequested) return;
            Phase++;
            try { await changed(); } catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
