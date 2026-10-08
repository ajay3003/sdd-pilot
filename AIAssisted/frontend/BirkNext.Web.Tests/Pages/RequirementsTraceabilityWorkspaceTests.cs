using BirkNext.ProjectImport;
using BirkNext.Web.Models;
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
/// Requirements Traceability reads the current workspace through the explorers' role authority: a Sample Project, an imported project
/// and the manual workspace are equal sources; a role with several candidates is never picked; no documents is neutral, not 0%;
/// the result follows the current project and the exact selected revisions. Generic fixtures only.
/// </summary>
public sealed class RequirementsTraceabilityWorkspaceTests : BunitContext
{
    private const string Constitution = ProjectImportActivationTests.Constitution;
    private const string Plan = ProjectImportActivationTests.Plan;
    private const string Tasks = ProjectImportActivationTests.Tasks;

    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly ProjectImportActivation _imports;
    private readonly List<ArtifactTraceabilityReport> _published = [];

    public RequirementsTraceabilityWorkspaceTests()
    {
        _samples.Repository = _workspace;
        _explorers = new ArtifactExplorerContext(_workspace, _samples, _samples);
        _imports = new ProjectImportActivation(_workspace);
        var dashboard = new Mock<IDashboardSnapshotService>();
        dashboard.Setup(d => d.Publish(It.IsAny<ArtifactTraceabilityReport>())).Callback<ArtifactTraceabilityReport>(_published.Add);

        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IArtifactTraceabilityService, ArtifactTraceabilityService>();
        Services.AddSingleton(dashboard.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private static string Spec(string id) =>
        $"# Feature Specification: Cart\n\n## User Scenarios & Testing\n\n### User Story 1 - Add to cart (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **{id}**: System MUST store carts.\n";

    /// <summary>The role cards collapse once a report is built; expanding shows them again.</summary>
    private static AngleSharp.Dom.IElement Role(IRenderedComponent<ArtifactTraceability> cut, string role)
    {
        if (cut.FindAll($"[data-testid='at-role-{role}']").Count == 0) cut.Find(".at-input-panel-toggle").Click();
        return cut.Find($"[data-testid='at-role-{role}']");
    }

    private IReadOnlyList<string> LatestRequirementIds => _published.Last().SpecToPlan.Select(c => c.ItemId).ToList();

    private void Import(string sha, params (string Path, string Content)[] documents)
    {
        var preview = ProjectImportActivationTests.Preview(sha, source: documents.Length == 0, documents);
        _imports.Activate(preview, ProjectImportArtifactDiscovery.From(preview),
            ProjectImportActivationTests.Commit(preview, documents.Length == 0 ? ProjectImportSourceState.Created : ProjectImportSourceState.NotDetected, documents.Length == 0 ? Guid.NewGuid() : null));
    }

    private void SelectSample(string slug, string requirementId)
    {
        _samples.SetProjectConstitution(slug, Constitution);
        _samples.SetProjectSpecification(slug, Spec(requirementId));
        _samples.SetProjectPlan(slug, Plan);
        _samples.SetProjectTasks(slug, Tasks);
        _workspace.CurrentProject = slug;
    }

    [Fact]
    public void SampleProject_StillDrivesTraceability()
    {
        SelectSample("shop-sample", "FR-101");

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-101"));
        cut.Find("[data-testid='at-workspace-context']").TextContent.Should().Contain("shop sample").And.Contain("Sample Project");
        Role(cut, "specification").TextContent.Should().Contain("Selected").And.Contain("Source: Sample Project");
    }

    [Fact]
    public void ImportedProject_DrivesTraceability_WithoutASampleProject()
    {
        Import(new string('a', 64), ("shop/.specify/memory/constitution.md", Constitution), ("shop/specs/001-cart/spec.md", Spec("FR-201")),
            ("shop/specs/001-cart/plan.md", Plan), ("shop/specs/001-cart/tasks.md", Tasks));

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-201"));
        _samples.GetSelectedProject().Should().BeNull();
        var context = cut.Find("[data-testid='at-workspace-context']").TextContent;
        context.Should().Contain("shop").And.Contain("Project Import");
        Role(cut, "specification").TextContent.Should().Contain("spec.md").And.Contain("Source: Project Import").And.NotContain("import:").And.NotContain(":\\");
        cut.Markup.Should().NotContain("No Sample Project selected");
    }

    [Fact]
    public void ManualWorkspace_DrivesTraceability()
    {
        _explorers.Import(new(WorkspaceArtifactType.Specification, Spec("FR-301"), "requirements.md", "File")).IsSuccess.Should().BeTrue();
        _explorers.Import(new(WorkspaceArtifactType.Plan, Plan, "roadmap.md", "File")).IsSuccess.Should().BeTrue();

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-301"));
        cut.Find("[data-testid='at-workspace-context']").TextContent.Should().Contain("None (manual workspace)").And.Contain("Manual workspace");
        Role(cut, "specification").TextContent.Should().Contain("Source: Workspace import");
    }

    [Fact]
    public void AmbiguousRole_IsNeverPicked_UntilTheUserSelectsOne()
    {
        Import(new string('b', 64), ("shop/specs/001-cart/spec.md", Spec("FR-401")), ("shop/specs/002-pay/spec.md", Spec("FR-402")),
            ("shop/specs/001-cart/plan.md", Plan));

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='at-selection-required']").TextContent.Should().Contain("Specification"));
        var spec = Role(cut, "specification");
        spec.TextContent.Should().Contain("Needs selection").And.Contain("2 candidates");
        spec.QuerySelector("a")!.GetAttribute("href").Should().Be("specification-explorer");
        _published.Should().BeEmpty();
        cut.Markup.Should().NotContain("%");
        cut.Find("button.btn-primary, button[disabled]").HasAttribute("disabled").Should().BeTrue();

        var state = _explorers.GetStateAsync(WorkspaceArtifactType.Specification).GetAwaiter().GetResult();
        _explorers.Select(WorkspaceArtifactType.Specification, state.Artifacts.Single(a => a.SourcePath!.Contains("002-pay")).Id);

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-402"));
        cut.FindAll("[data-testid='at-selection-required']").Should().BeEmpty();
    }

    [Fact]
    public void SourceOnlyImport_IsNeutral_NotZeroPercent()
    {
        Import(new string('c', 64));

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='at-no-documents']").TextContent.Should().Contain("No document artifacts are available for traceability"));
        cut.Markup.Should().NotContain("0%").And.NotContain("Failed");
        cut.FindAll("[data-testid='artifact-traceability-error']").Should().BeEmpty();
        _published.Should().BeEmpty();
    }

