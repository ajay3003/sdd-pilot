using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Updater;

/// <summary>One installed BirkNext version: its folder under the installations root, its identity and the database migrations it ships.</summary>
public sealed record InstalledVersion(string Folder, string BuildVersion, string Informational, string Channel, long? BuildId, string ApplicationVersion, string? SourceCommit,
    IReadOnlyList<string> Migrations, DateTimeOffset InstalledAt)
{
    public PackageVersion Version => PackageVersion.Parse(ApplicationVersion, Channel, BuildVersion, Informational, SourceCommit, BuildId?.ToString())!;

    public static InstalledVersion From(PackageInfo p, string folder, DateTimeOffset at) =>
        new(folder, p.Version.BuildVersion, p.Version.Informational, p.Version.Channel, p.Version.BuildId, p.Version.Application.ToString(3), p.Version.SourceCommit,
            p.Migrations, at);
}

/// <summary>Which version is active and which one it replaced (kept so an upgrade without database changes can be undone).</summary>
public sealed record InstallationState(InstalledVersion? Current, InstalledVersion? Previous);

/// <summary>The stages of one update, in order. A journal that is not Completed after a restart is an interrupted update.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdateStage { Validated, Staging, Staged, Stopping, Stopped, Activated, Started, Completed }

/// <summary>The minimal record of an update in progress: never a secret, only what recovery needs.</summary>
public sealed record UpdateJournal(string Id, UpdateStage Stage, string? FromBuild, string ToBuild, string? StagingFolder, string? CandidateFolder,
    IReadOnlyList<string> MigrationsAdded, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string? Message = null);

/// <summary>
/// The per-user installations root (default %LOCALAPPDATA%\BirkNext\Installations): one folder per installed build, a staging folder while an
/// update unpacks, installations.json (current and previous) and update-journal.json. The tester's data folder is elsewhere and is never
/// touched by the updater. State files are written atomically (temporary file, then replace).
/// </summary>
public sealed class InstallationStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Root { get; } = Path.GetFullPath(root);
    public string StateFile => Path.Combine(Root, "installations.json");
    public string JournalFile => Path.Combine(Root, "update-journal.json");
    public string TrustPolicyFile => Path.Combine(Root, "update-trust.json");
    public string LogFile => Path.Combine(Root, "logs", $"updater-{DateTime.Now:yyyyMMdd}.log");
    public string StableUpdater => Path.Combine(Root, "Update-BirkNext.exe");

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "Installations");

    public string FolderOf(string name) => Path.Combine(Root, name);
    public string NewStagingFolder() => Path.Combine(Root, $".staging-{Guid.NewGuid():N}");

    public InstallationState Read() => ReadJson<InstallationState>(StateFile) ?? new(null, null);
    public UpdateJournal? Journal() => ReadJson<UpdateJournal>(JournalFile);

    public void Write(InstallationState state) => WriteJson(StateFile, state);
    public void Write(UpdateJournal journal) => WriteJson(JournalFile, journal with { UpdatedAt = DateTimeOffset.UtcNow });
    public void ClearJournal() { if (File.Exists(JournalFile)) File.Delete(JournalFile); }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (JsonException) { return null; }
    }

    private void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Installed version folders other than the current and previous one (old versions the updater may remove when nothing runs them).</summary>
    public IEnumerable<string> Obsolete(InstallationState state) =>
        Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) != "logs"
                && File.Exists(Path.Combine(d, "Start-BirkNext.exe"))
                && !string.Equals(Path.GetFileName(d), state.Current?.Folder, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(d), state.Previous?.Folder, StringComparison.OrdinalIgnoreCase))
            : [];
}
