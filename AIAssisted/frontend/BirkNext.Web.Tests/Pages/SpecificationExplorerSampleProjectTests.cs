using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class SpecificationExplorerSampleProjectTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();

    public SpecificationExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void SpecificationExplorer_LoadsFromSelectedSampleProject()
    {
        const string projectASlug = "project-a";
        const string projectASpec = "# Project A Specification\n\n## Feature\nTest feature";

        _documentResolver.SetProjectSpecification(projectASlug, projectASpec);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project a");
            markup.Should().Contain("Project A Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_SwitchesProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectASpec = "# Project A Specification";
        const string projectBSpec = "# Project B Specification";

        _documentResolver.SetProjectSpecification(projectASlug, projectASpec);
        _documentResolver.SetProjectSpecification(projectBSlug, projectBSpec);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Specification"));

        // Switch to project B
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project b");
            markup.Should().Contain("Project B Specification");
            markup.Should().NotContain("Project A Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_ShowsMissingStateWhenSpecNotFound()
    {
        const string projectSlug = "project-without-spec";

        // Register project but don't set a specification
        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("spec.md is not available");
        });
    }

    [Fact]
    public void SpecificationExplorer_DoesNotUseWorkspaceAsAutomaticSource()
    {
        const string projectSlug = "project-a";
        const string workspaceSpec = "# Workspace Specification";
        const string sampleProjectSpec = "# Sample Project Specification";

        _documentResolver.SetProjectSpecification(projectSlug, sampleProjectSpec);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Specification, workspaceSpec);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Sample Project Specification");
            markup.Should().NotContain("Workspace Specification");
        });
    }

    [Fact]
    public void SpecificationExplorer_ClearsProjectHeaderOnProjectDeselection()
    {
        const string projectSlug = "project-a";
        const string projectSpec = "# Project A Specification";

        _documentResolver.SetProjectSpecification(projectSlug, projectSpec);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Sample Project:"));

        // Deselect project
        _documentResolver.SetSelectedProject(null);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Header should be gone when no project is selected
            markup.Should().NotContain("Sample Project:");
        });
    }

    [Fact]
    public void SpecificationExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project:");
            markup.Should().NotContain("spec.md is not available");
        });
    }

    [Fact]
    public void SpecificationExplorer_ReloadsSameProjectWithoutDuplication()
    {
        const string projectSlug = "project-a";
        const string projectSpec = "# Project A Specification\n\n## Feature\nTest feature";

        _documentResolver.SetProjectSpecification(projectSlug, projectSpec);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<SpecificationExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Specification"));

        var firstRender = cut.Markup;
        var featureCount = firstRender.Split("Feature").Length - 1;

        // Re-render same project
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var secondRender = cut.Markup;
            var secondFeatureCount = secondRender.Split("Feature").Length - 1;
            secondFeatureCount.Should().Be(featureCount);
        });
    }
}

