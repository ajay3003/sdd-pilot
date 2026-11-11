using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace BirkNext.Launcher;

/// <summary>
/// A Windows job object that terminates its processes when the launcher ends (also when the launcher is killed), so a backend can never be
/// orphaned on its port. PostgreSQL is deliberately NOT in the job: it is stopped cleanly by pg_ctl, and survives a launcher crash safely.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private readonly IntPtr _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try { Marshal.StructureToPtr(info, ptr, false); SetInformationJobObject(_handle, 9 /* ExtendedLimitInformation */, ptr, (uint)length); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public bool Add(Process process) => AssignProcessToJobObject(_handle, process.Handle);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>The packaged backend (self-contained BirkNext.Api.exe), configured only through its own process environment.</summary>
internal sealed class BackendProcess(PackageLayout package, DataLayout data, LauncherLog log) : IDisposable
{
    private Process? _process;
    private KillOnCloseJob? _job;
    private StreamWriter? _console;
    public string ConsoleLog => Path.Combine(data.Logs, "backend-console.log");

    /// <summary>
    /// The backend's settings for TesterNative. Secrets (the connection string) exist only in the child's environment — never in a file, an
    /// argument or a log. The developer's own environment cannot redirect it to another database: every database setting is set explicitly.
    /// </summary>
    internal static Dictionary<string, string> Environment(PackageLayout package, DataLayout data, Ports.Assignment ports, DatabaseCredentials credentials) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["DOTNET_ENVIRONMENT"] = "Production",
        ["ASPNETCORE_URLS"] = $"http://localhost:{ports.Backend}",
        ["ConnectionStrings__Default"] = credentials.AppConnectionString(ports.Database),
        // Used only to create a new isolated restore database owned by the application role (empty = not provisioned → restore BLOCKED).
        ["Backup__RestoreProvisioning__ConnectionString"] = credentials.ProvisioningConnectionString(ports.Database),
        ["FRONTEND_ORIGIN"] = $"http://localhost:{ports.Frontend}",
        ["BirkNext__RuntimeProfile"] = "TesterNative",
        ["BirkNext__Distribution"] = BirkNext.BuildInfo.RuntimeProfiles.WindowsTesterPackage,
        // Local single-user execution (performance tests and other active tests) under the backend's own loopback, Host, Origin and session checks.
        ["Authorization__LocalTrustedExecution"] = "true",
        // The Tester Package is a per-user installation: its own user maintains its configuration (System Settings saves), under the same
        // loopback, Host, Origin and local-session checks. Never a header; never in any other environment.
        ["Authorization__LocalSystemConfiguration"] = "true",
        // Backup & Transfer for the same single user (full backup, isolated restore, packages), under the same loopback/Host/Origin/session checks.
        ["Authorization__LocalWorkspaceTransfer"] = "true",
        ["PerformanceTests__Runner"] = "Native",
        ["PerformanceTests__Native__K6Path"] = package.K6Exe,
        ["PerformanceTests__Native__K6Sha256"] = package.Manifest.K6.Sha256,
        // Backup & Transfer: pg_dump/pg_restore are the package's own bundled tools (same build as the managed server), never the global PATH.
        ["Backup__PostgresTools__Mode"] = "Host",
        ["Backup__PostgresTools__BinDirectory"] = package.PostgresBin,
        ["BIRKNEXT_APP_DATA_DIR"] = data.AppData,
        ["LoggingSettings__LogPath"] = Path.Combine(data.Logs, "backend"),
        ["GitSources__SettingsDirectory"] = Path.Combine(data.AppData, "git-sources"),
    };

    public void Start(Ports.Assignment ports, DatabaseCredentials credentials)
    {
        if (!File.Exists(package.BackendExe))
            throw new LauncherException("This BirkNext package is incomplete: the BirkNext service is missing.", "Download the BirkNext Tester Package again and extract the whole archive.");
        var info = new ProcessStartInfo(package.BackendExe) { WorkingDirectory = package.Backend, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BirkNext__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("PerformanceTests__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("Backup__", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        foreach (var (k, v) in Environment(package, data, ports, credentials)) info.Environment[k] = v;
        Directory.CreateDirectory(data.Logs);
        _console = new StreamWriter(new FileStream(ConsoleLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var written = 0L;
        void Write(string? line)
        {
            if (line is null || _console is null) return;
            // Bounded (8 MB per run); the backend's own rolling log has the full record.
            if (Interlocked.Add(ref written, line.Length) > 8 * 1024 * 1024) return;
            lock (_console) _console.WriteLine(LauncherLog.Redact(line));
        }
        _process.OutputDataReceived += (_, e) => Write(e.Data);
        _process.ErrorDataReceived += (_, e) => Write(e.Data);
        try { _process.Start(); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new LauncherException("Windows did not allow the BirkNext service to start.", "An application-control or antivirus policy may block it. Ask IT, and send the diagnostics.", e.Message);
        }
        _job = new KillOnCloseJob();
        if (!_job.Add(_process)) log.Warn("The backend could not be bound to the launcher's lifetime (job object); it is stopped explicitly on exit.");
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        log.Info($"Backend started (pid {_process.Id}) on http://localhost:{ports.Backend}.");
    }

    public bool HasExited => _process is null || _process.HasExited;

    /// <summary>
    /// Healthy = the backend answers its API (after startup migrations, which run before it listens). 401 is healthy: the API is up and asks the
    /// browser for its local session. The process must still be ours and alive.
    /// </summary>
    public async Task<bool> WaitHealthyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (HasExited) return false;
            try
            {
                using var r = await http.GetAsync($"http://localhost:{port}/api/system/build-info", ct);
                if ((int)r.StatusCode is 200 or 401) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>The backend's own explanation when it stopped during startup (e.g. a database newer than this build), from its console output.</summary>
    public string? StartupFailure()
    {
        var tail = PostgresManager.Tail(ConsoleLog, 80);
        var line = tail.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("InvalidOperationException", StringComparison.Ordinal) || l.Contains("requires a newer BirkNext", StringComparison.Ordinal));
        return line;
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(15_000); } catch (InvalidOperationException) { }
            log.Info("Backend stopped.");
        }
    }

    public void Dispose() { Stop(); _job?.Dispose(); _console?.Dispose(); _process?.Dispose(); }
}

/// <summary>
/// Serves the packaged Blazor WebAssembly frontend on loopback. <c>appsettings.json</c> is served with the actual backend address (the package
/// file is not modified); deep links fall back to index.html. The Blazor-Environment header follows the existing local launcher convention
/// (the loopback http backend is accepted only in Development; Production keeps requiring https).
/// </summary>
internal sealed class FrontendHost(PackageLayout package, LauncherLog log) : IAsyncDisposable
{
    private WebApplication? _app;

    internal static string AppSettings(string packaged, int backendPort)
    {
        var node = JsonNode.Parse(packaged) as JsonObject ?? new JsonObject();
        node["BackendUrl"] = $"http://localhost:{backendPort}";
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public async Task StartAsync(int port, int backendPort)
    {
        if (!File.Exists(Path.Combine(package.Wwwroot, "index.html")))
            throw new LauncherException("This BirkNext package is incomplete: the BirkNext web application is missing.", "Download the BirkNext Tester Package again and extract the whole archive.");
        var settingsPath = Path.Combine(package.Wwwroot, "appsettings.json");
        var settings = AppSettings(File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : "{}", backendPort);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = package.Wwwroot, WebRootPath = package.Wwwroot, EnvironmentName = "Production" });
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var files = new PhysicalFileProvider(package.Wwwroot);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";
        types.Mappings[".wasm"] = "application/wasm";
        _app.Use(async (context, next) =>
        {
            context.Response.Headers["Blazor-Environment"] = "Development";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Path.Equals("/appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "application/json";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync(settings);
                return;
            }
            await next();
        });
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = false });
        _app.MapFallback(async context =>
        {
            // Deep links (/dashboard, /system-settings…) load the app; missing files under _framework or with an extension stay 404.
            if (Path.HasExtension(context.Request.Path.Value) || context.Request.Path.StartsWithSegments("/_framework")) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
        });
        try { await _app.StartAsync(); }
        catch (IOException e)
        {
            throw new LauncherException("The selected port is already used by another application.", "Close the other application or start BirkNext again; it will choose another port.", e.Message);
        }
        log.Info($"Frontend served on http://localhost:{port}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
    }
}
