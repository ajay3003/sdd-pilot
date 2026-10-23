using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestData;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings

namespace BirkNext.Api.Services.TestData;

/// <summary>An asset registered in the catalog from a source candidate or manually: metadata and safe structure only, never a value.</summary>
public sealed record RegisteredTestDataAsset
{
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public TestDataDataType DataType { get; init; }
    /// <summary>Environment id, or null for Shared.</summary>
    public string? EnvironmentId { get; init; }
    public List<string> Fields { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string? SchemaRef { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? RepositoryKey { get; init; }
    public string? ArchiveName { get; init; }
    public string? CandidateId { get; init; }
    public string? CandidateReason { get; init; }
    public string? StructureFingerprint { get; init; }
    /// <summary>"Source Analysis candidate" or "Manually registered".</summary>
    public string Origin { get; init; } = "Manually registered";
    public DateTimeOffset RegisteredAt { get; init; }
    public string Id => $"{RegisteredTestDataProvider.ProviderId}:{Alias}";
}

/// <summary>Lifecycle decisions made in Test Data for any asset (by id): explicit approval of one version (fingerprint), and deprecation.</summary>
public sealed record TestDataLifecycleRecord
{
    public string AssetId { get; init; } = "";
    public string? ApprovedFingerprint { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public bool Deprecated { get; init; }
    public DateTimeOffset? DeprecatedAt { get; init; }
}

/// <summary>
/// The catalog's own records, as one small JSON file per installation (workstation-local QA configuration, like the Critical E2E store):
/// registered asset metadata, lifecycle decisions, inbound consumer requirements and explicit usage references. Nothing sensitive is written:
/// no value, no identity, no secret — values stay with their sources (configuration, local approved configuration, Schema Studio).
/// </summary>
public sealed class TestDataRegistryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<TestDataRegistryStore> _logger;
    private Registry _registry;

    private sealed record Registry
    {
        public List<RegisteredTestDataAsset> Assets { get; init; } = [];
        public List<TestDataLifecycleRecord> Lifecycle { get; init; } = [];
        public List<ConsumerTestDataRequirement> Requirements { get; init; } = [];
        public List<TestDataUsageReference> Usages { get; init; } = [];
    }

    public TestDataRegistryStore(IHostEnvironment environment, ILogger<TestDataRegistryStore> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "test-data"), logger) { }

    public TestDataRegistryStore(string directory, ILogger<TestDataRegistryStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "registry.json");
        _registry = Read() ?? new Registry();
    }

    public IReadOnlyList<RegisteredTestDataAsset> Assets() { lock (_gate) return [.. _registry.Assets]; }
    public TestDataLifecycleRecord? Lifecycle(string assetId) { lock (_gate) return _registry.Lifecycle.FirstOrDefault(l => l.AssetId == assetId); }
    public IReadOnlyList<TestDataLifecycleRecord> Lifecycles() { lock (_gate) return [.. _registry.Lifecycle]; }
    public IReadOnlyList<ConsumerTestDataRequirement> Requirements() { lock (_gate) return [.. _registry.Requirements]; }
    public IReadOnlyList<TestDataUsageReference> Usages() { lock (_gate) return [.. _registry.Usages]; }

    /// <summary>Adds a registered asset; refused when the alias or the same source candidate (repository + candidate id) is already registered.</summary>
    public (RegisteredTestDataAsset? Asset, string? Existing) Register(RegisteredTestDataAsset asset)
    {
        lock (_gate)
        {
            if (_registry.Assets.FirstOrDefault(a => a.CandidateId is not null && a.CandidateId == asset.CandidateId && a.RepositoryKey == asset.RepositoryKey) is { } same) return (null, same.Id);
            if (_registry.Assets.FirstOrDefault(a => a.Alias == asset.Alias) is { } taken) return (null, taken.Id);
            _registry = _registry with { Assets = [.. _registry.Assets, asset] };
            Write();
            return (asset, null);
        }
    }

    public void SetLifecycle(TestDataLifecycleRecord record)
    {
        lock (_gate)
        {
            _registry = _registry with { Lifecycle = [.. _registry.Lifecycle.Where(l => l.AssetId != record.AssetId), record] };
            Write();
        }
    }

    public ConsumerTestDataRequirement AddRequirement(ConsumerTestDataRequirement requirement)
    {
        lock (_gate)
        {
            // The same consumer need registered again (same consumer, usage, fields, type, environment) is the same requirement.
            if (_registry.Requirements.FirstOrDefault(r => r.Consumer.Key == requirement.Consumer.Key && r.Usage == requirement.Usage && r.RequestedDataType == requirement.RequestedDataType
                    && string.Equals(r.EnvironmentId, requirement.EnvironmentId, StringComparison.OrdinalIgnoreCase) && r.RequestedFields.SequenceEqual(requirement.RequestedFields)) is { } existing)
                return existing;
            _registry = _registry with { Requirements = [.. _registry.Requirements, requirement] };
            Write();
            return requirement;
        }
    }

