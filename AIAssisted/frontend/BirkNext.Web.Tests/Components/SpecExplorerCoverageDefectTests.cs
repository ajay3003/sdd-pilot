using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Tests documenting and verifying fixes for identified coverage overlay defects.
/// </summary>
public sealed class SpecExplorerCoverageDefectTests : BunitContext
{
    private const string SpecWithRequirements = @"
# Requirements
- FR-001: User login
- FR-002: User logout
";

    private void SetupJSInterop() => JSInterop.SetupVoid("fileImport.initDropZone", _ => true);

    private ExtractionCandidate MakeCandidate(
        string title,
        ScenarioKind kind = ScenarioKind.Requirement,
        CandidateReviewStatus status = CandidateReviewStatus.Accepted) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
        ReviewStatus = status,
    };

    // ───────────────────────────────────────────────────────────────────────
    // DEFECT: Callout wording mismatch
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DEFECT_CoverageCallout_SaysSection_ButCountsRequirements()
    {
        SetupJSInterop();

        var candidates = new List<ExtractionCandidate>();
        // Empty candidates means no semantic model linkages

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, candidates));

        cut.WaitForAssertion(() => cut.Find("[data-testid='se-coverage-callout']").Should().NotBeNull());

        var callout = cut.Find("[data-testid='se-coverage-callout']");
        var text = callout.TextContent;

        // FIXED: Callout now correctly says "requirement(s)" not "section(s)"
        text.Should().Contain("2 requirement");
        text.Should().Contain("need");
    }

    // ───────────────────────────────────────────────────────────────────────
    // ARCHITECTURAL ISSUE: ApplyCoverageOverlay is disconnected
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ISSUE_ApplyCoverageOverlay_IsDisconnectedFromDisplay()
    {
        SetupJSInterop();

        // Pass ExtractionCandidates to ApplyCoverageOverlay
        var candidates = new List<ExtractionCandidate>
        {
            MakeCandidate("FR-001", ScenarioKind.Requirement, CandidateReviewStatus.Accepted),
            MakeCandidate("FR-002", ScenarioKind.Requirement, CandidateReviewStatus.Rejected),
        };

        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, candidates));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // ISSUE: ApplyCoverageOverlay() calculates coverage from ExtractionCandidates
        // But the UI uses GetSectionHealth() which reads from semantic model
        // The semantic model has NO knowledge of ExtractionCandidates
        // Result: Section filter shows coverage based on semantic analysis, not on provided candidates

        var reqSection = cut.FindAll("[role='treeitem']").FirstOrDefault(r =>
            r.TextContent.Contains("Requirements"));

        // With the candidates we provided:
        // - FR-001 is Accepted (should be Covered)
        // - FR-002 is Rejected (should be Missing)
        // So section should show Partial

        reqSection.Should().NotBeNull();
        var sectionText = reqSection!.TextContent;

        // BUT: This will show "Needs Attention" because semantic model has no linkage
        // (semantic model links based on text matching, not on ExtractionCandidates)
        sectionText.Should().Contain("Needs Attention");

        // The Candidates we passed are completely ignored for coverage display!
    }
}
