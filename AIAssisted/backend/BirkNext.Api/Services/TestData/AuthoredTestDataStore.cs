using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestData;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings

namespace BirkNext.Api.Services.TestData;

/// <summary>One saved version of an authored resource. Versions are append-only: an edit adds a version, nothing rewrites an earlier one.</summary>
public sealed record AuthoredTestDataVersion
{
    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? EnvironmentId { get; init; }
    public List<string> AppliesTo { get; init; } = [];
    public TestDataResourceContent Content { get; init; } = new();
    public string Fingerprint { get; init; } = "";
    public string? ChangeNote { get; init; }
}

public sealed record AuthoredTestDataResource
{
    public string Alias { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public List<AuthoredTestDataVersion> Versions { get; init; } = [];
    public AuthoredTestDataVersion Current => Versions[^1];
}

/// <summary>
/// Synthetic values a tester authored in Test Data ("Add data"), one small JSON file next to the Test Data registry (workstation-local, like
/// the registry). Unlike the registry it holds values — only values that passed <see cref="TestDataResourceRules"/> (no credential-like or
/// identity-number content). Writes are atomic (temp file + replace); an edit must name the version it started from, so a concurrent edit is a
/// conflict instead of a silent overwrite.
/// </summary>
public sealed class AuthoredTestDataStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() },
    };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private List<AuthoredTestDataResource> _resources;

    public AuthoredTestDataStore(IHostEnvironment environment, TimeProvider? clock = null)
        : this(System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "test-data"), clock) { }

    public AuthoredTestDataStore(string directory, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "authored.json");
        _clock = clock ?? TimeProvider.System;
        _resources = Read();
    }

    public IReadOnlyList<AuthoredTestDataResource> Resources() { lock (_gate) return [.. _resources]; }
    public AuthoredTestDataResource? Find(string alias) { lock (_gate) return _resources.FirstOrDefault(r => r.Alias == alias); }

    public enum Outcome { Saved, Conflict, NotFound, AliasTaken, WriteFailed }

    /// <summary>Creates version 1. Refused when the alias exists here (the caller also checks every other catalog source).</summary>
    public (Outcome Outcome, AuthoredTestDataResource? Resource) Create(TestDataResourceDraft draft)
    {
        lock (_gate)
        {
            if (_resources.Any(r => r.Alias == draft.Alias)) return (Outcome.AliasTaken, null);
            var now = _clock.GetUtcNow();
            var resource = new AuthoredTestDataResource { Alias = draft.Alias, CreatedAt = now, Versions = [Version(draft, 1, now)] };
            return Commit([.. _resources, resource]) ? (Outcome.Saved, resource) : (Outcome.WriteFailed, null);
        }
    }

    /// <summary>Adds a version. <paramref name="draft"/>.ExpectedVersion must be the current version (optimistic concurrency).</summary>
    public (Outcome Outcome, AuthoredTestDataResource? Resource) AddVersion(string alias, TestDataResourceDraft draft)
    {
        lock (_gate)
        {
            var existing = _resources.FirstOrDefault(r => r.Alias == alias);
            if (existing is null) return (Outcome.NotFound, null);
            if (draft.ExpectedVersion != existing.Current.Version) return (Outcome.Conflict, existing);
            var updated = existing with { Versions = [.. existing.Versions, Version(draft, existing.Current.Version + 1, _clock.GetUtcNow())] };
            return Commit([.. _resources.Select(r => r.Alias == alias ? updated : r)]) ? (Outcome.Saved, updated) : (Outcome.WriteFailed, null);
        }
    }

    public bool Remove(string alias)
    {
        lock (_gate)
        {
            if (_resources.All(r => r.Alias != alias)) return false;
            return Commit([.. _resources.Where(r => r.Alias != alias)]);
        }
    }

    /// <summary>The value fingerprint the catalog approves (same rule as every other asset: the payload text).</summary>
    public static string Fingerprint(TestDataResourceContent content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TestDataResourceRules.Payload(content)))).ToLowerInvariant();

    private static AuthoredTestDataVersion Version(TestDataResourceDraft d, int version, DateTimeOffset at) => new()
    {
        Version = version, CreatedAt = at, Label = Trim(d.Label, 200), Description = Trim(d.Description, 2000), EnvironmentId = Trim(d.EnvironmentId, 200),
        AppliesTo = [.. d.AppliesTo.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.Ordinal).Take(20)],
        Content = d.Content, Fingerprint = Fingerprint(d.Content), ChangeNote = Trim(d.ChangeNote, 500),
    };

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    private bool Commit(List<AuthoredTestDataResource> next)
    {
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, overwrite: true);
            _resources = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
            return false;
        }
    }

    private List<AuthoredTestDataResource> Read()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<AuthoredTestDataResource>>(File.ReadAllText(_path), Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
