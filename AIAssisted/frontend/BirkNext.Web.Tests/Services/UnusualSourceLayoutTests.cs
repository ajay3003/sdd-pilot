using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// V8 decisions (2026-11-26): no specification is ever chosen by itself, and unusual source layouts keep every document. The CdcReplay case from
/// M2LB: ONE feature folder holding several specification documents (spec.md plus supporting ones) in a module next to other modules. It used to
/// be shown as «4 spesifikasjoner» and as chosen without any action (the only pack was viewed by itself and its spec.md picked as the pack file).
/// </summary>
public sealed class UnusualSourceLayoutTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    private const string SpecOther = "# Feature Specification: Orders\n\n## User Scenarios & Testing\n\n### User Story 1 - Order (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST store orders.\n";
    private const string Supporting = "# Domain Term Inventory\n\nTerms used by the feature.\n";

    public UnusualSourceLayoutTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private void Import(params (string Path, string Content)[] documents)
    {
        var preview = Preview(new string('u', 64), source: false, documents);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private void ImportCdcReplayLike() => Import(
        ("CdcReplay/specs/001-translate/spec.md", Spec), ("CdcReplay/specs/001-translate/plan.md", Plan), ("CdcReplay/specs/001-translate/tasks.md", Tasks),
        ("CdcReplay/specs/001-translate/domain-term-inventory.md", Supporting), ("CdcReplay/specs/001-translate/verification.md", Supporting.Replace("Domain Term Inventory", "Verification")),
        ("CdcReplay/autodoc/architecture.md", "# Architecture\n\nCdcReplay replays change events.\n"),
        ("Orders/specs/001-orders/spec.md", SpecOther), ("Orders/specs/002-refunds/spec.md", SpecOther.Replace("Orders", "Refunds")));

    [Fact]
    public async Task A_module_with_one_feature_folder_is_one_specification_and_nothing_is_chosen_until_the_tester_chooses_it()
    {
        ImportCdcReplayLike();
        await _explorers.SelectModuleAsync("CdcReplay");

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        state.Module.Should().Be("CdcReplay");
        state.Packs!.Select(p => p.Pack).Should().Equal(["001-translate"], "one feature folder is one specification; no folder is invented");
        state.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        state.PackRequired.Should().BeTrue("the only specification of a module is not chosen by itself (V8)");
        state.Selected.Should().BeNull();

        await _explorers.SelectPackAsync("001-translate", "CdcReplay");
        var chosen = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        chosen.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        chosen.Selected!.SourcePath.Should().EndWith("CdcReplay/specs/001-translate/spec.md", "inside the chosen pack its Spec-Kit spec.md is the specification");
    }

    [Fact]
    public async Task Every_document_of_the_layout_stays_discoverable_with_its_own_identity_and_provenance()
    {
        ImportCdcReplayLike();
        await _explorers.SelectModuleAsync("CdcReplay");
        await _explorers.SelectPackAsync("001-translate", "CdcReplay");

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        var cdc = state.Artifacts.Where(a => (a.SourcePath ?? "").StartsWith("CdcReplay/", StringComparison.Ordinal)).ToList();
        cdc.Should().Contain(a => a.SourcePath!.EndsWith("/spec.md"));
        cdc.Select(a => a.Id).Should().OnlyHaveUniqueItems("each document keeps its own canonical identity");
        cdc.Should().OnlyContain(a => a.Module == "CdcReplay", "documents keep their module provenance");
        foreach (var path in new[] { "CdcReplay/specs/001-translate/spec.md", "CdcReplay/specs/001-translate/plan.md", "CdcReplay/specs/001-translate/tasks.md",
                     "CdcReplay/specs/001-translate/domain-term-inventory.md", "CdcReplay/specs/001-translate/verification.md" })
            _repository.SddLifecycle.Revisions.Should().Contain(r => (r.SourceReference ?? r.FileName).EndsWith(path, StringComparison.Ordinal),
                $"{path} is kept by the import (no source document is dropped)");
    }

    [Fact]
    public async Task Changing_the_module_clears_the_specification_and_an_explicit_choice_is_restored_when_coming_back()
    {
        ImportCdcReplayLike();
        await _explorers.SelectModuleAsync("Orders");
        await _explorers.SelectPackAsync("002-refunds", "Orders");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Pack.Should().Be("002-refunds");

        await _explorers.SelectModuleAsync("CdcReplay");
        var cdc = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        cdc.Selected.Should().BeNull("Orders' specification never carries over to CdcReplay");
        cdc.PackRequired.Should().BeTrue();

        await _explorers.SelectModuleAsync("Orders");
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Pack.Should().Be("002-refunds", "an explicit choice is restored");
    }

    [Fact]
    public async Task A_project_without_modules_and_a_single_specification_still_asks_for_it()
    {
        Import(("docs/requirements/spec.md", Spec));
        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);
        state.Selected.Should().BeNull($"the only specification is not chosen by itself (V8); reason was {state.Reason}, packs {state.Packs?.Count}, status {state.Status}");
        state.Artifacts.Should().ContainSingle("it is listed so it can be chosen");

        _explorers.Select(WorkspaceArtifactType.Specification, state.Artifacts.Single().Id);
        (await _explorers.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Loaded);
    }
}
