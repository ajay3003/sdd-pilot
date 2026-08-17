using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class TaskExplorerSampleProjectTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();

    public TaskExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void TaskExplorer_LoadsFromSelectedSampleProject()
    {
        const string projectSlug = "project-a";
        const string projectTasks = "# Implementation Plan\n\n## Phase 1\n\nT001 - Setup infrastructure\nT002 - Configure deployment";

        _documentResolver.SetProjectTasks(projectSlug, projectTasks);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project a");
            markup.Should().Contain("Implementation Plan");
        });
    }

    [Fact]
    public void TaskExplorer_SwitchesProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectATasks = "# Project A Tasks\n\nT001 - Task A1";
        const string projectBTasks = "# Project B Tasks\n\nT010 - Task B1";

        _documentResolver.SetProjectTasks(projectASlug, projectATasks);
        _documentResolver.SetProjectTasks(projectBSlug, projectBTasks);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Tasks"));

        // Switch to project B
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project b");
            markup.Should().Contain("Project B Tasks");
            markup.Should().NotContain("Project A Tasks");
        });
    }

    [Fact]
    public void TaskExplorer_ShowsMissingStateWhenTasksNotFound()
    {
        const string projectSlug = "project-without-tasks";

        // Register project but don't set tasks
        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("tasks.md is not available");
        });
    }

    [Fact]
    public void TaskExplorer_DoesNotUseWorkspaceAsAutomaticSource()
    {
        const string projectSlug = "project-a";
        const string workspaceTasks = "# Workspace Tasks";
        const string sampleProjectTasks = "# Sample Project Tasks";

        _documentResolver.SetProjectTasks(projectSlug, sampleProjectTasks);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Tasks, workspaceTasks);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Sample Project Tasks");
            markup.Should().NotContain("Workspace Tasks");
        });
    }

    [Fact]
    public void TaskExplorer_ClearsProjectHeaderOnProjectDeselection()
    {
        const string projectSlug = "project-a";
        const string projectTasks = "# Project A Tasks";

        _documentResolver.SetProjectTasks(projectSlug, projectTasks);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<TaskExplorer>();

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
    public void TaskExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project:");
            markup.Should().NotContain("tasks.md is not available");
        });
    }

    [Fact]
    public void TaskExplorer_ReloadsSameProjectWithoutDuplication()
    {
        const string projectSlug = "project-a";
        const string projectTasks = "# Project A Tasks\n\n## Phase 1\nT001 - Setup";

        _documentResolver.SetProjectTasks(projectSlug, projectTasks);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Tasks"));

        var firstRender = cut.Markup;
        var phaseCount = firstRender.Split("Phase").Length - 1;

        // Re-render same project
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var secondRender = cut.Markup;
            var secondPhaseCount = secondRender.Split("Phase").Length - 1;
            secondPhaseCount.Should().Be(phaseCount);
        });
    }

    [Fact]
    public void TaskExplorer_DifferentTaskIDsDoNotLeakBetweenProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectATasks = "# Project A\n\nT001 - Task 1\nT002 - Task 2";
        const string projectBTasks = "# Project B\n\nT001 - Different Task 1\nT010 - Task 10";

        _documentResolver.SetProjectTasks(projectASlug, projectATasks);
        _documentResolver.SetProjectTasks(projectBSlug, projectBTasks);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("T002");
        });

        // Switch to project B which also has T001 but different content
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            // Should show B's task structure, not A's
            markup.Should().Contain("T010");
            markup.Should().NotContain("T002");
        });
    }
}
