using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class DataModelExplorerSampleProjectTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();

    public DataModelExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);
        Services.AddSingleton<IDataModelAnalysisService>(new DataModelAnalysisService());
        Services.AddSingleton<IReportExportService>(new ReportExportService());

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void DataModelExplorer_LoadsFromSelectedSampleProject()
    {
        const string projectSlug = "project-a";
        const string projectDataModel = "# Data Model\n\n## Entity: User\n\n**Table**: `Users`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";

        _documentResolver.SetProjectDataModel(projectSlug, projectDataModel);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project a");
            markup.Should().Contain("Data Model");
        });
    }

    [Fact]
    public void DataModelExplorer_SwitchesProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectADataModel = "# Project A Data Model\n\n## Entity: Product\n\n**Table**: `Products`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";
        const string projectBDataModel = "# Project B Data Model\n\n## Entity: Order\n\n**Table**: `Orders`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";

        _documentResolver.SetProjectDataModel(projectASlug, projectADataModel);
        _documentResolver.SetProjectDataModel(projectBSlug, projectBDataModel);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Data Model"));

        // Switch to project B
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project b");
            markup.Should().Contain("Project B Data Model");
            markup.Should().NotContain("Project A Data Model");
        });
    }

    [Fact]
    public void DataModelExplorer_ShowsMissingStateWhenDataModelNotFound()
    {
        const string projectSlug = "project-without-datamodel";

        // Register project but don't set data model
        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("data-model.md is not available");
        });
    }

    [Fact]
    public void DataModelExplorer_DoesNotUseWorkspaceAsAutomaticSource()
    {
        const string projectSlug = "project-a";
        const string workspaceDataModel = "# Workspace Data Model";
        const string sampleProjectDataModel = "# Sample Project Data Model\n\n## Entity: Entity1\n\n**Table**: `Entity1Table`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";

        _documentResolver.SetProjectDataModel(projectSlug, sampleProjectDataModel);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.DataModel, workspaceDataModel);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Sample Project Data Model");
            markup.Should().NotContain("Workspace Data Model");
        });
    }

    [Fact]
    public void DataModelExplorer_ClearsProjectHeaderOnProjectDeselection()
    {
        const string projectSlug = "project-a";
        const string projectDataModel = "# Project A Data Model\n\n## Entity: TestEntity\n\n**Table**: `TestTable`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";

        _documentResolver.SetProjectDataModel(projectSlug, projectDataModel);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<DataModelExplorer>();

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
    public void DataModelExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project:");
            markup.Should().NotContain("data-model.md is not available");
        });
    }

    [Fact]
    public void DataModelExplorer_ReloadsSameProjectWithoutDuplication()
    {
        const string projectSlug = "project-a";
        const string projectDataModel = "# Project A Data Model\n\n## Entity: User\n\n**Table**: `Users`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |\n| Name | Name | nvarchar(256) |";

        _documentResolver.SetProjectDataModel(projectSlug, projectDataModel);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Data Model"));

        var firstRender = cut.Markup;
        var entityCount = firstRender.Split("## Entity").Length - 1;

        // Re-render same project
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var secondRender = cut.Markup;
            var secondEntityCount = secondRender.Split("## Entity").Length - 1;
            secondEntityCount.Should().Be(entityCount);
        });
    }

    [Fact]
    public void DataModelExplorer_DifferentDataModelsDoNotLeakBetweenProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectADataModel = "# Project A\n\n## Entity: User\n\n**Table**: `Users`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";
        const string projectBDataModel = "# Project B\n\n## Entity: Order\n\n**Table**: `Orders`\n\n### Fields\n\n| Property | Column | Type |\n|---|---|---|\n| Id | Id | UUID |";

        _documentResolver.SetProjectDataModel(projectASlug, projectADataModel);
        _documentResolver.SetProjectDataModel(projectBSlug, projectBDataModel);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Project A");
        });

        // Switch to project B which has different entities
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Should show B's data model structure, not A's
            markup.Should().Contain("Project B");
            markup.Should().Contain("Order");
            markup.Should().NotContain("Project A");
            markup.Should().NotContain("User");
        });
    }
}