    public bool RemoveRequirement(string id)
    {
        lock (_gate)
        {
            if (_registry.Requirements.All(r => r.Id != id)) return false;
            _registry = _registry with { Requirements = [.. _registry.Requirements.Where(r => r.Id != id)] };
            Write();
            return true;
        }
    }

    public TestDataUsageReference Bind(TestDataUsageReference usage)
    {
        lock (_gate)
        {
            // One binding per consumer + requirement (or + asset when no requirement): rebinding replaces it, never duplicates it.
            var kept = _registry.Usages.Where(u => !(u.Consumer.Key == usage.Consumer.Key && (usage.RequirementId is not null ? u.RequirementId == usage.RequirementId : u.AssetId == usage.AssetId && u.Usage == usage.Usage)));
            _registry = _registry with { Usages = [.. kept, usage] };
            Write();
            return usage;
        }
    }

    public bool Unbind(string id)
    {
        lock (_gate)
        {
            if (_registry.Usages.All(u => u.Id != id)) return false;
            _registry = _registry with { Usages = [.. _registry.Usages.Where(u => u.Id != id)] };
            Write();
            return true;
        }
    }

    private Registry? Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Registry>(File.ReadAllText(_path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Test Data registry could not be read; starting empty");
            return null;
        }
    }

    private void Write()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_registry, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex) { _logger.LogWarning(ex, "Test Data registry could not be written"); }
    }
}

/// <summary>
/// Registered assets as a catalog source: metadata and structure only. They carry no value — the value, when a consumer needs one, is supplied
/// through an existing value source (configuration, local approved configuration) under the same alias; BirkNext never generates it.
/// </summary>
public sealed class RegisteredTestDataProvider(TestDataRegistryStore store) : ITestDataProvider
{
    public const string ProviderId = "registered";
    public string Id => ProviderId;
    public string Label => "Registered assets";
    public string Description => "Assets registered in Test Data from reviewed source candidates or manually: type, scope, structure and provenance (no values).";
    public string? Detail => "Registration records metadata and safe structure only. Values are never generated or stored here; supply them through a configured value source under the same alias.";
    public bool Configured => store.Assets().Count > 0;
    public string? StatusLabel(TestDataSourceStatus status) => status == TestDataSourceStatus.Available ? "Registered assets" : "No registered assets";

    public IReadOnlyList<TestDataAsset> Assets() => [.. store.Assets().Select(Asset)];

    public static TestDataAsset Asset(RegisteredTestDataAsset a)
    {
        var (kind, media) = a.DataType switch
        {
            TestDataDataType.JsonPayload => (TestDataKind.Structured, "application/json"),
            TestDataDataType.XmlPayload => (TestDataKind.Structured, "application/xml"),
            TestDataDataType.FileFixture => (TestDataKind.File, "application/octet-stream"),
            TestDataDataType.ExpectedResult => (TestDataKind.ExpectedResult, "application/json"),
            _ => (TestDataKind.Scalar, (string?)null),
        };
        var provenance = new List<TestDataFact> { new("Registered as", a.Origin) };
        if (a.ArchiveName is { Length: > 0 } archive) provenance.Add(new("Source", archive));
        if (a.SnapshotId is { } snapshot) provenance.Add(new("Source snapshot", snapshot.ToString("D")));
        if (a.SchemaRef is { Length: > 0 } schema) provenance.Add(new("Contract", schema));
        if (a.CandidateReason is { Length: > 0 } reason) provenance.Add(new("Candidate reason", reason));
        provenance.Add(new("Registered", a.RegisteredAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'")));
        return new TestDataAsset
        {
            Id = a.Id, Alias = a.Alias, Label = a.Label, Kind = kind, MediaType = media, Environment = a.EnvironmentId, Approval = TestDataApproval.Unapproved,
            ApprovalDetail = "Registered in Test Data; approve it explicitly here before consumers use it.", Sensitivity = TestDataSensitivity.Sensitive,
            Source = "Registered assets", Provenance = provenance, Fields = [.. a.Fields], Tags = [.. a.Tags], SchemaRef = a.SchemaRef, CandidateId = a.CandidateId,
            ReviewRequested = a.CandidateId is not null,
            Schema = a.DataType == TestDataDataType.XmlPayload && a.SchemaRef is { } xsd && xsd.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
                ? new TestDataSchemaReference(xsd, a.SnapshotId, null, null, xsd) : null,
        };
    }
}
