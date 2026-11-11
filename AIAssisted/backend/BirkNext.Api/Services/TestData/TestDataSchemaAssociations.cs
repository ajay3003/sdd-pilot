using System.Text.Json;
using BirkNext.Schemas;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>One XSD file of an explicit association (text + its fingerprint, the same SHA-256 the shared XSD capability computes).</summary>
public sealed record TestDataSchemaAssociationFile(string Path, string Text, string Fingerprint);

/// <summary>
/// An XSD package a user explicitly associated with a schema path, stored server-side. Used only when no stored Source Analysis snapshot retains the
/// schema; validation against it is "Server-validated (explicit schema reference)", never "verified schema".
/// </summary>
public sealed record TestDataSchemaAssociation(string Id, string SchemaPath, string? TargetNamespace, string? DeclaredVersion, IReadOnlyList<TestDataSchemaAssociationFile> Files,
    string AssociatedBy, DateTimeOffset AssociatedAt, string? Note)
{
    public string PrimaryFingerprint => Files.First(f => string.Equals(f.Path, SchemaPath, StringComparison.OrdinalIgnoreCase)).Fingerprint;
}

/// <summary>What the API returns for an association: identity and file fingerprints, never the XSD text.</summary>
public sealed record TestDataSchemaAssociationView(string Id, string SchemaPath, string? TargetNamespace, string? DeclaredVersion, string PrimaryFingerprint,
    IReadOnlyList<string> Files, string AssociatedBy, DateTimeOffset AssociatedAt, string? Note);

public sealed record TestDataSchemaAssociationRequest(string SchemaPath, IReadOnlyList<TestDataSchemaAssociationUploadFile> Files, string? Note = null);
public sealed record TestDataSchemaAssociationUploadFile(string Path, string Text);

/// <summary>Persisted explicit schema associations (App_Data/test-data/schema-associations.json; lock + atomic replace). Append-only per content:
/// associating identical content again returns the existing association.</summary>
public sealed class TestDataSchemaAssociationStore
{
    private const int MaxFiles = 50, MaxFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<TestDataSchemaAssociation> _items;

    public TestDataSchemaAssociationStore(IHostEnvironment environment) : this(System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "test-data")) { }

    public TestDataSchemaAssociationStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "schema-associations.json");
        _items = File.Exists(_path) ? JsonSerializer.Deserialize<List<TestDataSchemaAssociation>>(File.ReadAllText(_path), Json) ?? [] : [];
    }

    public IReadOnlyList<TestDataSchemaAssociation> All() { lock (_gate) return [.. _items]; }

    public static TestDataSchemaAssociationView View(TestDataSchemaAssociation a) =>
        new(a.Id, a.SchemaPath, a.TargetNamespace, a.DeclaredVersion, a.PrimaryFingerprint, [.. a.Files.Select(f => f.Path)], a.AssociatedBy, a.AssociatedAt, a.Note);

    /// <summary>Validates the package with the shared XSD capability (dependency closure + analysis) before storing it; refuses anything it cannot read.</summary>
    public (TestDataSchemaAssociation? Association, string? Error) Associate(TestDataSchemaAssociationRequest request, string by, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.SchemaPath)) return (null, "A schema path is required.");
        if (request.Files is not { Count: > 0 } || request.Files.Count > MaxFiles) return (null, $"Provide 1–{MaxFiles} XSD files.");
        if (request.Files.Any(f => string.IsNullOrWhiteSpace(f.Path) || f.Text is null || System.Text.Encoding.UTF8.GetByteCount(f.Text) > MaxFileBytes))
            return (null, "Every file needs a path and at most 2 MB of text.");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in request.Files) map[f.Path.Replace('\\', '/')] = f.Text;
        var schemaPath = request.SchemaPath.Replace('\\', '/');
        if (!map.ContainsKey(schemaPath)) return (null, $"The package does not contain {schemaPath}.");
        IReadOnlyDictionary<string, string> closure;
        string? ns = null, version = null;
        try
        {
            closure = XmlSchemaPackageAnalyzer.DependencyClosure(map, schemaPath);
            var analysis = XmlSchemaPackageAnalyzer.Analyze(closure);
            var file = analysis.Files.FirstOrDefault(f => string.Equals(f.Name, schemaPath, StringComparison.OrdinalIgnoreCase));
            (ns, version) = (file?.TargetNamespace, file?.Version);
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            return (null, "The XSD package could not be read: " + XmlSchemaPackageAnalyzer.SafeIssue(e.Message));
        }
        var files = closure.Select(f => new TestDataSchemaAssociationFile(f.Key, f.Value, XmlSchemaPackageAnalyzer.Fingerprint(f.Value))).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            var primary = files.First(f => string.Equals(f.Path, schemaPath, StringComparison.OrdinalIgnoreCase)).Fingerprint;
            if (_items.FirstOrDefault(a => string.Equals(a.SchemaPath, schemaPath, StringComparison.OrdinalIgnoreCase) && a.PrimaryFingerprint == primary
                    && a.Files.Select(f => f.Fingerprint).SequenceEqual(files.Select(f => f.Fingerprint))) is { } same)
                return (same, null);
            var association = new TestDataSchemaAssociation("tdsa_" + Guid.NewGuid().ToString("N"), schemaPath, ns, version, files, by, now, request.Note);
            _items.Add(association);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
            File.Move(tmp, _path, overwrite: true);
            return (association, null);
        }
    }

    public void Clear()
    {
        lock (_gate) { _items = []; if (File.Exists(_path)) File.Delete(_path); }
    }
}

/// <summary>
/// Schema evidence resolution for Test Data, in trust order: a stored Source Analysis snapshot first (verified schema), then — only when no
/// snapshot retains the schema — an explicit association whose content matches the reference's declared fingerprint when one is declared.
/// Several associations with different content for the path are never chosen between: the result is unavailable until the reference is pinned.
/// </summary>
public sealed class CompositeTestDataSchemaSource(SourceAnalysisTestDataSchemaSource snapshots, TestDataSchemaAssociationStore associations) : ITestDataSchemaSource
{
    public async Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct)
    {
        if (await snapshots.FindAsync(reference, ct) is { } fromSnapshot) return fromSnapshot;
        if (reference.SourceSnapshotId is not null) return null; // pinned to a snapshot: never substituted
        return FromAssociations(associations.All(), reference);
    }

    public static TestDataSchemaPackage? FromAssociations(IReadOnlyList<TestDataSchemaAssociation> all, TestDataSchemaReference reference)
    {
        var matching = all.Where(a => string.Equals(a.SchemaPath, reference.SchemaPath, StringComparison.OrdinalIgnoreCase)
                                      && (reference.SchemaFingerprint is null || string.Equals(a.PrimaryFingerprint, reference.SchemaFingerprint, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matching.Count == 0) return null;
        var chosen = matching.OrderByDescending(a => a.AssociatedAt).First();
        var distinct = matching.Select(a => a.PrimaryFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new(Guid.Empty, chosen.Files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase), true, distinct)
        {
            Origin = TestDataSchemaOrigin.ExplicitAssociation, AssociationId = chosen.Id,
        };
    }
}
