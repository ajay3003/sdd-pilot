using System.Text.Json;
using BirkNext.ProjectImport;
using BirkNext.Web.Components.Explorers;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Module → Artifact Packs → Artifacts (ARTIFACT-ARCH-01/UX-01): feature folders (<c>specs/001-…</c>) are packs, not competing versions of one
/// artifact. A pack resolves its own artifact of each role, else the shared (module/project) one, and never a sibling pack's. Viewing a pack is
/// navigation, never authority; only two artifacts of a role in the same pack ask the user to choose. Generic fixtures only.
/// </summary>
public sealed class ArtifactPackTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    private const string SpecB = "# Feature Specification: Refunds\n\n## User Scenarios & Testing\n\n### User Story 1 - Refund (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST refund payments.\n";
    private const string SpecC = "# Feature Specification: Invoices\n\n## User Scenarios & Testing\n\n### User Story 1 - Invoice (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST issue invoices.\n";
    private const string DataModel = "# Data Model: Cart\n\n## Entities\n\n### Cart\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n";

    public ArtifactPackTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private SddProjectImportRecord Import(params (string Path, string Content)[] documents)
    {
        var preview = Preview(new string('p', 64), source: false, documents);
        return new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private Task<ArtifactExplorerState> State(WorkspaceArtifactType role) => _explorers.GetStateAsync(role);

    private void ImportThreePacks() => Import(
        (".specify/memory/constitution.md", Constitution),
        ("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/001-cart/tasks.md", Tasks), ("specs/001-cart/data-model.md", DataModel),
        ("specs/002-refund/spec.md", SpecB), ("specs/002-refund/plan.md", Plan.Replace("Cart", "Refund")),
        ("specs/003-invoice/spec.md", SpecC));

    [Fact]
    public async Task Import_GroupsFeatureFolders_IntoPacks_WithTheirRoles_AndNoAmbiguity()
    {
        var record = Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/001-cart/tasks.md", Tasks),
            ("specs/002-refund/spec.md", SpecB), ("specs/002-refund/plan.md", Plan.Replace("Cart", "Refund")));

        record.AmbiguousRoles.Should().BeEmpty("one artifact per role per pack is organisation, not ambiguity");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Packs!.Select(p => (p.Pack, p.FeatureId, p.DisplayName, p.Artifacts)).Should().Equal(("001-cart", "001", "Cart", 3), ("002-refund", "002", "Refunds", 2));
        spec.Packs![0].RolesPresent.Should().Equal(WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks);
        spec.Packs![1].RolesPresent.Should().Equal(WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan);
        spec.PackRequired.Should().BeTrue("several packs and none viewed: the user selects one to explore");
        spec.Selected.Should().BeNull();

        var rows = ProjectImportPresentation.Roles(ProjectImportArtifactDiscovery.From(Preview(new string('q', 64), false,
            ("specs/001-cart/spec.md", Spec), ("specs/002-refund/spec.md", SpecB))));
        rows.Single(r => r.Role == WorkspaceArtifactType.Specification).Should().Match<ProjectImportRoleRow>(r =>
            r.Status == "2 artifacts across 2 packs" && r.Detail == "One per artifact pack" && r.Tone == ProjectImportTone.Available);
    }

    [Fact]
    public async Task A_single_pack_is_selected_automatically()
    {
        Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan));

        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.PackRequired.Should().BeFalse();
        spec.HasPacks.Should().BeFalse("one pack is simply the project's documents");
        spec.Pack.Should().Be("001-cart");
        spec.Reason.Should().Be(ExplorerSelectionReason.OnlyArtifact);
    }

    [Fact]
    public async Task Three_packs_are_never_a_selection_problem_and_each_resolves_its_own_artifacts_plus_shared_ones()
    {
        ImportThreePacks();

        var overview = await _explorers.GetModuleOverviewAsync();
        overview.Role(WorkspaceArtifactType.Specification)!.ModulesNeedingSelection.Should().Be(0, "three packs are not three competing specifications");

        await _explorers.SelectPackAsync("002-refund");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Pack == "002-refund" && s.Reason == ExplorerSelectionReason.PackArtifact);
        spec.Content.Should().Be(SpecB);
        spec.Artifacts.Should().OnlyContain(a => a.Feature == "002-refund" || a.Feature == null, "a pack's view never lists a sibling pack's artifacts");

        var constitution = await State(WorkspaceArtifactType.Constitution);
        constitution.Reason.Should().Be(ExplorerSelectionReason.SharedArtifact);
        ArtifactPacks.ScopeOf(constitution.Selected!).Should().Be(ArtifactScope.Project);

        var tasks = await State(WorkspaceArtifactType.Tasks);
        tasks.Status.Should().Be(ExplorerArtifactStatus.Empty, "pack 001-cart's tasks never stand in for pack 002-refund");
        (await State(WorkspaceArtifactType.DataModel)).Status.Should().Be(ExplorerArtifactStatus.Empty);

        await _explorers.SelectPackAsync("003-invoice");
        (await State(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Empty);
        _repository.Get(WorkspaceArtifactType.Plan).Should().BeNull("older consumers never read a sibling pack's plan");
        _repository.Get(WorkspaceArtifactType.Specification)!.Text.Should().Be(SpecC);
    }

    [Fact]
    public async Task A_pack_local_constitution_wins_over_the_shared_one_only_for_its_pack()
    {
        Import((".specify/memory/constitution.md", Constitution),
            ("specs/001-cart/spec.md", Spec), ("specs/001-cart/constitution.md", Constitution.Replace("Test first", "Pack rule")),
            ("specs/002-refund/spec.md", SpecB));

        await _explorers.SelectPackAsync("001-cart");
        var local = await State(WorkspaceArtifactType.Constitution);
        local.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Reason == ExplorerSelectionReason.PackArtifact);
        local.Content.Should().Contain("Pack rule");

        await _explorers.SelectPackAsync("002-refund");
        var shared = await State(WorkspaceArtifactType.Constitution);
        shared.Reason.Should().Be(ExplorerSelectionReason.SharedArtifact);
        shared.Content.Should().NotContain("Pack rule");
    }

    [Fact]
    public async Task Two_artifacts_of_a_role_in_the_same_pack_are_the_only_ambiguity()
    {
        var record = Import(("specs/001-cart/feature.md", Spec), ("specs/001-cart/feature-draft.md", Spec.Replace("store carts", "keep carts")),
            ("specs/002-refund/spec.md", SpecB));

        record.AmbiguousRoles.Should().Equal("Specification");
        await _explorers.SelectPackAsync("002-refund");
        (await State(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Loaded, "pack 002 is unambiguous");

        await _explorers.SelectPackAsync("001-cart");
        var ambiguous = await State(WorkspaceArtifactType.Specification);
        ambiguous.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.SelectionRequired && s.AmbiguousInPack && !s.PackRequired);
        ambiguous.Artifacts.Should().HaveCount(2).And.OnlyContain(a => a.Feature == "001-cart");
        var overview = await _explorers.GetModuleOverviewAsync();
        overview.Role(WorkspaceArtifactType.Specification)!.ModulesNeedingSelection.Should().Be(1);

        _explorers.Select(WorkspaceArtifactType.Specification, ambiguous.Artifacts[1].Id);
        var chosen = await State(WorkspaceArtifactType.Specification);
        chosen.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Reason == ExplorerSelectionReason.Explicit);
        chosen.Selected!.IsAuthoritative.Should().BeFalse("choosing what to view never makes an artifact authoritative");
        _repository.SddLifecycle.ExplorerSelections.Should().ContainSingle().Which.Pack.Should().Be("001-cart");

        await _explorers.SelectPackAsync("002-refund");
        (await State(WorkspaceArtifactType.Specification)).Content.Should().Be(SpecB, "a choice in pack 001 never applies to pack 002");
    }

    [Fact]
    public async Task The_spec_kit_feature_file_is_the_pack_s_artifact_when_supporting_documents_look_like_the_same_role()
    {
        // A quickstart that reads like a specification sits next to the pack's spec.md (seen in a real Spec-Kit repository).
        var record = Import(("specs/001-cart/spec.md", Spec), ("specs/001-cart/quickstart.md", Spec.Replace("store carts", "try the cart")),
            ("specs/002-refund/spec.md", SpecB));

        record.AmbiguousRoles.Should().BeEmpty("spec.md is the pack's specification by Spec-Kit structure");
        await _explorers.SelectPackAsync("001-cart");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Reason == ExplorerSelectionReason.PackFile);
        spec.Selected!.SourcePath.Should().Be("specs/001-cart/spec.md");
        spec.Artifacts.Should().Contain(a => a.SourcePath == "specs/001-cart/quickstart.md", "the other candidate stays selectable");
        (await _explorers.GetModuleOverviewAsync()).Role(WorkspaceArtifactType.Specification)!.ModulesNeedingSelection.Should().Be(0);

        _explorers.Select(WorkspaceArtifactType.Specification, "workspace:specs/001-cart/quickstart.md");
        (await State(WorkspaceArtifactType.Specification)).Should().Match<ArtifactExplorerState>(s => s.Reason == ExplorerSelectionReason.Explicit && s.Selected!.SourcePath == "specs/001-cart/quickstart.md",
            "an explicit choice always wins");
        ArtifactPacks.IsPackFile("Person/specs/002-x/spec.md", WorkspaceArtifactType.Specification).Should().BeTrue();
        ArtifactPacks.IsPackFile("Person/specs/002-x/contracts/spec.md", WorkspaceArtifactType.Specification).Should().BeFalse();
    }

    [Fact]
    public async Task The_spec_kit_constitution_wins_over_a_governance_document_and_real_ambiguity_still_asks()
    {
        // Seen in a real repository: the project's .specify/memory/constitution.md next to a governance document that reads like one.
        Import((".specify/memory/constitution.md", Constitution), ("docs/governance.md", Constitution.Replace("Test first", "Shared libraries")),
            ("specs/001-cart/spec.md", Spec));
        var constitution = await State(WorkspaceArtifactType.Constitution);
        constitution.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Reason == ExplorerSelectionReason.SharedArtifact);
        constitution.Selected!.SourcePath.Should().Be(".specify/memory/constitution.md");
        constitution.Artifacts.Should().HaveCount(2, "the governance document stays selectable");
    }

    [Fact]
    public async Task Without_packs_the_spec_kit_constitution_also_resolves()
    {
        Import((".specify/memory/constitution.md", Constitution), ("docs/governance.md", Constitution.Replace("Test first", "Shared libraries")));
        var constitution = await State(WorkspaceArtifactType.Constitution);
        constitution.Should().Match<ArtifactExplorerState>(s => s.Status == ExplorerArtifactStatus.Loaded && s.Reason == ExplorerSelectionReason.PackFile);
        constitution.Selected!.SourcePath.Should().Be(".specify/memory/constitution.md");
    }

    [Fact]
    public async Task Two_constitutions_outside_spec_kit_locations_still_ask_the_user()
    {
        Import(("a/docs/constitution.md", Constitution), ("b/docs/constitution.md", Constitution.Replace("Test first", "Review first")));
        (await State(WorkspaceArtifactType.Constitution)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
    }

    [Fact]
    public async Task A_selection_saved_before_packs_migrates_to_its_pack_and_the_focus_survives_a_restore()
    {
        ImportThreePacks();
        var scope = _explorers.CurrentScope;
        // An earlier BirkNext stored the choice without a pack (and no pack focus): it now opens that artifact's pack.
        _repository.SddLifecycle.ExplorerSelections.Add(new SddExplorerSelection { WorkspaceScope = scope, Role = "Specification", ArtifactId = "workspace:specs/003-invoice/spec.md", Module = "" });

        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Pack.Should().Be("003-invoice");
        spec.Content.Should().Be(SpecC);

        await _explorers.SelectPackAsync("001-cart");
        var saved = JsonSerializer.Deserialize<SddLifecycleState>(JsonSerializer.Serialize(_repository.SddLifecycle))!;
        saved.PackFocus.Should().ContainSingle(f => f.WorkspaceScope == scope && f.Pack == "001-cart" && f.Module == "");
        JsonSerializer.Deserialize<SddLifecycleState>("{}")!.PackFocus.Should().BeEmpty("older saved workspaces restore without packs");
    }

    [Fact]
    public async Task Traceability_inputs_are_bound_to_the_viewed_pack_and_never_mix_sibling_packs()
    {
        ImportThreePacks();
        (await TraceabilityInputs.ResolveAsync(_explorers)).Should().Match<TraceabilityInputs>(i => i.PackRequired && i.Status == TraceabilityInputStatus.SelectionRequired);

        await _explorers.SelectPackAsync("001-cart");
        var cart = await TraceabilityInputs.ResolveAsync(_explorers);
        cart.Status.Should().Be(TraceabilityInputStatus.Ready);
        cart.Pack.Should().Be("001-cart");
        cart.Key.Should().Contain("pack=001-cart");

        await _explorers.SelectPackAsync("002-refund");
        var refund = await TraceabilityInputs.ResolveAsync(_explorers);
        refund.Key.Should().NotBe(cart.Key, "a result for one pack is never current for another");
        refund.Role(WorkspaceArtifactType.Specification).Content.Should().Be(SpecB);
        refund.Role(WorkspaceArtifactType.Tasks).IsLoaded.Should().BeFalse();
        refund.Role(WorkspaceArtifactType.Constitution).Content.Should().Be(Constitution, "the shared constitution applies to every pack");
    }

    [Fact]
    public async Task Workspace_projection_asks_for_a_pack_not_for_a_specification()
    {
        ImportThreePacks();
        var spec = await State(WorkspaceArtifactType.Specification);
        var status = new ArtifactRoleStatus(WorkspaceArtifactType.Specification, ArtifactRoleAvailability.Available, spec.Artifacts.Count,
            ArtifactRoleSelection.SelectionRequired, null, null, null, spec.Modules, spec.Module, spec.Packs, spec.Pack, spec.PackRequired);
        var snapshot = WorkspaceSnapshots.Loaded("Auto_shop", "import:x", "shop") with { Roles = [status] };

        snapshot.PackRequired.Should().BeTrue();
        snapshot.PackCount.Should().Be(3);
        var documents = ProjectInputPresentation.Documents(snapshot);
        documents.StatusLabel.Should().Be("Select a pack");
        documents.ActionLabel.Should().Be("Select pack");
        documents.Detail.Should().Contain("3 artifact packs").And.NotContain("none is selected");
    }

    [Fact]
    public void Documentation_Drift_labels_a_comparison_across_packs_and_not_one_within_a_pack()
    {
        static DriftDocument Doc(string path, int revision) => new($"authored:{path}:{revision}", DriftOrigin.Authored, "authored|Specification|" + path, path, path,
            "Specification", "fp" + revision, "imp", "R1", "a.zip", Guid.NewGuid(), revision, null, null, "", null, true, true, "import:imp");

        DocumentationDrift.PackContext(Doc("specs/001-cart/spec.md", 1), Doc("specs/002-refund/spec.md", 1)).Should()
            .Be("Cross-pack comparison — pack 001-cart vs pack 002-refund. These are different features, not revisions of one artifact.");
        DocumentationDrift.PackContext(Doc("specs/001-cart/spec.md", 1), Doc("specs/001-cart/spec.md", 2)).Should().BeNull("same pack, same type, another revision is ordinary drift");
        DocumentationDrift.PackContext(Doc("docs/spec.md", 1), Doc("specs/001-cart/spec.md", 1)).Should().BeNull();
    }

    private IRenderedComponent<ArtifactExplorerHost> Host(WorkspaceArtifactType role)
    {
        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        return Render<ArtifactExplorerHost>(p => p.Add(x => x.Role, role));
    }

    [Fact]
    public async Task The_explorer_offers_pack_cards_then_a_pack_selector_and_says_when_a_pack_has_no_artifact()
    {
        ImportThreePacks();
        var cut = Host(WorkspaceArtifactType.Plan);

        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer-choose-pack] h2").TextContent.Should().Be("Choose a feature to explore"));
        cut.Markup.Should().NotContain("represents the current workspace").And.NotContain("needs selection");
        var cards = cut.FindAll("[data-testid=artifact-explorer-pack-choice]");
        cards.Select(c => c.GetAttribute("data-pack")).Should().Equal("001-cart", "002-refund", "003-invoice");
        cards[0].TextContent.Should().Contain("Cart").And.Contain("pack 001-cart").And.Contain("4 documents").And.Contain("Data Model");

        cards[1].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer-pack]").TextContent.Should().Be("Refunds (002-refund)"));
        cut.Find("[data-testid=artifact-explorer-scope]").TextContent.Should().Be("Pack 002-refund");
        cut.Find("[data-testid=artifact-explorer-reason]").TextContent.Should().Be("This pack's artifact", "the exact selection reason stays under Details");
        cut.Find("[data-testid=artifact-explorer-primary]").TextContent.Should().StartWith("Primary ", "testers see the primary document, not the selection mechanics");
        cut.Find("[data-testid=artifact-explorer-pack-selector]").GetAttribute("value").Should().Be("002-refund");

        cut.Find("[data-testid=artifact-explorer-pack-selector]").Change("003-invoice");
        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid=artifact-explorer-empty]");
            empty.GetAttribute("data-state").Should().Be("NotInPack");
            empty.QuerySelector("h2")!.TextContent.Should().Be("No plan is available for this feature");
        });
        cut.Find("[data-testid=artifact-explorer-pack-bar]");
        _repository.SddLifecycle.ExplorerSelections.Should().BeEmpty("viewing a pack stores focus, never a selection or authority");
        (await State(WorkspaceArtifactType.Specification)).Pack.Should().Be("003-invoice", "every explorer views the same pack");
    }

    [Fact]
    public void Same_pack_ambiguity_is_worded_for_the_pack()
    {
        Import(("specs/001-cart/feature.md", Spec), ("specs/001-cart/feature-draft.md", Spec.Replace("store carts", "keep carts")), ("specs/002-refund/spec.md", SpecB));
        _explorers.SelectPackAsync("001-cart").GetAwaiter().GetResult();
        var cut = Host(WorkspaceArtifactType.Specification);

        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer-candidates]").TextContent.Should()
            .Contain("2 Specification artifacts were found in pack Cart (001-cart)").And.Contain("Choose the Specification artifact this pack uses."));
        cut.Markup.Should().NotContain("represents the current workspace");
    }
}

