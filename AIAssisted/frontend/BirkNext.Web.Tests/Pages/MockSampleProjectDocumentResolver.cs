using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Mock resolver for testing that provides controlled Sample Project behavior.
/// Used by explorer page source-of-truth tests.
/// </summary>
internal sealed class MockSampleProjectDocumentResolver : ISampleProjectDocumentResolver
{
    private readonly Dictionary<string, string> _projectSpecifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedProject;

    public void RegisterProject(string projectSlug)
    {
        var projectName = projectSlug.Replace("-", " ");
        var files = new List<SampleFileDto>();
        _projects[projectSlug] = new SampleProjectDto(
            Slug: projectSlug,
            Name: projectName,
            Domain: "test",
            Description: $"Test project {projectName}",
            AbsolutePath: $"/SampleData/{projectSlug}",
            HasReadme: false,
            Files: files);
    }

    public void SetProjectSpecification(string projectSlug, string specContent, string documentType = "spec.md")
    {
        _projectSpecifications[$"{projectSlug}:{documentType}"] = specContent;
        var projectName = projectSlug.Replace("-", " ");
        var fileDto = GetFileDto(documentType);
        var files = new List<SampleFileDto> { fileDto };
        _projects[projectSlug] = new SampleProjectDto(
            Slug: projectSlug,
            Name: projectName,
            Domain: "test",
            Description: $"Test project {projectName}",
            AbsolutePath: $"/SampleData/{projectSlug}",
            HasReadme: false,
            Files: files);
    }

    public void SetProjectPlan(string projectSlug, string planContent)
    {
        SetProjectSpecification(projectSlug, planContent, "plan.md");
    }

    public void SetProjectTasks(string projectSlug, string tasksContent)
    {
        SetProjectSpecification(projectSlug, tasksContent, "tasks.md");
    }

    public void SetProjectDataModel(string projectSlug, string dataModelContent)
    {
        SetProjectSpecification(projectSlug, dataModelContent, "data-model.md");
    }

    public async Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        var filename = GetFilename(documentType);

        if (!_projects.TryGetValue(projectSlug, out var project))
            return SampleProjectDocumentResult.InvalidProject($"Project '{projectSlug}' not found");

        var key = $"{projectSlug}:{filename}";
        if (!_projectSpecifications.TryGetValue(key, out var content))
            return SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, filename);

        return SampleProjectDocumentResult.Success(projectSlug, documentType, filename, content);
    }

    public Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SampleProjectDto>>(_projects.Values.ToList());
    }

    public string? GetSelectedProject() => _selectedProject;

    public void SetSelectedProject(string? projectSlug) => _selectedProject = projectSlug;

    public void ClearProjectCache(string projectSlug) { }

    private static string GetFilename(ExplorerDocumentType documentType) =>
        documentType switch
        {
            ExplorerDocumentType.Specification => "spec.md",
            ExplorerDocumentType.Plan => "plan.md",
            ExplorerDocumentType.Tasks => "tasks.md",
            ExplorerDocumentType.DataModel => "data-model.md",
            _ => throw new ArgumentException($"Unknown document type: {documentType}"),
        };

    private static SampleFileDto GetFileDto(string filename) =>
        new SampleFileDto(
            Filename: filename,
            Exists: true,
            ArtifactKind: filename switch
            {
                "plan.md" => "Plan",
                "tasks.md" => "Tasks",
                "data-model.md" => "DataModel",
                _ => "Specification"
            },
            ReviewerName: null,
            ReviewerRoute: null,
            IsSupported: true,
            IsContextOnly: false);
}
