using System.Diagnostics;

namespace BirkNext.Api.Services.LocalHttpsProxy;

public interface IProxyEdgeProcess : IDisposable
{
    int Id { get; }
    DateTimeOffset StartedAt { get; }
    bool Running { get; }
    Task StopAsync();
}

public interface IProxyEdgeLauncher
{
    IProxyEdgeProcess? Launch(string executable, IReadOnlyList<string> arguments);
}

/// <summary>Owns only the process handle returned by this launch. Never searches for or kills other Edge processes.</summary>
public sealed class ProxyEdgeLauncher : IProxyEdgeLauncher
{
    public IProxyEdgeProcess? Launch(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info);
        return process is null ? null : new OwnedProcess(process);
    }

    private sealed class OwnedProcess(Process process) : IProxyEdgeProcess
    {
        public int Id { get; } = process.Id;
        public DateTimeOffset StartedAt { get; } = process.StartTime;
        public bool Running { get { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } } }
        public async Task StopAsync()
        {
            if (!Running) return;
            process.CloseMainWindow();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (Running) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        public void Dispose() => process.Dispose();
    }
}
