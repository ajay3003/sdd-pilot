using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BirkNext.Updater;

/// <summary>A package the updater refuses, with the reason in the tester's words.</summary>
public sealed class PackageRejectedException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>What the updater learned from a ZIP before anything was changed.</summary>
public sealed record PackageInfo(
    string ZipPath, string Prefix, PackageVersion Version, int UpdateContract, IReadOnlyList<string> Migrations, int FileCount, long UncompressedBytes,
    IReadOnlyDictionary<string, string> FileHashes);

/// <summary>
/// Reads a BirkNext Windows tester ZIP without extracting it, then extracts it into a staging folder while checking every file. A ZIP is untrusted
/// input: entry names are validated (no absolute or drive paths, no "..", no alternate data streams, no Windows device names, no trailing dots or
/// spaces, no case-insensitive duplicates, no symbolic links), sizes and compression ratios are bounded, and the files must be exactly the ones the
/// package manifest lists, each with its SHA-256. Nothing outside the staging folder is ever written.
/// </summary>
public static class PackageInspector
{
    public const int SupportedUpdateContract = 1;
    public const int MaxEntries = 20_000;
    public const long MaxUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private const long RatioCheckFloor = 1024 * 1024;
    private const int MaxRatio = 200;

    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        "Start-BirkNext.exe", "Update-BirkNext.exe", "build-manifest.json", "package-manifest.json", "config/runtime-manifest.json",
        "AIAssisted/backend/BirkNext.Api.exe", "AIAssisted/frontend/wwwroot/index.html",
    ];

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
        "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
    };

    /// <summary>A safe relative path ("a/b/c.txt") or the reason it is not.</summary>
    public static string? Problem(string name)
    {
        if (name.Length == 0 || name.Length > 400) return "an empty or overlong name";
        var normalized = name.Replace('\\', '/');
        // Absolute first, so a drive path is reported as what it is (its ':' would otherwise read as a forbidden character).
        if (normalized.StartsWith('/') || (normalized.Length >= 2 && normalized[1] == ':')) return "an absolute path";
        if (name.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) return "characters Windows does not allow in file names";
        foreach (var segment in normalized.TrimEnd('/').Split('/'))
        {
            if (segment is "" or "." or "..") return "a path that leaves the package folder";
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return "a name ending in a dot or space";
            if (DeviceNames.Contains(segment.Split('.')[0])) return "a Windows device name";
        }
        return null;
    }

    private static bool IsSymlink(ZipArchiveEntry e) => ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    public static PackageInfo Inspect(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new PackageRejectedException("The selected file does not exist.");
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException e) { throw new PackageRejectedException("The selected file is not a ZIP archive, or it is damaged.", e.Message); }
        catch (IOException e) { throw new PackageRejectedException("The selected file cannot be read. Close any program that has it open.", e.Message); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new PackageRejectedException("The archive has far more files than a BirkNext package.", $"{zip.Entries.Count} entries");
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (Problem(e.FullName) is { } problem) throw new PackageRejectedException($"The archive contains {problem}. It is not a BirkNext package.", e.FullName);
                if (IsSymlink(e)) throw new PackageRejectedException("The archive contains a link to another location. It is not a BirkNext package.", e.FullName);
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                var name = e.FullName.Replace('\\', '/');
                if (!files.TryAdd(name, e)) throw new PackageRejectedException("The archive contains the same file twice. It is not a BirkNext package.", name);
                total += e.Length;
                if (total > MaxUncompressedBytes) throw new PackageRejectedException("The archive would unpack to far more than a BirkNext package.", $"> {MaxUncompressedBytes} bytes");
                if (e.Length > RatioCheckFloor && e.CompressedLength > 0 && e.Length / e.CompressedLength > MaxRatio)
                    throw new PackageRejectedException("The archive contains a file compressed far more than any real file. It is not a BirkNext package.", name);
            }

            // The package may sit in one top-level folder (as the pipeline archive does) or at the root.
            var prefix = files.ContainsKey("Start-BirkNext.exe") ? ""
                : files.Keys.Where(k => k.EndsWith("/Start-BirkNext.exe", StringComparison.OrdinalIgnoreCase) && k.Count(c => c == '/') == 1).Select(k => k[..(k.IndexOf('/') + 1)]).SingleOrDefault()
                ?? throw new PackageRejectedException("This is not a BirkNext Windows tester package: Start-BirkNext.exe is missing.");
            if (files.Keys.Any(k => !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new PackageRejectedException("The archive contains files outside the BirkNext package folder.");
            var missing = RequiredFiles.Where(r => !files.ContainsKey(prefix + r)).ToList();
            if (missing.Count > 0)
                throw new PackageRejectedException(missing.Contains("Update-BirkNext.exe")
                    ? "This package is too old for the updater (it does not contain Update-BirkNext.exe). Use a newer package from the pipeline."
                    : $"This BirkNext package is incomplete: {string.Join(", ", missing)} missing.", string.Join(", ", missing));

            JsonElement Manifest(string relative)
            {
                var e = files[prefix + relative];
                if (e.Length > MaxManifestBytes) throw new PackageRejectedException($"The package's {relative} is too large.");
                try { using var s = e.Open(); using var doc = JsonDocument.Parse(s); return doc.RootElement.Clone(); }
                catch (JsonException ex) { throw new PackageRejectedException($"The package's {relative} cannot be read.", ex.Message); }
            }
            static string? Str(JsonElement e, params string[] path)
            {
                foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
                return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Number ? e.GetRawText() : null;
            }

            var build = Manifest("build-manifest.json");
            if (Str(build, "product") != "BirkNext" || Str(build, "distribution", "kind") != "WindowsTesterPackage")
                throw new PackageRejectedException("This archive is not a BirkNext Windows tester package.", "build-manifest product/distribution");
            if (Str(build, "distribution", "architecture") is not "x64") throw new PackageRejectedException("This package is not for 64-bit Windows (x64).");
            if (Str(build, "distribution", "runtimeProfile") is not "TesterNative") throw new PackageRejectedException("This package is not the Windows tester edition of BirkNext.");
            var version = PackageVersion.Parse(Str(build, "applicationVersion"), Str(build, "channel"), Str(build, "buildVersion"), Str(build, "informationalVersion"),
                Str(build, "sourceCommit"), Str(build, "pipelineRunId")) ?? throw new PackageRejectedException("The package's version information is missing or invalid.");

            var runtime = Manifest("config/runtime-manifest.json");
            var contract = runtime.TryGetProperty("updateContract", out var c) && c.TryGetInt32(out var n) ? n : 0;
            if (contract < 1) throw new PackageRejectedException("This package is too old for the updater. Use a newer package from the pipeline.", "no updateContract");
            if (contract > SupportedUpdateContract)
                throw new PackageRejectedException("This package needs a newer updater. Extract it once and run its own Update-BirkNext.exe.", $"updateContract {contract}");

            var package = Manifest("package-manifest.json");
            if (!package.TryGetProperty("fileHashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
                throw new PackageRejectedException("The package's file list is missing.");
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hashes.EnumerateArray())
            {
                if (Str(h, "path") is not { } path || Str(h, "sha256") is not { Length: 64 } sha || Problem(path) is not null || !expected.TryAdd(path.Replace('\\', '/'), sha.ToLowerInvariant()))
                    throw new PackageRejectedException("The package's file list is damaged.");
            }
            var inArchive = files.Keys.Select(k => k[prefix.Length..]).Where(k => !k.Equals("package-manifest.json", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notListed = inArchive.Where(k => !expected.ContainsKey(k)).ToList();
            var absent = expected.Keys.Where(k => !inArchive.Contains(k)).ToList();
            if (notListed.Count > 0) throw new PackageRejectedException("The archive contains files that are not part of this BirkNext package.", string.Join(", ", notListed.Take(5)));
            if (absent.Count > 0) throw new PackageRejectedException($"This BirkNext package is incomplete: {absent.Count} file(s) missing.", string.Join(", ", absent.Take(5)));

            var migrations = package.TryGetProperty("migrations", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => Str(x, "id")).Where(x => x is not null).Cast<string>().ToList() : [];
            return new(Path.GetFullPath(zipPath), prefix, version, contract, migrations, expected.Count, total, expected);
        }
    }

    /// <summary>
    /// Files of an already installed copy of the inspected package that are missing or differ from the package's list (empty = the folder is exactly
    /// this package). Used when the version being installed is still installed as the previous version (after a rollback): it is re-verified and
    /// reused instead of unpacked again.
    /// </summary>
    public static IReadOnlyList<string> Differences(PackageInfo info, string folder, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var differences = new List<string>();
        var done = 0;
        foreach (var (relative, expected) in info.FileHashes)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) differences.Add(relative);
            else
            {
                using var stream = File.OpenRead(path);
                if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != expected) differences.Add(relative);
            }
            progress?.Invoke(++done, info.FileHashes.Count);
            if (differences.Count >= 20) break;
        }
        return differences;
    }

    /// <summary>
    /// Extracts the inspected package into <paramref name="staging"/> (which must be empty or absent), hashing every file as it is written and
    /// stopping at the first difference from the manifest. Never writes outside the staging folder.
    /// </summary>
    public static void Extract(PackageInfo info, string staging, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("The staging folder is not empty.");
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(info.ZipPath);
        var done = 0;
        var buffer = new byte[1 << 16];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var relative = name[info.Prefix.Length..];
            // package-manifest.json is the list itself (it cannot contain its own hash); every other file must be listed.
            var isList = relative.Equals("package-manifest.json", StringComparison.OrdinalIgnoreCase);
            if (!isList && !info.FileHashes.ContainsKey(relative)) throw new PackageRejectedException("The archive changed after it was checked. Select it again.", relative);
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PackageRejectedException("The archive contains a path that leaves the package folder.", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var source = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long written = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    // The declared size is only a header; the real decompressed bytes are bounded too.
                    if (written > entry.Length || written > MaxUncompressedBytes) throw new PackageRejectedException("A file in the archive is larger than it claims.", relative);
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (info.FileHashes.TryGetValue(relative, out var expected) && actual != expected)
                    throw new PackageRejectedException("A file in the package is damaged (its checksum does not match). Download the package again.", relative);
            }
            progress?.Invoke(++done, info.FileCount + 1);
        }
    }
}
