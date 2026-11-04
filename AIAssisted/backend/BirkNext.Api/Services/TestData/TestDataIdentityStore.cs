using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>One period during which an asset was known by an alias. <see cref="Until"/> is null for the current alias.</summary>
public sealed record TestDataAliasPeriod(string Alias, DateTimeOffset Since, DateTimeOffset? Until = null, string? EndedBy = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataIdentityStatus { Active, Removed }

/// <summary>
/// The immutable identity of one logical Test Data asset. <see cref="AssetId"/> is generated once and never derived from the alias, the provider
/// label or the value; the alias-derived legacy ids it has carried are kept so every older reference still resolves to it.
/// </summary>
public sealed record TestDataAssetIdentity
{
    public string AssetId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    /// <summary>Every legacy id (<c>{provider}:{alias}</c>) this asset has had, oldest first; the last is current.</summary>
    public List<string> LegacyIds { get; init; } = [];
    /// <summary>Distinguishes assets that shared one legacy id when the identity was first assigned (null when the legacy id was unique).</summary>
    public string? Disambiguator { get; init; }
    public List<TestDataAliasPeriod> Aliases { get; init; } = [];
    public TestDataIdentityStatus Status { get; init; } = TestDataIdentityStatus.Active;
    /// <summary>"Migrated" (assigned to an asset that existed before stable ids) or "Created".</summary>
    public string Origin { get; init; } = "Created";
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Why the mapping needs review (e.g. two assets shared one legacy id: both kept, never merged).</summary>
    public string? Review { get; init; }
    public List<TestDataValidationEvidence> Validations { get; init; } = [];

    [JsonIgnore] public string CurrentAlias => Aliases.LastOrDefault(a => a.Until is null)?.Alias ?? Aliases.LastOrDefault()?.Alias ?? "";
    [JsonIgnore] public string CurrentLegacyId => LegacyIds.LastOrDefault() ?? "";
}

/// <summary>
/// Persisted Test Data identities (App_Data/test-data/identities.json; same lock + atomic replace as the registry). Assignment is idempotent: an
/// existing asset is found again by its current legacy id (+ disambiguator) and keeps its id; a removed asset's alias, when registered again,
/// gets a NEW id — a reused alias never inherits another asset's history. Two assets sharing one legacy id are never merged.
/// </summary>
public sealed class TestDataIdentityStore
{
    public const string IdPrefix = "tda_";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<TestDataIdentityStore>? _logger;
    private Document _document;

    private sealed record Document
    {
        public int Version { get; init; } = 1;
        public DateTimeOffset? MigratedAt { get; init; }
        public List<TestDataAssetIdentity> Identities { get; init; } = [];
    }

    public TestDataIdentityStore(IHostEnvironment environment, ILogger<TestDataIdentityStore>? logger = null)
        : this(System.IO.Path.Combine(environment.ContentRootPath, "App_Data", "test-data"), logger) { }

    public TestDataIdentityStore(string directory, ILogger<TestDataIdentityStore>? logger = null)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "identities.json");
        _document = Read();
    }

    public IReadOnlyList<TestDataAssetIdentity> Identities() { lock (_gate) return [.. _document.Identities]; }

    /// <summary>
    /// Assigns (or finds) the stable id of each asset. Assets sharing one legacy id are disambiguated by provenance and flagged for review.
    /// Returns the asset with <c>AssetId</c> resolved. Idempotent; the first pass over an existing installation records identities as Migrated.
    /// </summary>
    public IReadOnlyDictionary<TestDataAsset, TestDataAssetIdentity> Assign(IReadOnlyList<TestDataAsset> assets, DateTimeOffset now)
    {
        var result = new Dictionary<TestDataAsset, TestDataAssetIdentity>(ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            var changed = false;
            var migrating = _document.MigratedAt is null;
            var identities = _document.Identities;
            foreach (var group in assets.GroupBy(a => a.Id, StringComparer.Ordinal))
            {
                var members = group.ToList();
                var shared = members.Count > 1;
                foreach (var asset in members)
                {
                    var disambiguator = shared ? Disambiguator(asset) : null;
                    var identity = identities.FirstOrDefault(i => i.Status == TestDataIdentityStatus.Active && i.CurrentLegacyId == asset.Id
                                                                   && (i.Disambiguator == disambiguator || (!shared && i.Disambiguator is null)));
                    if (identity is null && shared)
                        identity = identities.FirstOrDefault(i => i.Status == TestDataIdentityStatus.Active && i.CurrentLegacyId == asset.Id && i.Disambiguator == disambiguator);
                    if (identity is null)
                    {
                        identity = new TestDataAssetIdentity
                        {
                            AssetId = NewId(), ProviderId = asset.SourceId, LegacyIds = [asset.Id], Disambiguator = disambiguator,
                            Aliases = [new(asset.Alias, now)], Origin = migrating ? "Migrated" : "Created", CreatedAt = now,
                            Review = shared ? $"The legacy id {asset.Id} is shared by {members.Count} assets; they are kept as separate assets until the duplicate is resolved." : null,
                        };
                        identities.Add(identity);
                        changed = true;
                    }
                    else if (shared && identity.Review is null)
                    {
                        var i = identities.IndexOf(identity);
                        identities[i] = identity = identity with { Review = $"The legacy id {asset.Id} is shared by {members.Count} assets; they are kept as separate assets until the duplicate is resolved." };
                        changed = true;
                    }
                    else if (!shared && identity.Review is not null && identity.Disambiguator is null)
                    {
                        var i = identities.IndexOf(identity);
                        identities[i] = identity = identity with { Review = null };
                        changed = true;
                    }
                    result[asset] = identity;
                }
            }
            if (migrating) { _document = _document with { MigratedAt = now }; changed = true; }
            if (changed) Write();
        }
        return result;
    }

    /// <summary>A rename: same AssetId, the old alias becomes historical and the new legacy id is added. Null when the asset is unknown.</summary>
    public TestDataAssetIdentity? Rename(string assetId, string newAlias, string newLegacyId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var i = _document.Identities.FindIndex(x => x.AssetId == assetId && x.Status == TestDataIdentityStatus.Active);
            if (i < 0) return null;
            var current = _document.Identities[i];
            var aliases = current.Aliases.Select(a => a.Until is null ? a with { Until = now, EndedBy = "Renamed" } : a).ToList();
            aliases.Add(new(newAlias, now));
            var updated = current with { Aliases = aliases, LegacyIds = [.. current.LegacyIds, newLegacyId] };
            _document.Identities[i] = updated;
            Write();
            return updated;
        }
    }

    /// <summary>The asset was removed: its identity is kept (history and references resolve to "removed"), its alias period ends.</summary>
    public void MarkRemoved(string legacyId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = false;
            for (var i = 0; i < _document.Identities.Count; i++)
            {
                var x = _document.Identities[i];
                if (x.Status != TestDataIdentityStatus.Active || x.CurrentLegacyId != legacyId) continue;
                _document.Identities[i] = x with
                {
                    Status = TestDataIdentityStatus.Removed,
                    Aliases = [.. x.Aliases.Select(a => a.Until is null ? a with { Until = now, EndedBy = "Removed" } : a)],
                };
                changed = true;
            }
            if (changed) Write();
        }
    }

    /// <summary>After a reset: every active identity whose asset no longer exists is marked Removed (its alias may be reused by a NEW asset).</summary>
    public int RetainOnly(IEnumerable<string> presentLegacyIds, DateTimeOffset now)
    {
        var present = presentLegacyIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var count = 0;
            for (var i = 0; i < _document.Identities.Count; i++)
            {
                var x = _document.Identities[i];
                if (x.Status != TestDataIdentityStatus.Active || present.Contains(x.CurrentLegacyId)) continue;
                _document.Identities[i] = x with
                {
                    Status = TestDataIdentityStatus.Removed,
                    Aliases = [.. x.Aliases.Select(a => a.Until is null ? a with { Until = now, EndedBy = "Removed" } : a)],
                };
                count++;
            }
            if (count > 0) Write();
            return count;
        }
    }

    /// <summary>Appends one validation result to the asset's history (append-only).</summary>
    public void RecordValidation(string assetId, TestDataValidationEvidence evidence)
    {
        lock (_gate)
        {
            var i = _document.Identities.FindIndex(x => x.AssetId == assetId);
            if (i < 0) return;
            _document.Identities[i] = _document.Identities[i] with { Validations = [.. _document.Identities[i].Validations, evidence] };
            Write();
        }
    }

    /// <summary>
    /// Compatibility resolver for any stored reference: a stable id, any legacy id the asset ever had, or an alias (current first; a historical
    /// alias only while no active asset uses it now). Returns the identity (Active or Removed) or null.
    /// </summary>
    public TestDataAssetIdentity? Resolve(string reference)
    {
        lock (_gate)
        {
            var all = _document.Identities;
            if (reference.StartsWith(IdPrefix, StringComparison.Ordinal)) return all.FirstOrDefault(x => x.AssetId == reference);
            var byLegacy = all.Where(x => x.LegacyIds.Contains(reference, StringComparer.Ordinal)).ToList();
            if (byLegacy.Count > 0)
                // The current holder of the legacy id wins; otherwise the asset that had it most recently.
                return byLegacy.FirstOrDefault(x => x.Status == TestDataIdentityStatus.Active && x.CurrentLegacyId == reference)
                    ?? byLegacy.OrderByDescending(x => x.Aliases.LastOrDefault()?.Since).First();
            var active = all.FirstOrDefault(x => x.Status == TestDataIdentityStatus.Active && x.CurrentAlias == reference);
            if (active is not null) return active;
            var renamed = all.Where(x => x.Status == TestDataIdentityStatus.Active && x.Aliases.Any(a => a.Alias == reference && a.EndedBy == "Renamed")).ToList();
            return renamed.Count == 1 ? renamed[0] : null;
        }
    }

    /// <summary>Clears every identity (only on a full local data reset).</summary>
    public void Clear()
    {
        lock (_gate) { _document = new Document(); Write(); }
    }

    private static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>Provenance that separates assets sharing one legacy id: their own version timestamp, candidate or label (never the value).</summary>
    private static string Disambiguator(TestDataAsset asset) =>
        string.Join("|", asset.SourceId, asset.Versions?.FirstOrDefault()?.VersionCreatedAt?.ToString("O") ?? asset.VersionCreatedAt?.ToString("O") ?? "",
            asset.CandidateId ?? "", asset.Label ?? "", asset.Schema?.SourceSnapshotId?.ToString() ?? "", asset.Schema?.SchemaPath ?? "");

    private Document Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch (JsonException ex)
        {
            // Never silently start over on a corrupt file: identities would be re-issued. Keep a copy and fail loudly.
            _logger?.LogError(ex, "Test Data identities file is unreadable: {Path}", _path);
            throw new InvalidOperationException($"Test Data identities ({_path}) could not be read; nothing was changed.", ex);
        }
    }

    private void Write()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_document, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Rename request: the new alias for a registered asset (its TestDataAssetId is unchanged).</summary>
public sealed record TestDataRenameRequest(string Alias);
