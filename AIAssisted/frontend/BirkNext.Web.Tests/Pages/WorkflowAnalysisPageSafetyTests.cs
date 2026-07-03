using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class WorkflowAnalysisPageSafetyTests : BunitContext
{
    private readonly WorkspaceSessionService _workspace = new();
    private readonly TaskAlignmentSessionService _alignmentSession = new();

    public WorkflowAnalysisPageSafetyTests()
    {
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
        Services.AddSingleton<IArtifactTraceabilityService, ArtifactTraceabilityService>();
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        Services.AddSingleton(_alignmentSession);
        Services.AddSingleton<TaskSpecAlignmentService>();
    }

    [Fact]
    public void RequirementsTraceability_OpensWithNoWorkspace()
    {
        var cut = Render<ArtifactTraceability>();

        cut.Markup.Should().Contain("Requirements Traceability");
        cut.Markup.Should().Contain("0 artifacts loaded");
        cut.FindAll("[data-testid='artifact-traceability-error']").Should().BeEmpty();
    }

    [Fact]
    public void RequirementsTraceability_OpensWithPartialWorkspace()
    {
        _workspace.Set(WorkspaceArtifactKind.Specification, MinimalSpecification());

        var cut = Render<ArtifactTraceability>();

        cut.Markup.Should().Contain("Requirements Traceability");
        cut.Markup.Should().Contain("1 artifact loaded");
        cut.FindAll("[data-testid='artifact-traceability-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_OpensWithNoWorkspace()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.Markup.Should().Contain("Both inputs are required to run analysis.");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_OpensWithSpecAndTasksLoaded()
    {
        LoadSpecAndTasks();

        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.Markup.Should().Contain("Spec Alignment");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_StaleEmptySavedSession_DoesNotCrash()
    {
        var spec = MinimalSpecification();
        var tasks = MinimalTasks();
        _workspace.Set(WorkspaceArtifactKind.Specification, spec);
        _workspace.Set(WorkspaceArtifactKind.Tasks, tasks);
        _alignmentSession.SaveResult(
            new AlignmentReport { TotalTasks = 1, Findings = null! },
            _workspace.ProjectName,
            spec,
            tasks);

        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.Markup.Should().Contain("Spec Alignment");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    private void LoadSpecAndTasks()
    {
        _workspace.Set(WorkspaceArtifactKind.Specification, MinimalSpecification());
        _workspace.Set(WorkspaceArtifactKind.Tasks, MinimalTasks());
    }

    private static string MinimalSpecification() => """
        # Feature Specification

        ## Functional Requirements
        - FR-001: Users can sign in with valid credentials.

        ## Success Criteria
        - SC-001: Valid users reach the dashboard after sign-in.
        """;

    private static string MinimalTasks() => """
        # Tasks

        - [ ] T001 Implement sign-in flow for FR-001 and SC-001
        """;
}
