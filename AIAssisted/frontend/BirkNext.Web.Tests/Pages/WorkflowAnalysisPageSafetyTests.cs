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
    private readonly BirkNext.Web.Services.Explorers.ArtifactExplorerContext _explorers;

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
        Services.AddSingleton(new Mock<ISampleProjectDocumentResolver>().Object);
        var samples = new MockSampleProjectDocumentResolver();
        _explorers = new BirkNext.Web.Services.Explorers.ArtifactExplorerContext(_workspace, samples, samples);
        Services.AddSingleton<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>(_explorers);
        Services.AddSingleton(_alignmentSession);
        Services.AddSingleton<TaskSpecAlignmentService>();
    }

    [Fact]
    public void RequirementsTraceability_OpensWithNoWorkspace()
    {
        var cut = Render<ArtifactTraceability>();

        cut.Markup.Should().Contain("Requirements Traceability");
        cut.WaitForAssertion(() => cut.Find("[data-testid='at-no-documents']"));
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.FindAll("[data-testid='artifact-traceability-error']").Should().BeEmpty();
    }

    [Fact]
    public void RequirementsTraceability_OpensWithPartialWorkspace()
    {
        _workspace.Set(WorkspaceArtifactKind.Specification, MinimalSpecification());

        var cut = Render<ArtifactTraceability>();

        cut.Markup.Should().Contain("Requirements Traceability");
        cut.WaitForAssertion(() => cut.Find("[data-testid='at-workspace-context']"));
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.FindAll("[data-testid='artifact-traceability-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_OpensWithNoWorkspace()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.WaitForAssertion(() => cut.Find("[data-testid='ir-no-project']").TextContent.Should().Contain("No project is currently loaded"));
        cut.Find("[data-testid='ir-import-project']").GetAttribute("href").Should().Be("project-import");
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_OpensWithSpecAndTasksLoaded()
    {
        LoadSpecAndTasks();

        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.WaitForAssertion(() => cut.Find("[data-testid='ir-status']").GetAttribute("data-state").Should().Be("Current"));
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    [Fact]
    public void ImplementationReview_StaleEmptySavedSession_DoesNotCrash()
    {
        var spec = MinimalSpecification();
        var tasks = MinimalTasks();
        _explorers.Import(new(WorkspaceArtifactType.Specification, spec, "spec.md", "File"));
        _explorers.Import(new(WorkspaceArtifactType.Tasks, tasks, "tasks.md", "File"));
        _alignmentSession.SaveResult(
            new AlignmentReport { TotalTasks = 1, Findings = null! },
            "manual-workspace",
            spec,
            tasks);

        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().Contain("Implementation Review");
        cut.Markup.Should().NotContain("No Sample Project selected");
        cut.FindAll("[data-testid='implementation-review-error']").Should().BeEmpty();
    }

    private void LoadSpecAndTasks()
    {
        // A manual workspace: documents imported in the explorers, no Sample Project.
        _explorers.Import(new(WorkspaceArtifactType.Specification, MinimalSpecification(), "spec.md", "File"));
        _explorers.Import(new(WorkspaceArtifactType.Tasks, MinimalTasks(), "tasks.md", "File"));
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
