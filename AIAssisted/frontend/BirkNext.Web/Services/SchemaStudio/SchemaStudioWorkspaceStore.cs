using System.Text;
using System.Text.Json;

namespace BirkNext.Web.Services.SchemaStudio;

public sealed record SavedSchemaPackage(string Id, string Name, string Fingerprint, DateTimeOffset SavedAt,
    string? ProjectScope, SchemaProvenance? Provenance, IReadOnlyDictionary<string, string>? Files);

/// <summary>
/// Bounded schema-analysis history stored as a normal BirkNext workspace artifact. Source Analysis packages retain an
/// immutable snapshot reference; locally uploaded packages retain their XSD files so they can be reopened after restart.
/// </summary>
public sealed class SchemaStudioWorkspaceStore
{
    public const int MaxSavedPackages = 20;
    public const int MaxPackageContentBytes = 20 * 1024 * 1024;
    public const int MaxDocumentBytes = 24 * 1024 * 1024;
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SavedSchemaPackage> _packages = [];

    public IReadOnlyList<SavedSchemaPackage> Packages => _packages.OrderByDescending(p => p.SavedAt).ToArray();

    public SavedSchemaPackage Save(SchemaPackage package, string? projectScope)
    {
        var scope = NormalizeScope(projectScope);
        if (package.Provenance is { SourceSnapshotId: not null } sourceProvenance
            && string.IsNullOrWhiteSpace(sourceProvenance.SourceSnapshotFingerprint))
            throw new InvalidDataException("Source-bound packages require the exact snapshot fingerprint before they can be saved.");
        var isSourceBound = package.Provenance is { SourceSnapshotId: not null };
        var contentBytes = isSourceBound ? 0 : package.Files.Sum(f => Encoding.UTF8.GetByteCount(f.Text));
        if (contentBytes > MaxPackageContentBytes) throw new InvalidDataException("Saved XSD files exceed the 20 MB workspace limit.");
        var id = package.Fingerprint + (isSourceBound ? ":" + package.Provenance!.SourceSnapshotId : ":local");
        var saved = new SavedSchemaPackage(id, package.Name, package.Fingerprint, DateTimeOffset.UtcNow, scope,
            package.Provenance, isSourceBound ? null : package.Files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase));
        var prior = _packages.FindIndex(p => p.Id == id && p.ProjectScope == scope);
        var priorPackage = prior >= 0 ? _packages[prior] : null;
        if (prior >= 0) _packages.RemoveAt(prior);
        else if (_packages.Count >= MaxSavedPackages) throw new InvalidOperationException($"A workspace supports at most {MaxSavedPackages} saved Schema Studio packages.");
        _packages.Add(saved);
        try { EnsureDocumentLimit(); }
        catch
        {
            _packages.Remove(saved);
            if (priorPackage is not null) _packages.Add(priorPackage);
            throw;
        }
        return saved;
    }

    public bool Remove(string id, string? projectScope)
    {
        var scope = NormalizeScope(projectScope);
        var removed = _packages.RemoveAll(p => p.Id == id && p.ProjectScope == scope) > 0;
        return removed;
    }

    public SchemaPackage ReopenLocal(SavedSchemaPackage saved, SchemaStudioService studio)
    {
        if (saved.Files is null) throw new InvalidOperationException("This saved package is bound to Source Analysis and must be reopened from its snapshot.");
        var package = studio.Import(saved.Files, saved.Name, saved.Provenance);
        if (!string.Equals(package.Fingerprint, saved.Fingerprint, StringComparison.Ordinal))
        {
            studio.Remove(package.Id);
            throw new InvalidDataException("Saved XSD content does not match the package fingerprint recorded at save time.");
        }
        return package;
    }

    public string SnapshotJson() => SnapshotJson(null, includeAllWhenScopeOmitted: true);

    public string SnapshotJson(string? projectScope) => SnapshotJson(projectScope, includeAllWhenScopeOmitted: false);

    private string SnapshotJson(string? projectScope, bool includeAllWhenScopeOmitted)
    {
        var scope = NormalizeScope(projectScope);
        var selected = includeAllWhenScopeOmitted ? _packages : _packages.Where(p => p.ProjectScope == scope).ToList();
        var json = JsonSerializer.Serialize(new WorkspaceDocument(CurrentVersion, selected), Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Saved Schema Studio packages exceed the 24 MB workspace limit.");
        return json;
    }

    public void RestoreJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { _packages.Clear(); return; }
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Saved Schema Studio packages exceed the 24 MB workspace limit.");
        var document = JsonSerializer.Deserialize<WorkspaceDocument>(json, Json)
            ?? throw new InvalidDataException("Saved Schema Studio package document is empty.");
        if (document.Version != CurrentVersion) throw new InvalidDataException("Saved Schema Studio package document uses an unsupported version.");
        if (document.Packages is null || document.Packages.Count > MaxSavedPackages)
            throw new InvalidDataException($"A workspace supports at most {MaxSavedPackages} saved Schema Studio packages.");
        if (document.Packages.Any(package => package is null))
            throw new InvalidDataException("Saved Schema Studio package document contains an empty entry.");
        foreach (var package in document.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 500 || string.IsNullOrWhiteSpace(package.Fingerprint)
                || string.IsNullOrWhiteSpace(package.Id) || package.Id.Length > 256 || package.Files is { Count: > SchemaStudioService.MaxFiles }
                || package.ProjectScope is { Length: > 512 })
                throw new InvalidDataException("Saved Schema Studio package metadata is invalid.");
            if (package.Provenance is { SourceSnapshotId: not null } source
                && (string.IsNullOrWhiteSpace(source.SourceSnapshotFingerprint) || package.Files is { Count: > 0 }))
                throw new InvalidDataException("Source-bound packages must identify their immutable snapshot and reference it instead of copying XSD files.");
            if (package.Provenance?.SourceSnapshotId is null && package.Files is null)
                throw new InvalidDataException("A local saved package must retain its bounded XSD files.");
            if (package.Files is { } files)
            {
                if (files.Keys.Any(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')
                    || path.Length > 1024 || path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")))
                    throw new InvalidDataException("Saved Schema Studio package contains an unsafe XSD path.");
                if (files.Any(f => !f.Key.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
                    || f.Value is null || Encoding.UTF8.GetByteCount(f.Value) > SchemaStudioService.MaxFileBytes)
                    || files.Sum(f => f.Value is null ? 0 : Encoding.UTF8.GetByteCount(f.Value)) > MaxPackageContentBytes)
                    throw new InvalidDataException("Saved Schema Studio package exceeds its file or content limits.");
            }
        }
        _packages.Clear();
        _packages.AddRange(document.Packages);
    }

    private void EnsureDocumentLimit()
    {
        var json = JsonSerializer.Serialize(new WorkspaceDocument(CurrentVersion, _packages), Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Saving this package would exceed the 24 MB Schema Studio workspace limit.");
    }

    private static string? NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
    private sealed record WorkspaceDocument(int Version, IReadOnlyList<SavedSchemaPackage>? Packages);
}
