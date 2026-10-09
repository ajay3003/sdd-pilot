using System.Text;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services.Explorers;

/// <summary>Where an explorer artifact comes from. Only metadata: explorers treat every source the same way.</summary>
public enum ExplorerArtifactSource
{
    /// <summary>A document of the selected Sample Project, found by generic discovery and classified by role.</summary>
    SampleProject,
    /// <summary>A document imported into the workspace (file, drop or paste, or a Project Import archive) and kept as an artifact revision.</summary>
    Workspace,
}

public enum ExplorerArtifactCurrentness { Current, Historical, Superseded }

/// <summary>One artifact of a role, as the explorer lists it. The file name is metadata, never the artifact's identity.</summary>
public sealed record ExplorerArtifact(
    string Id,
    WorkspaceArtifactType Role,
    string DisplayName,
    string FileName,
    string? SourcePath,
    ExplorerArtifactSource Source,
    string? Origin,
    int? Revision,
    int RevisionCount,
    string Authority,
    ExplorerArtifactCurrentness Currentness,
    DateTimeOffset? CapturedAt,
    string? DocumentIdentity = null)
{
    /// <summary>Approved or Baseline. Explicit repository authority only; never inferred from being latest or first.</summary>
    public bool IsAuthoritative => Authority is "Approved" or "Baseline";
}

public enum ExplorerArtifactStatus
{
    /// <summary>No artifact with this role in the current workspace.</summary>
    Empty,
    /// <summary>Several artifacts with this role and nothing decides between them: the user chooses.</summary>
    SelectionRequired,
    Loaded,
    /// <summary>The selected project is no longer in the Sample Projects catalog.</summary>
    ProjectUnavailable,
    /// <summary>The workspace's documents could not be read. Not the same as "no artifact".</summary>
    Error,
}

public enum ExplorerSelectionReason
{
    /// <summary>Chosen by the user: in an explorer, on Sample Projects, or by importing it.</summary>
    Explicit,
    /// <summary>The only Approved/Baseline artifact of the role.</summary>
    Authoritative,
    /// <summary>The only artifact of the role.</summary>
    OnlyArtifact,
}

public sealed record ArtifactExplorerState(
    WorkspaceArtifactType Role,
    ExplorerArtifactStatus Status,
    string? ProjectSlug,
    string? ProjectName,
    IReadOnlyList<ExplorerArtifact> Artifacts,
    ExplorerArtifact? Selected = null,
    ExplorerSelectionReason? Reason = null,
    string? Content = null,
    string? Error = null)
{
    public bool HasProject => ProjectSlug is not null;
}

public sealed record ArtifactImportRequest(WorkspaceArtifactType Role, string Content, string? FileName, string Origin);

/// <summary>Result of an import. <see cref="SuggestedRole"/> is a warning only: the role the user chose is kept.</summary>
public sealed record ArtifactImportResult(string? ArtifactId, string? Error, WorkspaceArtifactType? SuggestedRole)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// What every document explorer reads: the artifacts of one ROLE in the current workspace, from whichever source they came.
/// Explorers never look up files by name and do not depend on a Sample Project being selected.
/// </summary>
public interface IArtifactExplorerContext
{
    /// <summary>Raised when artifacts, selection, authority or the selected project change, and after a reset.</summary>
    event EventHandler? Changed;

    /// <summary>The workspace scope: the selected Sample Project slug, the current imported project (<c>import:{id}</c>), or null for the manual workspace.</summary>
    string? CurrentScope { get; }

    Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default);

    /// <summary>Chooses which artifact the explorers show for the role. Not authority: approval and baselines do not change.</summary>
    void Select(WorkspaceArtifactType role, string artifactId);

    /// <summary>Adds a file, dropped file or pasted text to the workspace as an artifact revision of the role, and shows it.</summary>
    ArtifactImportResult Import(ArtifactImportRequest request);
}

/// <summary>
/// The workspace has two kinds of artifacts. A selected Sample Project's documents are found by generic discovery and resolved
/// on demand (they are never copied). Imported documents are artifact revisions in the workspace repository's lifecycle,
/// scoped to the project they were imported into, or to the manual workspace when no project is selected. Both are listed
/// together by role. Another project's imports never appear.
///
/// The artifact shown for a role is chosen in this order:
/// 1. explicit: the explorer selection, the Sample Projects choice, or the repository's current selection;
/// 2. the only authoritative (Approved/Baseline) artifact;
/// 3. the only artifact.
/// Otherwise the user must choose. It is never the first file, the latest file or a merge of several.
/// </summary>
public sealed class ArtifactExplorerContext : IArtifactExplorerContext, IDisposable
{
    /// <summary>Same limit as the file importer.</summary>
    public const int MaxImportBytes = 1_048_576;
    public const string SamplePrefix = "sample:";
    public const string WorkspacePrefix = "workspace:";

