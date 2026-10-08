using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>The documents of one preview, classified by the generic artifact discovery. Nothing here is activated.</summary>
public sealed record ProjectImportArtifactDiscovery(
    IReadOnlyList<DiscoveredDocument> Documents,
    IReadOnlyList<SampleRoleSummary> Roles,
    IReadOnlyList<ProjectImportSkippedDocument> Skipped)
{
    public IReadOnlyList<SampleRoleSummary> DetectedRoles => Roles.Where(r => r.State != SampleRoleState.NotFound).ToList();
    public IReadOnlyList<SampleRoleSummary> AmbiguousRoles => Roles.Where(r => r.State == SampleRoleState.Multiple).ToList();
    public IReadOnlyList<DiscoveredDocument> NeedsReview => Documents.Where(d => d.Status == ArtifactDiscoveryStatus.NeedsReview).ToList();
    public IReadOnlyList<DiscoveredDocument> Unclassified => Documents.Where(d => d.Status is ArtifactDiscoveryStatus.Unclassified or ArtifactDiscoveryStatus.ParseError).ToList();
    /// <summary>Documents that become artifact revisions: every detected document (a role with several keeps all of them, none selected).</summary>
    public IReadOnlyList<DiscoveredDocument> ArtifactDocuments => Roles.SelectMany(r => r.Documents).ToList();
    public bool HasArtifacts => ArtifactDocuments.Count > 0;

    public static ProjectImportArtifactDiscovery From(ProjectImportPreview preview) =>
        FromAsync(preview, progress: null, yieldEvery: 0).GetAwaiter().GetResult();

    /// <summary>
    /// The preview's document roles. The backend classifies with the same classifier (its source is compiled into both projects), so its
    /// result is used as is; only a preview without it (an older backend) is classified here, reporting progress.
    /// </summary>
    public static async Task<ProjectImportArtifactDiscovery> FromAsync(ProjectImportPreview preview, IProgress<(int Done, int Total)>? progress,
        int yieldEvery = 8, CancellationToken cancellationToken = default)
    {
        var documents = ServerDiscovery(preview) ?? await ArtifactDocumentDiscovery.ClassifyAsync(preview.Documents.Select(d =>
            new ArtifactDocumentDiscovery.Candidate(d.RelativePath, d.FileName, d.Content)), progress, yieldEvery, cancellationToken);
        return new(documents, ArtifactDocumentDiscovery.Roles(documents, _ => null), preview.SkippedDocuments);
    }

    /// <summary>The backend's classification when it covers exactly the preview's documents; otherwise null (classify here instead).</summary>
    public static List<DiscoveredDocument>? ServerDiscovery(ProjectImportPreview preview) =>
        preview.Discovery is { } server && server.Select(d => d.RelativePath).Order(StringComparer.Ordinal)
            .SequenceEqual(preview.Documents.Select(d => d.RelativePath).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            ? server.OrderBy(d => d.RelativePath, StringComparer.Ordinal).ToList()
            : null;
}

/// <summary>
/// Activates a Project Import in the Shared Artifact Repository. Detected documents become artifact revisions in the import's own scope
/// (<see cref="ProjectImportScope"/>), with origin <c>ProjectImport</c> and the import id as provenance; a role with one document is selected,
/// a role with several waits for the user's choice. The import becomes the current project, replacing a selected Sample Project; the
/// manual workspace's documents and earlier imports stay in the repository as history and are not shown with this import.
/// Source Analysis owns the snapshot; this records only its id as shared provenance.
/// </summary>
public sealed class ProjectImportActivation(WorkspaceArtifactRepository repository, IWorkspaceUpdateCoordinator? updates = null)
{
    public const string Origin = "ProjectImport";

    public SddProjectImportRecord? Current =>
        repository.SddLifecycle.CurrentProjectImportId is { } id ? repository.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == id) : null;

    public SddProjectImportRecord Activate(ProjectImportPreview preview, ProjectImportArtifactDiscovery discovery, ProjectImportCommitResult? commit)
    {
        var lifecycle = repository.SddLifecycle;
        // A selected Sample Project is replaced by the imported project (one current project).
        repository.ProjectName = null;
        var record = lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == preview.ImportId);
        if (record is null) lifecycle.ProjectImports.Add(record = new SddProjectImportRecord { ImportId = preview.ImportId });
        record.ProjectName = preview.ProjectName;
        record.ProjectNameBasis = preview.ProjectNameBasis.ToString();
        record.ArchiveFileName = preview.Archive.FileName;
        record.ArchiveSha256 = preview.Archive.Sha256;
        record.ArchiveSizeBytes = preview.Archive.SizeBytes;
        record.ImportedAt = commit?.Provenance.ImportedAt ?? DateTimeOffset.UtcNow;
        record.DetectedRoles = discovery.Roles.Where(r => r.State == SampleRoleState.Detected).Select(r => r.Role.ToString()).ToList();
        record.AmbiguousRoles = discovery.AmbiguousRoles.Select(r => r.Role.ToString()).ToList();
        record.ArtifactDocumentCount = discovery.ArtifactDocuments.Count;
        ApplySource(record, commit?.Source, preview.Source.Detected);

        lifecycle.CurrentProjectImportId = preview.ImportId;
        var scope = ProjectImportScope.For(preview.ImportId)!;
        foreach (var role in discovery.Roles)
        {
            foreach (var document in role.Documents)
            {
                var content = preview.Documents.First(d => d.RelativePath == document.RelativePath).Content;
                // The archive-relative path is the document identity within the import (two spec.md in different folders stay two documents).
                repository.AddArtifactRevision(role.Role, ArtifactDocumentDiscovery.Normalize(content), document.RelativePath, document.RelativePath,
                    scope, Origin, select: role.State == SampleRoleState.Detected, projectImportId: preview.ImportId);
            }
        }
        // Session artifacts follow the import's scope only: roles the import does not provide (or has to choose) are not carried over.
        repository.RefreshSessionArtifacts();
        updates?.NotifyMutation();
        repository.NotifyArtifactsChanged();
        return record;
    }

    /// <summary>Records the source outcome of a retried commit on the current import (the documents are unchanged).</summary>
    public void UpdateSource(string importId, ProjectImportSourceResult source)
    {
        if (repository.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is not { } record) return;
        ApplySource(record, source, sourceDetected: true);
        updates?.NotifyMutation();
        repository.NotifyArtifactsChanged();
    }

    /// <summary>Leaves the imported project: the workspace returns to the manual workspace. The import's revisions stay as history.</summary>
    public void Close()
    {
        if (repository.SddLifecycle.CurrentProjectImportId is null) return;
        repository.SddLifecycle.CurrentProjectImportId = null;
        repository.RefreshSessionArtifacts();
        updates?.NotifyMutation();
        repository.NotifyArtifactsChanged();
    }

    private static void ApplySource(SddProjectImportRecord record, ProjectImportSourceResult? source, bool sourceDetected)
    {
        record.SourceState = (source?.State ?? (sourceDetected ? ProjectImportSourceState.NotCreated : ProjectImportSourceState.NotDetected)).ToString();
        record.SourceSnapshotId = source?.SnapshotId;
        record.SourceAnalyzedAt = source?.AnalyzedAt;
    }
}
