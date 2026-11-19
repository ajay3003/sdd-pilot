using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BirkNext.BuildInfo;

namespace BirkNext.Launcher;

/// <summary>The launcher's command line. One entry point; the default command starts BirkNext.</summary>
public sealed record LauncherOptions
{
    public string Command { get; init; } = "start";
    public string? RuntimeProfile { get; init; }
    public string? DataDirectory { get; init; }
    public string? PackageRoot { get; init; }
    public bool NoBrowser { get; init; }
    /// <summary>Acceptance only: stop BirkNext after it is healthy instead of waiting for the tester.</summary>
    public int? ExitAfterSeconds { get; init; }

    public static LauncherOptions Parse(IReadOnlyList<string> args)
    {
        var o = new LauncherOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new LauncherException($"{args[i]} needs a value.", "See Start-BirkNext.exe --help.", exitCode: 64);
            o = args[i].ToLowerInvariant() switch
            {
                "--runtime-profile" => o with { RuntimeProfile = Value() },
                "--data-dir" => o with { DataDirectory = Value() },
                "--package-root" => o with { PackageRoot = Value() },
                "--no-browser" => o with { NoBrowser = true },
                "--exit-after" => o with { ExitAfterSeconds = int.TryParse(Value(), out var s) && s > 0 ? s : throw new LauncherException("--exit-after needs seconds.", "Use --exit-after 30.", exitCode: 64) },
                "--status" or "--stop" or "--backup" or "--diagnostics" or "--validate" or "--help" => o with { Command = args[i].TrimStart('-').ToLowerInvariant() },
                _ => throw new LauncherException($"Unknown option {args[i]}.", "See Start-BirkNext.exe --help.", exitCode: 64),
            };
        }
        if (o.DataDirectory is { } d && !Path.IsPathFullyQualified(d))
            throw new LauncherException("--data-dir must be a full path.", "Use for example --data-dir D:\\BirkNextData.", exitCode: 64);
        return o;
    }

    public const string Help = """
        Start-BirkNext.exe                 Start BirkNext (local database, BirkNext service, browser).
          --no-browser                     Do not open the browser.
          --status                         Show the local database and runtime state.
          --stop                           Stop a running BirkNext (or only the local database when BirkNext is not running).
          --backup                         Write a verified backup of the local database.
          --diagnostics                    Write a diagnostics archive (logs and state, no passwords).
          --validate                       Check the package and this PC without starting anything.
          --runtime-profile TesterNative   The runtime profile (the package default; developer mode uses scripts/start-local.ps1).
          --data-dir <full path>           Use another data folder (default %LOCALAPPDATA%\BirkNext\TesterNative).
        """;
}

/// <summary>
/// The TesterNative start sequence: package and PC checks, the per-user data folder, a single-instance lock, the bundled PostgreSQL (first-run
/// setup or identity-verified reuse), a verified backup before a different BirkNext build opens the database, the backend (which applies its
/// migrations under its own lock), the frontend, health, and the browser. Every failure stops with a tester message; nothing falls back to Podman
/// or to another database.
/// </summary>
public sealed class TesterLauncher(LauncherOptions options, IConsoleUi ui, ShutdownCoordinator? shutdown = null)
{
    private LauncherLog? _log;
    private string? _stage;
    private bool _ready;
    private readonly Stopwatch _stageClock = new();

    /// <summary>
    /// A startup stage: shown to the tester and logged with how long the previous stage took, so a slow or stuck stage is visible in the log and
    /// in the diagnostics archive (not only as the last line on screen).
    /// </summary>
    private void Stage(string step)
    {
        // The same stage announced twice in a row (the launcher, then the database helper it calls) is shown once.
        if (string.Equals(step, _stage, StringComparison.Ordinal)) return;
        if (_stage is not null) _log?.Info($"Stage done: {_stage} ({_stageClock.Elapsed.TotalSeconds:0.0} s)");
        _log?.Info($"Stage: {step}");
        _stage = step;
        _stageClock.Restart();
        ui.Step(step);
    }