    private readonly IWorkspaceSessionService _workspace;
    private readonly ISampleProjectDocumentResolver _resolver;
    private readonly ISampleProjectArtifactDiscovery _discovery;
    private readonly IWorkspaceUpdateCoordinator? _updates;
    private readonly IWorkspaceStateManager? _stateManager;

    public event EventHandler? Changed;

    public ArtifactExplorerContext(
        IWorkspaceSessionService workspace,
        ISampleProjectDocumentResolver resolver,
        ISampleProjectArtifactDiscovery discovery,
        IWorkspaceUpdateCoordinator? updates = null,
        IWorkspaceStateManager? stateManager = null)
    {
        _workspace = workspace;
        _resolver = resolver;
        _discovery = discovery;
        _updates = updates;
        _stateManager = stateManager;
        _workspace.ReviewContextRebuildNeeded += OnRepositoryChanged;
        if (_workspace is WorkspaceArtifactRepository repository) repository.ProjectSelectionChanged += OnRepositoryChanged;
        // A saved workspace resumed or cleared replaces the lifecycle without an artifact event.
        if (_stateManager is not null) _stateManager.WorkspaceChanged += OnWorkspaceChanged;
    }

    /// <summary>A selected Sample Project, else the current imported project, else the manual workspace (null). The two projects are mutually exclusive.</summary>
    public string? CurrentScope => _resolver.GetSelectedProject() is { Length: > 0 } slug && !string.IsNullOrWhiteSpace(slug)
        ? slug
        : ProjectImportScope.For(_workspace.SddLifecycle.CurrentProjectImportId);

    public async Task<ArtifactExplorerState> GetStateAsync(WorkspaceArtifactType role, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        var imported = WorkspaceArtifacts(role, scope);
        var artifacts = new List<(ExplorerArtifact Artifact, Func<string?> Content, bool IsRepositoryCurrent)>();
        string? chosenSamplePath = null;
        string? projectName = null;

        if (ProjectImportScope.IsImport(scope))
        {
            // An imported project: its documents are revisions in its own scope (listed below); there is no catalog to discover.
            var importId = ProjectImportScope.ImportIdOf(scope);
            projectName = _workspace.SddLifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == importId)?.ProjectName ?? importId;
        }
        else if (scope is not null)
        {
            var namesTask = ProjectNameAsync(scope, cancellationToken);
            SampleProjectDiscoveryResult? discovery;
            try
            {
                discovery = await _discovery.DiscoverAsync(scope, cancellationToken);
            }
            catch (Exception ex)
            {
                return new ArtifactExplorerState(role, ExplorerArtifactStatus.Error, scope, await namesTask, [],
                    Error: $"The documents of project '{scope}' could not be read: {ex.Message}");
            }
            projectName = await namesTask;
            if (discovery is null)
                return new ArtifactExplorerState(role, ExplorerArtifactStatus.ProjectUnavailable, scope, projectName, []);
            if (discovery.Error is not null && discovery.Documents.Count == 0)
                return new ArtifactExplorerState(role, ExplorerArtifactStatus.Error, scope, projectName, [], Error: discovery.Error);

            var summary = discovery.Role(role);
            chosenSamplePath = summary.ChosenPath;
            foreach (var document in summary.Documents)
            {
                var content = _discovery.GetContent(discovery.ProjectSlug, document.RelativePath);
                artifacts.Add((new ExplorerArtifact(
                    SamplePrefix + document.RelativePath, role, DisplayNameOf(content, document.FileName), document.FileName,
                    document.RelativePath, ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown",
                    ExplorerArtifactCurrentness.Current, null, content is null ? null : MarkdownTokenizer.DocumentFingerprint(content)), () => content, false));
            }
        }

