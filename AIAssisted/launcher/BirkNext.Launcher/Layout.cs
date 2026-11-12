using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BuildInfo;

namespace BirkNext.Launcher;

/// <summary>A launcher failure a tester can act on: a short message, a safe next step, and technical detail kept for the log.</summary>
public sealed class LauncherException(string message, string action, string? technical = null, int exitCode = 2) : Exception(message)
{
    public string Action { get; } = action;
    public string? Technical { get; } = technical;
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// The package's own runtime description (config/runtime-manifest.json), written by the package build from pinned inputs. It names the default
/// runtime profile of the distribution and the bundled components with their versions and hashes; nothing in it is a secret.
/// </summary>
public sealed record RuntimeManifest
{
    public int Format { get; init; }
    public string Distribution { get; init; } = "";
    public string DefaultRuntimeProfile { get; init; } = "";
    public BundledPostgres PostgreSql { get; init; } = new();
    public BundledK6 K6 { get; init; } = new();
    public string? DotNetRuntime { get; init; }

    public sealed record BundledPostgres
    {
        public string Version { get; init; } = "";
        public int Major { get; init; }
        public string Directory { get; init; } = "runtime/postgresql";
    }

    public sealed record BundledK6
    {
        public string Version { get; init; } = "";
        public string Executable { get; init; } = "runtime/k6/k6.exe";
        public string Sha256 { get; init; } = "";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static RuntimeManifest Read(string path)
    {
        if (!File.Exists(path))
            throw new LauncherException("This BirkNext package is incomplete: its runtime description is missing.", "Download the BirkNext Tester Package again and extract the whole archive.",
                $"Missing {path}");
        RuntimeManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(path), Json); }
        catch (JsonException e) { throw new LauncherException("This BirkNext package is damaged: its runtime description cannot be read.", "Download the BirkNext Tester Package again.", e.Message); }
        if (manifest is null || manifest.Format != 1 || manifest.PostgreSql.Major < 10 || string.IsNullOrWhiteSpace(manifest.K6.Sha256) || manifest.K6.Sha256.Length != 64)
            throw new LauncherException("This BirkNext package is damaged: its runtime description is not valid.", "Download the BirkNext Tester Package again.", $"Invalid {path}");
        return manifest;
    }
}

/// <summary>Where the extracted (read-only, replaceable) package keeps its parts.</summary>
public sealed class PackageLayout(string root, RuntimeManifest manifest)
{
    public string Root { get; } = root;
    public RuntimeManifest Manifest { get; } = manifest;
    public string Backend => Path.Combine(Root, "AIAssisted", "backend");
    public string BackendExe => Path.Combine(Backend, "BirkNext.Api.exe");
    public string Wwwroot => Path.Combine(Root, "AIAssisted", "frontend", "wwwroot");
    public string PostgresBin => Path.Combine(Root, Manifest.PostgreSql.Directory.Replace('/', Path.DirectorySeparatorChar), "bin");
    public string K6Exe => Path.Combine(Root, Manifest.K6.Executable.Replace('/', Path.DirectorySeparatorChar));
    public string PackageManifest => Path.Combine(Root, "package-manifest.json");
    public string BuildManifest => Path.Combine(Root, "build-manifest.json");
    public string Tool(string name) => Path.Combine(PostgresBin, name + ".exe");

    public static string RuntimeManifestPath(string root) => Path.Combine(root, "config", "runtime-manifest.json");

    /// <summary>The package's build identity (informational version) from build-manifest.json; "unknown" when absent.</summary>
    public string BuildVersion()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(BuildManifest));
            return doc.RootElement.TryGetProperty("informationalVersion", out var v) && v.GetString() is { Length: > 0 } s ? s : "unknown";
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return "unknown"; }
    }
}

/// <summary>
/// The per-user data of the TesterNative profile, outside the package (an upgrade replaces the package, never this folder). The default is
/// %LOCALAPPDATA%\BirkNext\TesterNative — a profile-specific folder, so it can never be the developer setup's data.
/// </summary>
public sealed class DataLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Database => Path.Combine(Root, "database", "postgres-data");
    public string DatabaseParent => Path.Combine(Root, "database");
    public string Config => Path.Combine(Root, "config");
    public string Logs => Path.Combine(Root, "logs");
    public string Diagnostics => Path.Combine(Root, "diagnostics");
    public string Backups => Path.Combine(Root, "backups");
    public string RuntimeState => Path.Combine(Root, "runtime-state");
    public string Temporary => Path.Combine(Root, "temporary");
    public string AppData => Path.Combine(Root, "app-data");
    public string ClusterFile => Path.Combine(Config, "cluster.json");
    public string SecretsFile => Path.Combine(Config, "db-credentials.dat");
    public string PortsFile => Path.Combine(Config, "ports.json");
    public string ProfileFile => Path.Combine(Config, "runtime-profile.json");
    public string InitializationFile => Path.Combine(RuntimeState, "initialization.json");
    public string SessionFile => Path.Combine(RuntimeState, "session.json");
    public string StopRequestFile => Path.Combine(RuntimeState, "stop-request.json");
    public string LastRunFile => Path.Combine(RuntimeState, "last-run.json");
    public string LastErrorFile => Path.Combine(RuntimeState, "last-startup-error.json");

    public static string Default(RuntimeProfile profile) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", profile.ToString());

    /// <summary>Creates the data folders. A folder the tester cannot write to (read-only location, managed folder, a file in the way) is a
    /// plain-language startup failure, never an unhandled exception: this runs before the log exists, so the message is all the tester gets.</summary>
    public void Create()
    {
        try
        {
            foreach (var d in new[] { Root, DatabaseParent, Config, Logs, Diagnostics, Backups, RuntimeState, Temporary, AppData })
                Directory.CreateDirectory(d);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new LauncherException($"BirkNext cannot write to its data folder: {Root}",
                "Make sure your Windows user can write to that folder, or start BirkNext with another data folder (Start-BirkNext.exe --data-dir <folder>).",
                $"{e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>Small JSON state files, written atomically (temp + replace) so an interrupted write never leaves half a file.</summary>
public static class StateFile
{
    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), RuntimeManifest.Json) : null; }
        catch (JsonException) { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, RuntimeManifest.Json));
        File.Move(temp, path, overwrite: true);
    }
}