    public async Task<int> RunAsync(CancellationToken stop)
    {
        if (options.Command == "help") { ui.Line(LauncherOptions.Help); return 0; }
        var root = options.PackageRoot ?? AppContext.BaseDirectory;
        var manifest = RuntimeManifest.Read(PackageLayout.RuntimeManifestPath(root));
        var package = new PackageLayout(Path.GetFullPath(root), manifest);
        var (profile, source) = ProfileSelection.Resolve(options.RuntimeProfile, manifest.DefaultRuntimeProfile);
        var data = new DataLayout(options.DataDirectory ?? DataLayout.Default(profile));
        data.Create();
        try { OwnerOnly.Apply(data.Root); } catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        var log = new LauncherLog(data.Logs);
        _log = log;
        log.Info($"Start-BirkNext {options.Command}: profile {profile} ({source}), package {package.BuildVersion()}, data {data.Root}");
        ui.Title($"BirkNext {package.BuildVersion()} — {RuntimeProfiles.DisplayName(profile)}");
        try { return await DispatchAsync(package, data, log, stop); }
        catch (LauncherException e)
        {
            log.Error($"{e.Message} | {e.Technical}");
            StateFile.Write(data.LastErrorFile, new { at = DateTimeOffset.UtcNow, message = e.Message, action = e.Action, build = package.BuildVersion() });
            ui.Failure(e.Message, e.Action, log.FilePath);
            return e.ExitCode;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never a raw stack trace as the tester's message: the technical detail goes to the log.
            log.Error($"Unexpected failure: {e}");
            StateFile.Write(data.LastErrorFile, new { at = DateTimeOffset.UtcNow, message = "BirkNext could not start because of an unexpected error.", build = package.BuildVersion() });
            ui.Failure("BirkNext could not start because of an unexpected error.", "Start BirkNext again. If it keeps failing, send the diagnostics (Start-BirkNext.exe --diagnostics).", log.FilePath);
            return 2;
        }
    }

