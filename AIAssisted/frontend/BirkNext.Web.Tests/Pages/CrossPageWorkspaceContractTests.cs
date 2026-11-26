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
/// One current workspace, many pages. Project Dashboard and the sidebar read the same current-workspace projection, so for the same workspace
/// they agree on whether a workspace, project and each artifact role exists — whatever opened first, and after a project switch, an import or a
/// reset, without a reload. (Recommended Workflow, which this also compared, was retired on 2026-11-19; its review decisions are on the Dashboard.)
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
        _workflowApi.Setup(api => api.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<ArtifactRevisionRef>>()))
            .ReturnsAsync((Guid _, bool c, bool s, bool p, bool t, bool d, IReadOnlyList<ArtifactRevisionRef> _) =>
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

        // Review decisions and workspace actions (moved to the Dashboard from Recommended Workflow)
        Services.AddSingleton(_workflowApi.Object);
        Services.AddScoped<IWorkflowReadinessService, WorkflowReadinessService>();
        Services.AddSingleton(new Mock<IWorkspacePersistenceApiService> { CallBase = true }.Object);
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

    /// <summary>What the Dashboard states about the workspace, read from its markup.</summary>
    private sealed record PageView(bool HasWorkspace, string? Name, IReadOnlyDictionary<string, string> Roles);

    private static PageView Dashboard(IRenderedComponent<Dashboard> cut) => new(
        !cut.Markup.Contains("No project imported yet"),
        cut.FindAll("[data-testid=db-workspace-name]").SingleOrDefault()?.TextContent.Replace("📁", "").Trim(),
        cut.FindAll("[data-testid=db-artifact-card]").ToDictionary(b => b.GetAttribute("data-role")!, b => b.GetAttribute("data-availability")!));

    [Fact]
    public void PersonModule_TheDashboardShowsTheWorkspaceProjectAllFiveRolesAndItsReviewDecisions()
    {
        _workspace.CurrentProject = "person-module";

        var dashboard = Render<Dashboard>();

        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("5 document types"));
        Dashboard(dashboard).Name.Should().Be("Person Module");
        // Review decisions moved here from Recommended Workflow: the same readiness, the same review steps.
        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=review-decision][data-step=SpecificationExplorer]"));
        dashboard.Find("[data-testid=review-decisions-count]").TextContent.Should().StartWith("0 / ", "nothing has been decided yet");
        dashboard.FindAll("[data-testid=workspace-menu]").Should().ContainSingle("Save, Save As, Manage Workspaces and Clear moved here too");
    }

    [Fact]
    public void Opening_the_sidebar_first_does_not_change_the_Dashboard()
    {
        _workspace.CurrentProject = "skole";
        Render<NavMenu>();

        var dashboard = Render<Dashboard>();

        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("2 document types"));
        Dashboard(dashboard).Name.Should().Be("Skole");
    }

    [Fact]
    public void ProjectSwitch_UpdatesTheDashboard_WithoutReload_AndNeverShowsTheOldProject()
    {
        _workspace.CurrentProject = "person-module";
        var dashboard = Render<Dashboard>();
        dashboard.WaitForAssertion(() => Dashboard(dashboard).Name.Should().Be("Person Module"));

        _workspace.CurrentProject = "skole";

        dashboard.WaitForAssertion(() => Dashboard(dashboard).Name.Should().Be("Skole"));
        dashboard.Markup.Should().NotContain("Person Module");
    }

    [Fact]
    public void Reset_EmptiesTheDashboard_WithoutReload()
    {
        _workspace.CurrentProject = "person-module";
        var dashboard = Render<Dashboard>();
        dashboard.WaitForAssertion(() => Dashboard(dashboard).HasWorkspace.Should().BeTrue());

        _workspace.ClearAll();

        dashboard.WaitForAssertion(() => Dashboard(dashboard).HasWorkspace.Should().BeFalse());
        dashboard.Markup.Should().NotContain("Person Module");
        dashboard.FindAll("[data-testid=review-decisions]").Should().BeEmpty("no workspace, no review decisions");
    }

    [Fact]
    public void ManualImportWithoutSampleProject_UpdatesTheDashboard()
    {
        var dashboard = Render<Dashboard>();
        dashboard.WaitForAssertion(() => Dashboard(dashboard).HasWorkspace.Should().BeFalse());

        Services.GetRequiredService<IArtifactExplorerContext>()
            .Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Requirements\n\nFR-001", "requirements.md", "File"));

        dashboard.WaitForAssertion(() => dashboard.Find("[data-testid=db-workspace-roles]").TextContent.Should().Be("1 document type"));
        Dashboard(dashboard).Roles.GetValueOrDefault(nameof(WorkspaceArtifactType.Specification)).Should().Be(nameof(ArtifactRoleAvailability.Available));
    }

    [Fact]
    public void Sidebar_SeesTheSampleProjectsRequirements_LikeTheDashboard()
    {
        // All Modules lists Quality Review (the default, focused Manual Testing view lists only its own pages — V6).
        Services.GetRequiredService<NavigationSectionState>().SetView(WorkspaceView.AllModules);
        var nav = Render<NavMenu>();
        nav.WaitForAssertion(() => nav.Find("[data-testid=nav-applicability-quality-review]").TextContent.Should().Be("No evidence"));

        _workspace.CurrentProject = "person-module";

        nav.WaitForAssertion(() => nav.FindAll("[data-testid=nav-applicability-quality-review]").Should().BeEmpty(
            "the selected project has a specification, so Quality Review is applicable — not 'No evidence' for a missing workspace"));
    }
}
