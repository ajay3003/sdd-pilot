using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.Scim;

/// <summary>An environment settings file reduced to key paths: allow-listed structural values are kept, every other value becomes "(set)" or "(empty)".</summary>
public sealed record ScimSettingsFile(string Path, Dictionary<string, string> Values);

/// <summary>What the SCIM analysis reads from uploaded archives. In memory only; source text is never persisted.</summary>
public sealed record ScimSourceSet(List<SourceFile> Code, List<SourceFile> Documents, List<ScimSettingsFile> Settings);

/// <summary>
/// Reads uploaded repository archives (zip) for SCIM analysis, bounded and in memory: C# sources and project files (tests included, for test
/// coverage), SCIM specification/contract markdown, SCIM pipeline YAML and the Service Bus emulator topology. Environment settings files are
/// reduced to key paths at read time — values are dropped except a small allow-list of non-secret structural keys — so a connection string,
/// key or password never leaves the reader. Nothing is extracted to disk.
/// </summary>
public static class ScimSourceReader
{
    public const long MaxArchiveBytes = SourceArchiveReader.MaxArchiveBytes;
    private const int MaxEntries = 30_000;
    private const long MaxFileBytes = 1024 * 1024;
    private const long MaxTotalBytes = 150 * 1024 * 1024;
    private static readonly string[] SkippedSegments = ["bin", "obj", "node_modules", ".git", ".vs", "migrations", "wwwroot"];

    /// <summary>Settings keys whose values are structural and never secret. Everything else is reduced to (set)/(empty).</summary>
    private static readonly string[] KeptValueSuffixes =
    [
        "AzureAd:Instance", "AzureAd:TenantId", "ServiceBus:Disabled", "Scim:PageSize", "AllowedHosts",
    ];

    public static (SourceArchive? Archive, ScimSourceSet Files, string? Error) Read(string fileName, byte[] bytes)
    {
        var name = System.IO.Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        var empty = new ScimSourceSet([], [], []);
        if (bytes.Length == 0) return (null, empty, $"{name} is empty.");
        if (bytes.Length > MaxArchiveBytes) return (null, empty, $"{name} is larger than the {MaxArchiveBytes / (1024 * 1024)} MB limit.");
        var code = new List<SourceFile>();
        var documents = new List<SourceFile>();
        var settings = new List<ScimSettingsFile>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return (null, empty, $"{name} has more than {MaxEntries} entries.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                var kind = Classify(path);
                if (kind is null || entry.Length > MaxFileBytes) continue;
                total += entry.Length;
                if (total > MaxTotalBytes) return (null, empty, $"{name} has more than {MaxTotalBytes / (1024 * 1024)} MB of source to analyze.");
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                switch (kind)
                {
                    case FileKind.Code: code.Add(new SourceFile(path, content)); break;
                    case FileKind.Document: documents.Add(new SourceFile(path, content)); break;
                    case FileKind.Settings: settings.Add(new ScimSettingsFile(path, Reduce(content))); break;
                }
            }
        }
        catch (InvalidDataException) { return (null, empty, $"{name} is not a valid zip archive."); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (new SourceArchive(name, sha, code.Count + documents.Count + settings.Count), new ScimSourceSet(code, documents, settings), null);
    }

    private enum FileKind { Code, Document, Settings }

    private static FileKind? Classify(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[..^1].Any(s => SkippedSegments.Contains(s.ToLowerInvariant()))) return null;
        var file = segments[^1];
        if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return FileKind.Code;
        if (file.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return FileKind.Settings;
        var scimPath = path.Contains("scim", StringComparison.OrdinalIgnoreCase);
        if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && (scimPath || path.Contains("EntraAdapterDocs", StringComparison.OrdinalIgnoreCase))) return FileKind.Document;
        if ((file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) && scimPath) return FileKind.Document;
        if (file.Equals("servicebus-config.json", StringComparison.OrdinalIgnoreCase)) return FileKind.Document;
        return null;
    }

    /// <summary>Flattens a settings file to "Section:Key" paths. Only allow-listed structural values survive; all others become (set)/(empty).</summary>
    public static Dictionary<string, string> Reduce(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            void Walk(JsonElement element, string prefix)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject()) Walk(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}");
                    return;
                }
                var raw = element.ValueKind switch { JsonValueKind.String => element.GetString() ?? "", JsonValueKind.Null => "", _ => element.GetRawText() };
                // Topic names under a HendelsesTopics section are entity names, not secrets.
                var keep = KeptValueSuffixes.Any(k => prefix.EndsWith(k, StringComparison.OrdinalIgnoreCase)) || prefix.Contains(":HendelsesTopics:", StringComparison.OrdinalIgnoreCase);
                values[prefix] = keep ? raw : raw.Length == 0 ? "(empty)" : "(set)";
            }
            Walk(doc.RootElement, "");
        }
        catch (JsonException) { values["(unparseable)"] = "(set)"; }
        return values;
    }
}
