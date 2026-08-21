using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BirkNext.Web.Tests.Pages;

public sealed class QaFindingPreviewListTests : BunitContext
{
    private sealed class TestHost : ComponentBase
    {
        [Parameter]
        public List<QaFinding> Items { get; set; } = [];

        [Parameter]
        public int PreviewLimit { get; set; } = 5;

        [Parameter]
        public bool ShowAll { get; set; }

        [Parameter]
        public string ContextLabel { get; set; } = "findings";

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<QaFindingPreviewList>(0);
            builder.AddAttribute(1, "Items", Items);
            builder.AddAttribute(2, "PreviewLimit", PreviewLimit);
            builder.AddAttribute(3, "ShowAll", ShowAll);
            builder.AddAttribute(4, "OnShowAllChanged", EventCallback.Factory.Create<bool>(this, val => ShowAll = val));
            builder.AddAttribute(5, "ContextLabel", ContextLabel);
            builder.CloseComponent();
        }
    }

    [Fact]
    public void QaFindingPreviewList_WithEightItems_ShowsFiveThenAllThenLess()
    {
        var items = Enumerable.Range(1, 8)
            .Select(i => new QaFinding
            {
                RuleCode = $"QA-FINDING-{i:D3}",
                Title = $"Title {i}",
                Description = $"Description {i}",
                Severity = i % 2 == 0 ? QaSeverity.High : QaSeverity.Medium,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            })
            .ToList();

        var host = Render<TestHost>(p => p.Add(h => h.Items, items).Add(h => h.ContextLabel, "constitution findings"));

        // Initial: 5 visible, 3 hidden
        host.Markup.Should().Contain("QA-FINDING-001");
        host.Markup.Should().Contain("QA-FINDING-005");
        host.Markup.Should().NotContain("QA-FINDING-006");
        host.Markup.Should().NotContain("QA-FINDING-008");

        // Find Show all button
        var showAllButton = host.Find("button.qr-show-toggle");
        showAllButton.Should().NotBeNull();
        showAllButton.TextContent.Trim().Should().Be("Show all 8 constitution findings");
        showAllButton.GetAttribute("aria-label").Should().Be("Show all 8 constitution findings");
        showAllButton.GetAttribute("aria-expanded").Should().Be("false");
        showAllButton.GetAttribute("aria-controls").Should().Be("qa-finding-list");
        host.Find("#qa-finding-list").Should().NotBeNull();

        // Click Show all
        showAllButton.Click();

        // After toggle: all 8 visible
        host.WaitForAssertion(() =>
        {
            host.Markup.Should().Contain("QA-FINDING-006");
            host.Markup.Should().Contain("QA-FINDING-008");
        });

        // Find Show less button
        var showLessButton = host.Find("button.qr-show-toggle");
        showLessButton.TextContent.Trim().Should().Be("Show fewer constitution findings");
        showLessButton.GetAttribute("aria-label").Should().Be("Show fewer constitution findings");
        showLessButton.GetAttribute("aria-expanded").Should().Be("true");
        showLessButton.GetAttribute("aria-controls").Should().Be("qa-finding-list");

        // Click Show less
        showLessButton.Click();

        // Back to preview: 5 visible, 3 hidden
        host.WaitForAssertion(() =>
        {
            host.Markup.Should().NotContain("QA-FINDING-006");
            host.Markup.Should().NotContain("QA-FINDING-008");
        });
    }

    [Fact]
    public void QaFindingPreviewList_WithFourItems_ShowsAllWithoutToggle()
    {
        var items = Enumerable.Range(1, 4)
            .Select(i => new QaFinding
            {
                RuleCode = $"QA-FINDING-{i:D3}",
                Title = $"Title {i}",
                Description = $"Description {i}",
                Severity = QaSeverity.Medium,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            })
            .ToList();

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        // All 4 visible, no button
        host.Markup.Should().Contain("QA-FINDING-001");
        host.Markup.Should().Contain("QA-FINDING-004");
        host.FindAll("button.qr-show-toggle").Should().BeEmpty();
    }

    [Fact]
    public void QaFindingPreviewList_ShowAll_IsNotCappedAt999()
    {
        var items = Enumerable.Range(1, 1001)
            .Select(i => new QaFinding
            {
                RuleCode = $"QA-FINDING-{i:D4}",
                Title = $"Title {i}",
                Description = $"Description {i}",
                Severity = QaSeverity.Low,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            })
            .ToList();

        var host = Render<TestHost>(p => p.Add(h => h.Items, items).Add(h => h.ShowAll, true));

        // Verify final item is rendered (not truncated at 999)
        host.WaitForAssertion(() =>
        {
            host.Markup.Should().Contain("QA-FINDING-1001");
        });
    }

    [Fact]
    public void QaFindingPreviewList_CoverageFinding_RendersConciseDescription()
    {
        var items = new List<QaFinding>
        {
            new()
            {
                RuleCode = "PP-02",
                Title = "Principle 2",
                Description = "Rule 'PP-02' (Principle) has no coverage in the Specification, Plan and Tasks.",
                Severity = QaSeverity.High,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            }
        };

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        host.Markup.Should().Contain("PP-02");
        host.Markup.Should().NotContain("qr-finding-category");
        host.Markup.Should().Contain("Missing coverage in Specification, Plan and Tasks.");
        host.Markup.Should().Contain("Problem:");
        host.Markup.Should().NotContain("Rule 'PP-02' (Principle) has no coverage");
    }

    [Fact]
    public void QaFindingPreviewList_Finding_ShowsCanonicalRuleTitle()
    {
        var items = new List<QaFinding>
        {
            new()
            {
                RuleCode = "PRINCIPLE-001",
                Title = "Headless API Communication",
                Description = "Test description",
                Severity = QaSeverity.Medium,
                Category = QaCategory.Architecture,
                AffectedArtifact = null,
            }
        };

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        host.Markup.Should().Contain("PRINCIPLE-001 — Headless API Communication");
        host.Find(".qr-finding-description").TextContent.Should().Contain("Problem: Test description");
        host.Markup.Should().NotContain("qr-finding-category");
    }

    [Fact]
    public void QaFindingPreviewList_Finding_DoesNotDuplicateRuleCode()
    {
        var items = new List<QaFinding>
        {
            new()
            {
                RuleCode = "PP-02",
                Title = "PP-02",
                Description = "Some description",
                Severity = QaSeverity.Critical,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            }
        };

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        host.Markup.Should().Contain("PP-02");
        host.Markup.Should().NotContain("PP-02 — PP-02");
    }

    [Fact]
    public void QaFindingPreviewList_Finding_DoesNotRepeatCategoryInsideGroupedCard()
    {
        var items = new List<QaFinding>
        {
            new()
            {
                RuleCode = "PP-02",
                Title = "Principle 2",
                Description = "Test description",
                Severity = QaSeverity.High,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            }
        };

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        var markup = host.Markup;
        markup.Should().NotContain("qr-finding-category");
        markup.Should().NotContain("Constitution");
        markup.Should().Contain("PP-02 — Principle 2");
    }

    [Fact]
    public void QaFindingPreviewList_ShowAll_UsesSemanticStyledButton()
    {
        var items = Enumerable.Range(1, 8)
            .Select(i => new QaFinding
            {
                RuleCode = $"TEST-{i:D3}",
                Title = $"Finding {i}",
                Description = $"Description {i}",
                Severity = QaSeverity.Medium,
                Category = QaCategory.Constitution,
                AffectedArtifact = null,
            })
            .ToList();

        var host = Render<TestHost>(p => p.Add(h => h.Items, items).Add(h => h.PreviewLimit, 5));

        // Initial state: Show all button visible
        var showAllButton = host.Find("button.qr-show-toggle");
        showAllButton.Should().NotBeNull();
        showAllButton.TagName.Should().Be("BUTTON");
        showAllButton.TextContent.Trim().Should().Be("Show all 8 findings");
        showAllButton.GetAttribute("aria-label").Should().Be("Show all 8 findings");

        // Click to show all
        showAllButton.Click();

        host.WaitForAssertion(() =>
        {
            // All 8 should be visible
            host.Markup.Should().Contain("TEST-006");
            host.Markup.Should().Contain("TEST-008");

            // Button changes to "Show less"
            var showLessButton = host.Find("button.qr-show-toggle");
            showLessButton.TextContent.Trim().Should().Be("Show fewer findings");
            showLessButton.TagName.Should().Be("BUTTON");
            showLessButton.HasAttribute("aria-label").Should().BeTrue();
        });
    }

    [Fact]
    public void QaFindingPreviewList_NonCoverageFinding_PreservesMeaningfulDescription()
    {
        var items = new List<QaFinding>
        {
            new()
            {
                RuleCode = "SPEC-001",
                Title = "Specification Clarity",
                Description = "The specification should clearly define all API endpoints",
                Severity = QaSeverity.Medium,
                Category = QaCategory.Specification,
                AffectedArtifact = null,
            }
        };

        var host = Render<TestHost>(p => p.Add(h => h.Items, items));

        // Should preserve the meaningful description for non-coverage findings
        host.Markup.Should().Contain("The specification should clearly define all API endpoints");
        // Should NOT incorrectly replace with coverage text
        host.Markup.Should().NotContain("Missing coverage in");
    }
}
