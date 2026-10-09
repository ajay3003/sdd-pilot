using System.Collections.Concurrent;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// Process-wide run control: one in-flight run per environment + integration (lease), the cancellation handle of each running run, and the
/// background task that executes it. A run left Running by a stopped process is not in here and is recovered as interrupted.
/// </summary>
public sealed class ActiveEventRunCoordinator
{
    private readonly ConcurrentDictionary<string, Guid> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancel, Task Task)> _running = new();

    public static string Key(string environmentId, string integrationId) => $"{environmentId}\n{integrationId}";

    public bool TryAcquire(string key, Guid runId) => _leases.TryAdd(key, runId);
    public void Release(string key, Guid runId) => _leases.TryRemove(new KeyValuePair<string, Guid>(key, runId));
    public Guid? LeaseHolder(string key) => _leases.TryGetValue(key, out var id) ? id : null;
    public bool IsRunning(Guid runId) => _running.ContainsKey(runId);

    /// <summary>True while any active event run is executing in this process.</summary>
    public bool HasRunning => !_running.IsEmpty;

    public void Launch(Guid runId, string key, Func<CancellationToken, Task> execute)
    {
        var cancel = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await gate.Task;
            try { await execute(cancel.Token); }
            finally
            {
                _running.TryRemove(runId, out _);
                Release(key, runId);
                cancel.Dispose();
            }
        });
        _running[runId] = (cancel, task);
        gate.SetResult();
    }

    public bool Cancel(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var run)) return false;
        try { run.Cancel.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>Tests and graceful shutdown: the background task of a running run, or a completed task.</summary>
    public Task Completion(Guid runId) => _running.TryGetValue(runId, out var run) ? run.Task : Task.CompletedTask;
}
