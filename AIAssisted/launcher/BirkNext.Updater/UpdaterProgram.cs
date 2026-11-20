using System.Runtime.InteropServices;

namespace BirkNext.Updater;

/// <summary>
/// Update-BirkNext.exe. Exit codes: 0 done, 1 refused or failed (nothing changed unless the message says so), 3 cancelled, 64 usage.
/// <code>
/// Update-BirkNext.exe                     choose a downloaded package, check it, upgrade, start BirkNext
/// Update-BirkNext.exe &lt;package.zip&gt;      the same with that package
/// Update-BirkNext.exe --validate &lt;zip&gt;    check only; nothing is installed or stopped
/// Update-BirkNext.exe --status            installed and previous version, unfinished update
/// Update-BirkNext.exe --recover           resolve an interrupted update (also done automatically on every start)
/// Update-BirkNext.exe --rollback          back to the previous version (only when the database was not changed)
/// Options: --yes (no questions; for scripted use), --no-start, --no-browser, --data-dir &lt;folder&gt;, --installations-dir &lt;folder&gt;
/// </code>
/// </summary>
public static class UpdaterProgram
{
    private sealed record Options(string Command, string? Zip, bool Yes, bool NoStart, bool NoBrowser, string? DataDir, string? InstallationsDir);

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try { options = Parse(args); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 64; }

        var store = new InstallationStore(options.InstallationsDir ?? InstallationStore.DefaultRoot);
        var instance = new InstanceControl(options.DataDir ?? InstanceControl.DefaultDataRoot);
        Shortcuts.Enabled = options.InstallationsDir is null;
        var engine = new UpdateEngine(store, instance);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { Console.Title = "BirkNext Updater"; } catch (IOException) { }

        Console.WriteLine("BirkNext Updater");
        Console.WriteLine();
        // An interrupted update is resolved first, every time (the journal says how far it got).
        if (store.Journal() is not null)
        {
            var recovered = engine.Recover();
            Report(recovered);
            if (options.Command == "recover") return Done(recovered.Ok ? 0 : 1, options);
            Console.WriteLine();
        }
        else if (options.Command == "recover") { Console.WriteLine("  No unfinished update."); return 0; }

        var state = store.Read();
        Line("Installed", state.Current is { } c ? $"{c.Version.Display}" : "not installed with the updater yet");
        if (instance.LastUsedBuild() is { } used && used.Informational != state.Current?.Informational) Line("Your data last used", used.Display);
        if (state.Previous is { } p) Line("Previous version", p.Version.Display);
        Line("BirkNext", instance.IsRunning ? $"running{(instance.RunningUrl() is { } url ? $" at {url}" : "")}" : "not running");
        Console.WriteLine();
        switch (options.Command)
        {
            case "status": return 0;
            case "rollback":
                var rolled = await engine.RollbackAsync(cts.Token);
                Report(rolled);
                return Done(rolled.Ok ? 0 : 1, options);
        }

        var zip = options.Zip;
        if (zip is null)
        {
            Console.WriteLine("  Select the BirkNext package you downloaded from the Azure DevOps pipeline (BirkNext-Tester-Windows-x64-….zip).");
            zip = FileDialog.PickZip();
            if (zip is null) { Console.WriteLine("  No package selected. Nothing was changed."); return Done(3, options); }
        }
        Line("Package", Path.GetFileName(zip));

