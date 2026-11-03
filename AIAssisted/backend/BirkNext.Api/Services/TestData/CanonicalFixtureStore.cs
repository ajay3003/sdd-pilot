using System.Text.Json;
using System.Text.Json.Nodes;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// The backend-owned home of canonical payload fixtures (a Test Pack's declared baseline XML, e.g. a pack's valid report). A registered
/// Schema Studio fixture otherwise lives in a browser workspace artifact, which a workspace auto-save from a session without it, a new project or a
/// local data reset removes — so a canonical fixture is kept here instead: workstation-local (App_Data, beside the Test Data registry), outside
/// the database, never replaced by a workspace save, untouched by Test Data or Maintenance reset. Items keep the saved-XML shape Schema Studio
/// writes, so the one Schema Studio provider reads them; every version of a fixture shares one alias and therefore one stable asset id.
/// Versions are append-only: an approved version is never rewritten — a regenerated XML is a new (draft) version.
/// </summary>
public sealed class CanonicalFixtureStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<CanonicalFixtureStore> _logger;
    private JsonArray _items;

    public CanonicalFixtureStore(IHostEnvironment environment, ILogger<CanonicalFixtureStore> logger)
        : this(System.IO.Path.Combine(environment.ContentRootPath, "App_Data", "test-data"), logger) { }

    public CanonicalFixtureStore(string directory, ILogger<CanonicalFixtureStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "canonical-fixtures.json");
        _items = Read();
    }

    /// <summary>The stored fixtures as one saved-XML document (the shape the Schema Studio provider reads).</summary>
    public string Document()
    {
        lock (_gate) return new JsonObject { ["version"] = 1, ["items"] = _items.DeepClone() }.ToJsonString(Json);
    }

    /// <summary>Aliases and versions stored, for tests and diagnostics.</summary>
    public IReadOnlyList<(string Alias, int Version)> Versions()
    {
        lock (_gate) return [.. _items.OfType<JsonObject>().Select(i => (Alias(i) ?? "", Version(i)))];
    }

    /// <summary>
    /// Stores one saved-XML item as the next version of its canonical fixture: the alias must be a declared canonical fixture's, the intent
    /// positive and the declared capability recorded; the version number is assigned here (1, then the next), approval is always cleared
    /// (approval is a separate Test Data decision). Returns the stored version, or why it was refused.
    /// </summary>
    public (int? Version, string? Error) Add(JsonObject item, IReadOnlyList<TestDataCanonicalFixture> fixtures, DateTimeOffset now)
    {
        if (item["testData"] is not JsonObject registration || Alias(item) is not { } alias) return (null, "The fixture has no Test Data registration.");
        if (fixtures.FirstOrDefault(f => string.Equals(f.Alias, alias, StringComparison.Ordinal)) is not { } fixture)
            return (null, $"{alias} is not a canonical fixture; save it as an ordinary Schema Studio fixture.");
        if (!string.Equals(Text(registration, "intent"), "positive", StringComparison.Ordinal)) return (null, "A canonical fixture is a positive (valid) payload.");
        var capabilities = registration["facts"]?["capabilities"] as JsonArray;
        if (capabilities?.Any(c => c?.GetValue<string>() == fixture.Capability) != true)
            return (null, $"The fixture was not created for {fixture.Capability}; it cannot be the canonical {alias}.");
        if (!string.Equals(Text(registration["facts"] as JsonObject, "validationStatus"), "Valid", StringComparison.Ordinal))
            return (null, "Only XML that validated against its XSD is stored as a canonical fixture version.");
        lock (_gate)
        {
            var version = _items.OfType<JsonObject>().Where(i => Alias(i) == alias).Select(Version).DefaultIfEmpty(0).Max() + 1;
            var stored = (JsonObject)item.DeepClone();
            var storedRegistration = (JsonObject)stored["testData"]!;
            storedRegistration["version"] = version;
            storedRegistration["approved"] = false;
            storedRegistration["registeredAt"] = now;
            if (stored["id"] is null || _items.OfType<JsonObject>().Any(i => Text(i, "id") == Text(stored, "id"))) stored["id"] = Guid.NewGuid();
            _items.Add(stored);
            Write();
            return (version, null);
        }
    }

    /// <summary>
    /// Adopts canonical fixtures that still live only in workspace artifacts (installations before this store): for each declared fixture the
    /// store does not hold yet, its positive registrations with the declared capability are copied — only when they are deterministic (one
    /// project scope, distinct versions). An ambiguous set is left where it is and reported, never merged. Idempotent. Returns what was adopted.
    /// </summary>
    public IReadOnlyList<string> Adopt(IEnumerable<string> workspaceDocuments, IReadOnlyList<TestDataCanonicalFixture> fixtures)
    {
        var adopted = new List<string>();
        lock (_gate)
        {
            var held = _items.OfType<JsonObject>().Select(Alias).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var pending = fixtures.Where(f => !held.Contains(f.Alias)).ToList();
            if (pending.Count == 0) return adopted;
            var found = new List<JsonObject>();
            foreach (var document in workspaceDocuments)
            {
                try
                {
                    if (JsonNode.Parse(document) is JsonObject root && Items(root) is { } items)
                        found.AddRange(items.OfType<JsonObject>());
                }
                catch (JsonException) { }
            }
            foreach (var fixture in pending)
            {
                var candidates = found.Where(i => Alias(i) == fixture.Alias && i["testData"] is JsonObject r && Text(r, "intent") == "positive"
                    && (r["facts"]?["capabilities"] as JsonArray)?.Any(c => c?.GetValue<string>() == fixture.Capability) == true && !string.IsNullOrEmpty(Text(i, "xml"))).ToList();
                if (candidates.Count == 0) continue;
                var scopes = candidates.Select(i => Text(i, "projectScope") ?? "").Distinct(StringComparer.Ordinal).Count();
                var versions = candidates.Select(Version).ToList();
                if (scopes > 1 || versions.Distinct().Count() != versions.Count)
                {
                    _logger.LogWarning("Canonical fixture {Alias} is ambiguous in workspace artifacts ({Count} registrations, {Scopes} scopes); not adopted", fixture.Alias, candidates.Count, scopes);
                    continue;
                }
                foreach (var candidate in candidates.OrderBy(Version)) _items.Add(candidate.DeepClone());
                adopted.Add(fixture.Alias);
            }
            if (adopted.Count > 0) Write();
        }
        return adopted;
    }

    private static JsonArray? Items(JsonObject document) =>
        (document["items"] ?? document["Items"]) as JsonArray;

    private static string? Alias(JsonObject item) => item["testData"] is JsonObject r ? Text(r, "alias")?.Trim() : null;
    private static int Version(JsonObject item) => item["testData"]?["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 1;
    private static string? Text(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private JsonArray Read()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonNode.Parse(File.ReadAllText(_path)) is JsonObject root && Items(root) is { } items ? (JsonArray)items.DeepClone() : []; }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Canonical fixture store {Path} could not be read; it is left untouched", _path);
            throw new InvalidOperationException($"The canonical fixture store {_path} is unreadable; it was not replaced.", ex);
        }
    }

    /// <summary>
    /// Removes every stored canonical fixture — only for the explicitly confirmed "Reset everything" scope. Ordinary deletes stay refused (409) and
    /// every other reset keeps the store. Returns the number of stored versions removed.
    /// </summary>
    public int ClearAllForFullReset()
    {
        lock (_gate)
        {
            var count = _items.Count;
            _items = [];
            if (File.Exists(_path)) File.Delete(_path);
            _logger.LogWarning("Canonical fixture store cleared by a full reset ({Count} stored version(s))", count);
            return count;
        }
    }

    private void Write()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, new JsonObject { ["version"] = 1, ["items"] = _items.DeepClone() }.ToJsonString(Json));
        File.Move(temp, _path, overwrite: true);
    }
}
