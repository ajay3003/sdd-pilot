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
        showAllButton.TextContent.Trim().Should().Be("Show all 8");
        showAllButton.GetAttribute("aria-label").Should().Be("Show all 8 constitution findings");

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
        showLessButton.TextContent.Trim().Should().Be("Show less");
        showLessButton.GetAttribute("aria-label").Should().Be("Show fewer constitution findings");

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
}
