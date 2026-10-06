using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.SampleProjects;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Document explorers are consumers of the current workspace: they open artifacts by role, whatever their file name or
/// source, and do not depend on a Sample Project being selected.
/// </summary>
public sealed class ExplorerPageSourceOfTruthTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly Mock<IConstitutionAnalysisService> _constitution = new();

    public ExplorerPageSourceOfTruthTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var realConstitution = new ConstitutionAnalysisService();
        _constitution.Setup(s => s.Parse(It.IsAny<string>())).Returns<string>(realConstitution.Parse);

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton(_constitution.Object);
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<ISampleProjectArtifactDiscovery>(_samples);
        Services.AddSingleton<IArtifactExplorerContext>(new ArtifactExplorerContext(_workspace, _samples, _samples));
    }

    private IRenderedComponent<IComponent> RenderExplorer(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => Render<ConstitutionExplorer>(),
        WorkspaceArtifactType.Plan => Render<PlanExplorer>(),
        WorkspaceArtifactType.Tasks => Render<TaskExplorer>(),
        WorkspaceArtifactType.DataModel => Render<DataModelExplorer>(),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static TheoryData<WorkspaceArtifactType, string, string> CustomFiles => new()
    {
        { WorkspaceArtifactType.Constitution, "governance-rules.md", "# Governance Rules\n\n## Core Principles\n\n### I. Test First\nAll code MUST have tests.\n" },
        { WorkspaceArtifactType.Plan, "implementation-roadmap.md", "# Implementation Roadmap\n\n## Summary\nBuild it.\n\n## Technical Context\n**Language**: C#\n" },
        { WorkspaceArtifactType.Tasks, "delivery-backlog.md", "# Delivery Backlog\n\n## Phase 1: Setup\n\n- [ ] T001 Create project\n" },
        { WorkspaceArtifactType.DataModel, "domain-schema.md", "# Domain Schema\n\n## Entities\n\n### Student\n| Field | Type |\n|---|---|\n| Id | int |\n" },
    };

    [Theory]
    [MemberData(nameof(CustomFiles))]
    public void NoSampleProject_ImportedCustomFileName_OpensInItsExplorer(WorkspaceArtifactType role, string fileName, string content)
    {
        _workspace.AddArtifactRevision(role, content, fileName, null, null, "File", select: true);

        var cut = RenderExplorer(role);

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer']").GetAttribute("data-status").Should().Be("Loaded");
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be(fileName);
            cut.Markup.Should().NotContain("No Sample Project selected");
        });
    }

    [Theory]
    [InlineData(WorkspaceArtifactType.Constitution, "Constitution")]
    [InlineData(WorkspaceArtifactType.Plan, "Plan")]
    [InlineData(WorkspaceArtifactType.Tasks, "Task")]
    [InlineData(WorkspaceArtifactType.DataModel, "Data Model")]
    public void EmptyWorkspace_ShowsSharedActionableEmptyState(WorkspaceArtifactType role, string label)
    {
        var cut = RenderExplorer(role);

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid='artifact-explorer-empty']");
            empty.QuerySelector("h2")!.TextContent.Should().Be($"No {label} artifact is loaded");
            empty.QuerySelector("[data-testid='artifact-explorer-import-toggle']")!.TextContent.Trim().Should().Be($"Import {label.ToLowerInvariant()}");
            empty.QuerySelector("a[href='sample-projects']")!.TextContent.Should().Be("Open Sample Projects");
            cut.Markup.Should().NotContain("No Sample Project selected");
            cut.Markup.Should().NotContain(ArtifactExplorerRoles.CommonFileName(role) + " files");
        });
    }

    [Fact]
    public void SampleProjectWithoutConstitution_ShowsRoleSpecificEmptyStateForThatProject()
    {
        _samples.SetProjectSpecification("project-a", "# Spec");
        _samples.SetSelectedProject("project-a");

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-empty'] h2").TextContent.Should().Be("No Constitution artifact was detected in project a");
            cut.Markup.Should().NotContain("No Sample Project selected");
        });
    }

    [Fact]
    public void ConstitutionExplorer_UsesSampleProjectArtifact_NotManualWorkspaceOrLegacyScratch()
    {
        _samples.AddDocument("project-b", WorkspaceArtifactType.Constitution, ".specify/memory/constitution.md", "# Project B Constitution");
        _samples.SetSelectedProject("project-b");
        _workspace.Set(WorkspaceArtifactKind.Constitution, "# Workspace Constitution");

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Project B Constitution");
            cut.Markup.Should().NotContain("Workspace Constitution");
            JSInterop.Invocations.Should().NotContain(i => i.Identifier == "localStorage.getItem",
                "explorers keep no content of their own outside the repository");
        });
    }

    [Fact]
    public void PasteImport_AddsArtifactToRepositoryAndOpensIt()
    {
        var cut = Render<PlanExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-empty']"));

        cut.Find("[data-testid='artifact-explorer-import-toggle']").Click();
        cut.Find("label[for$='-text']").TextContent.Should().Be("Plan content");
        cut.Find("[data-testid='artifact-import-name']").Change("implementation-roadmap.md");
        cut.Find("[data-testid='artifact-import-text']").Input("# Implementation Roadmap\n\n## Summary\nShip it.\n");
        cut.Find("[data-testid='artifact-import-panel']").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("implementation-roadmap.md");
            cut.Find("[data-testid='artifact-explorer-source']").TextContent.Should().Contain("Pasted text");
        });
        var revision = _workspace.SddLifecycle.Revisions.Single();
        revision.Role.Should().Be("Plan");
        revision.Origin.Should().Be("Paste");
        revision.Authority.Should().Be("Unknown");
        _workspace.Get(WorkspaceArtifactType.Plan)!.Text.Should().StartWith("# Implementation Roadmap");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Import_SavesWorkspaceNow_AndReportsAFailedSave(bool saved)
    {
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(saved);
        Services.AddSingleton(autoSave.Object);
        var cut = Render<TaskExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-import-toggle']"));

        cut.Find("[data-testid='artifact-explorer-import-toggle']").Click();
        cut.Find("[data-testid='artifact-import-text']").Input("# Delivery Backlog\n\n- [ ] T001 Build\n");
        cut.Find("[data-testid='artifact-import-panel']").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("pasted-task.md");
            autoSave.Verify(a => a.SaveNowAsync(), Times.Once);
            cut.FindAll("[data-testid='artifact-explorer-save-warning']").Should().HaveCount(saved ? 0 : 1);
        });
    }

    [Fact]
    public void MultipleArtifacts_WithoutAuthority_AskToChoose_ThenSelectorSwitches()
    {
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Tasks, "# Backlog A\n\n- [ ] T001 A\n", "backlog-a.md", null, null, "File", select: false);
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Tasks, "# Backlog B\n\n- [ ] T001 B\n", "backlog-b.md", null, null, "File", select: false);

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='artifact-explorer-choice']").Should().HaveCount(2));
        cut.Find("[data-testid='artifact-explorer-choose'] h2").TextContent.Should().Be("Choose a Task artifact");

        cut.FindAll("[data-testid='artifact-explorer-choice']")[1].Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("Backlog B"));
        var selector = cut.Find("[data-testid='artifact-explorer-selector']");
        cut.Find($"label[for='{selector.Id}']").TextContent.Should().Be("2 Task artifacts");

        selector.Change(ArtifactExplorerContext.WorkspacePrefix + "backlog-a.md");
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("Backlog A"));
    }

    [Fact]
    public void AuthoritativeArtifact_IsOpenedAndMarked()
    {
        _workspace.AddArtifactRevision(WorkspaceArtifactType.DataModel, "# Draft schema\n", "draft.md", null, null, "File", select: false);
        var approved = _workspace.AddArtifactRevision(WorkspaceArtifactType.DataModel, "# Approved schema\n", "domain-schema.md", null, null, "File", select: false)!;
        _workspace.SetArtifactAuthority(approved.RevisionId, "Approved");

        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("Approved schema");
            cut.Find("[data-testid='artifact-explorer-authoritative']").TextContent.Should().Contain("Approved");
            cut.Find("[data-testid='artifact-explorer-reason']").TextContent.Should().Contain("authoritative");
        });
    }

    [Fact]
    public void HistoricalArtifact_ShowsItsStateAsText()
    {
        var revision = _workspace.AddArtifactRevision(WorkspaceArtifactType.Plan, "# Old plan\n", "old-plan.md", null, null, "File", select: true)!;
        _workspace.SetArtifactAuthority(revision.RevisionId, "Historical");

        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-currentness']").TextContent.Should().Be("Historical"));
    }

    [Fact]
    public void Reset_EmptiesOpenExplorerWithoutPolling()
    {
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Constitution, "# Rules\n", "governance-rules.md", null, null, "File", select: true);
        var cut = Render<ConstitutionExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-file']"));

        _workspace.ClearAll();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-empty']");
            cut.Markup.Should().NotContain("governance-rules.md");
        });
    }

    [Fact]
    public void ProjectSwitch_UpdatesExplorer()
    {
        _samples.SetProjectPlan("project-a", "# Plan A\n");
        _samples.SetProjectPlan("project-b", "# Plan B\n");
        _samples.SetSelectedProject("project-a");
        var cut = Render<PlanExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("Plan A"));

        _samples.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-name']").TextContent.Should().Be("Plan B");
            cut.Markup.Should().NotContain("Plan A");
        });
    }

    [Fact]
    public void ParseFailure_IsShownAsAnalysisError_NotEmptyState()
    {
        _constitution.Setup(s => s.Parse(It.IsAny<string>())).Throws(new FormatException("Unexpected structure"));
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Constitution, "# Broken\n", "broken.md", null, null, "File", select: true);

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='explorer-analysis-error']").TextContent.Should().Contain("Unexpected structure");
            cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("broken.md");
            cut.FindAll("[data-testid='artifact-explorer-empty']").Should().BeEmpty();
        });
    }

    [Fact]
    public void RepositoryError_IsShownAsError_NotEmptyState()
    {
        _samples.RegisterProject("p");
        _samples.SetSelectedProject("p");
        _samples.DiscoveryFailure = new HttpRequestException("backend down");

        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='artifact-explorer-error']").TextContent.Should().Contain("backend down");
            cut.FindAll("[data-testid='artifact-explorer-empty']").Should().BeEmpty();
        });
    }

    [Fact]
    public void ImportMismatch_KeepsChosenRole_AndWarns()
    {
        var cut = Render<ConstitutionExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-import-toggle']"));
        cut.Find("[data-testid='artifact-explorer-import-toggle']").Click();
        cut.Find("[data-testid='artifact-import-name']").Change("tasks.md");
        cut.Find("[data-testid='artifact-import-text']").Input("# Tasks: Feature\n\n## Phase 1: Setup\n\n- [ ] T001 Create project structure\n- [ ] T002 [P] Configure linting\n- [ ] T003 Add CI\n");
        cut.Find("[data-testid='artifact-import-panel']").Submit();

        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='artifact-explorer-role-warning']").TextContent.Should().Contain("looks like a Task artifact"));
        _workspace.SddLifecycle.Revisions.Single().Role.Should().Be("Constitution");
    }
}
