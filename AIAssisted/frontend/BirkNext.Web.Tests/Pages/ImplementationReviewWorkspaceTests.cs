using BirkNext.ProjectImport;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Implementation Review reads the Specification and Task artifacts of the current workspace through the explorers' role authority:
/// an imported project works without a Sample Project, a role with several candidates is never picked, and a result stays bound to the
/// workspace scope and both fingerprints. Generic fixtures only.
/// </summary>
public sealed class ImplementationReviewWorkspaceTests : BunitContext
{
    private const string Spec = ProjectImportActivationTests.Spec;
    private const string Tasks = "# Tasks: Cart\n\n## Phase 1: Setup\n\n- [ ] T001 Create project\n- [ ] T002 Store carts for FR-001\n";

    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly TaskAlignmentSessionService _session = new();

    public ImplementationReviewWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _samples.Repository = _workspace;
        _explorers = new ArtifactExplorerContext(_workspace, _samples, _samples);
        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        Services.AddSingleton(_session);
        Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<TaskSpecAlignmentService>();
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private void Import(string sha, params (string Path, string Content)[] documents)
    {
        var preview = ProjectImportActivationTests.Preview(sha, false, documents);
        new ProjectImportActivation(_workspace).Activate(preview, ProjectImportArtifactDiscovery.From(preview),
            ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.NotDetected));
    }

    [Fact]
    public void ImportedProject_WithSpecificationAndTasks_IsReviewed_WithoutASampleProject()
    {
        Import(new string('a', 64), ("shop/specs/001/spec.md", Spec), ("shop/specs/001/tasks.md", Tasks));

        var cut = Render<TaskToSpecAlignment>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
        _samples.GetSelectedProject().Should().BeNull();
        cut.Find("[data-testid=ir-status]").TextContent.Should().Contain("shop").And.Contain("imported project");
        cut.Markup.Should().NotContain("No Sample Project selected");
        _session.Snapshot!.ProjectName.Should().StartWith("import:", "the result is bound to the workspace scope, not a display name");
        _session.Report!.TotalTasks.Should().Be(2);
    }

    [Fact]
    public void AmbiguousSpecification_IsNeverPicked_AndNamesTheRoleToSelect()
    {
        Import(new string('b', 64), ("shop/specs/001/spec.md", Spec), ("shop/specs/002/spec.md", Spec.Replace("Cart", "Checkout")),
            ("shop/specs/001/tasks.md", Tasks));

        var cut = Render<TaskToSpecAlignment>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=ir-prerun]"));
        cut.Find("[data-testid=ir-artifact-spec]").GetAttribute("data-state").Should().Be("selection-required");
        cut.Find("[data-testid=ir-artifact-spec]").TextContent.Should().Contain("Needs selection");
        cut.Find("[data-testid=ir-artifact-spec-choose]").GetAttribute("href").Should().Be("specification-explorer");
        cut.Find("[data-testid=ir-artifact-tasks]").TextContent.Should().Contain("Selected");
        cut.Find("[data-testid=ir-needs]").TextContent.Should().StartWith("Specification needs selection");
        _session.Report.Should().BeNull("nothing runs on an arbitrary candidate");

        var spec = _explorers.GetStateAsync(WorkspaceArtifactType.Specification).GetAwaiter().GetResult();
        cut.InvokeAsync(() => _explorers.Select(WorkspaceArtifactType.Specification, spec.Artifacts.Single(a => a.SourcePath!.Contains("/001/")).Id));

        cut.WaitForAssertion(() => cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
    }

    [Fact]
    public void SwitchingProjects_DropsTheEarlierResult()
    {
        Import(new string('c', 64), ("one/spec.md", Spec), ("one/tasks.md", Tasks));
        var cut = Render<TaskToSpecAlignment>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
        var first = _session.Snapshot!.ProjectName;

        cut.InvokeAsync(() => Import(new string('d', 64), ("two/spec.md", Spec), ("two/tasks.md", Tasks + "- [ ] T003 Add checkout\n")));

        cut.WaitForAssertion(() => _session.Snapshot!.ProjectName.Should().NotBe(first));
        _session.Report!.TotalTasks.Should().Be(3);
        cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current");
    }
}
