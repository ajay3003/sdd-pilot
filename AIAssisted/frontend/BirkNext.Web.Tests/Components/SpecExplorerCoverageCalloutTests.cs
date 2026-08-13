using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Tests for coverage overlay callout correctness.
/// Verifies that the callout displays correct terminology and counts.
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
    public void CoverageCallout_WithMissingRequirements_DisplaysCorrectCount()
    {
        SetupJSInterop();

        // No candidates provided - semantic model will find 3 requirements with no acceptance scenarios
        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, []));

        cut.WaitForAssertion(() => cut.Find("[data-testid='se-coverage-callout']").Should().NotBeNull());

        var callout = cut.Find("[data-testid='se-coverage-callout']");
        var text = callout.TextContent;

        // Should show "3 requirements need coverage attention" (not "3 sections")
        text.Should().Contain("3 requirement");
        text.Should().Contain("need");
    }

    [Fact]
    public void CoverageCallout_WithSingleMissingRequirement_DisplaysSingularForm()
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

        cut.WaitForAssertion(() => cut.Find("[data-testid='se-coverage-callout']").Should().NotBeNull());

        var callout = cut.Find("[data-testid='se-coverage-callout']");
        var text = callout.TextContent;

        // Singular form: "1 requirement needs coverage attention"
        text.Should().Contain("1 requirement");
        text.Should().Contain("needs");
    }

    [Fact]
    public void CoverageCallout_WithNoCandidates_ShowsAllRequirementsNeedAttention()
    {
        SetupJSInterop();

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, []));

        cut.WaitForAssertion(() => cut.Find("[data-testid='se-coverage-callout']").Should().NotBeNull());

        // Should show callout since all requirements lack acceptance scenario links
        var callout = cut.FindAll("[data-testid='se-coverage-callout']");
        callout.Should().HaveCount(1);
    }
}