/// <summary>Document Quality Review per artifact pack: each pack runs on its own inputs plus shared ones; the module figure is an aggregate.</summary>
public sealed class ArtifactPackReviewTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly List<(string? Constitution, string? Spec, string? Plan, string? Tasks)> _calls = [];
    private readonly Moq.Mock<IQualityReviewService> _review = new();

    private const string SpecB = "# Feature Specification: Refunds\n\n## User Scenarios & Testing\n\n### User Story 1 - Refund (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST refund payments.\n";

    public ArtifactPackReviewTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        _review.Setup(r => r.RunAsync(Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((string? c, string? s, string? p, string? t, string? d, IEnumerable<string> _) =>
            {
                _calls.Add((c, s, p, t));
                // A plan-dependent rule can only fire when the pack has its own plan: findings reflect the pack's inputs.
                return new QualityReviewReport { AssessedPacks = 1, OverallScore = p is null ? 50 : 90, TotalFindings = p is null ? 3 : 1 };
            });
        var preview = Preview(new string('r', 64), source: false,
            (".specify/memory/constitution.md", Constitution),
            ("specs/001-cart/spec.md", Spec), ("specs/001-cart/plan.md", Plan), ("specs/001-cart/tasks.md", Tasks),
            ("specs/002-refund/spec.md", SpecB));
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    [Fact]
    public async Task Every_pack_is_reviewed_on_its_own_inputs_and_the_module_figure_is_an_aggregate()
    {
        var packs = (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Packs!;

        var summary = await ArtifactPackReview.RunAsync(_explorers, _review.Object, packs, ["quality"]);

        _calls.Should().HaveCount(2);
        _calls[0].Should().Be((Constitution, Spec, Plan, Tasks));
        _calls[1].Should().Be((Constitution, SpecB, null, null), "pack 002 never borrows pack 001's plan or tasks; the shared constitution applies");
        summary.Packs.Select(p => (p.Pack.Pack, p.CompletenessLabel, p.Score, p.Findings)).Should().Equal(("001-cart", "Complete", 90d, 1), ("002-refund", "Partial", 50d, 3));
        summary.Packs[1].Inputs.Should().Contain(i => i.Role == WorkspaceArtifactType.Constitution && i.Scope == ArtifactScope.Project);
        summary.Headline.Should().Be("2 artifact packs assessed · 1 complete · 1 partial · 4 findings across 2 packs");
        summary.AggregateScore.Should().Be(70);
        _repository.SddLifecycle.PackFocus.Should().BeEmpty("reviewing every pack never changes the pack being viewed");
    }

    [Fact]
    public void The_panel_shows_pack_scores_first_and_labels_the_aggregate()
    {
        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        Services.AddSingleton(_review.Object);
        var packs = _explorers.GetStateAsync(WorkspaceArtifactType.Specification).GetAwaiter().GetResult().Packs!;
        var cut = Render<BirkNext.Web.Components.ArtifactPackReviewPanel>(p => p.Add(x => x.Packs, packs).Add(x => x.ReviewPackIds, ["quality"]));

        cut.Find("[data-testid=apr-run]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=apr-headline]").TextContent.Should().Contain("2 artifact packs assessed"));
        var rows = cut.FindAll("[data-testid=apr-row]");
        rows.Select(r => r.GetAttribute("data-pack")).Should().Equal("001-cart", "002-refund");
        rows[1].QuerySelector("[data-testid=apr-inputs]")!.TextContent.Should().Be("Constitution (shared), Specification");
        rows[1].QuerySelector("[data-testid=apr-score]")!.TextContent.Should().Be("50%");
        cut.Find("[data-testid=apr-aggregate]").TextContent.Should().Contain("Module aggregate (mean of assessed packs): 70%");
    }
}
