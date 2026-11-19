using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Services.SampleProjects;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A monorepo's features (M2LB, 2026-11-19): AutorisasjonService's feature 001 has no spec.md of its own, and the Specification Explorer loaded a
/// project-level note (an Aspire skill reference under .claude/, a docs/analysis note) as that feature's specification; a choice made for one
/// feature carried over to its siblings, and every feature listed those notes as alternatives "for this feature". A module feature's spec, plan,
/// tasks or data model is now its own; a project-level document is used for it only when the tester opens it for that feature.
/// </summary>
public sealed class MonorepoFeatureDocumentTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    private static string SpecOf(string title) =>
        $"# Feature Specification: {title}\n\n## User Scenarios & Testing\n\n### User Story 1 - {title} (Priority: P1)\n\n**Acceptance Scenarios**:\n\n1. **Given** a user, **When** they act, **Then** it works.\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST support {title}.\n";

    public MonorepoFeatureDocumentTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private void Import(params (string Path, string Content)[] documents)
    {
        var preview = Preview(new string('f', 64), source: false, documents);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
    }

    private Task<ArtifactExplorerState> Spec() => _explorers.GetStateAsync(WorkspaceArtifactType.Specification);

    private void ImportMonorepo() => Import(
        ("auth/specs/001-initial/plan.md", "# Implementation Plan: Initial\n\n## Summary\n\nSet up the service.\n"),
        ("auth/specs/002-sync/spec.md", SpecOf("User sync")),
        ("auth/specs/003-translate/plan.md", "# Implementation Plan: Translate\n\n## Summary\n\nTranslate.\n"),
        ("billing/specs/001-pay/spec.md", SpecOf("Pay")),
        ("docs/analysis/attendance-notes.md", SpecOf("Attendance analysis")));

    [Fact]
    public async Task A_feature_without_its_own_spec_shows_none_and_offers_project_notes_only_as_a_choice()
    {
        ImportMonorepo();
        await _explorers.SelectModuleAsync("auth");
        await _explorers.SelectPackAsync("001-initial");

        var spec = await Spec();
        spec.Status.Should().Be(ExplorerArtifactStatus.Empty, "a project-level note is not this feature's specification");
        spec.Selected.Should().BeNull();
        spec.Artifacts.Select(a => a.SourcePath).Should().Equal(["docs/analysis/attendance-notes.md"], "it is offered, not chosen");

        await _explorers.SelectPackAsync("002-sync");
        var own = await Spec();
        own.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        own.Selected!.SourcePath.Should().Be("auth/specs/002-sync/spec.md");
        own.Artifacts.Select(a => a.SourcePath).Should().Equal(["auth/specs/002-sync/spec.md"], "project notes are not alternatives for a feature with its own spec");
    }

    [Fact]
    public async Task Opening_a_project_note_for_one_feature_does_not_carry_over_to_its_siblings()
    {
        ImportMonorepo();
        await _explorers.SelectModuleAsync("auth");
        await _explorers.SelectPackAsync("001-initial");
        var note = (await Spec()).Artifacts.Single();
        _explorers.Select(WorkspaceArtifactType.Specification, note.Id);

        var chosen = await Spec();
        chosen.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        chosen.Reason.Should().Be(ExplorerSelectionReason.Explicit);
        chosen.Selected!.SourcePath.Should().Be("docs/analysis/attendance-notes.md");

        await _explorers.SelectPackAsync("003-translate");
        (await Spec()).Status.Should().Be(ExplorerArtifactStatus.Empty, "the note was opened for feature 001 only");

        await _explorers.SelectPackAsync("001-initial");
        (await Spec()).Selected!.SourcePath.Should().Be("docs/analysis/attendance-notes.md", "feature 001 keeps its choice");
    }

    [Fact]
    public void Assistant_tooling_notes_are_never_project_artifacts_while_the_spec_kit_constitution_still_is()
    {
        var skill = SampleArtifactClassifier.Classify("Aspire/.claude/skills/aspire-monitoring/references/monitoring.md", SpecOf("Monitoring"));
        skill.Status.Should().Be(ArtifactDiscoveryStatus.Unclassified);
        skill.Role.Should().BeNull();
        skill.Reasons.Should().ContainSingle().Which.Should().Contain("AI assistant configuration (.claude)");

        SampleArtifactClassifier.Classify("docs/analysis/attendance-notes.md", SpecOf("Attendance")).Role.Should().Be(WorkspaceArtifactType.Specification);
        SampleArtifactClassifier.PathKey(".claude/commands/spec.md").Should().NotBe(SampleArtifactClassifier.PathKey("spec.md"), "a cached classification never crosses the tooling boundary");
        SampleArtifactClassifier.Classify(".specify/memory/constitution.md", "# Project Constitution\n\n## Core Principles\n\n### I. Tests first\n\nEvery change MUST have tests.\n\n## Governance\n\nThis constitution supersedes other practices.\n")
            .Role.Should().Be(WorkspaceArtifactType.Constitution);
    }
}
