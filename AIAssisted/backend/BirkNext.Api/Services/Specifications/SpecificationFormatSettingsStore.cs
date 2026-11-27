using System.Text.Json;
using Path = System.IO.Path;
using System.Text.Json.Serialization;
using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications;

/// <summary>
/// System Settings → Spesifikasjonsformat (2026-11-27): the specification format of each application, chosen explicitly. Stored in App_Data as JSON
/// (like the other System Settings stores); only explicit choices are written, so <see cref="SpecificationFormat.SpecKit"/> applies to every
/// application nobody configured. The single authoritative format selection: every processing entry point asks
/// <see cref="SpecificationProcessingRouter"/>, which reads it here. Each change is appended to a history (who, when, from → to).
/// Changing a format never touches snapshots, catalogs, plans, cases, approvals or executions.
/// </summary>
public sealed class SpecificationFormatSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private string FilePath => Path.Combine(directory, "specification-format.json");

    public static string DefaultDirectory(IHostEnvironment environment) => Path.Combine(AppDataLocation.Root(environment), "specification-format");

    public sealed record Entry(SpecificationFormat Format, DateTimeOffset UpdatedAt, string UpdatedBy);

    public sealed record HistoryEntry(string ApplicationKey, SpecificationFormat From, SpecificationFormat To, DateTimeOffset At, string By);

    public sealed record Document
    {
        public Dictionary<string, Entry> Applications { get; init; } = new(StringComparer.Ordinal);
        public List<HistoryEntry> History { get; init; } = [];
    }

    public Document Read()
    {
        lock (_gate) return ReadUnlocked();
    }

    private Document ReadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Json) ?? new();
            return doc with { Applications = new(doc.Applications, StringComparer.Ordinal) };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable file must not silently switch applications to another route: refuse until it is repaired.
            throw new InvalidOperationException("Innstillingen for spesifikasjonsformat kan ikke leses. Ingen behandling velges før filen er reparert.", e);
        }
    }

    /// <summary>The configured format of an application; <see cref="SpecificationFormat.SpecKit"/> when never chosen.</summary>
    public (SpecificationFormat Format, Entry? Explicit) FormatOf(string? applicationKey)
    {
        if (string.IsNullOrWhiteSpace(applicationKey)) return (SpecificationFormat.SpecKit, null);
        var doc = Read();
        return doc.Applications.TryGetValue(applicationKey, out var e) ? (e.Format, e) : (SpecificationFormat.SpecKit, null);
    }

    public Entry Save(string applicationKey, SpecificationFormat format, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(applicationKey) || applicationKey.Length > 300) throw new ArgumentException("Velg en applikasjon.");
        if (!Enum.IsDefined(format)) throw new ArgumentException("Ukjent spesifikasjonsformat.");
        lock (_gate)
        {
            var doc = ReadUnlocked();
            var previous = doc.Applications.TryGetValue(applicationKey, out var p) ? p.Format : SpecificationFormat.SpecKit;
            var entry = new Entry(format, now, actor);
            doc.Applications[applicationKey] = entry;
            if (previous != format || p is null) doc.History.Add(new HistoryEntry(applicationKey, previous, format, now, actor));
            var saved = doc with { History = [.. doc.History.TakeLast(500)] };
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, FilePath, overwrite: true);
            return entry;
        }
    }
}
