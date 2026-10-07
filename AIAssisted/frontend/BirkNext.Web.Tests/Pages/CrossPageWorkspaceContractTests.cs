using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// One current workspace, many pages. The Dashboard, Recommended Workflow and the sidebar read the same current-workspace
/// projection, so for the same workspace they agree on whether a workspace, project and each artifact role exists — whatever
/// page was opened first, and after a project switch, an import or a reset, without a reload.
/// </summary>
public sealed class CrossPageWorkspaceContractTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly DashboardPageTests.SampleProjectsHttpHandler _catalog = new();
    private readonly Mock<IRecommendedWorkflowApiService> _workflowApi = new();

    public CrossPageWorkspaceContractTests()
    {
        _catalog.SetProjects(Project("person-module", "Person Module", "constitution.md", "spec.md", "plan.md", "tasks.md", "data-model.md"),
                             Project("skole", "Skole", "spec.md", "tasks.md"));
        _workflowApi.Setup(api => api.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, bool c, bool s, bool p, bool t, bool d) =>
            [
                new WorkflowStepViewModel
                {
                    Number = 1, Key = "SpecificationExplorer", Title = "Specification Explorer", Description = "Review the specification.",
                    Route = "specification-explorer", ActionLabel = "Open Specification Explorer", Color = "#2563eb",
                    Status = s ? WorkflowStepStatus.Available : WorkflowStepStatus.Locked, CanOpen = s, IsCurrent = s,
                    RequiresApproval = true, RequiresManualReview = true, ApprovalState = ApprovalState.Pending, ReviewState = ReviewState.NotStarted,
                    Prerequisites = s ? PrerequisiteState.Available : PrerequisiteState.Missing
                }
            ]);

        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        DashboardPageTests.AddCurrentWorkspace(Services, _workspace, _catalog);

        // Recommended Workflow
        Services.AddSingleton(_workflowApi.Object);
        Services.AddScoped<IWorkflowReadinessService, WorkflowReadinessService>();
        Services.AddSingleton(Mock.Of<IWorkspacePersistenceApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionRestoreService>());
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.StartMonitoringAsync()).Returns(Task.CompletedTask);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Dashboard
        Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());

        // Sidebar
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton<NavigationSectionState>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
    }

    private static SampleProjectDto Project(string slug, string name, params string[] files) =>
        new(slug, name, "test", name, $"C:\\SampleData\\{slug}", false,
            files.Select(f => new SampleFileDto(f, true, null, "", "", true, false)).ToArray());

    /// <summary>What a page states about the workspace, read from its markup.</summary>
    private sealed record PageView(bool HasWorkspace, string? Name, IReadOnlyDictionary<string, string> Roles);

    private static PageView Dashboard(IRenderedComponent<Dashboard> cut) => new(
        !cut.Markup.Contains("No workspace loaded"),
        cut.FindAll("[data-testid=db-workspace-name]").SingleOrDefault()?.TextContent.Replace("📁", "").Trim(),
        cut.FindAll("[data-testid=db-role-badge]").ToDictionary(b => b.GetAttribute("data-role")!, b => b.GetAttribute("data-availability")!));

    private static PageView Workflow(IRenderedComponent<RecommendedWorkflow> cut) => new(
        cut.FindAll("[data-testid=rw-no-workspace]").Count == 0,
        cut.Find("[data-testid=rw-project]").TextContent.Trim(),
        cut.FindAll("[data-testid=rw-role-badge]").ToDictionary(b => b.GetAttribute("data-role")!, b => b.GetAttribute("data-availability")!));

    private static void ShouldAgree(PageView dashboard, PageView workflow)
    {
        workflow.HasWorkspace.Should().Be(dashboard.HasWorkspace, "both pages must agree whether a workspace exists");
        // A page that shows no badge for a role (the Dashboard without a workspace) states it is missing.
        static string Of(PageView page, WorkspaceArtifactType role) => page.Roles.GetValueOrDefault(role.ToString(), nameof(ArtifactRoleAvailability.Missing));
        foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles)
            Of(workflow, role).Should().Be(Of(dashboard, role), $"both pages must agree on {role} availability");
        if (dashboard.HasWorkspace) workflow.Name.Should().Be(dashboard.Name, "both pages must name the same project");
    }

    [Fact]
    public void PersonModule_DashboardAndWorkflow_AgreeOnWorkspaceProjectAndAllFiveRoles()
    {
        _workspace.CurrentProject = "person-module";

        var dashboard = Render<Dashboard>();
        var workflow = Render<RecommendedWorkflow>();

        workflow.WaitForAssertion(() => workflow.Find("[data-testid=rw-artifact-roles]").TextContent.Should().Be("5 artifact roles available"));
        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("5 artifact roles available"));
        ShouldAgree(Dashboard(dashboard), Workflow(workflow));
        Dashboard(dashboard).Name.Should().Be("Person Module");
        Workflow(workflow).Roles.Values.Should().OnlyContain(a => a == nameof(ArtifactRoleAvailability.Available));
        workflow.Markup.Should().NotContain("No workspace loaded");
        workflow.Find("[data-testid=rw-next-action] .rw-next-action-title").TextContent.Should().NotBe("Load project artifacts");
        workflow.Markup.Should().NotContain("of 5");
        workflow.Find("[data-testid=rw-release-readiness-card] .metric-value").TextContent.Should().Be("—", "nothing has been reviewed yet: not assessed, not 0%");
        workflow.Find("[data-testid=rw-save-status]").TextContent.Should().Be("Not saved", "an unsaved workspace is still loaded");
        workflow.Markup.Should().Contain("Specification Explorer", "the real next step comes from the review steps");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigationOrder_DoesNotChangeTheState(bool dashboardFirst)
    {
        _workspace.CurrentProject = "skole";

        IRenderedComponent<Dashboard>? dashboard = null;
        IRenderedComponent<RecommendedWorkflow>? workflow = null;
        if (dashboardFirst) { dashboard = Render<Dashboard>(); workflow = Render<RecommendedWorkflow>(); }
        else { workflow = Render<RecommendedWorkflow>(); dashboard = Render<Dashboard>(); }

        workflow.WaitForAssertion(() => workflow.Find("[data-testid=rw-artifact-roles]").TextContent.Should().Be("2 artifact roles available"));
        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("2 artifact roles available"));
        ShouldAgree(Dashboard(dashboard), Workflow(workflow));
    }

    [Fact]
    public void ProjectSwitch_UpdatesBothPages_WithoutReload_AndNeverShowsTheOldProject()
    {
        _workspace.CurrentProject = "person-module";
        var dashboard = Render<Dashboard>();
        var workflow = Render<RecommendedWorkflow>();
        workflow.WaitForAssertion(() => Workflow(workflow).Name.Should().Be("Person Module"));

        _workspace.CurrentProject = "skole";

        workflow.WaitForAssertion(() => Workflow(workflow).Name.Should().Be("Skole"));
        dashboard.WaitForAssertion(() => Dashboard(dashboard).Name.Should().Be("Skole"));
        ShouldAgree(Dashboard(dashboard), Workflow(workflow));
        workflow.Markup.Should().NotContain("Person Module");
        dashboard.Markup.Should().NotContain("Person Module");
    }

    [Fact]
    public void Reset_EmptiesBothPagesConsistently_WithoutReload()
    {
        _workspace.CurrentProject = "person-module";
        var dashboard = Render<Dashboard>();
        var workflow = Render<RecommendedWorkflow>();
        workflow.WaitForAssertion(() => Workflow(workflow).HasWorkspace.Should().BeTrue());

        _workspace.ClearAll();

        workflow.WaitForAssertion(() => Workflow(workflow).HasWorkspace.Should().BeFalse());
        dashboard.WaitForAssertion(() => Dashboard(dashboard).HasWorkspace.Should().BeFalse());
        ShouldAgree(Dashboard(dashboard), Workflow(workflow));
        workflow.Find("[data-testid=rw-next-action] .rw-next-action-title").TextContent.Should().Be("Load project artifacts");
        workflow.Markup.Should().NotContain("Person Module");
        dashboard.Markup.Should().NotContain("Person Module");
    }

    [Fact]
    public void ManualImportWithoutSampleProject_UpdatesBothPages()
    {
        var dashboard = Render<Dashboard>();
        var workflow = Render<RecommendedWorkflow>();
        workflow.WaitForAssertion(() => Workflow(workflow).HasWorkspace.Should().BeFalse());

        Services.GetRequiredService<IArtifactExplorerContext>()
            .Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Requirements\n\nFR-001", "requirements.md", "File"));

        workflow.WaitForAssertion(() => workflow.Find("[data-testid=rw-artifact-roles]").TextContent.Should().Be("1 artifact role available"));
        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("1 artifact role available"));
        workflow.Find("[data-testid=rw-project]").TextContent.Should().Be("Not assigned", "a manual workspace has no project, and is still a workspace");
        ShouldAgree(Dashboard(dashboard), Workflow(workflow) with { Name = "Unsaved workspace" });
    }

    [Fact]
    public void Sidebar_SeesTheSampleProjectsRequirements_LikeTheDashboard()
    {
        var nav = Render<NavMenu>();
        nav.WaitForAssertion(() => nav.Find("[data-testid=nav-applicability-quality-review]").TextContent.Should().Be("No evidence"));

        _workspace.CurrentProject = "person-module";

        nav.WaitForAssertion(() => nav.FindAll("[data-testid=nav-applicability-quality-review]").Should().BeEmpty(
            "the selected project has a specification, so Quality Review is applicable — not 'No evidence' for a missing workspace"));
    }
}
