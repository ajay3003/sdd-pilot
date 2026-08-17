using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class ExplorerPageSourceOfTruthTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly SampleProjectResolver _resolver = new();

    public ExplorerPageSourceOfTruthTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_resolver);

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true).SetVoidResult();
    }

    [Fact]
    public void ConstitutionExplorer_UsesSampleProjectBeforeWorkspaceAndStandaloneStorage()
    {
        _resolver.SetProject("project-b", "Project B", ExplorerDocumentType.Constitution, "# Project B Constitution");
        _resolver.SetSelectedProject("project-b");
        _workspace.Set(WorkspaceArtifactKind.Constitution, "# Workspace Constitution");
        SetStandaloneScratch("ce-standalone-constitution", "# Standalone Constitution");

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B Constitution");
            cut.Markup.Should().NotContain("Workspace Constitution");
            cut.Markup.Should().NotContain("Standalone Constitution");
            JSInterop.Invocations
                .Where(invocation => invocation.Identifier == "localStorage.getItem")
                .Select(invocation => invocation.Arguments.Count == 1 ? invocation.Arguments[0]?.ToString() : null)
                .Should()
                .NotContain("ce-standalone-constitution");
        });
    }

    [Fact]
    public void ConstitutionExplorer_UsesStandaloneScratchWhenWorkspaceIsEmpty()
    {
        SetStandaloneScratch("ce-standalone-constitution", "# Standalone Constitution");

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Be("# Standalone Constitution"));
    }

    [Fact]
    public void ConstitutionExplorer_StaysEmptyWhenWorkspaceAndStandaloneScratchAreEmpty()
    {
        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Constitution).Should().BeNull());
    }

    [Fact]
    public void PlanExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Plan).Should().BeNull());
    }

    [Fact]
    public void TaskExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Tasks).Should().BeNull());
    }

    [Fact]
    public void DataModelExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.DataModel).Should().BeNull());
    }

    [Theory]
    [InlineData("pe-standalone-plan", WorkspaceArtifactKind.Plan, "# Standalone Plan")]
    [InlineData("te-standalone-tasks", WorkspaceArtifactKind.Tasks, "# Standalone Tasks")]
    [InlineData("dme-standalone-datamodel", WorkspaceArtifactKind.DataModel, "# Standalone Data Model")]
    public void ExplorerPages_UseStandaloneScratchWhenWorkspaceIsEmpty(
        string storageKey,
        WorkspaceArtifactKind kind,
        string text)
    {
        SetStandaloneScratch(storageKey, text);

        switch (kind)
        {
            case WorkspaceArtifactKind.Plan:
                Render<PlanExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            case WorkspaceArtifactKind.Tasks:
                Render<TaskExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            case WorkspaceArtifactKind.DataModel:
                Render<DataModelExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            default:
                throw new InvalidOperationException($"Unsupported kind {kind}");
        }
    }

    private void SetStandaloneScratch(string storageKey, string text)
    {
        JSInterop.Setup<string?>("localStorage.getItem", invocation =>
            invocation.Arguments.Count == 1
            && string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal))
            .SetResult(text);
    }
}

/// <summary>
/// Simple test resolver that always returns no projects selected.
/// Used for legacy explorers tests that don't require Sample Project loading.
/// </summary>
internal sealed class SampleProjectResolver : ISampleProjectDocumentResolver
{
    private readonly Dictionary<(string ProjectSlug, ExplorerDocumentType Type), SampleProjectDocumentResult> _documents = [];
    private readonly Dictionary<string, Models.SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedProject;

    public void SetProject(string projectSlug, string projectName, ExplorerDocumentType documentType, string content)
    {
        var filename = documentType switch
        {
            ExplorerDocumentType.Constitution => "constitution.md",
            ExplorerDocumentType.Specification => "spec.md",
            ExplorerDocumentType.Plan => "plan.md",
            ExplorerDocumentType.Tasks => "tasks.md",
            ExplorerDocumentType.DataModel => "data-model.md",
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null),
        };

        _documents[(projectSlug, documentType)] =
            SampleProjectDocumentResult.Success(projectSlug, documentType, filename, content);

        _projects[projectSlug] = new Models.SampleProjectDto(
            projectSlug,
            projectName,
            "test",
            $"Test project {projectName}",
            $"/SampleData/{projectSlug}",
            false,
            [new Models.SampleFileDto(filename, true, documentType.ToString(), null, null, true, false)]);
    }

    public Task<SampleProjectDocumentResult> ResolveAsync(
        string projectSlug,
        ExplorerDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_documents.TryGetValue((projectSlug, documentType), out var result)
            ? result
            : SampleProjectDocumentResult.InvalidProject("Test resolver"));
    }

    public Task<IReadOnlyList<Models.SampleProjectDto>> GetAvailableProjectsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<Models.SampleProjectDto>>(_projects.Values.ToList());
    }

    public string? GetSelectedProject() => _selectedProject;

    public void SetSelectedProject(string? projectSlug) => _selectedProject = projectSlug;

    public void ClearProjectCache(string projectSlug) { }
}
