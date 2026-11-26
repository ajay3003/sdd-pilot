using Path = System.IO.Path;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning.OfflineTranslation;

/// <summary>
/// Owns the embedded offline translator's helper process (the bundled Python running birknext_translate.py): started on first use, one request at
/// a time over stdin/stdout (no port, no network: the bundled runtime has no socket module), stopped when idle, on timeout, and when BirkNext stops.
/// On Windows the helper runs in a job object that kills it if the backend exits without stopping it and caps its memory. The bundle is verified
/// (every file's SHA-256) before the helper is started; nothing is ever downloaded.
/// </summary>
public sealed class OfflineTranslationEngine : IAsyncDisposable
{
    public const string ProbeSentence = "Open the page, fill in all required fields and select Save.";
    /// <summary>
    /// Loading normally takes about a second. The first start on a cold PC (a fresh non-persistent AVD session in particular) reads and
    /// antivirus-scans the 82 MB model and the 61 MB CTranslate2 DLL first: a tester's first start exceeded 90 s on 2026-11-23 and the second
    /// one succeeded. 180 s lets that first start finish.
    /// </summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(180);
    /// <summary>The helper uses about 125 MB while idle; restarting it costs the model load again, so it stays up for an hour of inactivity
    /// (was 10 minutes, which made a tester wait again after a short break). This is the default; System Settings → Translation chooses another
    /// value, or never (<see cref="TranslationSettings.OfflineIdleStopMinutes"/>).</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(TranslationSettings.DefaultOfflineIdleStopMinutes);
    public const long MemoryLimitBytes = 2L * 1024 * 1024 * 1024;

    private readonly IConfiguration _config;
    private readonly ILogger<OfflineTranslationEngine> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _verifyLock = new();
    private (string Key, OfflineTranslationBundle? Bundle, string? Problem)? _verified;
    private Process? _process;
    /// <summary>The helper's last error-output lines (bounded), so a failed start can say why instead of "did not load".</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stderrTail = new();
    private JobHandle? _job;
    private ITimer? _idle;
    private OfflineTranslationStatus? _probed;
    private bool _disposed;

    private readonly TranslationSettingsStore? _settings;

    public OfflineTranslationEngine(IConfiguration config, ILogger<OfflineTranslationEngine> log, TimeProvider? clock = null, TranslationSettingsStore? settings = null)
    {
        _config = config; _log = log; _clock = clock ?? TimeProvider.System; _settings = settings;
    }

    /// <summary>The translation settings were saved: a running, idle helper gets its stop rescheduled with the new "keep loaded" choice at once
    /// (cancelled for "never"). A helper busy translating reschedules by itself when the translation ends.</summary>
    public void SettingsChanged()
    {
        if (_disposed || !_gate.Wait(0)) return;
        try { if (_process is { HasExited: false }) ScheduleIdleStop(); }
        finally { _gate.Release(); }
    }

    /// <summary>How long the helper may stay idle before it stops: the saved setting (read each time, so a change needs no restart); null = never.</summary>
    public TimeSpan? IdleStopAfter()
    {
        int minutes;
        try { minutes = _settings?.Effective().Settings.OfflineIdleStopMinutes ?? TranslationSettings.DefaultOfflineIdleStopMinutes; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { minutes = TranslationSettings.DefaultOfflineIdleStopMinutes; }
        return minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);
    }

    /// <summary>The bundle in use and its verification (cached until a candidate folder or its manifest changes).</summary>
    public (OfflineTranslationBundle? Bundle, string? Problem) Bundle()
    {
        // First bundle that exists; a clone holding only Git LFS pointers is used only when no other bundle exists (so its message is shown).
        var present = OfflineTranslationBundle.Candidates(_config).Where(d => File.Exists(Path.Combine(d, OfflineTranslationBundle.ManifestFile))).ToList();
        var directory = present.FirstOrDefault(d => !OfflineTranslationBundle.IsLfsPointer(Path.Combine(d, OfflineTranslationBundle.ManifestFile))) ?? present.FirstOrDefault();
        if (directory is null) return (null, "The offline translation model is not installed with this BirkNext (no runtime/translation folder). Use the complete BirkNext Tester Package; on a developer PC build it once with scripts/build-offline-translation.ps1 (see its help) into %LOCALAPPDATA%/BirkNext/offline-translation.");
        var manifest = new FileInfo(Path.Combine(directory, OfflineTranslationBundle.ManifestFile));
        var key = $"{Path.GetFullPath(directory)}|{manifest.Length}|{manifest.LastWriteTimeUtc.Ticks}";
        lock (_verifyLock)
        {
            if (_verified is { } v && v.Key == key) return (v.Bundle, v.Problem);
            var (bundle, problem) = OfflineTranslationBundle.Verify(directory);
            _verified = (key, bundle, problem);
            if (problem is not null) _log.LogWarning("Offline translation bundle in {Directory} is not usable: {Problem}", directory, problem);
            return (bundle, problem);
        }
    }

