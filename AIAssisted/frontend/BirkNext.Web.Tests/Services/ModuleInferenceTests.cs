using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Canonical module identity: only strong Spec-Kit structure (<c>.specify/</c>, or <c>specs/&lt;feature&gt;/</c> with a Spec-Kit document) creates
/// a module; a documentation tree's own <c>specs/</c> folder never does, whatever it is called. Paths mirror the M2LB archive layout.
/// </summary>
public sealed class ModuleInferenceTests
{
    private static IReadOnlyList<string> Modules(params string[] paths)
    {
        var roots = ArtifactModules.RootsOf(paths);
        return [.. paths.Select(p => ArtifactModules.ModuleOf(p, roots)).Distinct().Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void M2lb_documentation_trees_never_become_modules()
    {
        string[] paths =
        [
            "Frontend/specs/001-initial-dashboard/spec.md",
            "Frontend/specs/002-child-search-profile/spec.md",
            "Frontend/docs/superpowers/specs/2026-04-30-settings-devmode-design.md",
            "Frontend/docs/superpowers/specs/2026-05-12-admin-services-graphql-refactor-design.md",
            "docs/superpowers/specs/2026-06-16-architecture-overview-skill-design.md",
            "docs/superpowers/specs/2026-09-03-version-reporting-design.md",
        ];
        var roots = ArtifactModules.RootsOf(paths);

        roots.Should().BeEquivalentTo(["Frontend"]);
        ArtifactModules.ModuleOf("Frontend/docs/superpowers/specs/2026-04-30-settings-devmode-design.md", roots).Should().Be("Frontend", "a module's own docs stay with the module");
        ArtifactModules.ModuleOf("docs/superpowers/specs/2026-06-16-architecture-overview-skill-design.md", roots).Should().Be(ArtifactModules.ProjectLevel, "repository docs are project-wide");
        Modules(paths).Should().Equal(ArtifactModules.ProjectLevel, "Frontend");
        ArtifactModules.FeatureOf("Frontend/docs/superpowers/specs/2026-04-30-settings-devmode-design.md", roots).Should().BeNull("a documentation tree is not an Artifact Pack");
        ArtifactModules.FeatureOf("Frontend/specs/001-initial-dashboard/spec.md", roots).Should().Be("001-initial-dashboard");
    }

    [Fact]
    public void The_rule_is_structural_not_a_folder_name_list()
    {
        // Same shape, any names: still no pseudo-module.
        Modules("Api/specs/001-x/plan.md", "Api/handbook/notes/specs/design-notes.md", "guides/specs/overview.md")
            .Should().Equal(ArtifactModules.ProjectLevel, "Api");
    }

    [Fact]
    public void Several_modules_keep_their_identity_and_support_docs_stay_with_them()
    {
        string[] paths =
        [
            "Autorisasjon/specs/001-autorisasjon-initial/spec.md",
            "Person/specs/001-person-module/tasks.md",
            "Person/docs/superpowers/specs/2026-05-27-person-profile-card-decoupling-design.md",
            "docs/superpowers/specs/2026-09-03-version-reporting-design.md",
        ];
        var roots = ArtifactModules.RootsOf(paths);
        roots.Should().BeEquivalentTo(["Autorisasjon", "Person"]);
        ArtifactModules.ModuleOf(paths[2], roots).Should().Be("Person");
        ArtifactModules.ModuleOf(paths[3], roots).Should().Be(ArtifactModules.ProjectLevel);
    }

    [Fact]
    public void A_nested_application_with_its_own_spec_kit_structure_is_a_real_module()
    {
        string[] paths = ["Parent/specs/001-a/spec.md", "Parent/SubService/.specify/memory/constitution.md", "Parent/SubService/specs/001-b/spec.md"];
        var roots = ArtifactModules.RootsOf(paths);
        roots.Should().BeEquivalentTo(["Parent", "Parent/SubService"]);
        ArtifactModules.ModuleOf("Parent/SubService/specs/001-b/spec.md", roots).Should().Be("Parent/SubService");
    }

    [Fact]
    public void Docs_only_packages_still_establish_modules_from_canonical_structure()
    {
        Modules("Foo/specs/001-x/spec.md").Should().Equal("Foo");
        Modules("Foo/docs/design/specs/2026-x-design.md").Should().Equal([ArtifactModules.ProjectLevel], "no Foo/docs/design module without Spec-Kit evidence");
        Modules("docs/specs/x.md").Should().Equal([ArtifactModules.ProjectLevel], "root docs are project-wide, never module docs");
    }

    [Fact]
    public void Explain_says_why_a_document_belongs_where_it_does()
    {
        var roots = ArtifactModules.RootsOf(["Frontend/specs/001-x/spec.md"]);
        ArtifactModules.Explain("Frontend/specs/001-x/spec.md", roots).Should().Be("Frontend · Spec-Kit module root");
        ArtifactModules.Explain("Frontend/docs/superpowers/specs/a-design.md", roots).Should().Be("Frontend · supporting document under the module");
        ArtifactModules.Explain("docs/superpowers/specs/b-design.md", roots).Should().StartWith("Project-level · supporting document");
    }

    private const string SpecLikeDesign = "# Settings dev mode design\n\n## Functional Requirements\n\n- **FR-001**: The system MUST show dev mode.\n- **FR-002**: The system MUST hide it in production.\n- **FR-003**: Admins MUST toggle it.\n\n## User Scenarios\n\n### User Story 1 - Toggle (Priority: P1)\n";

    [Fact]
    public void A_loose_design_document_in_specs_is_supporting_analysis_never_a_canonical_specification()
    {
        var loose = SampleArtifactClassifier.Classify("Frontend/docs/superpowers/specs/2026-04-30-settings-devmode-design.md", SpecLikeDesign);
        loose.Role.Should().Be(WorkspaceArtifactType.Research);
        loose.Reasons.Single().Should().Contain("Supporting design document");

        SampleArtifactClassifier.Classify("Frontend/specs/001-x/spec.md", SpecLikeDesign).Role.Should().Be(WorkspaceArtifactType.Specification, "the canonical Spec-Kit file keeps its role");
        SampleArtifactClassifier.Classify("Frontend/specs/001-x/settings.md", SpecLikeDesign).Role.Should().Be(WorkspaceArtifactType.Specification, "inside a feature folder the content still decides");
        SampleArtifactClassifier.Classify("docs/design/specs/x.md", "---\ntype: specification\n---\n" + SpecLikeDesign).Role.Should().Be(WorkspaceArtifactType.Specification, "declared front matter wins");
        const string PlanLike = "# Implementation Plan: Settings dev mode\n\n## Technical Context\n\nx\n\n## Project Structure\n\ny\n\n## Implementation Phases\n\nz\n";
        SampleArtifactClassifier.Classify("Frontend/docs/superpowers/plans/2026-04-30-settings-devmode.md", PlanLike).Role.Should().Be(WorkspaceArtifactType.Research, "a documentation tree's plans/ are supporting notes");
        SampleArtifactClassifier.Classify("Frontend/specs/001-x/plan.md", PlanLike).Role.Should().Be(WorkspaceArtifactType.Plan);
        SampleArtifactClassifier.Classify("specs/school.md", SpecLikeDesign).Role.Should().Be(WorkspaceArtifactType.Specification, "a project's own specs/ folder keeps content-based roles");
        SampleArtifactClassifier.Classify("docs/superpowers/specs/2026-09-03-version-reporting-design.md", SpecLikeDesign).Role.Should().Be(WorkspaceArtifactType.Research);
        SampleArtifactClassifier.Classify("Person/docs/superpowers/specs/2026-04-30-dockerfile-design.md", "# Dockerfile design\n\nShort notes.\n").Role
            .Should().Be(WorkspaceArtifactType.Research, "a weak design note in a documentation tree is retained as supporting evidence, not dropped");
        SampleArtifactClassifier.Classify("notes/misc.md", "# Notes\n\nShort notes.\n").Status.Should().Be(ArtifactDiscoveryStatus.Unclassified, "outside such a tree nothing changes");
    }

    [Fact]
    public void Documentation_drift_uses_the_same_resolver()
    {
        DriftDocument Authored(string path) => new(path, DriftOrigin.Authored, path, path, path, "Specification", "fp", "imp-1", "M2LB · Import 1", "M2LB.zip", null, 1, null,
            null, null, null, true, true, "import:imp-1");
        var documents = new[] { Authored("Frontend/specs/001-x/spec.md"), Authored("Frontend/docs/superpowers/specs/a-design.md"), Authored("docs/superpowers/specs/b-design.md") };
        var roots = DocumentationDrift.RootsOf(documents);

        DocumentationDrift.ModuleOf(documents[1], roots).Should().Be("Frontend");
        DocumentationDrift.ModuleOf(documents[2], roots).Should().Be(ArtifactModules.ProjectLevelLabel);
        DocumentationDrift.ModuleOf(documents[2]).Should().Be(ArtifactModules.ProjectLevelLabel, "no pseudo-module even without the catalog");
    }
}
