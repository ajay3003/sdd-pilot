using System.Text;
using System.Text.Json;

namespace BirkNext.Web.Services.SchemaStudio;

public sealed record SavedSchemaXml(Guid Id, string PackageFingerprint, SchemaRunProvenance? Provenance,
    string FileName, string ProfileName, Guid? ProfileId, int? Seed, string Xml, DateTimeOffset SavedAt, string? ProjectScope);

/// <summary>Explicitly saved, bounded generated XML evidence. XML content is retained only on user request.</summary>
public sealed class SchemaGeneratedXmlStore
{
    public const int MaxDocuments = 20;
    public const int MaxDocumentBytes = 24 * 1024 * 1024;
    private const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SavedSchemaXml> _documents = [];

    public IReadOnlyList<SavedSchemaXml> Documents => _documents.OrderByDescending(item => item.SavedAt).ToArray();

    public SavedSchemaXml Save(SchemaPackage package, string fileName, string profileName, int? seed,
        string xml, string? projectScope = null, Guid? profileId = null)
    {
        if (!package.Files.Any(file => string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected XSD file is not part of this schema package.");
        if (string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > SchemaStudioService.MaxPackageBytes)
            throw new InvalidDataException("Saved XML must contain at most 20 MB of content.");
        if (string.IsNullOrWhiteSpace(package.Fingerprint) || package.Fingerprint.Length != 64
            || !package.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("The schema fingerprint is invalid.");
        if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 120)
            throw new InvalidDataException("Profile identity must contain 1 to 120 characters.");
        var scope = NormalizeScope(projectScope);
        if (scope is { Length: > 512 }) throw new InvalidDataException("The XML workspace scope exceeds 512 characters.");
        var provenance = package.Provenance is { SourceSnapshotId: not null } source
            ? new SchemaRunProvenance(source.SourceSnapshotId, source.SourceSnapshotFingerprint,
                source.SelectedContractPath, source.ContractFingerprint)
            : null;
        if (provenance is { SourceSnapshotId: not null } && string.IsNullOrWhiteSpace(provenance.SourceSnapshotFingerprint))
            throw new InvalidDataException("Source-bound XML requires its immutable snapshot fingerprint.");

        var saved = new SavedSchemaXml(Guid.NewGuid(), package.Fingerprint, provenance, fileName,
            profileName.Trim(), profileId, seed, xml, DateTimeOffset.UtcNow, scope);
        var previous = _documents.ToArray();
        if (_documents.Count >= MaxDocuments) _documents.Remove(_documents.OrderBy(item => item.SavedAt).First());
        _documents.Add(saved);
        try { EnsureSize(); }
        catch { _documents.Clear(); _documents.AddRange(previous); throw; }
        return saved;
    }

    public bool Remove(Guid id, string? projectScope = null) =>
        _documents.RemoveAll(item => item.Id == id && item.ProjectScope == NormalizeScope(projectScope)) > 0;

    public string SnapshotJson(string? projectScope = null, bool includeAllWhenScopeOmitted = false)
    {
        var scope = NormalizeScope(projectScope);
        IReadOnlyList<SavedSchemaXml> items = includeAllWhenScopeOmitted
            ? _documents : _documents.Where(item => item.ProjectScope == scope).ToArray();
        var json = JsonSerializer.Serialize(new Document(Version, items), Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio XML exceeds the 24 MB workspace limit.");
        return json;
    }

    public void RestoreJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { _documents.Clear(); return; }
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio XML exceeds the 24 MB workspace limit.");
        var document = JsonSerializer.Deserialize<Document>(json, Json)
            ?? throw new InvalidDataException("Saved Schema Studio XML document is empty.");
        if (document.Version != Version || document.Items is null || document.Items.Count > MaxDocuments
            || document.Items.Any(item => item is null || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.PackageFingerprint)
                || item.PackageFingerprint.Length != 64 || !item.PackageFingerprint.All(Uri.IsHexDigit)
                || !SafeXsdPath(item.FileName) || string.IsNullOrWhiteSpace(item.ProfileName) || item.ProfileName.Length > 120
                || item.ProfileId == Guid.Empty
                || item.Xml is null || Encoding.UTF8.GetByteCount(item.Xml) > SchemaStudioService.MaxPackageBytes
                || item.ProjectScope is { Length: > 512 }
                || item.Provenance is { } provenance && (provenance.SourceSnapshotId is not null
                    && string.IsNullOrWhiteSpace(provenance.SourceSnapshotFingerprint)
                    || provenance.SourceSnapshotFingerprint is { Length: > 128 }
                    || provenance.SelectedContractPath is { Length: > 1024 }
                    || string.IsNullOrWhiteSpace(provenance.ContractFingerprint) || provenance.ContractFingerprint.Length > 128)))
            throw new InvalidDataException("Saved Schema Studio XML metadata is invalid or exceeds its limits.");
        if (document.Items.Select(item => item.Id).Distinct().Count() != document.Items.Count)
            throw new InvalidDataException("Saved Schema Studio XML contains duplicate identities.");
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document, Json)) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio XML exceeds the 24 MB workspace limit.");
        _documents.Clear();
        _documents.AddRange(document.Items);
    }

    private void EnsureSize()
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Document(Version, _documents), Json)) > MaxDocumentBytes)
            throw new InvalidDataException("Saving this XML would exceed the 24 MB Schema Studio workspace limit.");
    }

    private static bool SafeXsdPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
        && !path.Contains(':') && path.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
        && !path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..");
    private static string? NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
    private sealed record Document(int Version, IReadOnlyList<SavedSchemaXml>? Items);
}
