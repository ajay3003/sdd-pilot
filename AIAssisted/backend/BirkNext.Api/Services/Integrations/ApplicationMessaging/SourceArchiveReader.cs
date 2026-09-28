using System.IO.Compression;
using System.Security.Cryptography;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

/// <summary>One analyzable file of an uploaded source archive. Held in memory for the analysis only; never persisted.</summary>
public sealed record SourceFile(string Path, string Content);

/// <summary>
/// Reads an uploaded source archive (zip) in memory, bounded. Only C# sources, project files and central package files are read —
/// appsettings, secrets files and binaries are skipped, so no configuration value (connection string, SAS key) ever enters the analysis.
/// Nothing is extracted to disk; entry paths are only used as labels, so path traversal has no effect.
/// </summary>
public static class SourceArchiveReader
{
    public const long MaxArchiveBytes = 50 * 1024 * 1024;
    private const int MaxEntries = 20_000;
    private const long MaxFileBytes = 1024 * 1024;
    private const long MaxTotalBytes = 100 * 1024 * 1024;
    private static readonly string[] SkippedSegments = ["bin", "obj", "node_modules", ".git", ".vs", "migrations"];

    public static (SourceArchive? Archive, List<SourceFile> Files, string? Error) Read(string fileName, byte[] bytes)
    {
        var name = System.IO.Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        if (bytes.Length == 0) return (null, [], $"{name} is empty.");
        if (bytes.Length > MaxArchiveBytes) return (null, [], $"{name} is larger than the {MaxArchiveBytes / (1024 * 1024)} MB limit.");
        var files = new List<SourceFile>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return (null, [], $"{name} has more than {MaxEntries} entries.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!IsAnalyzable(path) || entry.Length > MaxFileBytes) continue;
                total += entry.Length;
                if (total > MaxTotalBytes) return (null, [], $"{name} has more than {MaxTotalBytes / (1024 * 1024)} MB of source to analyze.");
                using var reader = new StreamReader(entry.Open());
                files.Add(new SourceFile(path, reader.ReadToEnd()));
            }
        }
        catch (InvalidDataException) { return (null, [], $"{name} is not a valid zip archive."); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (new SourceArchive(name, sha, files.Count), files, null);
    }

    /// <summary>C# sources and MSBuild project/package files only; build output, migrations and dependencies are skipped.</summary>
    public static bool IsAnalyzable(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[..^1].Any(s => SkippedSegments.Contains(s.ToLowerInvariant()))) return false;
        var file = segments[^1];
        return file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) || file.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase);
    }
}
