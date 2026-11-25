using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>
/// Opt-in runtime diagnostics for full-suite stalls (2026-11-24): set <c>BIRKNEXT_TEST_STALL_PROBE</c> to a file path and the test process writes
/// one line per second — GC pause time and gen-2 count, thread-pool threads and queue, how long a queued work item waits before it runs, and how
/// late a dedicated thread wakes up (a whole-process pause shows in both; pool starvation only in the queue wait). Off by default; no effect on
/// normal runs.
/// </summary>
internal static class RuntimeStallProbe
{
    public const string Variable = "BIRKNEXT_TEST_STALL_PROBE";

    [ModuleInitializer]
    internal static void StartWhenRequested()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path) return;
        var thread = new Thread(() => Run(path)) { IsBackground = true, Name = "stall-probe", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private static void Run(string path)
    {
        using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        var heaps = AppContext.GetData("GCHeapCount") ?? Environment.GetEnvironmentVariable("DOTNET_GCHeapCount") ?? "default";
        var adaptation = AppContext.GetData("System.GC.DynamicAdaptationMode") ?? Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode") ?? "default";
        writer.WriteLine($"# pid={Environment.ProcessId} serverGC={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode} "
            + $"cpus={Environment.ProcessorCount} gcHeapCount={heaps} dynamicAdaptation={adaptation}");
        writer.WriteLine("time;wakeLateMs;poolQueueWaitMs;poolThreads;pendingItems;completedItems;gen0;gen1;gen2;gcPauseTotalMs;heapMB;committedMB;workingSetMB");
        var process = Stopwatch.StartNew();
        while (true)
        {
            var before = process.ElapsedMilliseconds;
            Thread.Sleep(1000);
            var late = process.ElapsedMilliseconds - before - 1000;
            // Time from queueing a work item to it running (bounded at 30 s so the probe itself never stalls forever).
            var queued = Stopwatch.StartNew();
            using var ran = new ManualResetEventSlim();
            ThreadPool.UnsafeQueueUserWorkItem(_ => ran.Set(), null);
            var waited = ran.Wait(30_000) ? queued.ElapsedMilliseconds : -1;
            var info = GC.GetGCMemoryInfo();
            writer.WriteLine(string.Join(';', DateTime.Now.ToString("HH:mm:ss"), late, waited, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                (long)GC.GetTotalPauseDuration().TotalMilliseconds, GC.GetTotalMemory(false) / (1024 * 1024), info.TotalCommittedBytes / (1024 * 1024),
                Environment.WorkingSet / (1024 * 1024)));
        }
    }
}
