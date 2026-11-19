using System.Runtime.InteropServices;

namespace BirkNext.Launcher;

/// <summary>What the tester sees: short steps, one ready message, and actionable errors (details stay in the log).</summary>
public interface IConsoleUi
{
    void Title(string title);
    void Step(string step);
    void Ready(string url, string? connectionCode, string logFile);
    void Done(string message);
    void Failure(string message, string action, string logFile);
    void Line(string text);
}

public sealed class ConsoleUi : IConsoleUi
{
    public void Title(string title)
    {
        try { Console.Title = title; } catch (IOException) { }
        Console.WriteLine(title);
        Console.WriteLine("BirkNext is preparing your local environment.");
        Console.WriteLine();
    }

    public void Step(string step) => Console.WriteLine($"  • {step}…");

    public void Ready(string url, string? connectionCode, string logFile)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Ready. BirkNext is running at {url}");
        Console.ResetColor();
        if (connectionCode is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Connection code: {connectionCode}");
            Console.ResetColor();
            Console.WriteLine("  The browser opened by this window is connected automatically. In another browser, enter this code when it asks to connect.");
        }
        Console.WriteLine();
        Console.WriteLine("  Keep this window open while you use BirkNext. Press Ctrl+C or close the window to stop BirkNext.");
        Console.WriteLine($"  Log: {logFile}");
    }

    public void Done(string message) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"  {message}"); Console.ResetColor(); }

    public void Failure(string message, string action, string logFile)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
        Console.WriteLine($"  What to do: {action}");
        Console.WriteLine($"  Details: {logFile}");
        Console.WriteLine("  To send the details to the BirkNext owner: Start-BirkNext.exe --diagnostics");
    }

    public void Line(string text) => Console.WriteLine(text);
}

/// <summary>
/// Console QuickEdit mode: a click into the console window starts a text selection, and from then on every write to the console blocks until a key
/// is pressed. A launcher that writes its progress would stop in the middle of starting BirkNext and look hung. QuickEdit is switched off for this
/// window (selection with the mouse is not needed; the log file holds every detail). Windows only; a no-op without a console.
/// </summary>
public static class ConsoleMode
{
    private const int StdInput = -10;
    private const uint EnableQuickEdit = 0x0040, EnableExtendedFlags = 0x0080;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>The input mode without QuickEdit (extended flags must be set for the change to take effect). Pure.</summary>
    public static uint WithoutQuickEdit(uint mode) => (mode & ~EnableQuickEdit) | EnableExtendedFlags;

    public static bool DisableQuickEdit()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return false;
        try
        {
            var handle = GetStdHandle(StdInput);
            return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, WithoutQuickEdit(mode));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

/// <summary>Start-BirkNext.exe. Exit codes: 0 ok, 2 startup failure, 3 already running, 64 usage, 130 start cancelled (Ctrl+C) during the package check.</summary>
public static class LauncherProgram
{
    public static async Task<int> Main(string[] args)
    {
        ConsoleMode.DisableQuickEdit();
        var ui = new ConsoleUi();
        // Never disposed: a close handler may still be waiting on it while Main returns.
        var shutdown = new ShutdownCoordinator();
        // Ctrl+C / Ctrl+Break: the launcher keeps running and stops in order.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Request(ShutdownReason.CtrlC); };
        // Closing the console window (CTRL_CLOSE_EVENT → SIGHUP), logoff and shutdown (→ SIGTERM): Windows terminates this process as soon as
        // the handler returns (about 5 s at most). Returning at once — as before 2026-11-19 — killed the launcher before its stop ran, leaving
        // the local database running. The handler now waits for the stop to complete; whatever is still running after the budget is ended by
        // the job object when Windows ends the launcher.
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; shutdown.RequestAndWait(ShutdownReason.WindowClosed, ShutdownCoordinator.CloseBudget); });
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; shutdown.RequestAndWait(ShutdownReason.LogoffOrShutdown, ShutdownCoordinator.CloseBudget); });
        LauncherOptions? options = null;
        try
        {
            options = LauncherOptions.Parse(args);
            var code = await new TesterLauncher(options, ui, shutdown).RunAsync(shutdown.Token);
            shutdown.MarkCompleted();
            KeepWindowOpenOnFailure(code, options, shutdown);
            return code;
        }
        catch (LauncherException e)
        {
            // Failures before the log exists (usage, package manifest, data folder not writable): same message format, and the window stays
            // open so a double-clicked start does not just flash and vanish.
            shutdown.MarkCompleted();
            ui.Failure(e.Message, e.Action, "(no log yet)");
            KeepWindowOpenOnFailure(e.ExitCode, options, shutdown);
            return e.ExitCode;
        }
        finally { shutdown.MarkCompleted(); }
    }

    private static void KeepWindowOpenOnFailure(int code, LauncherOptions? options, ShutdownCoordinator shutdown)
    {
        if (code == 0 || shutdown.Hurried || options is not { Command: "start", ExitAfterSeconds: null } || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.WriteLine("  Press Enter to close this window.");
        Console.ReadLine();
    }
}
