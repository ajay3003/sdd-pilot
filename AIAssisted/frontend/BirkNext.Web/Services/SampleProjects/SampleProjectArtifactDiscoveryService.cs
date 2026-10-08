using System.Collections.Concurrent;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services.SampleProjects;

public sealed record SampleProjectDiscoveryResult(
    string ProjectSlug,
    IReadOnlyList<DiscoveredDocument> Documents,
    IReadOnlyList<SampleFileDto> UnsupportedFiles,
    IReadOnlyList<SampleFileDto> SkippedDocuments,
    SampleDiscoveryStatsDto? Stats,
    IReadOnlyList<SampleRoleSummary> Roles,
    string? Error)
{
    public IReadOnlyList<DiscoveredDocument> NeedsReview => Documents.Where(d => d.Status == ArtifactDiscoveryStatus.NeedsReview).ToList();
    public IReadOnlyList<DiscoveredDocument> Unclassified => Documents.Where(d => d.Status == ArtifactDiscoveryStatus.Unclassified).ToList();
    public IReadOnlyList<DiscoveredDocument> ParseErrors => Documents.Where(d => d.Status == ArtifactDiscoveryStatus.ParseError).ToList();
    public int DetectedCount => Documents.Count(d => d.Status == ArtifactDiscoveryStatus.Detected);
    public SampleRoleSummary Role(WorkspaceArtifactType role) => Roles.First(r => r.Role == role);
}

public interface ISampleProjectArtifactDiscovery
{
    /// <summary>Discovers and classifies the project's documents. Cached per project until its file inventory changes.</summary>
    Task<SampleProjectDiscoveryResult> DiscoverAsync(SampleProjectDto project, CancellationToken cancellationToken = default);

    /// <summary>Discovers a project by slug from the current catalog; null when the slug is not in the catalog.</summary>
    Task<SampleProjectDiscoveryResult?> DiscoverAsync(string projectSlug, CancellationToken cancellationToken = default);

    /// <summary>The text of a discovered document (from the discovery fetch); null when unavailable.</summary>
    string? GetContent(string projectSlug, string relativePath);

    /// <summary>Explicit choice of the document explorers use when a role has several documents (session only).</summary>
    void ChooseDocument(string projectSlug, WorkspaceArtifactType role, string? relativePath);

    /// <summary>
    /// Re-reads the catalog on the next request. Classified documents stay cached per project and are re-read only when
    /// that project's file inventory (paths, sizes, modification times) changes.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Sample Project document discovery: takes the backend's bounded recursive inventory, reads the candidate Markdown
/// documents and classifies each with <see cref="SampleArtifactClassifier"/>. Read-only; it never changes sample files.
/// It does not copy documents into the session: the selected project's documents are resolved on demand by
/// <see cref="SampleProjectDocumentResolver"/> from this result, by role rather than by filename.
/// </summary>
public sealed class SampleProjectArtifactDiscoveryService(SampleProjectsApiService api) : ISampleProjectArtifactDiscovery
{
    private sealed record CacheEntry(string InventoryKey, SampleProjectDiscoveryResult Result, IReadOnlyDictionary<string, string> Contents);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Slug, WorkspaceArtifactType Role), string> _choices = new();
    private Task<List<SampleProjectDto>>? _catalog;

    public async Task<SampleProjectDiscoveryResult> DiscoverAsync(SampleProjectDto project, CancellationToken cancellationToken = default)
    {
        var key = InventoryKey(project);
        if (_cache.TryGetValue(project.Slug, out var cached) && cached.InventoryKey == key)
            return WithChoices(cached.Result);

        var candidates = project.Files.Where(f => f.IsSupported && f.Exists).ToList();
        var contents = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? error = null;
        if (candidates.Count > 0)
        {
            var bulk = await api.GetDocumentsAsync(project.Slug);
            if (bulk is not null)
            {
                foreach (var doc in bulk) contents[doc.RelativePath] = doc.Content;
            }
            else
            {
                foreach (var file in candidates)
                    contents[PathOf(file)] = await api.GetFileAsync(project.Slug, PathOf(file));
                if (contents.Values.All(v => v is null)) error = "The project documents could not be read from the backend.";
            }
        }

        var documents = ArtifactDocumentDiscovery.Classify(candidates.Select(file =>
            new ArtifactDocumentDiscovery.Candidate(PathOf(file), file.Filename, contents.GetValueOrDefault(PathOf(file)))));

        var result = new SampleProjectDiscoveryResult(
            project.Slug,
            documents,
            project.Files.Where(f => !f.IsSupported && f.SkipReason is null).OrderBy(PathOf, StringComparer.Ordinal).ToList(),
            project.Files.Where(f => f.SkipReason is not null).OrderBy(PathOf, StringComparer.Ordinal).ToList(),
            project.Discovery,
            [],
            error);
        var textByPath = contents.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => Normalize(kv.Value!), StringComparer.Ordinal);
        _cache[project.Slug] = new CacheEntry(key, result, textByPath);
        return WithChoices(result);
    }

    public async Task<SampleProjectDiscoveryResult?> DiscoverAsync(string projectSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectSlug)) return null;
        List<SampleProjectDto> projects;
        try { projects = await (_catalog ??= api.GetProjectsAsync()); }
        catch { _catalog = null; return null; }
        var project = projects.FirstOrDefault(p => p.Slug.Equals(projectSlug, StringComparison.OrdinalIgnoreCase));
        return project is null ? null : await DiscoverAsync(project, cancellationToken);
    }

    public string? GetContent(string projectSlug, string relativePath) =>
        _cache.TryGetValue(projectSlug, out var entry) && entry.Contents.TryGetValue(relativePath, out var text) ? text : null;

    public void ChooseDocument(string projectSlug, WorkspaceArtifactType role, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) _choices.TryRemove((projectSlug, role), out _);
        else _choices[(projectSlug, role)] = relativePath;
    }

    public void Invalidate() => _catalog = null;

    private SampleProjectDiscoveryResult WithChoices(SampleProjectDiscoveryResult result) => result with
    {
        Roles = ArtifactDocumentDiscovery.Roles(result.Documents, role => _choices.TryGetValue((result.ProjectSlug, role), out var chosen) ? chosen : null),
    };

    private static string PathOf(SampleFileDto file) => (file.RelativePath ?? file.Filename).Replace('\\', '/');

    /// <summary>Cache key: changes when any file is added, removed, resized or modified.</summary>
    private static string InventoryKey(SampleProjectDto project) =>
        ArtifactFingerprint.Compute(string.Join('\n', project.Files
            .Select(f => $"{PathOf(f)}|{f.SizeBytes}|{f.LastModifiedUtc?.Ticks}|{f.Exists}|{f.IsSupported}")
            .Order(StringComparer.Ordinal)));

    private static string Normalize(string text) => ArtifactDocumentDiscovery.Normalize(text);
}