    /// <summary>Clears the verification so the next use reads the bundle again (after a package was repaired).</summary>
    public void Forget() { lock (_verifyLock) { _verified = null; _probed = null; } }

    /// <summary>What System Settings shows. Without <paramref name="probe"/> nothing is started; with it the helper is started (if needed) and must
    /// translate the probe sentence before the state is "Ready". A successful probe is reused while the same helper runs.</summary>
    public async Task<OfflineTranslationStatus> StatusAsync(bool probe, CancellationToken ct)
    {
        var (bundle, problem) = Bundle();
        if (bundle is null)
            return new() { State = problem!.Contains("not installed", StringComparison.Ordinal) ? "NotInstalled" : "Damaged", Summary = "Offline translation — not available", Detail = problem, CheckedAt = _clock.GetUtcNow() };
        var facts = Facts(bundle);
        if (_probed is { } known && _process is { HasExited: false }) return known;
        if (!probe) return facts with { State = "Installed", Summary = "Offline translation — installed, not started yet", Detail = "The package is complete and unchanged. It starts on the first translation or test." };
        var watch = Stopwatch.StartNew();
        try
        {
            var answer = await RequestAsync(new JsonObject { ["op"] = "probe" }, TimeSpan.FromSeconds(60), ct);
            var sample = answer["sample"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sample) || string.Equals(sample.Trim(), ProbeSentence, StringComparison.Ordinal))
                return facts with { State = "Failed", Summary = "Offline translation — not working", Detail = "The model started but did not translate the probe sentence.", CheckedAt = _clock.GetUtcNow() };
            var ready = facts with
            {
                State = "Ready", Summary = "Offline translation — Ready", Detail = "The packaged model loaded and translated the probe sentence on this PC. Nothing leaves this PC.",
                ProbeSource = ProbeSentence, ProbeResult = sample, ProbeSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2), CheckedAt = _clock.GetUtcNow(),
            };
            _probed = ready;
            return ready;
        }
        catch (TestCaseTranslationException e)
        {
            return facts with { State = "Failed", Summary = "Offline translation — not working", Detail = e.Message, CheckedAt = _clock.GetUtcNow() };
        }
    }

    private static OfflineTranslationStatus Facts(OfflineTranslationBundle b) => new()
    {
        Model = $"{b.Manifest.Model.Id} (revision {b.Manifest.Model.Revision[..Math.Min(12, b.Manifest.Model.Revision.Length)]})",
        ModelLicense = b.Manifest.Model.License, Attribution = b.Manifest.Model.Attribution,
        Engine = $"{b.Manifest.Engine.Name} {b.Manifest.Engine.Version}, {b.Manifest.Engine.Tokenizer}, CPU",
        Languages = $"{TranslationLanguages.Name(b.Manifest.Model.Source)} → {TranslationLanguages.Name(b.Manifest.Model.Target)}",
    };

    /// <summary>Translates plain texts (placeholders already applied by the caller). Throws <see cref="TestCaseTranslationException"/>.</summary>
    public async Task<List<string>> TranslateAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var chars = texts.Sum(t => t.Length);
        // Generous on a slow AVD CPU: 60 s plus 1 s per 40 characters, at most 10 minutes.
        var timeout = TimeSpan.FromSeconds(Math.Min(600, 60 + chars / 40.0));
        var answer = await RequestAsync(new JsonObject { ["op"] = "translate", ["texts"] = new JsonArray([.. texts.Select(t => (JsonNode?)JsonValue.Create(t))]), ["beam"] = 4 }, timeout, ct);
        var list = answer["texts"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToList();
        if (list is null || list.Count != texts.Count) throw new TestCaseTranslationException("The offline translator returned a different number of texts. Nothing was changed.");
        return list;
    }

    private async Task<JsonNode> RequestAsync(JsonObject request, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            var process = await EnsureStartedAsync(ct);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            string? line;
            try
            {
                await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), timer.Token);
                await process.StandardInput.FlushAsync(timer.Token);
                line = await process.StandardOutput.ReadLineAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                StopLocked("timeout");
                throw new TestCaseTranslationException($"The offline translator did not answer within {timeout.TotalSeconds:0} seconds and was stopped. Nothing was changed. Try fewer test cases at a time.");
            }
            catch (OperationCanceledException)
            {
                // The caller gave up (the tester left or reloaded the page) after the request was sent: the helper still writes its answer, and the
                // next request would read that stale line as its own ("returned a different number of texts" for every later translation until
                // the helper stopped, 2026-11-26). The helper is stopped so its answer is never read; the next request starts it again.
                StopLocked("cancelled");
                throw;
            }
            catch (IOException)
            {
                StopLocked("pipe closed");
                throw new TestCaseTranslationException("The offline translator stopped unexpectedly. Nothing was changed. Try again; if it keeps failing, see System Settings → Translation.");
            }
            if (line is null)
            {
                StopLocked("exited");
                throw new TestCaseTranslationException("The offline translator stopped unexpectedly (for example, out of memory). Nothing was changed.");
            }
            var answer = JsonNode.Parse(line) ?? throw new TestCaseTranslationException("The offline translator answered unexpectedly. Nothing was changed.");
            if (answer["ok"]?.GetValue<bool>() != true)
                throw new TestCaseTranslationException($"The offline translator could not translate: {answer["error"]?.GetValue<string>() ?? "unknown error"}. Nothing was changed.");
            ScheduleIdleStop();
            return answer;
        }
        catch (JsonException)
        {
            throw new TestCaseTranslationException("The offline translator answered unexpectedly. Nothing was changed.");
        }
        finally { _gate.Release(); }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } running) return running;
        StopLocked(null);
        var (bundle, problem) = Bundle();
        if (bundle is null) throw new TestCaseTranslationException(problem! + " You can write the translated text by hand.");
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var info = new ProcessStartInfo(bundle.PythonExe)
        {
            WorkingDirectory = bundle.Directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-I", "-X", "utf8", bundle.HelperScript, "--model", bundle.ModelDirectory, "--spm", bundle.SentencePieceModel, "--threads", threads.ToString() })
            info.ArgumentList.Add(a);
        // The helper gets no inherited Python settings, proxy variables or BirkNext secrets.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || k.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Anthropic", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("ManualTesting", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Backup", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        info.Environment["OMP_NUM_THREADS"] = threads.ToString();
        // CTranslate2's own CPU kernels instead of Intel MKL: measured 125 MB private memory instead of ~950 MB (MKL reserves a large pool), for
        // ~30 % slower translation (0.11 s instead of 0.08 s for three sentences). AVD sessions share memory, so memory wins.
        info.Environment["CT2_USE_MKL"] = "0";
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("no process"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(e, "The offline translation helper could not be started");
            throw new TestCaseTranslationException("The offline translator could not be started on this PC (Windows refused to run it; ask IT whether application control blocks runtime\\translation\\python\\python.exe). You can write the translated text by hand.");
        }
        _process = process;
        if (OperatingSystem.IsWindows()) { _job = JobHandle.TryCreate(MemoryLimitBytes); _job?.Add(process); }
        _stderrTail.Clear();
        _ = DrainErrorsAsync(process);
        var started = Stopwatch.StartNew();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(StartTimeout);
        string? first;
        try { first = await process.StandardOutput.ReadLineAsync(timer.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { first = null; }
        JsonNode? ready = null;
        try { ready = first is null ? null : JsonNode.Parse(first); } catch (JsonException) { }
        if (ready?["ready"]?.GetValue<bool>() != true)
        {
            var error = ready?["error"]?.GetValue<string>() ?? StartFailure(process, first, started.Elapsed);
            _log.LogWarning("The offline translation helper did not start: {Reason} Last helper output: {Stderr}", error, string.Join(" | ", _stderrTail));
            StopLocked("start failed");
            throw new TestCaseTranslationException(error + " You can write the translated text by hand.");
        }
        _log.LogInformation("Offline translation helper started (pid {Pid}, {Threads} threads, engine {Engine})", process.Id, threads, ready["engine"]?.ToJsonString());
        return process;
    }

    /// <summary>
    /// Why the helper did not report ready when it printed no error of its own: it stopped (exit code and its last error line), it answered
    /// something else, or it was still loading when the start time ran out. "Did not load" alone left testers and IT nothing to act on.
    /// </summary>
    private string StartFailure(Process process, string? first, TimeSpan elapsed)
    {
        var exited = false;
        try { exited = process.WaitForExit(500); } catch (InvalidOperationException) { }
        var last = _stderrTail.LastOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (exited)
        {
            int? code = null;
            try { code = process.ExitCode; } catch (InvalidOperationException) { }
            return StartFailureMessage(code, last, elapsed, timedOut: false, first);
        }
        return StartFailureMessage(null, last, elapsed, timedOut: first is null, first);
    }

    internal static string StartFailureMessage(int? exitCode, string? lastError, TimeSpan elapsed, bool timedOut, string? firstLine)
    {
        var detail = lastError is { Length: > 0 } e ? $" Last message from the translator: {(e.Length > 200 ? e[..200] + "…" : e)}" : "";
        if (timedOut)
            return $"The offline translation model was still loading after {StartTimeout.TotalSeconds:0} seconds and was stopped (the PC may be busy, or antivirus may be scanning the model files the first time they are used). Try again: the next start is usually quick.{detail}";
        if (exitCode is { } code)
        {
            var hex = unchecked((uint)code).ToString("X8");
            var meaning = hex switch
            {
                "C0000005" => " (access violation: the translator crashed while loading)",
                "C000001D" => " (illegal instruction: this PC's processor lacks an instruction the translator needs)",
                "C0000135" => " (a required DLL was not found)",
                "C0000142" => " (a DLL failed to initialise)",
                "C0000409" => " (the translator stopped itself after a security check)",
                "C0000417" => " (invalid parameter in the C runtime)",
                _ => "",
            };
            return $"The offline translator stopped while loading the model (exit code {code} / 0x{hex}{meaning}).{detail}";
        }
        return firstLine is { Length: > 0 } line
            ? $"The offline translator answered unexpectedly while loading: {(line.Length > 120 ? line[..120] + "…" : line)}"
            : $"The offline translation model did not load.{detail}";
    }

    private async Task DrainErrorsAsync(Process process)
    {
        try
        {
            var lines = 0;
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                _stderrTail.Enqueue(line.Length > 300 ? line[..300] : line);
                while (_stderrTail.Count > 20) _stderrTail.TryDequeue(out _);
                if (++lines <= 50) _log.LogDebug("offline translation helper: {Line}", line.Length > 300 ? line[..300] : line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void ScheduleIdleStop()
    {
        _idle?.Dispose();
        _idle = IdleStopAfter() is { } after ? _clock.CreateTimer(_ => _ = StopWhenIdleAsync(), null, after, Timeout.InfiniteTimeSpan) : null;
    }

    private async Task StopWhenIdleAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // busy: the next request schedules another idle stop
        try { StopLocked("idle"); } finally { _gate.Release(); }
    }

    private void StopLocked(string? reason)
    {
        _probed = null;
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.WriteLine("{\"op\":\"exit\"}"); process.StandardInput.Flush(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
                    if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
            if (reason is not null) _log.LogInformation("Offline translation helper stopped ({Reason})", reason);
        }
        _job?.Dispose();
        _job = null;
    }

    /// <summary>Whether a helper process runs now (tests, diagnostics).</summary>
    public int? RunningProcessId => _process is { HasExited: false } p ? p.Id : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _idle?.Dispose();
        await _gate.WaitAsync();
        try { StopLocked("BirkNext is stopping"); } finally { _gate.Release(); }
    }

    /// <summary>A Windows job object: kill-on-close (the helper never outlives the backend) and a per-process memory cap.</summary>
    private sealed class JobHandle : IDisposable
    {
        private IntPtr _handle;
        private JobHandle(IntPtr handle) => _handle = handle;

        public static JobHandle? TryCreate(long memoryLimit)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ | 0x100 /* PROCESS_MEMORY */ },
                ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimit,
            };
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, 9, ptr, (uint)length)) { CloseHandle(handle); return null; }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return new JobHandle(handle);
        }

        public void Add(Process process)
        {
            try { AssignProcessToJobObject(_handle, process.Handle); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }

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
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
