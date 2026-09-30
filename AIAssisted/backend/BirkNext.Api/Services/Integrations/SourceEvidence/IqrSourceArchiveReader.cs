using System.IO.Compression;
using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>Bounded virtual workspace. Never writes archive entries or the upload to disk.</summary>
public static class IqrSourceArchiveReader
{
    public const int MaxArchiveBytes = 50 * 1024 * 1024;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxExpandedBytes = 100 * 1024 * 1024;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", ".git", ".vs", "coverage", "TestResults" };
    public sealed record Workspace(SourceArchive Archive, List<SourceFile> Files, List<string> Limitations, List<SourceConfigurationEvidence>? Configurations = null);

    public static (Workspace? Workspace, string? Error) Read(string name, byte[] bytes, CancellationToken ct = default)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return (null, "Only .zip source archives are supported.");
        if (bytes.Length == 0 || bytes.Length > MaxArchiveBytes) return (null, "Source archive must be between 1 byte and 50 MB.");
        var files = new List<SourceFile>();
        var limitations = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configurations = new List<SourceConfigurationEvidence>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > 20_000) return (null, "Source archive exceeds 20,000 entries.");
            long total = 0;
            long actualRead = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = entry.FullName.Replace('\\', '/');
                if (path.StartsWith('/') || path.Contains(':') || path.Contains('\0') || path.Split('/').Any(s => s is ".." or ".")
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    return (null, "Source archive contains an unsafe path or symbolic link.");
                if (!names.Add(path)) return (null, "Source archive contains duplicate paths.");
                if (entry.Length > MaxExpandedBytes - total) return (null, "Source archive exceeds the 100 MB expanded size limit.");
                total += entry.Length;
                if (path.EndsWith('/') || path.Split('/').Any(Ignored.Contains)) continue;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is ".zip" or ".tar" or ".gz" or ".7z") { limitations.Add("Nested archives are not analyzed."); continue; }
                if (extension is ".js" or ".ts" or ".py" or ".java" or ".go" or ".fs" or ".vb" or ".tf")
                    limitations.Add($"Not analyzed: {extension} source (unsupported language).");
                if (extension is not (".cs" or ".csproj" or ".sln" or ".json" or ".yaml" or ".yml" or ".props" or ".sql")) continue;
                if (entry.Length > MaxFileBytes) { limitations.Add("Source file exceeds the 2 MB per-file limit and was not analyzed."); continue; }
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    actualRead += count;
                    if (actualRead > MaxExpandedBytes) return (null, "Source archive exceeds the 100 MB actual read limit.");
                    if (buffer.Length + count > MaxFileBytes) return (null, "Source entry exceeds its bounded read limit.");
                    buffer.Write(chunk, 0, count);
                }
                if (buffer.Length != entry.Length) return (null, "Source entry length does not match archive metadata.");
                buffer.Position = 0;
                using var textReader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var content = textReader.ReadToEnd();
                if (extension is ".json" or ".yaml" or ".yml")
                {
                    var keys = new List<string>();
                    if (extension == ".json")
                    {
                        try
                        {
                            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                            void Walk(JsonElement element, string prefix)
                            {
                                if (keys.Count >= 1024 || element.ValueKind != JsonValueKind.Object) return;
                                foreach (var property in element.EnumerateObject())
                                {
                                    if (keys.Count >= 1024) break;
                                    var key = string.IsNullOrEmpty(prefix) ? property.Name : prefix + ":" + property.Name;
                                    keys.Add(SafeLabel(key));
                                    Walk(property.Value, key);
                                }
                            }
                            Walk(json.RootElement, "");
                        }
                        catch (JsonException) { limitations.Add("A JSON configuration file could not be parsed; values excluded and other evidence retained."); }
                    }
                    else
                    {
                        keys = Regex.Matches(content, @"(?m)^\s*([\p{L}_][\p{L}\p{N}_-]*)\s*:").Select(m => SafeLabel(m.Groups[1].Value)).Distinct().Take(1024).ToList();
                        limitations.Add("YAML key inventory is heuristic; YAML values and build/test execution results are not analyzed.");
                    }
                    configurations.Add(new SourceConfigurationEvidence(SafeLabel(path), keys, extension == ".json" ? SourceConfidence.StrongSourceEvidence : SourceConfidence.Partial));
                    limitations.Add("Configuration values excluded. Key inventory is bounded to 1,024 keys per file; formal schemas are not validated.");
                    continue;
                }
                files.Add(new SourceFile(path, content));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        { return (null, "Source archive is invalid or unreadable."); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (new Workspace(new SourceArchive(SafeLabel(Path.GetFileName(name.Replace('\\', '/'))), sha, files.Count + configurations.Count), files, [.. limitations], configurations), null);
    }

    public static string SafeLabel(string value)
    {
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(DependencyEvidenceRedaction.Redact(value));
        safe = Regex.Replace(safe, @"(?i)(SECRET_SENTINEL\w*|(?:password|clientsecret|accesskey|sharedaccesskey|token)[_=][^/\s]+)", "[redacted]");
        safe = Regex.Replace(safe, @"[^\p{L}\p{N}_./<>\[\] `:+?,\-]", "_");
        return safe.Length > 300 ? safe[..300] : safe;
    }
}