        UpdatePlan plan;
        Phase(1, "Checking package");
        try { plan = engine.Plan(zip); }
        catch (PackageRejectedException e)
        {
            Failure(e.Message, "Download the package again from the pipeline and select the downloaded ZIP. Nothing was changed.", store.LogFile);
            store.Log($"Rejected {zip}: {e.Message} {e.Detail}");
            return Done(1, options);
        }
        Phase(2, "Checking compatibility");
        Line("New version", plan.Package.Version.Display);
        Line("Source", plan.TrustDetail);
        Line("Files", $"{plan.Package.FileCount} files, all listed with their SHA-256");
        Line("Translation", plan.Package.OfflineTranslation is { } translation ? $"offline, {translation}" : "not part of this package");
        if (plan.Blocker is { } blocker)
        {
            Failure(blocker, "Nothing was changed.", store.LogFile);
            store.Log($"Refused {plan.Package.Version.BuildVersion}: {blocker}");
            return Done(1, options);
        }
        Console.WriteLine();
        Console.WriteLine($"  {VersionRules.Explain(plan.Path, plan.Installed?.Version, plan.Package.Version)}");
        if (plan.BirkNextRunning) Console.WriteLine("  BirkNext is running: it will be stopped and started again.");
        if (plan.ChangesDatabase)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  This version updates the database ({plan.MigrationsAdded.Count} change(s)). Your data is backed up and the backup is verified before the update.");
            Console.WriteLine($"  After this, the previous version can no longer open your data (going back means restoring that backup).");
            Console.ResetColor();
        }
        else if (plan.Installed is not null) Console.WriteLine("  No database changes: the previous version stays installed and you can go back with --rollback.");
        else Console.WriteLine("  On its first start BirkNext backs up your existing data (if any) before it updates the database.");
        Console.WriteLine("  Your projects, sources, test cases, settings and other data stay where they are. The updater never resets them.");
        if (options.Command == "validate") { Console.WriteLine(); Console.WriteLine("  The package can be installed. Nothing was changed."); return 0; }

        if (!options.Yes && !Confirm(plan.ChangesDatabase ? "Upgrade BirkNext and update the database?" : "Upgrade BirkNext?"))
        {
            Console.WriteLine("  Cancelled. Nothing was changed.");
            return Done(3, options);
        }

        var progress = new ConsoleProgress();
        var result = await engine.ApplyAsync(plan, progress, customDataRoot: options.DataDir is not null, startAfter: !options.NoStart, options.NoBrowser, cts.Token);
        Console.WriteLine();
        Report(result);
        return Done(result.Ok ? 0 : 1, options);
    }

    private static Options Parse(string[] args)
    {
        string command = "update";
        string? zip = null, data = null, installs = null;
        bool yes = false, noStart = false, noBrowser = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--validate": command = "validate"; break;
                case "--status": command = "status"; break;
                case "--recover": command = "recover"; break;
                case "--rollback": command = "rollback"; break;
                case "--yes": yes = true; break;
                case "--no-start": noStart = true; break;
                case "--no-browser": noBrowser = true; break;
                case "--data-dir": data = Path.GetFullPath(Next()); break;
                case "--installations-dir": installs = Path.GetFullPath(Next()); break;
                case var a when a.StartsWith("--", StringComparison.Ordinal): throw new ArgumentException($"Unknown option {a}. Run Update-BirkNext.exe --status for the installed version.");
                default: zip = zip is null ? Path.GetFullPath(args[i]) : throw new ArgumentException("Select one package."); break;
            }
        }
        if (command == "validate" && zip is null) throw new ArgumentException("--validate needs the package: Update-BirkNext.exe --validate <package.zip>");
        return new(command, zip, yes, noStart, noBrowser, data, installs);
    }

    private static int phase;
    private static void Phase(int n, string text) { phase = n; Console.WriteLine($"  {n}. {text}…"); }
    private static void Line(string label, string value) => Console.WriteLine($"  {label + ":",-20} {value}");

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write($"  {question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static void Report(UpdateResult r)
    {
        Console.ForegroundColor = r.Ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {r.Message}");
        Console.ResetColor();
        if (r.Url is { } url) Console.WriteLine($"  Open {url} (the BirkNext window shows the connection code).");
        if (r.Action is { } action) Console.WriteLine($"  What to do: {action}");
    }

    private static void Failure(string message, string action, string log)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
        Console.WriteLine($"  What to do: {action}");
        Console.WriteLine($"  Log: {log}");
    }

    /// <summary>A double-clicked updater keeps its window open so the result can be read.</summary>
    private static int Done(int code, Options options)
    {
        if (!options.Yes && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("  Press Enter to close this window.");
            Console.ReadLine();
        }
        return code;
    }

    private sealed class ConsoleProgress : IUpdateProgress
    {
        private int _last = -1;
        public void Phase(string text) { _last = -1; UpdaterProgram.Phase(++phase, text); }
        public void Fraction(int done, int total)
        {
            var pct = total == 0 ? 100 : done * 100 / total;
            if (pct / 10 == _last) return;
            _last = pct / 10;
            Console.WriteLine($"     {pct}% ({done} of {total} files checked)");
        }
    }
}

/// <summary>The Windows file dialog ("Select BirkNext ZIP"), opened in the Downloads folder. No WinForms: comdlg32 on its own STA thread.</summary>
public static class FileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize; public IntPtr hwndOwner; public IntPtr hInstance; public string lpstrFilter; public string? lpstrCustomFilter; public int nMaxCustFilter;
        public int nFilterIndex; public IntPtr lpstrFile; public int nMaxFile; public string? lpstrFileTitle; public int nMaxFileTitle; public string? lpstrInitialDir;
        public string lpstrTitle; public int Flags; public short nFileOffset; public short nFileExtension; public string? lpstrDefExt; public IntPtr lCustData;
        public IntPtr lpfnHook; public string? lpTemplateName; public IntPtr pvReserved; public int dwReserved; public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetOpenFileNameW(ref OpenFileName ofn);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    private const int OfnFileMustExist = 0x1000, OfnPathMustExist = 0x800, OfnNoChangeDir = 0x8, OfnExplorer = 0x80000;

    public static string? PickZip()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? result = null;
        var thread = new Thread(() =>
        {
            const int max = 4096;
            var buffer = Marshal.AllocHGlobal(max * 2);
            try
            {
                Marshal.Copy(new byte[max * 2], 0, buffer, max * 2);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(), hwndOwner = GetConsoleWindow(), lpstrFilter = "BirkNext package (*.zip)\0*.zip\0", lpstrFile = buffer,
                    nMaxFile = max, lpstrInitialDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    lpstrTitle = "Select BirkNext ZIP", Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer, lpstrDefExt = "zip",
                };
                if (GetOpenFileNameW(ref ofn)) result = Marshal.PtrToStringUni(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
