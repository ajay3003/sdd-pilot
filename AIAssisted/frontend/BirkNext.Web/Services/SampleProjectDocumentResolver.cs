using BirkNext.Web.Models;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Services;

/// <summary>
/// Central resolver for Sample Project document sources.
///
/// Enforces the production policy:
/// Automatic Explorer content comes exclusively from SampleData/{project}/
///
/// Documents are resolved by artifact ROLE, not by filename: the project's documents are discovered recursively and
/// classified by <see cref="ISampleProjectArtifactDiscovery"/> (front matter, canonical filenames as strong hints,
/// document structure from the shared Markdown engine and the domain extractors). For each role:
/// - one detected document → it is returned;
/// - several detected documents → the explicit choice is returned, otherwise "selection required" (never the first one);
/// - none → "not found" (neutral; roles are optional).
///
/// Does NOT support:
/// - cross-project fallback
/// - examples/* substitution
/// - workspace loading for automatic Explorers
/// - previous-project content persistence
/// </summary>
public sealed class SampleProjectDocumentResolver : ISampleProjectDocumentResolver
{
    private readonly SampleProjectsApiService _apiService;
    private readonly IWorkspaceSessionService _workspace;
    private readonly ISampleProjectArtifactDiscovery _discovery;

    public SampleProjectDocumentResolver(
        SampleProjectsApiService apiService,
        IWorkspaceSessionService workspace,
        ISampleProjectArtifactDiscovery? discovery = null)
    {
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _discovery = discovery ?? new SampleProjectArtifactDiscoveryService(apiService);
    }

    public async Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
            return SampleProjectDocumentResult.InvalidProject("Project slug cannot be empty");

        SampleProjectDiscoveryResult? discovery;
        try
        {
            discovery = await _discovery.DiscoverAsync(projectSlug, cancellationToken);
        }
        catch (Exception ex)
        {
            return SampleProjectDocumentResult.Error(projectSlug, documentType, $"Failed to discover documents: {ex.Message}");
        }
        if (discovery is null)
            return SampleProjectDocumentResult.InvalidProject($"Project '{projectSlug}' not found");

        var role = discovery.Role(ToRole(documentType));
        if (role.State == SampleRoleState.NotFound)
            return SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, null);

        var primary = role.Primary;
        if (primary is null)
            return SampleProjectDocumentResult.SelectionRequired(projectSlug, documentType, role.Documents.Select(d => d.RelativePath).ToList());

        var content = _discovery.GetContent(discovery.ProjectSlug, primary.RelativePath)
                      ?? await _apiService.GetFileAsync(projectSlug, primary.RelativePath);
        if (string.IsNullOrEmpty(content))
            return SampleProjectDocumentResult.Error(projectSlug, documentType, $"{primary.RelativePath} could not be read.");

        return SampleProjectDocumentResult.Success(projectSlug, documentType, primary.RelativePath, content);
    }

    public static WorkspaceArtifactType ToRole(ExplorerDocumentType documentType) => documentType switch
    {
        ExplorerDocumentType.Constitution => WorkspaceArtifactType.Constitution,
        ExplorerDocumentType.Specification => WorkspaceArtifactType.Specification,
        ExplorerDocumentType.Plan => WorkspaceArtifactType.Plan,
        ExplorerDocumentType.Tasks => WorkspaceArtifactType.Tasks,
        ExplorerDocumentType.DataModel => WorkspaceArtifactType.DataModel,
        _ => throw new ArgumentException($"Unknown document type: {documentType}"),
    };

    public static string RoleLabel(ExplorerDocumentType documentType) => SampleArtifactClassifier.Label(ToRole(documentType));

    /// <summary>
    /// Get all available Sample Projects.
    /// Returns only valid, discovered projects from SampleData.
    /// </summary>
    public async Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var projects = await _apiService.GetProjectsAsync();
            return projects ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Get the currently selected project.
    /// </summary>
    public string? GetSelectedProject()
    {
        return _workspace.CurrentProject;
    }

    /// <summary>
    /// Set the currently selected project.
    /// Does NOT automatically load documents—only sets the context.
    /// </summary>
    public void SetSelectedProject(string? projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            _workspace.CurrentProject = null;
            return;
        }

        _workspace.CurrentProject = projectSlug;
    }

    /// <summary>
    /// Clear all cached documents for the given project.
    /// Called when switching projects to prevent stale content.
    /// </summary>
    public void ClearProjectCache(string projectSlug)
    {
        // Clear Workspace artifacts for this project
        if (GetSelectedProject() == projectSlug)
        {
            // Note: IWorkspaceSessionService.Remove() requires an out parameter in some versions
            // For now, we rely on switching projects to naturally clear cached documents
            // when the new project's documents are loaded.
        }
    }
}

public interface ISampleProjectDocumentResolver
{
    Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(
        CancellationToken cancellationToken = default);

    string? GetSelectedProject();
    void SetSelectedProject(string? projectSlug);
    void ClearProjectCache(string projectSlug);
}

public enum ExplorerDocumentType
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
}

public sealed record SampleProjectDocumentResult(
    bool IsSuccess,
    string? ProjectSlug,
    ExplorerDocumentType? DocumentType,
    string? Filename,
    string? Content,
    bool IsMissing,
    string? ErrorMessage,
    bool RequiresSelection = false,
    IReadOnlyList<string>? Candidates = null)
{
    public static SampleProjectDocumentResult Success(
        string projectSlug,
        ExplorerDocumentType documentType,
        string filename,
        string content) =>
        new(
            IsSuccess: true,
            ProjectSlug: projectSlug,
            DocumentType: documentType,
            Filename: filename,
            Content: content,
            IsMissing: false,
            ErrorMessage: null);

    /// <summary>No document with this role was detected. Neutral: artifact roles are optional.</summary>
    public static SampleProjectDocumentResult MissingDocument(
        string projectSlug,
        ExplorerDocumentType documentType,
        string? filename) =>
        new(
            IsSuccess: false,
            ProjectSlug: projectSlug,
            DocumentType: documentType,
            Filename: filename,
            Content: null,
            IsMissing: true,
            ErrorMessage: $"No {SampleProjectDocumentResolver.RoleLabel(documentType)} document was detected in project '{projectSlug}'");

    /// <summary>Several documents have this role and none was chosen. All are kept; the user picks one on Sample Projects.</summary>
    public static SampleProjectDocumentResult SelectionRequired(
        string projectSlug,
        ExplorerDocumentType documentType,
        IReadOnlyList<string> candidates) =>
        new(
            IsSuccess: false,
            ProjectSlug: projectSlug,
            DocumentType: documentType,
            Filename: null,
            Content: null,
            IsMissing: false,
            ErrorMessage: $"{candidates.Count} {SampleProjectDocumentResolver.RoleLabel(documentType)} documents were detected in project '{projectSlug}'; choose which one to open on Sample Projects.",
            RequiresSelection: true,
            Candidates: candidates);

    public static SampleProjectDocumentResult InvalidProject(string message) =>
        new(
            IsSuccess: false,
            ProjectSlug: null,
            DocumentType: null,
            Filename: null,
            Content: null,
            IsMissing: false,
            ErrorMessage: message);

    public static SampleProjectDocumentResult Error(
        string projectSlug,
        ExplorerDocumentType documentType,
        string message) =>
        new(
            IsSuccess: false,
            ProjectSlug: projectSlug,
            DocumentType: documentType,
            Filename: null,
            Content: null,
            IsMissing: false,
            ErrorMessage: message);
}
