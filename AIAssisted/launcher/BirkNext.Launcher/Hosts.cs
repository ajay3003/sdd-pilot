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

/// <summary>The packaged backend (self-contained BirkNext.Api.exe), configured only through its own process environment.</summary>
internal sealed class BackendProcess(PackageLayout package, DataLayout data, LauncherLog log, LauncherJob? job = null) : IDisposable
{
    private Process? _process;
    private StreamWriter? _console;
    public int? ProcessId => _process?.Id;
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;
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
        // Local Tester Mode: the connected local tester gets every QA capability (active tests, test-plan and integration writes, System Settings,
        // Backup & Transfer) under the backend's own loopback-listener, loopback, Host, Origin and session checks — the same explicit setting a
        // developer or a local Release run uses, independent of the Production hosting environment above.
        ["BirkNext__LocalTesterMode"] = "true",
        ["PerformanceTests__Runner"] = "Native",
        ["PerformanceTests__Native__K6Path"] = package.K6Exe,
        ["PerformanceTests__Native__K6Sha256"] = package.Manifest.K6.Sha256,
        // Backup & Transfer: pg_dump/pg_restore are the package's own bundled tools (same build as the managed server), never the global PATH.
        ["Backup__PostgresTools__Mode"] = "Host",
        ["Backup__PostgresTools__BinDirectory"] = package.PostgresBin,
        // The embedded offline translator: the package's own runtime/translation (empty = not part of this package, reported as unavailable).
        ["ManualTesting__Translation__Offline__Directory"] = package.TranslationDirectory ?? "",
        ["BIRKNEXT_APP_DATA_DIR"] = data.AppData,
        ["LoggingSettings__LogPath"] = Path.Combine(data.Logs, "backend"),
        ["GitSources__SettingsDirectory"] = Path.Combine(data.AppData, "git-sources"),
        // A single-user desktop backend: Workstation GC. The published API defaults to Server GC (one heap per logical processor), which on a
        // 24-thread machine held 3.2 GB after two rounds of the Recommended Workflow calls where Workstation GC stayed at 1.0-1.3 GB. Hosted
        // deployments keep their own setting; only the Tester Package's backend is changed.
        ["DOTNET_gcServer"] = "0",
    };

    public void Start(Ports.Assignment ports, DatabaseCredentials credentials)
    {
        if (!File.Exists(package.BackendExe))
            throw new LauncherException("This BirkNext package is incomplete: the BirkNext service is missing.", "Download the BirkNext Tester Package again and extract the whole archive.");
        var info = new ProcessStartInfo(package.BackendExe) { WorkingDirectory = package.Backend, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BirkNext__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("PerformanceTests__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("Backup__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Authorization__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("ManualTesting__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("FRONTEND_ORIGIN", StringComparison.OrdinalIgnoreCase)).ToList())
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
        // Into the launcher's job at once (before it has started anything of its own): every process the backend starts later (k6, a
        // browser for Lighthouse…) is then in the job too, and all of it ends with the launcher.
        var inJob = job?.Add(_process) == true;
        if (job is not null && !inJob) log.Warn("The backend could not be bound to the launcher's lifetime (job object); it is stopped explicitly on exit.");
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        log.Info($"Backend started (pid {_process.Id}{(inJob ? ", in the launcher's job" : "")}) on http://localhost:{ports.Backend}.");
    }

    public bool HasExited => _process is null || _process.HasExited;

    /// <summary>
    /// Healthy = the backend answers its API (after startup migrations, which run before it listens). 401 is healthy: the API is up and asks the
    /// browser for its local session. The process must still be ours and alive.
    /// </summary>
    public async Task<bool> WaitHealthyAsync(int port, TimeSpan timeout, CancellationToken ct, Action<TimeSpan>? progress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var nextReport = started + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (HasExited) return false;
            // A bounded wait that says it is still working (a first start with migrations or a slow virus scan can take minutes).
            if (progress is not null && DateTime.UtcNow >= nextReport) { progress(DateTime.UtcNow - started); nextReport = DateTime.UtcNow + TimeSpan.FromSeconds(15); }
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

    /// <summary>
    /// Ends the backend and the processes it started (only this launcher's backend process tree, by its handle — never by name). The backend
    /// holds no state that needs a graceful stop: every write is a committed database transaction, and the database is stopped cleanly next.
    /// </summary>
    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5_000); } catch (InvalidOperationException) { }
            log.Info("Backend stopped.");
        }
    }

    public void Dispose() { Stop(); _console?.Dispose(); _process?.Dispose(); }
}

/// <summary>
/// Serves the packaged Blazor WebAssembly frontend on loopback. <c>appsettings.json</c> is served with the actual backend address (the package
/// file is not modified); deep links fall back to index.html. The frontend runs as Production, like any Release frontend: its loopback http
/// backend is accepted in every environment (Local Tester Mode), so no Development flag is needed (it was sent before 2026-11-18).
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
            context.Response.Headers["Blazor-Environment"] = "Production";
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

    /// <summary>Stops serving within <paramref name="timeout"/> (open browser requests are not waited for longer: the window may be closing).</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        using var cts = new CancellationTokenSource(timeout);
        try { await app.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
        log.Info("Frontend stopped.");
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));
}
