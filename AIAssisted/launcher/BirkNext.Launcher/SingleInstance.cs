namespace BirkNext.Launcher;

/// <summary>
/// One BirkNext launcher per data folder and Windows user: a per-user named mutex keyed by the data folder's full path, held while BirkNext runs.
/// Shared with Update-BirkNext.exe (linked source), which uses the same key to know whether the BirkNext of a data folder is running. An open
/// port or a session file proves nothing on its own; this lock is the instance identity.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>The mutex name for a data folder (case-insensitive full path, so the same folder written differently is the same instance).</summary>
    public static string NameFor(string dataRoot) =>
        $@"Local\BirkNext.Launcher.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24]}";

    public static SingleInstance? TryAcquire(string dataRoot)
    {
        var mutex = new Mutex(false, NameFor(dataRoot));
        try { if (mutex.WaitOne(0)) return new SingleInstance(mutex); }
        catch (AbandonedMutexException) { return new SingleInstance(mutex); } // A crashed launcher: the lock is ours now.
        mutex.Dispose();
        return null;
    }

    /// <summary>Whether a launcher holds the data folder right now (probes the lock and releases it at once).</summary>
    public static bool IsHeld(string dataRoot)
    {
        using var probe = TryAcquire(dataRoot);
        return probe is null;
    }

    public void Dispose() { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
}
