using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Dashboard right after a project import: project, what was found, what needs attention, the recommended next step with why, the
/// selected test environment, and an honest statement that nothing has been reviewed — in that order, with no bare dash as a state.
/// </summary>
public class DashboardTesterFirstPageTests : BunitContext
{
    private IRenderedComponent<Dashboard> Render(CurrentWorkspaceSnapshot workspace, WorkflowReadiness readiness)
    {
        var service = new Mock<IWorkflowReadinessService>();
        service.Setup(s => s.GetReadinessAsync()).ReturnsAsync(readiness);
        Services.AddSingleton(service.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(workspace).Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());
        return Render<Dashboard>();
    }

    private static WorkflowReadiness Imported(CurrentWorkspaceSnapshot workspace, FrontendAnalysisProfile? environment)
    {
        var gate = ReviewDecisionSemanticsTests.Gate("SpecificationExplorer", "Specification Explorer", "Specification", WorkflowStepStatus.Available);
        gate.IsCurrent = true;
        return ReviewDecisionSemanticsTests.Readiness(workspace, [gate]) with
        {
            Inputs = ProjectInputPresentation.Build(workspace, environment, null),
        };
    }

    [Fact]
    public void AfterImport_TheTesterSeesProjectFoundAttentionNextStepAndEnvironment()
    {
        var workspace = WorkspaceSnapshots.Loaded("M2LB", "import:abc", "M2LB", [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var cut = Render(workspace, Imported(workspace, new FrontendAnalysisProfile { Name = "Development", TargetUrl = "https://dev.birk.example.no" }));

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-next-step]"));
        cut.Find("[data-testid=db-import-success]").TextContent.Should().Contain("Project imported successfully").And.Contain("Nothing has been reviewed or tested yet");
        cut.Find("[data-testid=db-workspace-name]").TextContent.Trim().Should().Be("M2LB");
        cut.Find("[data-testid=db-test-environment]").TextContent.Should().Be("Test environment: Development");

        cut.Find("[data-testid=db-next-title]").TextContent.Should().Be("Review imported documents");
        cut.Find("[data-testid=db-next-reason]").TextContent.Should().Contain("has not been reviewed yet");
        cut.Find("[data-testid=db-next-action]").TextContent.Should().Be("Review requirements");
        cut.FindAll("[data-testid=db-found-fact]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=db-attention-item][data-key=dqr-not-run]").Should().ContainSingle();

        // The recommended step comes before the found/attention panels and the health areas.
        var markup = cut.Markup;
        markup.IndexOf("db-next-step", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("db-found", StringComparison.Ordinal));
        markup.IndexOf("db-attention", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("db-health", StringComparison.Ordinal));

        cut.Find("[data-testid=db-readiness-row][data-key=runtime]").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid=db-risks-message]").TextContent.Should().Contain("Risks have not been assessed yet");
        cut.Markup.Should().NotContain("No risks detected");
        cut.FindAll("[data-testid=db-health-status]").Should().OnlyContain(s => s.TextContent.Trim().Length > 2, "never a bare dash");
        cut.FindAll("h2").Select(h => h.TextContent.Trim()).Should().Contain(["Recommended next step", "What BirkNext found", "Needs attention", "Review readiness", "Project Health", "Project Artifacts"]);
    }

    [Fact]
    public void WithoutEnvironment_TheTesterSeesNotSelected()
    {
        var workspace = WorkspaceSnapshots.AllRoles();
        var cut = Render(workspace, Imported(workspace, null));

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-test-environment]").TextContent.Should().Be("Test environment: Not selected"));
        cut.FindAll("[data-testid=db-import-success]").Should().BeEmpty("only an imported project is confirmed as imported");
        cut.FindAll("[data-testid=db-attention-item][data-key=target-missing]").Should().ContainSingle()
            .Which.ClassList.Should().Contain("is-info", "a missing test environment is a note for later runtime testing, not a problem");
    }
}
