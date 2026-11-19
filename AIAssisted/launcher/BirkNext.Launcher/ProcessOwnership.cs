using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BirkNext.Launcher;

/// <summary>
/// The processes this launcher owns, as one Windows job object with KILL_ON_JOB_CLOSE. The packaged backend and the bundled PostgreSQL (pg_ctl,
/// its cmd wrapper, the postmaster and every server process) are in the job, so when this launcher ends in any way — window closed, Ctrl+C,
/// logoff, shutdown, Task Manager, crash — Windows closes the job's last handle and terminates them. Nothing is found by name: only processes
/// this launcher started are ever in its job, so another launcher's processes or a developer's dotnet/postgres are never affected.
/// The browser is deliberately NOT in the job (it is the tester's own browser and outlives BirkNext). The job does not allow breakaway, so a
/// process started inside it (pg_ctl's postmaster, a backend tool) stays in it.
/// </summary>
public sealed class LauncherJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private readonly IntPtr _handle;

    private LauncherJob(IntPtr handle) => _handle = handle;

    /// <summary>A new job, or null where Windows job objects are not available (the launcher then stops its processes explicitly only).</summary>
    public static LauncherJob? TryCreate(LauncherLog? log = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) { log?.Warn($"No job object (error {Marshal.GetLastWin32Error()}): owned processes are stopped explicitly on exit only."); return null; }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = KillOnJobClose } };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 9 /* ExtendedLimitInformation */, ptr, (uint)length))
            {
                log?.Warn($"The job object could not be set to stop its processes with the launcher (error {Marshal.GetLastWin32Error()}).");
                CloseHandle(handle);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return new LauncherJob(handle);
    }

    public bool Add(Process process) => AssignProcessToJobObject(_handle, process.Handle);

    internal bool Add(IntPtr processHandle) => AssignProcessToJobObject(_handle, processHandle);

    /// <summary>Is the process with this id in THIS job (diagnostics and acceptance; false when it has exited or cannot be opened).</summary>
    public bool Contains(int processId)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)processId);
        if (h == IntPtr.Zero) return false;
        try { return IsProcessInJob(h, _handle, out var inJob) && inJob; }
        finally { CloseHandle(h); }
    }

    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Runs a console tool in its own hidden console with no inherited handle (what ShellExecute with a hidden window did for pg_ctl start: the
/// server must not inherit this launcher's pipes or console), but created suspended and put in the launcher's job before its first instruction,
/// so everything it starts is owned by the job too. There is no window in which a child could escape the job.
/// </summary>
public static class HiddenProcess
{
    private const uint CreateSuspended = 0x4, CreateNewConsole = 0x10, CreateUnicodeEnvironment = 0x400, StartfUseShowWindow = 0x1, Infinite = 0xFFFFFFFF;

    /// <summary>Exit code, or null when <paramref name="timeout"/> passed first (the process keeps running in the job).</summary>
    public static int? Run(string executable, IReadOnlyList<string> arguments, LauncherJob? job, TimeSpan timeout, out int processId)
    {
        var commandLine = new StringBuilder(CommandLine(executable, arguments));
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = StartfUseShowWindow, wShowWindow = 0 /* SW_HIDE */ };
        if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNewConsole | CreateUnicodeEnvironment, IntPtr.Zero,
                Path.GetDirectoryName(executable), ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        processId = pi.dwProcessId;
        try
        {
            if (job is not null && !job.Add(pi.hProcess))
            {
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                throw new Win32Exception(error, "The process could not be put in the launcher's job.");
            }
            ResumeThread(pi.hThread);
            var wait = WaitForSingleObject(pi.hProcess, timeout == Timeout.InfiniteTimeSpan ? Infinite : (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
            if (wait != 0) return null;
            return GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : -1;
        }
        finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
    }

    /// <summary>The Windows command line for these arguments (the quoting rules of CommandLineToArgvW / the C runtime). Pure.</summary>
    public static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        Append(sb, executable);
        foreach (var a in arguments) { sb.Append(' '); Append(sb, a); }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        sb.Append('"');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute; public uint dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Why BirkNext is stopping. Window close, logoff and shutdown leave Windows' few seconds; the stop then uses short waits.</summary>
public enum ShutdownReason { None, CtrlC, WindowClosed, LogoffOrShutdown, StopRequested, BackendExited, TimedExit }

/// <summary>
/// One stop for every way the tester ends BirkNext. Ctrl+C only asks (the launcher keeps running and stops in order). Closing the window,
/// logging off or shutting down arrive as console control events that Windows follows with termination as soon as the handler returns: the
/// handler therefore WAITS here until the launcher reports its stop complete (or <see cref="CloseBudget"/> passes, after which the job object
/// still terminates every owned process when Windows ends the launcher).
/// </summary>
public sealed class ShutdownCoordinator : IDisposable
{
    /// <summary>Windows ends a console process about 5 s after CTRL_CLOSE_EVENT; the stop must finish inside this.</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(4.5);

    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _completed = new(false);
    private int _reason;

    public CancellationToken Token => _stop.Token;
    public ShutdownReason Reason => (ShutdownReason)Volatile.Read(ref _reason);
    /// <summary>Windows will end the process shortly: use the short timeouts.</summary>
    public bool Hurried => Reason is ShutdownReason.WindowClosed or ShutdownReason.LogoffOrShutdown;
    public bool IsCompleted => _completed.IsSet;

    /// <summary>Asks for a stop; the first reason is kept (a later window close after Ctrl+C still waits for the same stop).</summary>
    public void Request(ShutdownReason reason)
    {
        Interlocked.CompareExchange(ref _reason, (int)reason, (int)ShutdownReason.None);
        // A close that follows a Ctrl+C stop in progress must still use the short timeouts.
        if (reason is ShutdownReason.WindowClosed or ShutdownReason.LogoffOrShutdown) Volatile.Write(ref _reason, (int)reason);
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>For the close/logoff/shutdown handler: request the stop and block until it completed or the budget is used.</summary>
    public bool RequestAndWait(ShutdownReason reason, TimeSpan budget)
    {
        Request(reason);
        return _completed.Wait(budget);
    }

    /// <summary>The launcher has stopped everything it owns (called once, last).</summary>
    public void MarkCompleted() => _completed.Set();

    public void Dispose() { _stop.Dispose(); }
}
