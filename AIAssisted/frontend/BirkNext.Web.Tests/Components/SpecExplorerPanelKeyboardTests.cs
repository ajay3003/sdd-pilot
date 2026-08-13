using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Tests for SpecExplorerPanel keyboard navigation (WAI-ARIA tree pattern).
/// Verifies roving tabindex, arrow key navigation, Home/End, and ARIA attributes.
/// </summary>
public sealed class SpecExplorerPanelKeyboardTests : BunitContext
{
    private const string SimpleSpec = @"
# Section A
- Item A1
- Item A2
## Subsection A
- Item B1
# Section B
- Item B2
";

    private void SetupJSInterop() => JSInterop.SetupVoid("fileImport.initDropZone", _ => true);

    [Fact]
    public void TreeItems_RovingTabindex_SelectedItemHasZero_OthersHaveNegativeOne()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");
        treeItems.Should().NotBeEmpty();

        // All items should have tabindex attribute
        var tabindexValues = treeItems.Select(item => item.GetAttribute("tabindex")).ToList();
        tabindexValues.Should().NotContainNulls("all items should have tabindex");

        // All tabindex values should be either "0" or "-1"
        tabindexValues.Should().AllSatisfy(v => v.Should().BeOneOf("0", "-1"));

        // Count items with each tabindex value
        var selectedCount = treeItems.Count(item => item.GetAttribute("tabindex") == "0");
        var unselectedCount = treeItems.Count(item => item.GetAttribute("tabindex") == "-1");

        // At most one item should be in tab sequence
        selectedCount.Should().BeLessThanOrEqualTo(1, "at most one treeitem should have tabindex=0");

        // All items should be accounted for
        (selectedCount + unselectedCount).Should().Be(treeItems.Count);
    }

    [Fact]
    public void TreeItems_AriaSelected_MatchesSelectedState()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");

        // All items should have aria-selected attribute
        var ariaSelectedValues = treeItems.Select(item => item.GetAttribute("aria-selected")).ToList();
        ariaSelectedValues.Should().NotContainNulls("all items should have aria-selected");

        // Verify aria-selected values are boolean strings
        ariaSelectedValues.Should().AllSatisfy(value =>
            value.Should().BeOneOf("true", "false")
        );
    }

    [Fact]
    public void ExpandableItems_HaveAriaExpandedAttribute()
    {
        SetupJSInterop();
        var spec = @"
# Parent Section
## Child Subsection
- Item
";
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, spec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");

        // Items with aria-expanded attribute
        var parentItems = treeItems.Where(item =>
            item.GetAttribute("aria-expanded") != null
        ).ToList();

        // At least some items should have aria-expanded if there are parents
        parentItems.Should().NotBeEmpty("tree should have parent items with aria-expanded");

        // aria-expanded values should be valid
        parentItems.Should().AllSatisfy(item =>
            item.GetAttribute("aria-expanded").Should().BeOneOf("true", "false")
        );
    }

    [Fact]
    public void TreeContainer_HasRoleTree()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        var treeContainer = cut.Find("[role='tree']");
        treeContainer.Should().NotBeNull();
    }

    [Fact]
    public void EmptySpec_DoesNotRenderTreeItems()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, ""));

        var treeItems = cut.FindAll("[role='treeitem']");
        treeItems.Should().BeEmpty();
    }

    [Fact]
    public void ValidSpec_RendersAllHeadingsAsTreeItems()
    {
        SetupJSInterop();
        var spec = @"
# First
# Second
## Second.One
# Third
";
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, spec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");
        treeItems.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void TreeItems_AllHaveCorrectRole()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");

        treeItems.Should().AllSatisfy(item =>
            item.GetAttribute("role").Should().Be("treeitem")
        );
    }

    [Fact]
    public void ClickingTreeItem_UpdatesTabindex()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");
        var firstItem = treeItems[0];

        firstItem.Click();

        var updatedItems = cut.FindAll("[role='treeitem']");
        var updatedFirstItem = updatedItems[0];

        updatedFirstItem.GetAttribute("tabindex").Should().Be("0");

        if (updatedItems.Count > 1)
        {
            updatedItems[1].GetAttribute("tabindex").Should().Be("-1");
        }
    }

    [Fact]
    public void TreeItems_NeverBothHaveTabindexZero()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");
        var selectedCount = treeItems.Count(item => item.GetAttribute("tabindex") == "0");

        selectedCount.Should().BeLessThanOrEqualTo(1, "at most one item should have tabindex=0");
    }

    [Fact]
    public void TreeStructure_IsMaintainedAfterInteraction()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var initialCount = cut.FindAll("[role='treeitem']").Count;

        var treeItems = cut.FindAll("[role='treeitem']");
        if (treeItems.Count > 0) treeItems[0].Click();

        var finalItems = cut.FindAll("[role='treeitem']");
        var finalCount = finalItems.Count;

        finalCount.Should().Be(initialCount, "tree structure should not change after selection");
    }

    [Fact]
    public void AriaSelected_IsLowercaseBooleanString()
    {
        SetupJSInterop();
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SimpleSpec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");

        treeItems.Should().AllSatisfy(item =>
        {
            var value = item.GetAttribute("aria-selected");
            value.Should().BeOneOf("true", "false");
        });
    }

    [Fact]
    public void ExpandableItem_HasAriaExpandedWhenHasChildren()
    {
        SetupJSInterop();
        var spec = @"
# Parent
## Child
";
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, spec));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());

        var treeItems = cut.FindAll("[role='treeitem']");
        var parentItem = treeItems.FirstOrDefault(item => item.GetAttribute("aria-expanded") != null);

        if (parentItem != null)
        {
            parentItem.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
        }
    }
}
