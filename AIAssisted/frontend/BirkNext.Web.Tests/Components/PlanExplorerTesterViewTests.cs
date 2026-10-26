using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Plan Explorer for testers: the source specification resolves within the viewed Artifact Pack (module-relative paths, leading slashes, file
/// names) and never to a sibling pack; distinct listed constraints stay distinct; complexity derived from goals is labelled and not inflated;
/// testing intent, planned phases and "Plan states" vs "BirkNext verification" read plainly; parser noise stays out of the normal view.
/// </summary>
public class PlanExplorerTesterViewTests : BunitContext
{
    public PlanExplorerTesterViewTests() => Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();

    private static ExplorerArtifact Spec(string path, string? pack) => new($"workspace:{path}", WorkspaceArtifactType.Specification, path, path, path,
        ExplorerArtifactSource.Workspace, "File", 1, 1, "Unknown", ExplorerArtifactCurrentness.Current, null, Module: "Person", Feature: pack);

    /// <summary>The Specification state the Plan Explorer receives for pack 001: its own spec plus a shared one — never pack 002's.</summary>
    private static ArtifactExplorerState Pack001(params ExplorerArtifact[] extra)
    {
        var own = Spec("Person/specs/001-person-module/spec.md", "001-person-module");
        return new ArtifactExplorerState(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Loaded, "import:x", "M2LB", [own, .. extra], own,
            ExplorerSelectionReason.PackArtifact, "# spec", Module: "Person", Pack: "001-person-module");
    }

    [Theory]
    [InlineData("specs/001-person-module/spec.md", null)]
    [InlineData("/specs/001-person-module/spec.md", null)]
    [InlineData("Person/specs/001-person-module/spec.md", null)]
    [InlineData(null, "`specs\\001-person-module\\spec.md`")]
    [InlineData("spec.md", null)]
    public void SamePackSpecification_IsLinked_ForEveryWayThePlanNamesIt(string? specLink, string? input)
    {
        var plan = new PlanDocument { SpecLink = specLink, InputSource = input is null ? null : $"Feature specification from {input}" };
        var resolution = PlanSpecificationReferenceResolver.Resolve(plan, Pack001());
        resolution.Status.Should().Be(PlanSpecificationReferenceStatus.Resolved);
        resolution.Artifact!.SourcePath.Should().Be("Person/specs/001-person-module/spec.md");
        PlanTesterPresentation.SourceSpecification(resolution).State.Should().Be("Linked");
    }

    [Fact]
    public void SiblingPackPath_IsNeverLinked_EvenWithTheSameFileName()
    {
        // The state for pack 001 never lists pack 002's documents, so a path naming pack 002 cannot resolve.
        var plan = new PlanDocument { SpecLink = "specs/002-barn-parent-feilkode/spec.md" };
        var resolution = PlanSpecificationReferenceResolver.Resolve(plan, Pack001());
        resolution.Status.Should().Be(PlanSpecificationReferenceStatus.Missing);
        resolution.Detail.Should().Be("Referenced specification not found in this feature.");
        PlanTesterPresentation.SourceSpecification(resolution).State.Should().Be("Not found");
    }

    [Fact]
    public void TwoSpecificationsEndingWithTheNamedPath_NeedReview()
    {
        var plan = new PlanDocument { SpecLink = "spec.md" };
        var resolution = PlanSpecificationReferenceResolver.Resolve(plan, Pack001(Spec("Person/spec.md", null)));
        resolution.Status.Should().Be(PlanSpecificationReferenceStatus.Ambiguous);
        PlanTesterPresentation.SourceSpecification(resolution).Should().Be(("Needs review", "Specification reference requires review."));
    }

    private const string PlanWithBullets = """
        # Implementation Plan: Person Module Core

        ## Technical Context

        **Constraints**:
        - All data in Norway East
        - Fail-closed auth; no anonymous access
        - No hard deletes
        - No personal data in logs
        **Performance Goals**: GraphQL search p95 < 2s
        **Scale/Scope**: thousands of children per org unit

        ## Testing

        - Integration tests run against a real SQL Server in TestContainers
        - Access control is verified for every GraphQL query
        """;

    [Fact]
    public void ListedConstraints_StayDistinct_AndOneSentenceIsNotSplitFurther()
    {
        var plan = new PlanAnalysisService().Parse(PlanWithBullets);
        var constraints = plan.Constraints.Where(c => c.ConstraintType == ConstraintType.Constraint).Select(c => c.Title).ToList();
        constraints.Should().HaveCount(4, "one structured item per bullet");
        constraints.Should().Contain(c => c.Contains("Fail-closed auth; no anonymous access"), "a semicolon inside one bullet is the author's grouping");
        constraints.Should().Contain(c => c.Contains("No hard deletes"));
    }

    [Fact]
    public void HorizontalRuleAfterAField_IsNotPartOfTheConstraint()
    {
        var plan = new PlanAnalysisService().Parse("# Plan\n\n## Technical Context\n\n**Scale/Scope**: thousands per org unit; p95 < 2s search\n\n---\n\n## Testing\n\n- Unit tests\n");
        plan.Constraints.Select(c => c.Title).Should().NotContain(t => t.Contains("---"));
        plan.Constraints.Should().Contain(c => c.Title == "p95 < 2s search");
    }

    [Fact]
    public void GoalDerivedComplexity_IsLabelledDerived_AndNotInflatedByWording()
    {
        var plan = new PlanAnalysisService().Parse(PlanWithBullets);
        plan.ComplexityDerived.Should().BeTrue();
        var fromGoals = plan.ComplexityItems.Where(i => PlanTesterPresentation.ComplexityDimension(i) == "Scale / performance target").ToList();
        fromGoals.Should().NotBeEmpty().And.OnlyContain(i => i.Level == ComplexityLevel.Medium, "p95 and 'thousands' are both targets to verify, not difficulty");

        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.InitialView, "complexity"));
        cut.Find("[data-testid=pe-complexity-derived]").TextContent.Should().Contain("BirkNext-derived complexity signals — not authored Plan statements");
        cut.FindAll(".pe-complexity-badge").Should().OnlyContain(b => b.TextContent.StartsWith("Derived · "));
    }

    [Fact]
    public void Overview_LeadsWithTestingImpact_DeclaredInPlan_AndKeepsMetadataUnderTechnicalDetails()
    {
        var plan = new PlanAnalysisService().Parse(PlanWithBullets);
        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan));

        var items = cut.FindAll("[data-testid=pe-impact-item]").Select(i => i.TextContent).ToList();
        items.Should().Contain(i => i.Contains("Performance target") && i.Contains("p95 < 2s"));
        items.Should().Contain(i => i.Contains("Testing approach") && i.Contains("Access control is verified"));
        items.Should().OnlyContain(i => i.Contains("Declared in Plan"));
        cut.Find("[data-testid=pe-metadata]").Closest("details")!.HasAttribute("open").Should().BeFalse();
        cut.Markup.Should().NotContain("Lines ").And.NotContain("TableRow").And.NotContain("BlockQuote");
    }

    [Fact]
    public void TestingTab_ShowsTheAuthoredApproachFirst_FrameworksAsTechnicalDetail()
    {
        var plan = new PlanAnalysisService().Parse(PlanWithBullets + "\n**Frameworks**: xUnit, bUnit\n");
        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.InitialView, "testing"));
        var body = cut.Find(".pe-testing").InnerHtml;
        body.IndexOf("Access control is verified", StringComparison.Ordinal).Should().BeLessThan(body.IndexOf("pe-testing-technical", StringComparison.Ordinal));
        cut.Find("[data-testid=pe-testing-note]").TextContent.Should().NotContain("passed").And.Contain("Actual test results");
    }

    [Fact]
    public void EmptyArchitecture_SaysSoPlainly_WithAuthoringFormatOnlyAsTechnicalDetail()
    {
        var plan = new PlanAnalysisService().Parse("# Plan\n\n## Summary\n\nSmall change.\n");
        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.InitialView, "architecture"));
        var empty = cut.Find(".pe-empty-tab");
        empty.TextContent.Should().StartWith("No architecture decisions were identified in the primary Plan.");
        empty.QuerySelector("details")!.HasAttribute("open").Should().BeFalse("authoring instructions are not the primary message");
    }

    [Fact]
    public void Constitution_SeparatesPlanStatementsFromBirkNextVerification()
    {
        var plan = new PlanAnalysisService().Parse("# Plan\n\n## Constitution Check\n\n| Gate | Rule | Status | Notes |\n|---|---|---|---|\n| G1 | PP-01 | PASS | ok |\n");
        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.InitialView, "constitution"));
        cut.Find(".pe-gate-status").TextContent.Trim().Should().Be("Plan states: Met");
        cut.Find(".pe-gate-verification").TextContent.Should().Be("Not assessed");
    }

    [Fact]
    public void Tabs_PutTesterSectionsFirst()
    {
        var plan = new PlanAnalysisService().Parse(PlanWithBullets);
        var cut = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan));
        cut.FindAll("[data-testid=pe-tab]").Select(t => t.GetAttribute("data-tab")).Should()
            .Equal("overview", "risks", "testing", "phases", "constitution", "complexity", "architecture", "tech-context", "structure");
    }
}
