using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.TestPlanning.OfflineTranslation;

/// <summary>
/// The embedded offline translation bundle (runtime/translation of the Windows Tester Package, built by scripts/build-offline-translation.ps1):
/// its translation-manifest.json names every file with its SHA-256. A bundle is used only when every listed file is present and unchanged; a
/// missing or damaged bundle makes translation unavailable with the reason, and nothing is ever downloaded to repair it.
/// </summary>
public sealed record OfflineTranslationManifest
{
    public const int SupportedProtocol = 1;
    public int Format { get; init; }
    public int Protocol { get; init; }
    public EngineInfo Engine { get; init; } = new();
    public ModelInfo Model { get; init; } = new();
    public string Python { get; init; } = "";
    public string Helper { get; init; } = "";
    public string ModelDirectory { get; init; } = "";
    public string SentencePiece { get; init; } = "";
    public List<FileEntry> Files { get; init; } = [];

    public sealed record EngineInfo
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Tokenizer { get; init; } = "";
        [JsonPropertyName("python")] public string PythonVersion { get; init; } = "";
    }

    public sealed record ModelInfo
    {
        public string Id { get; init; } = "";
        public string Revision { get; init; } = "";
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";
        public string License { get; init; } = "";
        public string Attribution { get; init; } = "";
    }

    public sealed record FileEntry(string Path, string Sha256, long Size);
}

public sealed record OfflineTranslationBundle(string Directory, OfflineTranslationManifest Manifest)
{
    public const string ManifestFile = "translation-manifest.json";
    public const string DirectoryKey = "ManualTesting:Translation:Offline:Directory";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string PythonExe => Full(Manifest.Python);
    public string HelperScript => Full(Manifest.Helper);
    public string ModelDirectory => Full(Manifest.ModelDirectory);
    public string SentencePieceModel => Full(Manifest.SentencePiece);
    private string Full(string relative) => Path.GetFullPath(Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Where a bundle may be: the configured folder (the Tester Package launcher sets it), else the package layout beside the backend
    /// (runtime/translation two levels up), else the repository's Git LFS copy (AIAssisted/runtime/translation, found by walking up from the
    /// backend), else a developer build in %LOCALAPPDATA%\BirkNext\offline-translation. First existing wins.</summary>
    public static IReadOnlyList<string> Candidates(IConfiguration config) => Candidates(config, AppContext.BaseDirectory);

    public static IReadOnlyList<string> Candidates(IConfiguration config, string baseDirectory)
    {
        var list = new List<string>();
        if (config[DirectoryKey] is { Length: > 0 } configured) list.Add(configured);
        list.Add(Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "runtime", "translation")));
        // A source checkout: bin/Debug/net8.0 → BirkNext.Api → backend → AIAssisted/runtime/translation (any depth up to the drive root).
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "translation");
            if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(candidate, ManifestFile))) list.Add(candidate);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) list.Add(Path.Combine(local, "BirkNext", "offline-translation", "translation"));
        return list;
    }

    /// <summary>Reads and fully verifies the bundle in <paramref name="directory"/>: (bundle, null) or (null, the reason in plain words).</summary>
    public static (OfflineTranslationBundle? Bundle, string? Problem) Verify(string directory, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(directory, ManifestFile);
        if (!File.Exists(manifestPath)) return (null, "The offline translation model is not installed with this BirkNext (no translation-manifest.json).");
        if (IsLfsPointer(manifestPath)) return (null, LfsNotPulled);
        OfflineTranslationManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<OfflineTranslationManifest>(File.ReadAllText(manifestPath), Json); }
        catch (Exception e) when (e is JsonException or IOException) { return (null, "The offline translation package is damaged: its manifest cannot be read."); }
        if (manifest is not { Format: 1 } || manifest.Files.Count == 0) return (null, "The offline translation package is damaged: its manifest is not valid.");
        if (manifest.Protocol != OfflineTranslationManifest.SupportedProtocol)
            return (null, $"The offline translation package (protocol {manifest.Protocol}) does not match this BirkNext (protocol {OfflineTranslationManifest.SupportedProtocol}). Use the translation files of the same BirkNext package.");
        var root = Path.GetFullPath(directory);
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (null, "The offline translation package is damaged: its manifest names a file outside the package.");
            var info = new FileInfo(path);
            if (!info.Exists) return (null, $"The offline translation package is incomplete: {f.Path} is missing. Extract the whole BirkNext package again.");
            if (info.Length != f.Size && IsLfsPointer(path)) return (null, LfsNotPulled);
            if (info.Length != f.Size || !string.Equals(Sha(path), f.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, $"The offline translation package is damaged: {f.Path} has changed. Extract the whole BirkNext package again.");
        }
        var bundle = new OfflineTranslationBundle(root, manifest);
        foreach (var required in new[] { bundle.PythonExe, bundle.HelperScript, bundle.SentencePieceModel, Path.Combine(bundle.ModelDirectory, "model.bin") })
            if (!manifest.Files.Any(f => string.Equals(bundle.Full(f.Path), required, StringComparison.OrdinalIgnoreCase)))
                return (null, $"The offline translation package is damaged: {Path.GetFileName(required)} is not part of it.");
        return (bundle, null);
    }

    /// <summary>The bundle is in the repository through Git LFS but this clone holds only the pointer files.</summary>
    public const string LfsNotPulled = "The offline translation model is in the repository (Git LFS) but was not downloaded with this clone. Run 'git lfs install' and 'git lfs pull' once, then select Check again.";

    /// <summary>A Git LFS pointer file (a small text stub instead of the real content).</summary>
    public static bool IsLfsPointer(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024) return false;
        try { return File.ReadAllText(path).StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private static string Sha(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
