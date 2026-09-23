using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// What the running dedicated browser process was actually started with — read back from that process, not from
/// BirkNext's own launch record. Only the two arguments that matter are kept; the rest of the command line is discarded.
/// </summary>
public sealed record OwnedEdgeLaunchEvidence(string? ProxyServer, string? UserDataDirectory);

/// <summary>
/// Runtime evidence about the ONE Edge process BirkNext launched, by its process id. It never enumerates or reads other
/// Edge processes, never reads Edge settings, preferences, policy or the Windows proxy, and never changes anything.
/// </summary>
public interface IOwnedEdgeProcessInspector
{
    /// <summary>Whether this system can tell which process owns a loopback connection at all.</summary>
    bool CanAttributeConnections { get; }

    /// <summary>The proxy and profile arguments of process <paramref name="processId"/>, or null when they cannot be read.</summary>
    OwnedEdgeLaunchEvidence? ReadLaunchEvidence(int processId);

    /// <summary>
    /// Whether the loopback TCP connection from <paramref name="client"/> to the proxy port belongs to
    /// <paramref name="ownedProcessId"/> or to one of its child processes (Edge makes its requests from a network-service
    /// child). Null when the owner cannot be determined; false when it belongs to some other process.
    /// </summary>
    bool? ConnectionBelongsTo(IPEndPoint client, int proxyPort, int ownedProcessId, DateTimeOffset ownedStartedAt);
}

/// <summary>Where the operating system offers no way to read this evidence, the honest answer is always "unknown".</summary>
public sealed class UnavailableOwnedEdgeProcessInspector : IOwnedEdgeProcessInspector
{
    public bool CanAttributeConnections => false;
    public OwnedEdgeLaunchEvidence? ReadLaunchEvidence(int processId) => null;
    public bool? ConnectionBelongsTo(IPEndPoint client, int proxyPort, int ownedProcessId, DateTimeOffset ownedStartedAt) => null;
}

/// <summary>
/// Windows implementation. The command line comes from <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> on
/// a query-limited handle to the given process id; connection ownership from the owner-PID TCP table and the process
/// snapshot's parent links. All three need no elevation for a process the same user started.
/// </summary>
public sealed class WindowsOwnedEdgeProcessInspector : IOwnedEdgeProcessInspector
{
    private const int MaxAncestorHops = 4;

    public bool CanAttributeConnections => OperatingSystem.IsWindows();

    public OwnedEdgeLaunchEvidence? ReadLaunchEvidence(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (ReadCommandLine(processId) is not { Length: > 0 } commandLine) return null;
            string? proxy = null, profile = null;
            foreach (var argument in SplitArguments(commandLine))
            {
                if (argument.StartsWith("--proxy-server=", StringComparison.OrdinalIgnoreCase)) proxy = argument["--proxy-server=".Length..];
                else if (argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase)) profile = argument["--user-data-dir=".Length..];
            }
            return new(proxy, profile);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool? ConnectionBelongsTo(IPEndPoint client, int proxyPort, int ownedProcessId, DateTimeOffset ownedStartedAt)
    {
        if (!OperatingSystem.IsWindows() || client.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            if (OwnerOfConnection(client, proxyPort) is not { } owner) return null;
            if (owner == ownedProcessId) return true;
            var parents = ParentMap();
            for (int current = owner, hop = 0; hop < MaxAncestorHops && parents.TryGetValue(current, out var parent) && parent > 0; current = parent, hop++)
            {
                if (parent != ownedProcessId) continue;
                // A reused process id is not a child: the connection's owner must have started after the owned browser did.
                return StartedAt(owner) is not { } start || start >= ownedStartedAt.AddSeconds(-1);
            }
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static DateTimeOffset? StartedAt(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return process.StartTime; }
        catch (Exception) { return null; }
    }

    // ── Command line ─────────────────────────────────────────────────────────────────────────────────────────────

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern uint NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("shell32.dll", SetLastError = true)] private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static string? ReadCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (status != StatusInfoLengthMismatch || needed <= 0 || needed > 1 << 20) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    private static IEnumerable<string> SplitArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++) arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    // ── Connection ownership ─────────────────────────────────────────────────────────────────────────────────────

    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid; }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    private static int NetworkPort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    /// <summary>The process owning the client side of <paramref name="client"/> → 127.0.0.1:<paramref name="proxyPort"/>.</summary>
    private static int? OwnerOfConnection(IPEndPoint client, int proxyPort)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew; size now holds the new length.
                if (result != 0) return null;
                var rows = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
#pragma warning disable CS0618 // IPAddress.Address is the documented little-endian IPv4 value the table uses.
                var clientAddress = (uint)client.Address.Address;
#pragma warning restore CS0618
                for (var i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    if (row.LocalAddr == clientAddress && NetworkPort(row.LocalPort) == client.Port && NetworkPort(row.RemotePort) == proxyPort)
                        return (int)row.OwningPid;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    /// <summary>Process id → parent process id. Ids and parent links only; no names, paths or arguments are kept.</summary>
    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            return map;
        }
        finally { CloseHandle(snapshot); }
    }
}
