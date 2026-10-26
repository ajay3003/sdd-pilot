using Bunit;
using BirkNext.ProjectImport;
using BirkNext.TestCoverage;
using BirkNext.Web.Components.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The consumers of module-scoped selection: Requirements Traceability, Test Coverage &amp; Overlap, the workspace projection (Dashboard and
/// workflow), the import preview and the candidate review read one module at a time and say which. Demo Projects use the same model.
/// </summary>
public sealed class ModuleScopedConsumersTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    private const string SpecB = "# Feature Specification: Billing\n\n## User Scenarios & Testing\n\n### User Story 1 - Pay (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST charge cards.\n";
    private const string SpecC = "# Feature Specification: Refunds\n\n## User Scenarios & Testing\n\n### User Story 1 - Refund (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST refund payments.\n";

    public ModuleScopedConsumersTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private ProjectImportPreview ImportMonorepo()
    {
        var preview = Preview(new string('a', 64), source: true,
            ("cart/.specify/memory/constitution.md", Constitution),
            ("cart/.specify/templates/spec-template.md", Spec),
            ("cart/specs/001-cart/spec.md", Spec),
            ("cart/specs/001-cart/plan.md", Plan),
            ("cart/specs/001-cart/tasks.md", Tasks),
            ("billing/specs/001-pay/spec.md", SpecB),
            ("billing/specs/002-refund/spec.md", SpecC));
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.Created, Guid.NewGuid()));
        return preview;
    }

    [Fact]
    public async Task Traceability_IsBoundToOneModule_AndNeverMixesModules()
    {
        ImportMonorepo();

        var before = await TraceabilityInputs.ResolveAsync(_explorers);
        before.Status.Should().Be(TraceabilityInputStatus.SelectionRequired);
        before.ModuleRequired.Should().BeTrue();

        await _explorers.SelectModuleAsync("cart");
        var cart = await TraceabilityInputs.ResolveAsync(_explorers);
        cart.Status.Should().Be(TraceabilityInputStatus.Ready);
        cart.ModuleLabel.Should().Be("cart");
        cart.Role(WorkspaceArtifactType.Specification).Content.Should().Be(Spec);
        cart.Role(WorkspaceArtifactType.Specification).Content.Should().NotContain("charge cards", "billing requirements never bleed into cart");
        cart.Key.Should().Contain("module=cart");

        await _explorers.SelectModuleAsync("billing");
        _explorers.Select(WorkspaceArtifactType.Specification, "workspace:billing/specs/001-pay/spec.md");
        var billing = await TraceabilityInputs.ResolveAsync(_explorers);
        billing.Key.Should().NotBe(cart.Key, "a result for cart is never current for billing");
        billing.Role(WorkspaceArtifactType.Plan).State.Status.Should().Be(ExplorerArtifactStatus.Empty, "cart's plan never stands in for billing");
    }

    [Fact]
    public async Task TestCoverageEvidence_UsesTheSelectedRealSpecification_AndRecordsItsScope()
    {
        ImportMonorepo();

        var none = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        var unscoped = TestCoveragePresentation.WorkspaceEvidence(_repository, null, "shop", none);
        unscoped.SpecificationAvailable.Should().BeFalse("no module chosen: no specification is picked");
        unscoped.SpecificationArtifacts.Should().BeEmpty();
        TestCoveragePresentation.SpecificationScope(none).Should().StartWith("Needs selection");

        await _explorers.SelectModuleAsync("cart");
        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        var evidence = TestCoveragePresentation.WorkspaceEvidence(_repository, null, "shop", spec);
        evidence.SpecificationAvailable.Should().BeTrue();
        evidence.ArtifactScope.Should().Be("Module cart");
        var artifact = evidence.SpecificationArtifacts.Should().ContainSingle().Subject;
        artifact.Path.Should().Be("cart/specs/001-cart/spec.md");
        artifact.Path.Should().NotContain("template", "a Spec-Kit template is never the reviewed specification");
        artifact.Module.Should().Be("cart");
        artifact.Fingerprint.Should().Be(ArtifactFingerprint.Compute(Spec));
        TestCoveragePresentation.SpecificationScope(spec).Should().Be("Module cart: cart/specs/001-cart/spec.md", "one pack is simply the module's documents");
    }

    [Fact]
    public async Task WorkspaceProjection_SummarizesCandidatesPerModule_AndAsksForTheModuleFirst()
    {
        ImportMonorepo();
        var specification = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        var status = new ArtifactRoleStatus(WorkspaceArtifactType.Specification, ArtifactRoleAvailability.Available, specification.Artifacts.Count,
            ArtifactRoleSelection.SelectionRequired, null, null, null, specification.Modules, specification.Module);

        status.ModuleRequired.Should().BeTrue();
        status.DetectedCount.Should().Be(3);
        status.ModuleSummary.Should().Be("3 detected across 2 modules · 2 selected", "billing's two packs each have one specification: nothing needs selection");

        var snapshot = WorkspaceSnapshots.Loaded("Auto_shop", "import:x", "shop") with { Roles = [status] };
        snapshot.ModuleRequired.Should().BeTrue();
        var documents = ProjectInputPresentation.Documents(snapshot);
        documents.StatusLabel.Should().Be("Choose a module", "several modules and none chosen is navigation — not an ambiguity, not an error and not ready");
        documents.ActionLabel.Should().Be("Choose module");
    }

    [Fact]
    public void ImportPreview_CountsCandidatesPerModule_AndNeverClaimsOnePerRole()
    {
        var preview = Preview(new string('b', 64), source: false,
            ("cart/specs/001-cart/spec.md", Spec), ("cart/specs/001-cart/plan.md", Plan),
            ("billing/specs/001-pay/spec.md", SpecB), ("billing/specs/002-refund/spec.md", SpecC));
        var rows = ProjectImportPresentation.Roles(ProjectImportArtifactDiscovery.From(preview));

        var spec = rows.Single(r => r.Role == WorkspaceArtifactType.Specification);
        spec.Status.Should().Be("3 candidates in 2 modules");
        spec.Detail.Should().Be("One per module — selected automatically for each of 2 modules", "one per artifact pack is not ambiguity");
        rows.Single(r => r.Role == WorkspaceArtifactType.Plan).Detail.Should().Be("One per module — selected automatically for each of 1 module");
        rows.Single(r => r.Role == WorkspaceArtifactType.DataModel).Status.Should().Be("Not detected");
    }

    [Fact]
    public void CandidateReview_GroupsByModule_AndSelectsForOneModuleOnly()
    {
        ImportMonorepo();
        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        Services.AddSingleton(autoSave.Object);

        var cut = Render<ProjectArtifactCandidateReview>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=pac]").GetAttribute("data-has-modules").Should().Be("true");
            var spec = cut.Find("[data-testid=pac-role][data-role=Specification]");
            spec.QuerySelector("[data-testid=pac-role-count]")!.TextContent.Should().Be("3 candidates in 2 modules");
            spec.QuerySelector("[data-testid=pac-role-state]")!.TextContent.Should().Be("2 selected");
            cut.FindAll("[data-testid=pac-candidate]").Should().NotContain(c => c.TextContent.Contains("template"));
        });

        var billingRefund = cut.FindAll("[data-testid=pac-role][data-role=Specification] [data-testid=pac-module][data-module=billing] [data-testid=pac-candidate]")
            .Single(c => c.TextContent.Contains("002-refund"));
        // Two packs, one specification each: each is its pack's artifact; nothing to choose, nothing stored as a selection.
        billingRefund.QuerySelector("[data-testid=pac-use]").Should().BeNull();
        billingRefund.QuerySelector("[data-testid=pac-selected]")!.TextContent.Should().Be("This pack's artifact");
        billingRefund.TextContent.Should().Contain("pack 002-refund");
        cut.Find("[data-testid=pac-module][data-module=billing] .pac-module-name").TextContent.Should().Contain("2 across 2 artifact packs · one per pack");
        _repository.SddLifecycle.ExplorerSelections.Should().BeEmpty();
    }

    [Fact]
    public async Task DemoProject_UsesTheSameModuleModel_AsAnImportedProject()
    {
        _samples.AddDocument("demo", WorkspaceArtifactType.Specification, "orders/specs/001-a/spec.md", Spec);
        _samples.AddDocument("demo", WorkspaceArtifactType.Specification, "billing/specs/001-b/spec.md", SpecB);
        _repository.CurrentProject = "demo";

        var spec = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        spec.ModuleRequired.Should().BeTrue();
        spec.Modules!.Select(m => m.Module).Should().Equal("billing", "orders");

        await _explorers.SelectModuleAsync("orders");
        var orders = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        orders.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        orders.Selected!.SourcePath.Should().Be("orders/specs/001-a/spec.md");
        orders.Reason.Should().Be(ExplorerSelectionReason.OnlyArtifact);
    }
}
