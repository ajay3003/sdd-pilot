using BirkNext.Web.Components.Explorers;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Specification Explorer for testers: the feature first (title · module · feature number · primary document), provenance only under
/// Details, supporting documents counted separately from the primary specification, plain status and feature summary, and relationship
/// metrics explained together rather than as a contradiction.
/// </summary>
public class SpecificationExplorerTesterViewTests : BunitContext
{
    private const string Spec = """
        # Feature Specification: Missing Parent Failure Code

        **Feature Branch**: `002-barn-parent-feilkode`
        **Created**: 2026-05-02
        **Status**: Draft
        **Input**: User description: "Return a machine-readable failure code when a child's parent person is missing."

        ## User Scenarios & Testing
        """;

    [Fact]
    public void Header_ShowsStatusAndFeatureSummary_AndKeepsBranchAndDateAsTechnicalDetail()
    {
        var header = SpecificationHeader.Parse(Spec);
        header.Status.Should().Be("Draft");
        header.Summary.Should().Be("Return a machine-readable failure code when a child's parent person is missing.");
        header.FeatureBranch.Should().Be("002-barn-parent-feilkode");
        header.Created.Should().Be("2026-05-02");
        SpecificationHeader.Parse("# Spec\n\n## Section\n**Status**: Later").IsEmpty.Should().BeTrue("only the header before the first section counts");
    }

    [Theory]
    [InlineData(0, 45, 45, "All 45 tests are organized under user stories, but none is linked directly to a requirement ID. Direct requirement links are not established.")]
    [InlineData(10, 45, 45, "10 of 45 tests name a requirement ID directly; all 45 are organized under user stories.")]
    public void RelationshipMetrics_AreExplainedTogether_Neutrally(int linked, int underStory, int tests, string expected) =>
        SpecificationRelationshipText.Explain(linked, underStory, tests).Should().Be(expected);

    [Fact]
    public void RelationshipMetrics_NeedNoExplanation_WhenEveryTestNamesARequirement() =>
        SpecificationRelationshipText.Explain(45, 45, 45).Should().BeNull();

    [Fact]
    public void HeaderLines_AreNotStructureRows_TheyBelongToTheFeatureHeader()
    {
        var cut = Render<BirkNext.Web.Components.SpecExplorerPanel>(p => p.Add(x => x.InitialSpecMarkdown,
            Spec + "\n### User Story 1 - Parent missing (Priority: P1)\n\n**FR-001**: System MUST return a failure code.\n"));
        cut.FindAll(".se-row").Select(r => r.TextContent).Should().NotContain(t => t.Contains("Feature Branch") || t.Contains("Created:") || t.Contains("Status:"));
        cut.FindAll(".se-row").Should().NotBeEmpty();
    }

    [Fact]
    public void PackTitle_ComesFromTheSpecKitSpec_NotASupportingQuickstart()
    {
        ExplorerArtifact Doc(string path, string title) => new($"sample:{path}", WorkspaceArtifactType.Specification, title, path, path,
            ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown", ExplorerArtifactCurrentness.Current, null, Feature: "002-barn");
        var packs = BirkNext.Web.Services.Explorers.ArtifactPacks.PacksOf(
            [Doc("specs/002-barn/quickstart.md", "Quickstart: Validating the Missing-Parent Failure Code"), Doc("specs/002-barn/spec.md", "Feature Specification: Missing Parent Failure Code")], "");
        packs.Single().DisplayName.Should().Be("Missing Parent Failure Code");
    }

    private IRenderedComponent<ArtifactExplorerHost> RenderHost(params SampleFileDto[] files)
    {
        var handler = new DashboardPageTests.SampleProjectsHttpHandler();
        handler.SetProjects(new SampleProjectDto("person", "Person", "PERSON", "", "C:/SampleData/person", true, files));
        DashboardPageTests.AddCurrentWorkspace(Services, new WorkspaceArtifactRepository { CurrentProject = "person" }, handler);
        return Render<ArtifactExplorerHost>(p => p.Add(x => x.Role, WorkspaceArtifactType.Specification));
    }

    private static SampleFileDto File(string path) => new(path, true, "Specification", "", "", true, false);

    [Fact]
    public void PrimarySpecification_IsDistinguishedFromSupportingDocuments_AndProvenanceIsUnderDetails()
    {
        var cut = RenderHost(File("specs/002-barn/spec.md"), File("specs/002-barn/specification.md"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer]").GetAttribute("data-status").Should().Be("Loaded"));
        cut.FindAll("[data-testid=artifact-explorer-choose]").Should().BeEmpty("a supporting document never competes with the Spec-Kit spec.md");
        cut.Find("[data-testid=artifact-explorer-primary]").TextContent.Should().Be("Primary specification");
        cut.Find("[data-testid=artifact-explorer-supporting]").TextContent.Should().Be("1 supporting specification document");
        cut.Find("[data-testid=artifact-explorer-file]").TextContent.Should().EndWith("/spec.md", "the Spec-Kit spec.md is the primary specification");

        var details = cut.Find("[data-testid=artifact-explorer-details]");
        details.HasAttribute("open").Should().BeFalse("file, path, source and revision are technical details");
        details.QuerySelector("[data-testid=artifact-explorer-file]").Should().NotBeNull();
        cut.Find("[data-testid=artifact-explorer-summary]").TextContent.Should().NotContain("spec.md").And.NotContain("specs/");
        cut.Find(".axh-context-main").TextContent.Should().NotContain("This pack's", "the selection mechanics are technical detail").And.Contain("Primary specification");
    }

    [Fact]
    public void ManualImport_IsASecondaryAction_UnderDetails()
    {
        var cut = RenderHost(File("specs/001-person/spec.md"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer]").GetAttribute("data-status").Should().Be("Loaded"));
        cut.Find("[data-testid=artifact-explorer-details] [data-testid=artifact-explorer-import-toggle]").TextContent.Trim()
            .Should().Be("Add a specification manually");
    }
}
