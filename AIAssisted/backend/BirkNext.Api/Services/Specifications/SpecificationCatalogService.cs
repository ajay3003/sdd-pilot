using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Specifications;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Specifications;

/// <summary>
/// The server-held requirement catalog of each imported project. Captured once per specification content revision at Project Import, while the
/// archive's documents are still in memory, with the same role classifier and specification parser the browser uses (compiled into the API), so
/// a requirement the browser lists and one the server lists are the same requirement. Requirement links are checked against it instead of the
/// list the browser sends. Projects imported before the catalog existed have none: their links stay as recorded and are reported as such.
/// </summary>
public sealed class SpecificationCatalogService(AppDbContext db, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxRequirements = 5000;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public static string ProjectKeyOf(string importId) => $"import:{importId}";

    public sealed record Document(string Path, string Content);

    /// <summary>The requirements of every specification file among <paramref name="documents"/> (classified by the shared role classifier). The same
    /// requirement id in two files is two entries; within one file the parser's first reading wins (as in the browser).</summary>
    public static (List<SpecificationDocumentRef> Documents, List<SpecificationRequirementEntry> Entries) Read(IEnumerable<Document> documents)
    {
        var all = documents.Where(d => d.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
        var roles = ArtifactDocumentDiscovery.Classify(all.Select(d => new ArtifactDocumentDiscovery.Candidate(d.Path, System.IO.Path.GetFileName(d.Path), d.Content)));
        var specs = roles.Where(r => r.Role == WorkspaceArtifactType.Specification).Select(r => r.RelativePath).ToHashSet(StringComparer.Ordinal);
        var refs = new List<SpecificationDocumentRef>();
        var entries = new List<SpecificationRequirementEntry>();
        foreach (var doc in all.Where(d => specs.Contains(d.Path)).OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var path = SpecificationCatalogRules.NormalizePath(doc.Path);
            refs.Add(new(path, Sha(doc.Content)));
            var model = SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(doc.Content), doc.Content);
            foreach (var r in model.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.Id)).GroupBy(r => SpecificationCatalogRules.NormalizeId(r.Id)).Select(g => g.First()))
            {
                if (entries.Count >= MaxRequirements) break;
                var title = r.Text.Trim();
                entries.Add(new(r.Id.Trim(), path, title.Length > 500 ? title[..500] : title, Sha(title)));
            }
        }
        return (refs, entries);
    }

    /// <summary>Captures the catalog of an import. Idempotent per content: the same specification content is the existing revision; changed content
    /// is the next revision; an archive with no specification writes nothing (the project keeps its previous catalog, if any).</summary>
    public async Task<SpecificationCatalogView?> CaptureAsync(string importId, string? archiveSha256, Guid? snapshotId, IEnumerable<Document> documents, CancellationToken ct)
    {
        var (refs, entries) = Read(documents);
        if (refs.Count == 0) return null;
        var key = ProjectKeyOf(importId);
        var fingerprint = Sha(string.Join("\n", refs.Select(r => $"{r.Path}\u001f{r.Sha256}")) + "\n#\n" + string.Join("\n", entries.Select(e => $"{e.Path}\u001f{e.RequirementId}\u001f{e.TextSha256}")));
        if (await db.SpecificationCatalogs.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectKey == key && c.Fingerprint == fingerprint, ct) is { } same) return View(same);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = await db.SpecificationCatalogs.Where(c => c.ProjectKey == key).MaxAsync(c => (int?)c.Revision, ct) ?? 0;
            var record = new SpecificationCatalogRecord
            {
                Id = Guid.NewGuid(), ProjectKey = key, Revision = latest + 1, Fingerprint = fingerprint, ArchiveSha256 = archiveSha256, SnapshotId = snapshotId,
                CapturedAt = Now, DocumentsJson = JsonSerializer.Serialize(refs, Json), EntriesJson = JsonSerializer.Serialize(entries, Json),
            };
            db.SpecificationCatalogs.Add(record);
            try { await db.SaveChangesAsync(ct); return View(record); }
            catch (DbUpdateException)
            {
                // A concurrent capture won the revision number, or wrote the same content: read again and retry once more.
                db.Entry(record).State = EntityState.Detached;
                if (await db.SpecificationCatalogs.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectKey == key && c.Fingerprint == fingerprint, ct) is { } won) return View(won);
            }
        }
        throw new InvalidOperationException("The specification catalog could not be saved.");
    }

    public async Task<SpecificationCatalogView?> CurrentAsync(string projectKey, CancellationToken ct) =>
        await db.SpecificationCatalogs.AsNoTracking().Where(c => c.ProjectKey == projectKey).OrderByDescending(c => c.Revision).FirstOrDefaultAsync(ct) is { } r ? View(r) : null;

    public async Task<List<SpecificationCatalogView>> RevisionsAsync(string projectKey, CancellationToken ct) =>
        [.. (await db.SpecificationCatalogs.AsNoTracking().Where(c => c.ProjectKey == projectKey).OrderByDescending(c => c.Revision).Take(50).ToListAsync(ct)).Select(View)];

    /// <summary>The outcome of resolving one requirement a caller wants to link.</summary>
    public sealed record Resolution(SpecificationRequirementEntry? Entry, SpecificationCatalogView? Catalog, string? Problem);

    /// <summary>
    /// Resolves a requirement against the project's current catalog. Null catalog = the project has none (the caller falls back to its legacy
    /// check and says so). Otherwise the id must exist; when the same id is defined in several specification files the path must say which.
    /// </summary>
    public static Resolution Resolve(SpecificationCatalogView? catalog, string requirementId, string? specificationPath)
    {
        if (catalog is null) return new(null, null, null);
        var id = SpecificationCatalogRules.NormalizeId(requirementId);
        var matches = catalog.Requirements.Where(e => SpecificationCatalogRules.NormalizeId(e.RequirementId) == id).ToList();
        if (matches.Count == 0)
            return new(null, catalog, $"{requirementId.Trim()} is not a requirement of this project's imported specification (revision {catalog.Revision}).");
        var inPath = string.IsNullOrWhiteSpace(specificationPath) ? matches : matches.Where(e => SpecificationCatalogRules.SamePath(e.Path, specificationPath)).ToList();
        return inPath.Count switch
        {
            1 => new(inPath[0], catalog, null),
            0 => new(null, catalog, $"{requirementId.Trim()} is not in {specificationPath}; this project's specification defines it in {string.Join(", ", matches.Select(m => m.Path))}."),
            _ => new(null, catalog, $"{requirementId.Trim()} is defined in {inPath.Count} specification files ({string.Join(", ", inPath.Select(m => m.Path))}); choose the file."),
        };
    }

    /// <summary>Checks recorded references against the current catalog (read-only: links are never changed or deleted by a check).</summary>
    public static RequirementReferenceCheckResult Check(string projectKey, SpecificationCatalogView? catalog, IEnumerable<RequirementReference> references)
    {
        var checks = new List<RequirementReferenceCheck>();
        foreach (var r in references.Take(MaxRequirements))
        {
            if (catalog is null)
            {
                checks.Add(new(r.RequirementId, r.SpecificationPath, RequirementSourceState.NoServerCatalog,
                    "This project was imported before BirkNext kept a server copy of the specification (or the import had none). Import the project again to verify its links.", null, null, null));
                continue;
            }
            var resolved = Resolve(catalog, r.RequirementId, r.SpecificationPath);
            if (resolved.Entry is not { } e)
            {
                checks.Add(new(r.RequirementId, r.SpecificationPath, RequirementSourceState.NotInCurrentSpecification, resolved.Problem ?? "Not found.", catalog.Revision, null, null));
                continue;
            }
            var changed = r.LinkedTextSha256 is null || !string.Equals(r.LinkedTextSha256, e.TextSha256, StringComparison.OrdinalIgnoreCase);
            checks.Add(new(r.RequirementId, r.SpecificationPath, changed ? RequirementSourceState.ChangedSinceLinked : RequirementSourceState.Current,
                changed ? (r.LinkedTextSha256 is null ? "In the current specification; the link does not record the wording it was made against, so compare it." : "In the current specification, but its wording changed since the link was made.")
                        : "In the current specification with the wording it was linked against.",
                catalog.Revision, e.Title, e.Path));
        }
        return new(projectKey, catalog?.Revision, catalog?.CapturedAt, checks);
    }

    private static SpecificationCatalogView View(SpecificationCatalogRecord r) => new(r.Id, r.ProjectKey, r.Revision, r.CapturedAt, r.Fingerprint, r.ArchiveSha256, r.SnapshotId,
        JsonSerializer.Deserialize<List<SpecificationDocumentRef>>(r.DocumentsJson, Json) ?? [], JsonSerializer.Deserialize<List<SpecificationRequirementEntry>>(r.EntriesJson, Json) ?? []);

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