    private async Task<int> DispatchAsync(PackageLayout package, DataLayout data, LauncherLog log, CancellationToken stop)
    {
        switch (options.Command)
        {
            case "validate":
                await CheckPackageAsync(package, data, log, full: true, stop);
                ui.Done("The package and this PC are ready. Nothing was started.");
                return 0;
            case "diagnostics":
                ui.Done($"Diagnostics written: {Diagnostics.Write(package, data)}");
                return 0;
            case "status":
                return Status(package, data, new PostgresManager(package, data, log));
        }
        using var instance = SingleInstance.TryAcquire(data.Root);
        if (instance is null)
        {
            var session = StateFile.Read<SessionRecord>(data.SessionFile);
            if (options.Command == "stop" && session is not null)
            {
                // A running launcher stops itself in order (frontend, backend, database); this process only asks.
                StateFile.Write(data.StopRequestFile, new { requestedAt = DateTimeOffset.UtcNow, by = Environment.ProcessId });
                Stage("Asking the running BirkNext to stop");
                for (var i = 0; i < 120 && File.Exists(data.SessionFile); i++) await Task.Delay(500, CancellationToken.None);
                if (File.Exists(data.SessionFile)) throw new LauncherException("The running BirkNext did not stop.", "Close the BirkNext window.", exitCode: 3);
                ui.Done("BirkNext has stopped.");
                return 0;
            }
            if (options.Command == "start" && session is not null)
            {
                // Started again (e.g. the browser was closed): show the connection code again and open a connected browser.
                var running = StateFile.Read<Ports.Assignment>(data.PortsFile);
                var runningCode = ConnectionCode(data, TimeSpan.Zero);
                ui.Ready(session.FrontendUrl, runningCode, log.FilePath);
                ui.Line("  (BirkNext was already running; this window only opened the browser. Use the first BirkNext window to stop it.)");
                if (!options.NoBrowser)
                    Browser.Open(running is null ? session.FrontendUrl : await LocalAccess.ConnectedUrlAsync(session.FrontendUrl, running.Backend, runningCode, log), log);
                return 0;
            }
            throw new LauncherException("BirkNext is already running for this Windows user.", "Use the open BirkNext window, or close it first.", exitCode: 3);
        }
        // We hold the lock: a session or stop request left behind by a launcher that was killed is stale.
        foreach (var stale in new[] { data.SessionFile, data.StopRequestFile }) if (File.Exists(stale)) { File.Delete(stale); log.Info($"Removed stale {Path.GetFileName(stale)} (a previous launcher did not exit normally)."); }
        // Every process this launcher starts from here on (database server, backend) is in this job: when the launcher ends in any way, Windows
        // ends them too. Stopping them in order (below) stays the normal path; the job is the guarantee.
        using var job = options.Command is "start" or "backup" ? LauncherJob.TryCreate(log) : null;
        log.Info($"Launcher pid {Environment.ProcessId}; owned processes in a kill-on-close job: {(job is null ? "no" : "yes")}.");
        var postgres = new PostgresManager(package, data, log, job);
        switch (options.Command)
        {
            case "stop":
                Stage("Stopping local database");
                if (!postgres.Stop()) throw new LauncherException("The local database did not stop.", "Close BirkNext windows and try again.");
                ui.Done("The local database is stopped.");
                return 0;
        }

        BackendProcess? backend = null;
        FrontendHost? frontend = null;
        try
        {
            return await StartAndRunAsync(package, data, log, postgres, job, b => backend = b, f => frontend = f, stop);
        }
        finally
        {
            // One stop for every outcome: normal stop, Ctrl+C, window closed, backend exited, and a start that failed half-way (nothing it
            // started is left running). Only what this launcher owns: its frontend, its backend process, and the database of its own data folder.
            var hurried = shutdown?.Hurried == true;
            var clock = Stopwatch.StartNew();
            Stage("Stopping BirkNext");
            log.Info($"Stopping ({(_ready ? "after running" : "start did not complete")}; reason {shutdown?.Reason ?? ShutdownReason.None}).");
            if (frontend is not null) await frontend.StopAsync(hurried ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5));
            backend?.Dispose();
            postgres.Stop(hurried);
            if (File.Exists(data.SessionFile)) File.Delete(data.SessionFile);
            log.Info($"Stopped in {clock.Elapsed.TotalSeconds:0.0} s.");
        }
    }

    private async Task<int> StartAndRunAsync(PackageLayout package, DataLayout data, LauncherLog log, PostgresManager postgres, LauncherJob? job,
        Action<BackendProcess> started, Action<FrontendHost> served, CancellationToken stop)
    {
        Stage("Checking application files");
        await CheckPackageAsync(package, data, log, full: false, stop);
        var (ports, credentials, record) = PrepareDatabase(package, data, postgres, log);
        // Isolated restore needs a new database; the application role stays NOCREATEDB, so a separate narrow role creates it (see PostgresManager).
        try { credentials = postgres.EnsureRestoreProvisioner(ports.Database, credentials); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or IOException or UnauthorizedAccessException)
        { log.Warn($"The restore-provisioning role could not be prepared; isolated restore will report BLOCKED. {e.Message}"); }

        if (options.Command == "backup")
        {
            Stage("Writing backup");
            ui.Done($"Backup written: {postgres.Backup(ports.Database, credentials, "manual")}");
            return 0; // BirkNext is not running (we hold its lock): the caller stops the database.
        }

        // A different BirkNext build may migrate the schema: a verified backup first (never for the same build or a first run).
        var last = StateFile.Read<LastRunRecord>(data.LastRunFile);
        var build = package.BuildVersion();
        if (last is not null && last.Build != build)
        {
            Stage("Backing up before applying database updates");
            var size = postgres.DatabaseSize(ports.Database, credentials);
            var free = new DriveInfo(Path.GetPathRoot(data.Backups)!).AvailableFreeSpace;
            if (free < size * 2 + (256L << 20))
                throw new LauncherException("There is not enough free disk space to back up the database before the update.", "Free some disk space and start BirkNext again. Nothing was changed.");
            postgres.Backup(ports.Database, credentials, "pre-upgrade");
            foreach (var old in PostgresManager.PrunablePreUpgradeBackups(Directory.GetFiles(data.Backups, "*.dump")))
            { File.Delete(old); log.Info($"Older automatic pre-upgrade backup removed: {Path.GetFileName(old)}"); }
        }

        Stage("Applying database updates and starting BirkNext");
        var backend = new BackendProcess(package, data, log, job);
        started(backend);
        backend.Start(ports, credentials);
        bool healthy;
        try { healthy = await backend.WaitHealthyAsync(ports.Backend, TimeSpan.FromMinutes(5), stop, elapsed => ui.Step($"Still starting BirkNext ({(int)elapsed.TotalSeconds} s; a first start can take a few minutes)")); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { healthy = false; }
        if (!healthy)
        {
            var reason = backend.StartupFailure();
            if (stop.IsCancellationRequested)
            {
                // Ctrl+C or a closed window while BirkNext was starting: a cancelled start, not a failed one (the caller stops what was started).
                log.Warn($"Start cancelled while waiting for the BirkNext service. {reason}");
                throw new LauncherException("BirkNext start was cancelled. Everything it had started has been stopped.", "Start BirkNext again when you are ready.", exitCode: 130);
            }
            throw reason is not null && reason.Contains("requires a newer BirkNext", StringComparison.Ordinal)
                ? new LauncherException("The database requires a newer BirkNext than this package.", "Start the newer BirkNext version, or restore the backup made before the update (see the upgrade guide).", reason)
                : new LauncherException("BirkNext did not start.", "Start BirkNext again. If it keeps failing, send the diagnostics (Start-BirkNext.exe --diagnostics).",
                    reason ?? PostgresManager.Tail(backend.ConsoleLog));
        }
        StateFile.Write(data.LastRunFile, new LastRunRecord(build, DateTimeOffset.UtcNow));
        var frontend = new FrontendHost(package, log);
        served(frontend);
        await frontend.StartAsync(ports.Frontend, ports.Backend);
        var url = $"http://localhost:{ports.Frontend}/";
        StateFile.Write(data.SessionFile, new SessionRecord(url, Environment.ProcessId, DateTimeOffset.UtcNow, build));
        if (File.Exists(data.LastErrorFile)) File.Delete(data.LastErrorFile);
        var code = ConnectionCode(data, TimeSpan.FromSeconds(20));
        if (_stage is not null) log.Info($"Stage done: {_stage} ({_stageClock.Elapsed.TotalSeconds:0.0} s)");
        _stage = null;
        _ready = true;
        ui.Ready(url, code, log.FilePath);
        var server = postgres.ServerProcessId();
        log.Info($"Ready at {url} (database cluster {record.SystemIdentifier}). Owned: launcher {Environment.ProcessId}, backend {backend.ProcessId}{Owned(job, backend.ProcessId)}, "
            + $"database server {server?.ToString() ?? "?"}{Owned(job, server)}; ports backend {ports.Backend}, frontend {ports.Frontend}, database {ports.Database}.");
        if (!options.NoBrowser) Browser.Open(await LocalAccess.ConnectedUrlAsync(url, ports.Backend, code, log), log);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (options.ExitAfterSeconds is { } seconds) linked.CancelAfter(TimeSpan.FromSeconds(seconds));
        if (File.Exists(data.StopRequestFile)) File.Delete(data.StopRequestFile);
        while (!linked.IsCancellationRequested && !backend.HasExited && !File.Exists(data.StopRequestFile)) await Task.Delay(500, CancellationToken.None);
        if (File.Exists(data.StopRequestFile)) { log.Info("Stop requested (Start-BirkNext.exe --stop)."); File.Delete(data.StopRequestFile); shutdown?.Request(ShutdownReason.StopRequested); }
        else if (stop.IsCancellationRequested) log.Info($"Stop: {shutdown?.Reason ?? ShutdownReason.CtrlC}.");
        else if (linked.IsCancellationRequested) log.Info("Stop: --exit-after elapsed.");
        if (backend.HasExited && !stop.IsCancellationRequested)
        {
            // The backend ended on its own: a failure (non-zero exit, so the window stays open with the message); the caller still stops the database.
            log.Error($"The backend stopped unexpectedly (exit code {backend.ExitCode?.ToString() ?? "?"}).");
            shutdown?.Request(ShutdownReason.BackendExited);
            throw new LauncherException("BirkNext stopped unexpectedly.", "Start BirkNext again. If it happens again, send the diagnostics (Start-BirkNext.exe --diagnostics).",
                PostgresManager.Tail(backend.ConsoleLog), exitCode: 2);
        }
        return 0;
    }

    private static string Owned(LauncherJob? job, int? processId) => job is null || processId is null ? "" : job.Contains(processId.Value) ? " (in job)" : " (NOT in job)";

    /// <summary>How often a running package check reports its progress, and when a single file counts as slow (named, never aborted).</summary>
    public static readonly TimeSpan CheckHeartbeat = TimeSpan.FromSeconds(5), SlowFile = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quick check every start (hash of the runtime executables); the full hash of every file once per package build and folder. The full check
    /// runs in the background while this window reports progress every few seconds (a freshly extracted package is scanned by antivirus as it is
    /// read, so the first check can take minutes on a tester PC); it is never skipped and has no time limit. Ctrl+C cancels it.
    /// </summary>
    private async Task CheckPackageAsync(PackageLayout package, DataLayout data, LauncherLog log, bool full, CancellationToken stop)
    {
        var problems = Prerequisites.Problems(freeBytes: new DriveInfo(Path.GetPathRoot(data.Root)!).AvailableFreeSpace);
        if (problems.Count > 0) throw new LauncherException(problems[0], "Ask IT for help with this PC requirement.", string.Join(" | ", problems));
        var verifiedFile = Path.Combine(data.RuntimeState, "package-verified.json");
        if (!File.Exists(package.PackageManifest))
            throw new LauncherException("This BirkNext package is incomplete: its file list is missing.", "Download the BirkNext Tester Package again and extract the whole archive to a new folder.");
        var manifestHash = PackageIntegrity.ManifestHash(package.Root);
        var verified = StateFile.Read<VerifiedRecord>(verifiedFile);
        var needFull = full || verified is null || verified.ManifestSha256 != manifestHash || !PostgresManager.SamePath(verified.PackageRoot, package.Root);
        string Rel(string p) => Path.GetRelativePath(package.Root, p).Replace('\\', '/');
        var critical = new[] { package.BackendExe, Path.Combine(package.Backend, "BirkNext.Api.dll"), package.K6Exe, package.Tool("postgres"), package.Tool("initdb"), package.Tool("pg_ctl"),
            package.Tool("pg_dump"), package.Tool("pg_restore"), PackageLayout.RuntimeManifestPath(package.Root) }.Select(Rel).ToList();
        log.Info(needFull
            ? $"Package check: every file ({(verified is null ? "first check of this package folder" : "package or folder changed since the last full check")}) in {package.Root}."
            : "Package check: runtime files (every file was verified for this package and folder before).");
        if (needFull && options.Command == "start")
            ui.Line("    The first start after extracting checks every BirkNext file once; this can take a few minutes. Progress is shown below.");

        PackageIntegrity.Progress? latest = null;
        var clock = Stopwatch.StartNew();
        var verify = Task.Run(() => PackageIntegrity.Verify(package.Root, needFull ? null : critical, p => Volatile.Write(ref latest, p), stop), stop);
        var next = CheckHeartbeat;
        while (!verify.IsCompleted)
        {
            await Task.WhenAny(verify, Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None));
            if (verify.IsCompleted || clock.Elapsed < next) continue;
            next += CheckHeartbeat;
            if (Volatile.Read(ref latest) is not { } p) continue;
            var line = PackageIntegrity.Describe(p, clock.Elapsed, DateTime.UtcNow, SlowFile);
            ui.Step(line);
            log.Info(line);
        }
        PackageIntegrity.Result result;
        try { result = await verify; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log.Warn($"Package check cancelled after {clock.Elapsed.TotalSeconds:0} s.");
            throw new LauncherException("BirkNext start was cancelled while it was checking its files. Nothing was started.", "Start BirkNext again when you are ready.", exitCode: 130);
        }
        if (!result.Ok)
        {
            var (message, action) = result switch
            {
                { Missing.Count: > 0 } => ($"This BirkNext package is incomplete: {result.Missing.Count} file(s) are missing, for example {result.Missing[0]}.",
                    "Extract the whole archive again into a new, empty folder (wait until extraction has finished), then start BirkNext from there."),
                { Unreadable.Count: > 0 } => ($"BirkNext cannot read {result.Unreadable.Count} of its own file(s), for example {result.Unreadable[0].Split(" (")[0]}.",
                    "Another program (often antivirus) or folder permissions block them. Extract the archive again into a folder in your user profile and start again; if it keeps failing, ask IT."),
                _ => ($"This BirkNext package has been changed after it was built: {result.Changed.Count} file(s) differ, for example {result.Changed[0]}.",
                    "Download the BirkNext Tester Package again and extract the whole archive to a new folder."),
            };
            throw new LauncherException(message, action,
                $"missing: {string.Join(", ", result.Missing.Take(10))}; changed: {string.Join(", ", result.Changed.Take(10))}; unreadable: {string.Join(", ", result.Unreadable.Take(10))}");
        }
        if (needFull) StateFile.Write(verifiedFile, new VerifiedRecord(manifestHash, package.Root, DateTimeOffset.UtcNow, result.Checked));
        log.Info($"Package verified ({(needFull ? "all" : "runtime")} files: {result.Checked}) in {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    /// <summary>First run, or the existing cluster after identity verification. Never initializes over existing files.</summary>
    private (Ports.Assignment Ports, DatabaseCredentials Credentials, ClusterRecord Record) PrepareDatabase(PackageLayout package, DataLayout data, PostgresManager postgres, LauncherLog log)
    {
        var state = postgres.State(out var detail);
        log.Info($"Database state: {state} — {detail}");
        var saved = StateFile.Read<Ports.Assignment>(data.PortsFile);
        switch (state)
        {
            case ClusterState.UnknownFiles:
                throw new LauncherException("Database files are present but cannot be opened by BirkNext.", "Nothing was changed. Contact the BirkNext owner; do not delete the folder.", detail);
            case ClusterState.MissingFiles:
                throw new LauncherException("BirkNext's local database files are missing.", "Restore the data folder from a backup, or contact the BirkNext owner. A new empty database is not created automatically.", detail);
            case ClusterState.IncompatibleVersion:
                throw new LauncherException("The database requires an upgrade before BirkNext can start.", "Follow the PostgreSQL upgrade steps in the upgrade guide. Nothing was changed.", detail);
        }
        if (state is ClusterState.NotInitialized or ClusterState.InterruptedInitialization)
        {

            var taken = new HashSet<int>();
            var db = Ports.Choose(Ports.Preferred.Database, taken); taken.Add(db);
            var backendPort = Ports.Choose(saved?.Backend ?? Ports.Preferred.Backend, taken); taken.Add(backendPort);
            var frontendPort = Ports.Choose(saved?.Frontend ?? Ports.Preferred.Frontend, taken);
            var ports = new Ports.Assignment(db, backendPort, frontendPort);
            StateFile.Write(data.PortsFile, ports);
            var (record, credentials) = postgres.Initialize(db, package.BuildVersion(), Stage);
            return (ports, credentials, record);
        }
        var existing = postgres.Record!;
        var creds = DatabaseCredentials.Load(data.SecretsFile)
            ?? throw new LauncherException("The local database credentials are missing.", "Restore the data folder from a backup, or contact the BirkNext owner. Nothing was changed.");
        var current = saved ?? new Ports.Assignment(existing.Port, Ports.Preferred.Backend, Ports.Preferred.Frontend);
        Stage("Starting local database");
        if (postgres.IsRunning())
        {
            // Running on OUR data directory although we hold this data folder's lock: a server left behind by a launcher that did not stop it
            // (a window closed before 2026-11-19, a crash, an older package). Never reused as is — it belongs to no window and would outlive this
            // one too. Only when it proves to be our cluster (credentials, system identifier, data directory) is it stopped cleanly and started
            // again below, owned by this launcher. Anything else is reported, never stopped.
            var identity = postgres.VerifyIdentity(current.Database, creds, existing);
            if (!identity.Ok)
                throw new LauncherException("The local database is running but could not be verified.", "Close BirkNext, run Start-BirkNext.exe --stop, and start again.", identity.Detail);
            log.Info($"A local database left running by an earlier BirkNext window was found (server pid {postgres.ServerProcessId()?.ToString() ?? "?"}; {identity.Detail}); restarting it under this window.");
            Stage("Restarting the local database left running earlier");
            if (!postgres.Stop())
                throw new LauncherException("A local database left running earlier could not be stopped.", "Restart Windows, then start BirkNext again. Nothing was changed.");
        }
        if (!postgres.IsRunning())
        {
            var dbPort = current.Database;
            if (!Ports.IsFree(dbPort))
            {
                // Someone else's listener: never touch it; BirkNext moves to another port and remembers it.
                dbPort = Ports.Choose(dbPort + 1, new HashSet<int> { current.Backend, current.Frontend });
                log.Warn($"Port {current.Database} is used by another application; the local database now uses {dbPort}.");
            }
            current = current with { Database = dbPort };
            postgres.Start(dbPort, Stage);
            var identity = postgres.VerifyIdentity(dbPort, creds, existing);
            if (!identity.Ok)
            {
                postgres.Stop();
                throw new LauncherException("Database files are present but cannot be opened.", "Nothing was changed. Send the diagnostics (Start-BirkNext.exe --diagnostics).", identity.Detail);
            }
            log.Info($"Local database verified: {identity.Detail}");
        }
        var taken2 = new HashSet<int> { current.Database };
        var backend = Ports.IsFree(current.Backend) ? current.Backend : Ports.Choose(current.Backend + 1, taken2);
        taken2.Add(backend);
        var frontend = Ports.IsFree(current.Frontend) && current.Frontend != backend ? current.Frontend : Ports.Choose(current.Frontend + 1, taken2);
        current = current with { Backend = backend, Frontend = frontend };
        StateFile.Write(data.PortsFile, current);
        if (!postgres.Ready(current.Database, creds))
            throw new LauncherException("Unable to connect to the local database.", "Start BirkNext again. If it keeps failing, send the diagnostics.");
        return (current, creds, existing);
    }

    private int Status(PackageLayout package, DataLayout data, PostgresManager postgres)
    {
        var state = postgres.State(out var detail);
        ui.Line($"Runtime profile : TesterNative ({RuntimeProfiles.DisplayName(RuntimeProfile.TesterNative)})");
        ui.Line($"Package         : {package.BuildVersion()}");
        ui.Line($"PostgreSQL      : bundled {package.Manifest.PostgreSql.Version}");
        ui.Line($"k6              : bundled {package.Manifest.K6.Version}");
        ui.Line($"Database        : {state} — {detail}");
        ui.Line($"Database server : {(state == ClusterState.Initialized && postgres.IsRunning() ? "running" : "stopped")}");
        ui.Line($"Data folder     : {data.Root}");
        return 0;
    }

    /// <summary>The launch code the backend wrote (owner-only file in the data folder). Waits up to <paramref name="wait"/> for a first start.</summary>
    private static string? ConnectionCode(DataLayout data, TimeSpan wait)
    {
        var file = Path.Combine(data.AppData, "local-session", "launch-code.txt");
        var until = DateTime.UtcNow + wait;
        while (!File.Exists(file) && DateTime.UtcNow < until) Thread.Sleep(250);
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public sealed record SessionRecord(string FrontendUrl, int LauncherProcessId, DateTimeOffset StartedAt, string Build);
    public sealed record LastRunRecord(string Build, DateTimeOffset At);
    public sealed record VerifiedRecord(string ManifestSha256, string PackageRoot, DateTimeOffset At, int Files);
}

/// <summary>
/// Automatic local access for the browser this launcher opens: the launcher proves possession of the launch code to the backend
/// (POST api/local-session/bootstrap, launcher client header, no Origin, loopback) and receives a one-time token valid two minutes. The browser
/// gets the token in the URL fragment (never sent to a server); the page exchanges it once for its session cookie and removes it from the address.
/// The launch code itself is never put in a URL. Any failure falls back to the plain address and the code shown in this window.
/// </summary>
public static class LocalAccess
{
    public static async Task<string> ConnectedUrlAsync(string url, int backendPort, string? code, LauncherLog log)
    {
        if (string.IsNullOrWhiteSpace(code)) return url;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{backendPort}/api/local-session/bootstrap")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { code }),
            };
            request.Headers.Add("X-BirkNext-Client", "launcher");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { log.Warn($"Automatic browser connection not available ({(int)response.StatusCode}); the code is shown instead."); return url; }
            var token = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<BootstrapResponse>(response.Content))?.Token;
            // The token is never logged.
            return token is { Length: 64 } ? BootstrapUrl(url, token) : url;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.Warn($"Automatic browser connection failed ({e.GetType().Name}); the code is shown instead.");
            return url;
        }
    }

    /// <summary>The address with the one-time token in its fragment. Pure.</summary>
    public static string BootstrapUrl(string url, string token) => $"{url.Split('#')[0]}#bootstrap={token}";

    private sealed record BootstrapResponse(string Token, int ExpiresInSeconds);
}

