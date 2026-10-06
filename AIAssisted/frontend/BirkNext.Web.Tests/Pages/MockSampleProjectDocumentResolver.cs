using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Mock Sample Project source for page tests: the selected project (resolver) and its role-classified documents
/// (discovery). Documents carry an explicit role, so tests can use any file name or folder.
/// </summary>
internal sealed class MockSampleProjectDocumentResolver : ISampleProjectDocumentResolver, ISampleProjectArtifactDiscovery
{
    private sealed record Doc(WorkspaceArtifactType Role, string RelativePath, string Content);

    private readonly Dictionary<string, List<Doc>> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Slug, WorkspaceArtifactType Role), string> _choices = new();
    private string? _selectedProject;

    /// <summary>When set, discovery throws (backend unavailable).</summary>
    public Exception? DiscoveryFailure { get; set; }

    public void RegisterProject(string projectSlug)
    {
        var projectName = projectSlug.Replace("-", " ");
        _projects[projectSlug] = new SampleProjectDto(
            Slug: projectSlug,
            Name: projectName,
            Domain: "test",
            Description: $"Test project {projectName}",
            AbsolutePath: $"/SampleData/{projectSlug}",
            HasReadme: false,
            Files: []);
        _documents.TryAdd(projectSlug, []);
    }

    /// <summary>Adds a document with an explicit role at any relative path.</summary>
    public void AddDocument(string projectSlug, WorkspaceArtifactType role, string relativePath, string content)
    {
        if (!_projects.ContainsKey(projectSlug)) RegisterProject(projectSlug);
        var docs = _documents[projectSlug];
        docs.RemoveAll(d => d.RelativePath == relativePath);
        docs.Add(new Doc(role, relativePath, content));
    }

    public void SetProjectSpecification(string projectSlug, string specContent, string documentType = "spec.md") =>
        AddDocument(projectSlug, RoleOf(documentType), documentType, specContent);

    public void SetProjectPlan(string projectSlug, string planContent) => AddDocument(projectSlug, WorkspaceArtifactType.Plan, "plan.md", planContent);

    public void SetProjectTasks(string projectSlug, string tasksContent) => AddDocument(projectSlug, WorkspaceArtifactType.Tasks, "tasks.md", tasksContent);

    public void SetProjectDataModel(string projectSlug, string dataModelContent) => AddDocument(projectSlug, WorkspaceArtifactType.DataModel, "data-model.md", dataModelContent);

    public void SetProjectConstitution(string projectSlug, string content) => AddDocument(projectSlug, WorkspaceArtifactType.Constitution, "constitution.md", content);

    // ── ISampleProjectDocumentResolver ──────────────────────────────────────

    public Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        if (!_projects.ContainsKey(projectSlug))
            return Task.FromResult(SampleProjectDocumentResult.InvalidProject($"Project '{projectSlug}' not found"));

        var role = SampleProjectDocumentResolver.ToRole(documentType);
        var docs = _documents[projectSlug].Where(d => d.Role == role).ToList();
        if (docs.Count == 0)
            return Task.FromResult(SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, null));
        var chosen = docs.Count == 1 ? docs[0]
            : docs.FirstOrDefault(d => _choices.TryGetValue((projectSlug, role), out var path) && path == d.RelativePath);
        return Task.FromResult(chosen is null
            ? SampleProjectDocumentResult.SelectionRequired(projectSlug, documentType, docs.Select(d => d.RelativePath).ToList())
            : SampleProjectDocumentResult.Success(projectSlug, documentType, chosen.RelativePath, chosen.Content));
    }

    public Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SampleProjectDto>>(_projects.Values.ToList());

    public string? GetSelectedProject() => _selectedProject;

    public void SetSelectedProject(string? projectSlug) => _selectedProject = projectSlug;

    public void ClearProjectCache(string projectSlug) { }

    // ── ISampleProjectArtifactDiscovery ─────────────────────────────────────

    public Task<SampleProjectDiscoveryResult> DiscoverAsync(SampleProjectDto project, CancellationToken cancellationToken = default) =>
        Task.FromResult(Discover(project.Slug));

    public Task<SampleProjectDiscoveryResult?> DiscoverAsync(string projectSlug, CancellationToken cancellationToken = default)
    {
        if (DiscoveryFailure is not null) throw DiscoveryFailure;
        return Task.FromResult(_projects.ContainsKey(projectSlug) ? Discover(projectSlug) : null);
    }

    public string? GetContent(string projectSlug, string relativePath) =>
        _documents.TryGetValue(projectSlug, out var docs) ? docs.FirstOrDefault(d => d.RelativePath == relativePath)?.Content : null;

    public void ChooseDocument(string projectSlug, WorkspaceArtifactType role, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) _choices.Remove((projectSlug, role));
        else _choices[(projectSlug, role)] = relativePath;
    }

    public string? ChosenPath(string projectSlug, WorkspaceArtifactType role) => _choices.GetValueOrDefault((projectSlug, role));

    public void Invalidate() { }

    private SampleProjectDiscoveryResult Discover(string slug)
    {
        var documents = _documents[slug]
            .OrderBy(d => d.RelativePath, StringComparer.Ordinal)
            .Select(d => new DiscoveredDocument(d.RelativePath, d.RelativePath.Split('/').Last(), ArtifactDiscoveryStatus.Detected, d.Role,
                ArtifactConfidence.Confirmed, [], [], null, null))
            .ToList();
        var roles = SampleArtifactClassifier.RoleOrder
            .Select(role => new SampleRoleSummary(role, documents.Where(d => d.Role == role).ToList(), _choices.GetValueOrDefault((slug, role))))
            .ToList();
        return new SampleProjectDiscoveryResult(slug, documents, [], [], null, roles, null);
    }

    private static WorkspaceArtifactType RoleOf(string filename) => filename switch
    {
        "plan.md" => WorkspaceArtifactType.Plan,
        "tasks.md" => WorkspaceArtifactType.Tasks,
        "data-model.md" => WorkspaceArtifactType.DataModel,
        "constitution.md" => WorkspaceArtifactType.Constitution,
        _ => WorkspaceArtifactType.Specification,
    };
}
