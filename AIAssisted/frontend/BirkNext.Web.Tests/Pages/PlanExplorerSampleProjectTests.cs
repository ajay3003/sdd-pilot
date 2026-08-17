using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class PlanExplorerSampleProjectTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();

    public PlanExplorerSampleProjectTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
    }

    [Fact]
    public void PlanExplorer_LoadsFromSelectedSampleProject()
    {
        const string projectSlug = "project-a";
        const string projectPlan = "# Implementation Plan\n\n## Phase 1\nSetup infrastructure";

        _documentResolver.SetProjectPlan(projectSlug, projectPlan);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project a");
            markup.Should().Contain("Implementation Plan");
        });
    }

    [Fact]
    public void PlanExplorer_SwitchesProjects()
    {
        const string projectASlug = "project-a";
        const string projectBSlug = "project-b";
        const string projectAPlan = "# Project A Plan";
        const string projectBPlan = "# Project B Plan";

        _documentResolver.SetProjectPlan(projectASlug, projectAPlan);
        _documentResolver.SetProjectPlan(projectBSlug, projectBPlan);
        _documentResolver.SetSelectedProject(projectASlug);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Plan"));

        // Switch to project B
        _documentResolver.SetSelectedProject(projectBSlug);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("project b");
            markup.Should().Contain("Project B Plan");
            markup.Should().NotContain("Project A Plan");
        });
    }

    [Fact]
    public void PlanExplorer_ShowsMissingStateWhenPlanNotFound()
    {
        const string projectSlug = "project-without-plan";

        // Register project but don't set a plan
        _documentResolver.RegisterProject(projectSlug);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("plan.md is not available");
        });
    }

    [Fact]
    public void PlanExplorer_DoesNotUseWorkspaceAsAutomaticSource()
    {
        const string projectSlug = "project-a";
        const string workspacePlan = "# Workspace Plan";
        const string sampleProjectPlan = "# Sample Project Plan";

        _documentResolver.SetProjectPlan(projectSlug, sampleProjectPlan);
        _documentResolver.SetSelectedProject(projectSlug);
        _workspace.Set(WorkspaceArtifactKind.Plan, workspacePlan);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().Contain("Sample Project Plan");
            markup.Should().NotContain("Workspace Plan");
        });
    }

    [Fact]
    public void PlanExplorer_ClearsProjectHeaderOnProjectDeselection()
    {
        const string projectSlug = "project-a";
        const string projectPlan = "# Project A Plan";

        _documentResolver.SetProjectPlan(projectSlug, projectPlan);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<PlanExplorer>();

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
    public void PlanExplorer_HandlesEmptySelectedProject()
    {
        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            markup.Should().NotContain("Sample Project:");
            markup.Should().NotContain("plan.md is not available");
        });
    }

    [Fact]
    public void PlanExplorer_ReloadsSameProjectWithoutDuplication()
    {
        const string projectSlug = "project-a";
        const string projectPlan = "# Project A Plan\n\n## Phase 1\nSetup";

        _documentResolver.SetProjectPlan(projectSlug, projectPlan);
        _documentResolver.SetSelectedProject(projectSlug);

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Project A Plan"));

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
}

