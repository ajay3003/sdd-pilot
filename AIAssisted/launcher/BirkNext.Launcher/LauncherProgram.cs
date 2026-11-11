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
            Console.WriteLine("  Enter it when the browser asks to connect (once per browser, and again after BirkNext restarts).");
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

/// <summary>Start-BirkNext.exe. Exit codes: 0 ok, 2 startup failure, 3 already running, 64 usage.</summary>
public static class LauncherProgram
{
    public static async Task<int> Main(string[] args)
    {
        var ui = new ConsoleUi();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        // Closing the console window (CTRL_CLOSE_EVENT) arrives as SIGHUP: stop cleanly in the few seconds Windows allows.
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; stop.Cancel(); });
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        try
        {
            var options = LauncherOptions.Parse(args);
            var code = await new TesterLauncher(options, ui).RunAsync(stop.Token);
            if (code != 0 && options.Command == "start" && !Console.IsInputRedirected && options.ExitAfterSeconds is null)
            {
                Console.WriteLine();
                Console.WriteLine("  Press Enter to close this window.");
                Console.ReadLine();
            }
            return code;
        }
        catch (LauncherException e)
        {
            ui.Failure(e.Message, e.Action, "(no log yet)");
            return e.ExitCode;
        }
    }
}