        artifacts.AddRange(imported.Select(i => (i.Artifact, (Func<string?>)(() => i.Revision.Content), i.Revision.IsCurrentSelection)));
        var listed = artifacts.Select(a => a.Artifact).ToList();
        if (listed.Count == 0)
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty, scope, projectName, listed);

        var (selectedId, reason) = Resolve(role, scope, artifacts.Select(a => (a.Artifact, a.IsRepositoryCurrent)).ToList(), chosenSamplePath);
        if (selectedId is null)
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.SelectionRequired, scope, projectName, listed);

        var selected = artifacts.First(a => a.Artifact.Id == selectedId);
        var text = selected.Content();
        if (string.IsNullOrEmpty(text))
            return new ArtifactExplorerState(role, ExplorerArtifactStatus.Error, scope, projectName, listed, selected.Artifact, reason,
                Error: $"{selected.Artifact.SourcePath ?? selected.Artifact.FileName} could not be read.");

        return new ArtifactExplorerState(role, ExplorerArtifactStatus.Loaded, scope, projectName, listed, selected.Artifact, reason, text);
    }

    public void Select(WorkspaceArtifactType role, string artifactId)
    {
        var scope = CurrentScope;
        var selections = _workspace.SddLifecycle.ExplorerSelections;
        selections.RemoveAll(s => s.WorkspaceScope == scope && s.Role == role.ToString());
        selections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = role.ToString(), ArtifactId = artifactId });

        if (artifactId.StartsWith(SamplePrefix, StringComparison.Ordinal) && scope is not null)
        {
            // Keep the analysis pages that resolve Sample Project documents on the same document as the explorer.
            _discovery.ChooseDocument(scope, role, artifactId[SamplePrefix.Length..]);
        }
        else if (WorkspaceArtifacts(role, scope).FirstOrDefault(i => i.Artifact.Id == artifactId) is { } imported)
        {
            _workspace.SelectRevision(imported.Revision.RevisionId);
        }

        _updates?.NotifyMutation();
        OnRepositoryChanged(this, EventArgs.Empty);
    }

    public ArtifactImportResult Import(ArtifactImportRequest request)
    {
        var content = Normalize(request.Content ?? "");
        if (string.IsNullOrWhiteSpace(content))
            return new ArtifactImportResult(null, "The document is empty. Add text before importing it.", null);
        if (Encoding.UTF8.GetByteCount(content) > MaxImportBytes)
            return new ArtifactImportResult(null, "The document is larger than 1 MB and cannot be imported.", null);
        if (content.Contains('\0'))
            return new ArtifactImportResult(null, "Binary content was detected. Only plain-text Markdown or text documents can be imported.", null);

        var scope = CurrentScope;
        var fileName = SafeFileName(request.FileName) ?? $"pasted-{ArtifactExplorerRoles.Label(request.Role).ToLowerInvariant().Replace(' ', '-')}.md";
        var revision = _workspace.AddArtifactRevision(request.Role, content, fileName, null, scope, request.Origin, select: true);
        if (revision is null)
            return new ArtifactImportResult(null, "The document could not be added to the workspace.", null);

        var artifactId = WorkspacePrefix + revision.FileName;
        var selections = _workspace.SddLifecycle.ExplorerSelections;
        selections.RemoveAll(s => s.WorkspaceScope == scope && s.Role == request.Role.ToString());
        selections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = request.Role.ToString(), ArtifactId = artifactId });

        // The chosen role is kept. A strong classification as another role is reported, never applied silently.
        var classification = SampleArtifactClassifier.Classify(fileName, content);
        WorkspaceArtifactType? suggested = classification.Status == ArtifactDiscoveryStatus.Detected && classification.Role is { } detected && detected != request.Role
            ? detected
            : null;

        _updates?.NotifyMutation();
        OnRepositoryChanged(this, EventArgs.Empty);
        return new ArtifactImportResult(artifactId, null, suggested);
    }

    public void Dispose()
    {
        _workspace.ReviewContextRebuildNeeded -= OnRepositoryChanged;
        if (_workspace is WorkspaceArtifactRepository repository) repository.ProjectSelectionChanged -= OnRepositoryChanged;
        if (_stateManager is not null) _stateManager.WorkspaceChanged -= OnWorkspaceChanged;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnWorkspaceChanged(Guid? workspaceId) => Changed?.Invoke(this, EventArgs.Empty);

    private (string? Id, ExplorerSelectionReason? Reason) Resolve(
        WorkspaceArtifactType role, string? scope, IReadOnlyList<(ExplorerArtifact Artifact, bool IsRepositoryCurrent)> artifacts, string? chosenSamplePath)
    {
        bool Exists(string? id) => id is not null && artifacts.Any(a => a.Artifact.Id == id);

        var stored = _workspace.SddLifecycle.ExplorerSelections
            .LastOrDefault(s => s.WorkspaceScope == scope && s.Role == role.ToString())?.ArtifactId;
        if (Exists(stored)) return (stored, ExplorerSelectionReason.Explicit);

        var chosenSample = chosenSamplePath is null ? null : SamplePrefix + chosenSamplePath;
        if (Exists(chosenSample)) return (chosenSample, ExplorerSelectionReason.Explicit);

        var repositoryCurrent = artifacts.Where(a => a.IsRepositoryCurrent).Select(a => a.Artifact.Id).ToList();
        if (repositoryCurrent.Count == 1) return (repositoryCurrent[0], ExplorerSelectionReason.Explicit);

        var authoritative = artifacts.Where(a => a.Artifact.IsAuthoritative).Select(a => a.Artifact.Id).ToList();
        if (authoritative.Count == 1) return (authoritative[0], ExplorerSelectionReason.Authoritative);

        return artifacts.Count == 1 ? (artifacts[0].Artifact.Id, ExplorerSelectionReason.OnlyArtifact) : (null, null);
    }

    /// <summary>
    /// Imported artifacts of the role in this scope. Revisions with the same file name are one document's history: the
    /// document shows its current selection, otherwise its newest revision that is not superseded.
    /// </summary>
    private List<(ExplorerArtifact Artifact, SddArtifactRevision Revision)> WorkspaceArtifacts(WorkspaceArtifactType role, string? scope) =>
        _workspace.SddLifecycle.Revisions
            .Where(r => r.Role == role.ToString() && r.WorkspaceScope == scope && !string.IsNullOrWhiteSpace(r.Content))
            .GroupBy(r => r.FileName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var revision = g.FirstOrDefault(r => r.IsCurrentSelection)
                               ?? g.Where(r => CurrentnessOf(r) == ExplorerArtifactCurrentness.Current).MaxBy(r => r.Revision)
                               ?? g.MaxBy(r => r.Revision)!;
                var artifact = new ExplorerArtifact(
                    WorkspacePrefix + g.Key, role, DisplayNameOf(revision.Content, g.Key), g.Key, revision.SourceReference,
                    ExplorerArtifactSource.Workspace, revision.Origin, revision.Revision, g.Count(),
                    string.IsNullOrWhiteSpace(revision.Authority) ? "Unknown" : revision.Authority, CurrentnessOf(revision), revision.CapturedAt,
                    MarkdownTokenizer.DocumentFingerprint(revision.Content));
                return (artifact, revision);
            })
            .ToList();

    private static ExplorerArtifactCurrentness CurrentnessOf(SddArtifactRevision revision) =>
        revision.Authority == "Superseded" || revision.SupersededByRevisionId is not null ? ExplorerArtifactCurrentness.Superseded
        : revision.Authority is "Historical" or "RejectedAlternative" ? ExplorerArtifactCurrentness.Historical
        : ExplorerArtifactCurrentness.Current;

    private async Task<string?> ProjectNameAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _resolver.GetAvailableProjectsAsync(cancellationToken);
            return projects.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase))?.Name ?? slug;
        }
        catch
        {
            return slug;
        }
    }

    /// <summary>
    /// The document's first level-1 heading, else the file name. Front matter, HTML comments (such as Spec Kit's sync
    /// impact report) and fenced code are skipped.
    /// </summary>
    public static string DisplayNameOf(string? content, string fileName)
    {
        if (!string.IsNullOrEmpty(content))
        {
            var inFrontMatter = false;
            var inComment = false;
            var inFence = false;
            var lineNumber = 0;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (lineNumber++ == 0 && line == "---") { inFrontMatter = true; continue; }
                if (inFrontMatter) { if (line == "---") inFrontMatter = false; continue; }
                if (inComment) { if (line.Contains("-->", StringComparison.Ordinal)) inComment = false; continue; }
                if (line.StartsWith("<!--", StringComparison.Ordinal)) { inComment = !line.Contains("-->", StringComparison.Ordinal); continue; }
                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { inFence = !inFence; continue; }
                if (inFence) continue;
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    var title = line[2..].Trim().Trim('#').Trim();
                    if (title.Length > 0) return title.Length > 120 ? title[..117] + "…" : title;
                }
            }
        }
        return fileName;
    }

    private static string Normalize(string text)
    {
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>A display file name only: the last path segment, without control or path characters.</summary>
    private static string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim();
        if (name.Length == 0) return null;
        return name.Length > 200 ? name[..200] : name;
    }
}
