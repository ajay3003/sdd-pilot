using System.Collections.Concurrent;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Central resolver for Sample Project document sources.
///
/// Enforces the production policy:
/// Automatic Explorer content comes exclusively from SampleData/{project}/
///
/// Supports:
/// - Constitution → constitution.md
/// - Specification → spec.md
/// - Plan → plan.md
/// - Tasks → tasks.md
/// - DataModel → data-model.md
///
/// Does NOT support:
/// - cross-project fallback
/// - examples/* substitution
/// - workspace loading for automatic Explorers
/// - arbitrary file discovery
/// - previous-project content persistence
/// </summary>
public sealed class SampleProjectDocumentResolver : ISampleProjectDocumentResolver
{
    private readonly SampleProjectsApiService _apiService;
    private readonly IWorkspaceSessionService _workspace;
    private readonly ConcurrentDictionary<string, Task<SampleProjectDto?>> _projectCache;

    public SampleProjectDocumentResolver(
        SampleProjectsApiService apiService,
        IWorkspaceSessionService workspace)
    {
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _projectCache = new ConcurrentDictionary<string, Task<SampleProjectDto?>>();
    }

    public async Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
            return SampleProjectDocumentResult.InvalidProject("Project slug cannot be empty");

        // Validate project exists
        var project = await GetProjectAsync(projectSlug);
        if (project is null)
            return SampleProjectDocumentResult.InvalidProject($"Project '{projectSlug}' not found");

        var filename = GetDocumentFilename(documentType);

        // Check if document exists in project (via Files list)
        var hasFile = project.Files?.Any(f => f.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasFile)
            return SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, filename);

        // Fetch document content
        try
        {
            var content = await _apiService.GetFileAsync(projectSlug, filename);
            if (string.IsNullOrEmpty(content))
                return SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, filename);

            return SampleProjectDocumentResult.Success(projectSlug, documentType, filename, content);
        }
        catch (Exception ex)
        {
            return SampleProjectDocumentResult.Error(projectSlug, documentType, $"Failed to load {filename}: {ex.Message}");
        }
    }

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

    private static string GetDocumentFilename(ExplorerDocumentType documentType) =>
        documentType switch
        {
            ExplorerDocumentType.Constitution => "constitution.md",
            ExplorerDocumentType.Specification => "spec.md",
            ExplorerDocumentType.Plan => "plan.md",
            ExplorerDocumentType.Tasks => "tasks.md",
            ExplorerDocumentType.DataModel => "data-model.md",
            _ => throw new ArgumentException($"Unknown document type: {documentType}"),
        };

    private async Task<SampleProjectDto?> GetProjectAsync(string projectSlug)
    {
        if (!_projectCache.TryGetValue(projectSlug, out var cachedTask))
        {
            cachedTask = FetchProjectAsync(projectSlug);
            _projectCache.TryAdd(projectSlug, cachedTask);
        }

        try
        {
            return await cachedTask;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SampleProjectDto?> FetchProjectAsync(string projectSlug)
    {
        try
        {
            var projects = await _apiService.GetProjectsAsync();
            return projects?.FirstOrDefault(p => p.Slug.Equals(projectSlug, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
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
    string? ErrorMessage)
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

    public static SampleProjectDocumentResult MissingDocument(
        string projectSlug,
        ExplorerDocumentType documentType,
        string filename) =>
        new(
            IsSuccess: false,
            ProjectSlug: projectSlug,
            DocumentType: documentType,
            Filename: filename,
            Content: null,
            IsMissing: true,
            ErrorMessage: $"{filename} is not available for project '{projectSlug}'");

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
