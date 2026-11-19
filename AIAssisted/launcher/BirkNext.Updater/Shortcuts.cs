namespace BirkNext.Updater;

/// <summary>
/// Start menu shortcuts for the tester (per user, no administrator rights): "BirkNext" starts the active version, "BirkNext Updater" opens the
/// stable updater copy. Rewritten on every activation, so the shortcut always starts the version the updater activated. Only for the default
/// per-user installation; tests and isolated installations leave the Start menu alone.
/// </summary>
public static class Shortcuts
{
    public static bool Enabled { get; set; }

    public static void Write(string startExe, string updaterExe, Action<string> log)
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BirkNext");
            Directory.CreateDirectory(folder);
            Create(Path.Combine(folder, "BirkNext.lnk"), startExe, "Start BirkNext");
            if (File.Exists(updaterExe)) Create(Path.Combine(folder, "BirkNext Updater.lnk"), updaterExe, "Upgrade BirkNext from a downloaded package");
        }
        catch (Exception e) { log($"Start menu shortcuts not written: {e.GetType().Name}: {e.Message}"); }
    }

    private static void Create(string link, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = description;
            shortcut.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
