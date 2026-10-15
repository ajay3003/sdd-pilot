using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.Services.SchemaStudio;

public sealed record SavedSchemaDraft(string Id, string PackageFingerprint, Guid? SourceSnapshotId,
    string? SourceSnapshotFingerprint, string FileName, string Text, string? ProjectScope, DateTimeOffset UpdatedAt);

/// <summary>Bounded persistence for editable XSD drafts, kept separate from immutable imported package evidence.</summary>
public sealed class SchemaStudioDraftStore
{
    public const int MaxDrafts = 20;
    public const int MaxDocumentBytes = 24 * 1024 * 1024;
    private const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<SavedSchemaDraft> _drafts = [];

    public IReadOnlyList<SavedSchemaDraft> Drafts => _drafts.OrderByDescending(draft => draft.UpdatedAt).ToArray();

    public SavedSchemaDraft? Find(SchemaPackage package, string fileName, string? projectScope = null)
    {
        var id = DraftId(package.Fingerprint, package.Provenance?.SourceSnapshotId, fileName);
        return _drafts.FirstOrDefault(draft => draft.Id == id && draft.ProjectScope == NormalizeScope(projectScope)
            && draft.SourceSnapshotFingerprint == package.Provenance?.SourceSnapshotFingerprint);
    }

    public SavedSchemaDraft Save(SchemaPackage package, string fileName, string text, string? projectScope = null)
    {
        if (text is null) throw new InvalidDataException("Draft content is required.");
        if (package.Provenance is { SourceSnapshotId: not null } provenance
            && string.IsNullOrWhiteSpace(provenance.SourceSnapshotFingerprint))
            throw new InvalidDataException("Source-bound drafts require the immutable source snapshot fingerprint.");
        ValidateFileName(fileName);
        if (string.IsNullOrWhiteSpace(package.Fingerprint) || package.Fingerprint.Length > 128)
            throw new InvalidDataException("The source package fingerprint is invalid.");
        if (!package.Files.Any(file => string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected XSD file is not part of this schema package.");
        if (Encoding.UTF8.GetByteCount(text) > SchemaStudioService.MaxFileBytes)
            throw new InvalidDataException("A schema draft is limited to 2 MB.");
        var scope = NormalizeScope(projectScope);
        if (scope is { Length: > 512 }) throw new InvalidDataException("The draft workspace scope exceeds 512 characters.");
        var draft = new SavedSchemaDraft(DraftId(package.Fingerprint, package.Provenance?.SourceSnapshotId, fileName),
            package.Fingerprint, package.Provenance?.SourceSnapshotId, package.Provenance?.SourceSnapshotFingerprint,
            fileName, text, scope, DateTimeOffset.UtcNow);
        var prior = _drafts.FindIndex(item => item.Id == draft.Id && item.ProjectScope == scope);
        SavedSchemaDraft? previous = prior >= 0 ? _drafts[prior] : null;
        if (prior >= 0) _drafts.RemoveAt(prior);
        else if (_drafts.Count >= MaxDrafts) throw new InvalidOperationException($"A workspace supports at most {MaxDrafts} saved Schema Studio drafts.");
        _drafts.Add(draft);
        try { EnsureSize(); }
        catch
        {
            _drafts.Remove(draft);
            if (previous is not null) _drafts.Add(previous);
            throw;
        }
        return draft;
    }

    public bool Remove(SchemaPackage package, string fileName, string? projectScope = null)
    {
        var id = DraftId(package.Fingerprint, package.Provenance?.SourceSnapshotId, fileName);
        return _drafts.RemoveAll(draft => draft.Id == id && draft.ProjectScope == NormalizeScope(projectScope)) > 0;
    }

    public string SnapshotJson(string? projectScope = null, bool includeAllWhenScopeOmitted = false)
    {
        var scope = NormalizeScope(projectScope);
        var selected = includeAllWhenScopeOmitted ? _drafts : _drafts.Where(draft => draft.ProjectScope == scope).ToList();
        var json = JsonSerializer.Serialize(new Document(Version, selected), Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio drafts exceed the 24 MB workspace limit.");
        return json;
    }

    public void RestoreJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { _drafts.Clear(); return; }
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Saved Schema Studio drafts exceed the 24 MB workspace limit.");
        var document = JsonSerializer.Deserialize<Document>(json, Json)
            ?? throw new InvalidDataException("Saved Schema Studio draft document is empty.");
        if (document.Version != Version || document.Drafts is null || document.Drafts.Count > MaxDrafts
            || document.Drafts.Any(draft => draft is null || string.IsNullOrWhiteSpace(draft.Id)
                || draft.Id.Length > 128 || string.IsNullOrWhiteSpace(draft.PackageFingerprint)
                || draft.PackageFingerprint.Length > 128 || draft.SourceSnapshotFingerprint is { Length: > 128 }
                || draft.SourceSnapshotId is not null && string.IsNullOrWhiteSpace(draft.SourceSnapshotFingerprint)
                || draft.ProjectScope is { Length: > 512 } || draft.Text is null
                || Encoding.UTF8.GetByteCount(draft.Text) > SchemaStudioService.MaxFileBytes))
            throw new InvalidDataException("Saved Schema Studio drafts have invalid metadata or exceed their limits.");
        foreach (var draft in document.Drafts)
        {
            ValidateFileName(draft.FileName);
            if (!string.Equals(draft.Id, DraftId(draft.PackageFingerprint, draft.SourceSnapshotId, draft.FileName), StringComparison.Ordinal))
                throw new InvalidDataException("Saved Schema Studio draft identity does not match its package and file.");
        }
        if (document.Drafts.Select(draft => (draft.Id, draft.ProjectScope)).Distinct().Count() != document.Drafts.Count)
            throw new InvalidDataException("Saved Schema Studio drafts contain duplicate identities.");
        _drafts.Clear();
        _drafts.AddRange(document.Drafts);
    }

    private void EnsureSize()
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Document(Version, _drafts), Json)) > MaxDocumentBytes)
            throw new InvalidDataException("Saving this draft would exceed the 24 MB Schema Studio workspace limit.");
    }

    private static string DraftId(string fingerprint, Guid? sourceSnapshotId, string fileName)
    {
        var identity = string.Join("|", fingerprint, sourceSnapshotId?.ToString("D") ?? "local", fileName.Replace('\\', '/'));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 1024 || Path.IsPathRooted(fileName)
            || fileName.Contains(':') || !fileName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
            || fileName.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Draft XSD file path is invalid.");
    }

    private static string? NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
    private sealed record Document(int Version, IReadOnlyList<SavedSchemaDraft>? Drafts);
}