/// <summary>One launcher per data folder (a per-user named mutex, held while BirkNext runs).</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire(string dataRoot)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, $@"Local\BirkNext.Launcher.{key}");
        try { if (mutex.WaitOne(0)) return new SingleInstance(mutex); }
        catch (AbandonedMutexException) { return new SingleInstance(mutex); } // A crashed launcher: the lock is ours now.
        mutex.Dispose();
        return null;
    }

    public void Dispose() { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } _mutex.Dispose(); }
}

public static class Browser
{
    /// <summary>Microsoft Edge when installed, else the default browser. Never with extra flags or profiles.</summary>
    public static void Open(string url, LauncherLog log)
    {
        var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
        try
        {
            if (edge is not null) Process.Start(new ProcessStartInfo(edge) { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e) { log.Warn($"The browser could not be opened: {e.Message}"); }
    }
}

/// <summary>A diagnostics archive: launcher, database and backend logs and the non-secret state files. The credentials file is never included.</summary>
public static class Diagnostics
{
    public static string Write(PackageLayout package, DataLayout data)
    {
        Directory.CreateDirectory(data.Diagnostics);
        var zip = Path.Combine(data.Diagnostics, $"birknext-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string path, string name)
        {
            if (!File.Exists(path)) return;
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var e = archive.CreateEntry(name).Open();
            var text = new StreamReader(s).ReadToEnd();
            using var w = new StreamWriter(e);
            w.Write(LauncherLog.Redact(text));
        }
        foreach (var f in Directory.Exists(data.Logs) ? Directory.GetFiles(data.Logs, "*.log", SearchOption.AllDirectories) : []) Add(f, "logs/" + Path.GetRelativePath(data.Logs, f).Replace('\\', '/'));
        foreach (var f in new[] { data.ClusterFile, data.PortsFile, data.LastRunFile, data.LastErrorFile, data.SessionFile, data.InitializationFile })
            Add(f, "state/" + Path.GetFileName(f));
        Add(PackageLayout.RuntimeManifestPath(package.Root), "package/runtime-manifest.json");
        Add(package.BuildManifest, "package/build-manifest.json");
        var summary = archive.CreateEntry("summary.json").Open();
        JsonSerializer.Serialize(summary, new
        {
            createdAt = DateTimeOffset.UtcNow, package = package.BuildVersion(), os = Environment.OSVersion.VersionString, arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            prerequisites = Prerequisites.Problems(), note = "No passwords or credential files are included.",
        }, RuntimeManifest.Json);
        summary.Dispose();
        return zip;
    }
}