    [Fact]
    public void EmptyWorkspace_IsNeutral()
    {
        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='at-no-documents']"));
        cut.Markup.Should().NotContain("No Sample Project selected").And.NotContain("0%");
    }

    [Fact]
    public void SwitchingProjects_FollowsTheCurrentProject_WithNoStaleResult()
    {
        SelectSample("sample-a", "FR-501");
        var cut = Render<ArtifactTraceability>();
        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-501"));

        Import(new string('d', 64), ("b/spec.md", Spec("FR-502")), ("b/plan.md", Plan));
        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-502"));
        cut.Find("[data-testid='at-workspace-context']").TextContent.Should().Contain("Project Import");

        SelectSample("sample-c", "FR-503");
        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-503"));
        cut.Find("[data-testid='at-workspace-context']").TextContent.Should().Contain("sample c").And.Contain("Sample Project");
        _workspace.SddLifecycle.CurrentProjectImportId.Should().BeNull();
    }

    [Fact]
    public void InactiveImport_DoesNotLeakIntoTheCurrentImport()
    {
        Import(new string('e', 64), ("v1/spec.md", Spec("FR-601")), ("v1/plan.md", Plan));
        Import(new string('f', 64), ("v2/spec.md", Spec("FR-602")), ("v2/plan.md", Plan));

        var cut = Render<ArtifactTraceability>();

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-602"));
        Role(cut, "specification").TextContent.Should().Contain("Selected");
    }

    [Fact]
    public async Task ChangedArtifactContent_RebuildsTheResult()
    {
        _explorers.Import(new(WorkspaceArtifactType.Specification, Spec("FR-701"), "requirements.md", "File"));
        _explorers.Import(new(WorkspaceArtifactType.Plan, Plan, "roadmap.md", "File"));
        var cut = Render<ArtifactTraceability>();
        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-701"));
        var before = await TraceabilityInputs.ResolveAsync(_explorers);

        await cut.InvokeAsync(() => _explorers.Import(new(WorkspaceArtifactType.Specification, Spec("FR-702"), "requirements.md", "File")));

        cut.WaitForAssertion(() => LatestRequirementIds.Should().Equal("FR-702"));
        (await TraceabilityInputs.ResolveAsync(_explorers)).Key.Should().NotBe(before.Key);
    }

    [Fact]
    public async Task Inputs_UseTheExplorerRoleAuthority_AndBindToFingerprints()
    {
        _explorers.Import(new(WorkspaceArtifactType.Specification, Spec("FR-801"), "requirements.md", "File"));
        _explorers.Import(new(WorkspaceArtifactType.Tasks, Tasks, "backlog.md", "File"));

        var inputs = await TraceabilityInputs.ResolveAsync(_explorers);

        inputs.Status.Should().Be(TraceabilityInputStatus.Ready);
        inputs.Source.Should().Be(TraceabilityArtifactSource.ManualWorkspace);
        inputs.Roles.Select(r => r.Role).Should().Equal(WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks);
        inputs.Role(WorkspaceArtifactType.Specification).Fingerprint.Should().Be(ArtifactFingerprint.Compute(Spec("FR-801").Replace("\r\n", "\n")));
        inputs.Key.Should().Contain(inputs.Role(WorkspaceArtifactType.Specification).Fingerprint!);

        _explorers.Import(new(WorkspaceArtifactType.Constitution, Constitution, "rules.md", "File"));
        (await TraceabilityInputs.ResolveAsync(_explorers)).Key.Should().NotBe(inputs.Key);
    }

    [Fact]
    public async Task OneRoleOnly_IsInsufficient_NotAFailure()
    {
        _explorers.Import(new(WorkspaceArtifactType.Specification, Spec("FR-901"), "requirements.md", "File"));

        (await TraceabilityInputs.ResolveAsync(_explorers)).Status.Should().Be(TraceabilityInputStatus.Insufficient);
        var cut = Render<ArtifactTraceability>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("at least 2 are needed"));
        _published.Should().BeEmpty();
    }
}
