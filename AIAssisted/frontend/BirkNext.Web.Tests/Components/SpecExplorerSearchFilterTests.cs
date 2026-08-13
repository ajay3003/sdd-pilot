using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Tests for Specification Explorer search + section-filter interaction.
/// Verifies correctness of combined behaviors and edge cases.
/// </summary>
public sealed class SpecExplorerSearchFilterTests : BunitContext
{
    private const string TestSpec = @"
# Requirements
- FR-001: Login
- FR-002: Logout

## Authentication
- FR-003: Token handling
- AC-001: Token valid
- AC-002: Token expired

# Quality Assurance
- Testing section

## Tests
- TC-001: Login test
";

    private void SetupJSInterop() => JSInterop.SetupVoid("fileImport.initDropZone", _ => true);

    // A. All + empty search
    [Fact]
    public void AllFilter_EmptySearch_ShowsFullTree()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var rows = cut.FindAll("[role='treeitem']");
        rows.Count.Should().BeGreaterThanOrEqualTo(6, "full tree with all sections");
    }

    // B. All + search term
    [Fact]
    public void AllFilter_WithSearch_ShowsMatchingNodes()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // Find search input
        var searchInput = cut.Find("input[aria-label*='Search']");
        searchInput.Input("FR-001");

        // Wait for search results
        cut.WaitForAssertion(() =>
        {
            var matchCount = cut.FindAll(".se-search-count");
            matchCount.Should().NotBeEmpty("search count should be visible");
        });

        var matchBadge = cut.Find(".se-search-count");
        matchBadge.TextContent.Should().Contain("match", "search should find matches");
    }

    // C. Missing Coverage + empty search (no candidates, so no coverage states)
    [Fact]
    public void MissingCoverageFilter_EmptySearch_FiltersHeadingsOnly()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // Without candidates, all nodes should have Unknown coverage
        // So MissingCoverage filter should show no headings
        var filterButton = cut.FindAll("button[class*='se-filter-chip']")
            .FirstOrDefault(b => b.TextContent.Contains("Missing Coverage"));

        filterButton?.Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[role='treeitem']");
            // Without candidates, coverage is Unknown, so MissingCoverage filter should filter out everything
            rows.Should().HaveCount(0, "no nodes have Missing coverage status without test candidates");
        });
    }

    // G. Hidden nodes stay excluded
    [Fact]
    public void HiddenNodes_StayExcludedAfterSearch()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // Find and hide first node
        var hideButtons = cut.FindAll(".se-hide-btn");
        if (hideButtons.Count > 0)
        {
            var initialCount = cut.FindAll("[role='treeitem']").Count;
            hideButtons[0].Click();

            var afterHide = cut.FindAll("[role='treeitem']").Count;
            afterHide.Should().BeLessThan(initialCount, "hiding node should reduce count");

            // Now search - hidden should still be hidden
            var searchInput = cut.Find("input[aria-label*='Search']");
            searchInput.Input("FR");

            cut.WaitForAssertion(() =>
            {
                var afterSearch = cut.FindAll("[role='treeitem']").Count;
                afterSearch.Should().BeLessThan(initialCount, "hidden nodes should remain hidden even after search");
            });
        }
    }

    // I. Clearing search restores filtered tree
    [Fact]
    public void ClearingSearch_RestoresFilteredTree()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var initialCount = cut.FindAll("[role='treeitem']").Count;

        // Search
        var searchInput = cut.Find("input[aria-label*='Search']");
        searchInput.Input("FR-001");

        cut.WaitForAssertion(() =>
        {
            var searchCount = cut.FindAll("[role='treeitem']").Count;
            searchCount.Should().BeLessThan(initialCount, "search should reduce visible nodes");
        });

        // Clear search
        searchInput.Input("");

        cut.WaitForAssertion(() =>
        {
            var clearedCount = cut.FindAll("[role='treeitem']").Count;
            clearedCount.Should().Be(initialCount, "clearing search should restore tree");
        });
    }

    // J. Switching filter preserves/handles search
    [Fact]
    public void SwitchingFilter_PreservesSearchState()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // Apply search
        var searchInput = cut.Find("input[aria-label*='Search']");
        searchInput.Input("FR");

        cut.WaitForAssertion(() => cut.Find(".se-search-count").TextContent.Should().NotBeEmpty());

        var countWithSearch = cut.Find(".se-search-count").TextContent;

        // Switch filter
        var filterButtons = cut.FindAll("button[class*='se-filter-chip']");
        var coveredFilter = filterButtons.FirstOrDefault(b => b.TextContent.Contains("Covered"));
        coveredFilter?.Click();

        // Search input should still have the value
        var searchAfterFilter = cut.Find("input[aria-label*='Search']").GetAttribute("value");
        searchAfterFilter.Should().Be("FR", "search value should be preserved when switching filter");
    }

    [Fact]
    public void ParentVisibility_MaintainedWithSearch()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, TestSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        // Search for deeply nested item
        var searchInput = cut.Find("input[aria-label*='Search']");
        searchInput.Input("AC-001");

        cut.WaitForAssertion(() => cut.Find(".se-search-count").TextContent.Should().Contain("match"));

        // Get visible rows after search
        var rows = cut.FindAll("[role='treeitem']");
        var allText = string.Join(" ", rows.Select(r => r.TextContent));

        // Verify the match is found and ancestors are visible
        allText.Should().Contain("AC-001", "the matching item should be visible");
        // Parent visibility: ancestors that contain matching descendants should be visible
        // This verifies the hierarchical context is maintained
        rows.Count.Should().BeGreaterThan(1, "search should include ancestors to maintain hierarchy");
    }
}
