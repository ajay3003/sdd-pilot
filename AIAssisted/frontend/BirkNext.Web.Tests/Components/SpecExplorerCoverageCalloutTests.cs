using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Parsed document relationships are not workspace coverage evidence.
/// </summary>
public sealed class SpecExplorerCoverageCalloutTests : BunitContext
{
    private const string SpecWithRequirements = @"
# Requirements
- FR-001: User login
- FR-002: User logout
- FR-003: Session management
";

    private void SetupJSInterop() => JSInterop.SetupVoid("fileImport.initDropZone", _ => true);

    private ExtractionCandidate MakeCandidate(
        string title,
        ScenarioKind kind = ScenarioKind.Requirement) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    [Fact]
    public void ParsedRequirementsDoNotProduceCoverageCallout()
    {
        SetupJSInterop();

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, []));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());
        cut.FindAll("[data-testid='se-coverage-callout'], [data-testid='se-section-health'], [data-testid='se-quick-filters']").Should().BeEmpty();
        cut.Markup.Should().Contain("Requirements (3)");
    }

    [Fact]
    public void SingleParsedRequirementDoesNotImplyMissingCoverage()
    {
        SetupJSInterop();

        // Spec with just one requirement
        var spec = @"
# Requirements
- FR-001: Single requirement
";

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, spec)
            .Add(c => c.Candidates, []));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());
        cut.FindAll("[data-testid='se-coverage-callout']").Should().BeEmpty();
        cut.Markup.Should().Contain("Requirements (1)");
    }

    [Fact]
    public void NoReviewCandidatesDoesNotCreateCoverageVerdict()
    {
        SetupJSInterop();

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, []));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());
        cut.FindAll("[data-testid='se-coverage-callout'], [data-testid='se-section-health']").Should().BeEmpty();
    }

    [Fact]
    public void SpecificationExplorerScenarioDetails_UseSharedExpandableRenderer()
    {
        SetupJSInterop();
        const string scenario = """
            # Feature Specification
            ## User Story 1: Person creation
            **Acceptance Scenarios**:

            1. **Given** a person record is created, **When** the CDC event is
               processed, **Then** the complete identity payload is mapped through the adapter pipeline.
            """;

        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, scenario));
        cut.FindAll(".se-ctrl-btn").Single(button => button.TextContent.Contains("Expand All")).Click();
        var row = cut.FindAll("[role=treeitem]").Single(item => item.TextContent.Contains("person record is created"));
        row.Click();

        var rendered = cut.Find(".traceability-item__text");
        rendered.TextContent.Should().Contain("complete identity payload is mapped through the adapter pipeline");
        rendered.QuerySelector(".traceability-item__given").Should().NotBeNull();
        rendered.QuerySelector(".traceability-item__when").Should().NotBeNull();
        rendered.QuerySelector(".traceability-item__then").Should().NotBeNull();
    }
}
