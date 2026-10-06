using System.Text;
using Path = System.IO.Path;
using File = System.IO.File;

namespace BirkNext.Api.Services.SampleProjects;

/// <summary>
/// Read-only, bounded, recursive file inventory of one Sample Project folder.
///
/// This only enumerates files and marks which ones are candidate documents (Markdown). It never decides an artifact
/// role: the role comes from the frontend document classifier, which uses the shared Markdown engine and the domain
/// extractors. It is unrelated to Source Analysis (archive/technology discovery).
///
/// Safety: symbolic links and junctions (reparse points) are never followed or read, so nothing outside the project
/// root is reachable; build/dependency folders are skipped; depth, file count, listed entries, candidate documents
/// and document size are capped; a Markdown file containing NUL bytes is treated as binary.
/// </summary>
public static class SampleProjectDocumentInventory
{
    public const int MaxDepth = 16;
    public const int MaxFilesScanned = 10_000;
    public const int MaxListedFiles = 2_000;
    public const int MaxDocuments = 500;
    public const long MaxDocumentBytes = 1024 * 1024;
    private const int BinarySniffBytes = 8 * 1024;

    /// <summary>Build, dependency, tool and generated-output folders. Matched case-insensitively on one path segment.</summary>
    public static readonly IReadOnlySet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", "build", "out", "coverage", "TestResults",
        "artifacts", "packages", "vendor", ".terraform", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt", ".angular",
    };

    public static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };

    public static SampleProjectInventory Enumerate(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = new List<SampleInventoryFile>();
        var ignored = new List<string>();
        int scanned = 0, documents = 0, skippedLinks = 0;
        var truncated = false;

        var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // Deterministic: never rely on file-system enumeration order.
            var ordered = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            foreach (var file in ordered.OfType<FileInfo>())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skippedLinks++; continue; }
                if (++scanned > MaxFilesScanned) { truncated = true; break; }
                var relative = Relative(root, file.FullName);
                if (relative is null) continue;
                var isDocument = DocumentExtensions.Contains(file.Extension);
                string? skip = null;
                if (isDocument)
                {
                    if (documents >= MaxDocuments) skip = SampleInventorySkipReason.DocumentLimit;
                    else if (file.Length > MaxDocumentBytes) skip = SampleInventorySkipReason.TooLarge;
                    else if (LooksBinary(file.FullName) is { } binary) skip = binary;
                    if (skip is null) documents++;
                }
                if (files.Count < MaxListedFiles || (isDocument && skip is null))
                    files.Add(new SampleInventoryFile(relative, file.Name, file.Extension.ToLowerInvariant(), file.Length,
                        file.LastWriteTimeUtc, isDocument, skip));
                else truncated = true;
            }
            if (scanned > MaxFilesScanned) break;

            // Push in reverse so subdirectories are visited in ordinal order.
            foreach (var sub in ordered.OfType<DirectoryInfo>().Reverse())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skippedLinks++; continue; }
                var relative = Relative(root, sub.FullName);
                if (relative is null) continue;
                if (IgnoredDirectories.Contains(sub.Name)) { ignored.Add(relative); continue; }
                if (depth + 1 > MaxDepth) { truncated = true; continue; }
                pending.Push((sub, depth + 1));
            }
        }

        var sortedFiles = files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        return new SampleProjectInventory(sortedFiles, Math.Min(scanned, MaxFilesScanned), ignored.Order(StringComparer.Ordinal).ToList(),
            skippedLinks, truncated);
    }

    /// <summary>
    /// Normalises a client-supplied relative path and returns it only when it names a readable candidate document of the
    /// inventory. Anything else (traversal, absolute paths, links, skipped or unlisted files) yields null.
    /// </summary>
    public static SampleInventoryFile? FindReadableDocument(SampleProjectInventory inventory, string? requestedPath)
    {
        var normalized = NormalizeRelative(requestedPath);
        if (normalized is null) return null;
        return inventory.Files.FirstOrDefault(f => f.IsDocument && f.SkipReason is null &&
                                                    string.Equals(f.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':') || p.Contains('\0')) return null;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or "..")) return null;
        return string.Join('/', segments);
    }

    /// <summary>Resolves an inventory path to a full path and re-checks containment (defence in depth).</summary>
    public static string? ToContainedFullPath(string projectRoot, SampleInventoryFile file)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(full);
        return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? full : null;
    }

    public static async Task<string?> ReadDocumentAsync(string projectRoot, SampleInventoryFile file, CancellationToken ct = default)
    {
        var full = ToContainedFullPath(projectRoot, file);
        if (full is null) return null;
        var info = new FileInfo(full);
        if (info.Length > MaxDocumentBytes) return null;
        return await File.ReadAllTextAsync(full, Encoding.UTF8, ct);
    }

    private static string? Relative(string root, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return full[(root.Length + 1)..].Replace('\\', '/');
    }

    private static string? LooksBinary(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[BinarySniffBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0 ? SampleInventorySkipReason.Binary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SampleInventorySkipReason.Unreadable;
        }
    }
}

public static class SampleInventorySkipReason
{
    public const string TooLarge = "TooLarge";
    public const string Binary = "Binary";
    public const string Unreadable = "Unreadable";
    public const string DocumentLimit = "DocumentLimit";
}

/// <param name="RelativePath">Forward-slash path relative to the project root, original case kept.</param>
/// <param name="IsDocument">A Markdown file: a candidate for artifact-role classification.</param>
/// <param name="SkipReason">Why a candidate document is not readable (too large, binary, unreadable, over the limit); null when readable.</param>
public sealed record SampleInventoryFile(
    string RelativePath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime LastModifiedUtc,
    bool IsDocument,
    string? SkipReason);

public sealed record SampleProjectInventory(
    IReadOnlyList<SampleInventoryFile> Files,
    int FilesScanned,
    IReadOnlyList<string> IgnoredDirectories,
    int SkippedLinks,
    bool Truncated)
{
    public int DocumentCount => Files.Count(f => f.IsDocument && f.SkipReason is null);
}
