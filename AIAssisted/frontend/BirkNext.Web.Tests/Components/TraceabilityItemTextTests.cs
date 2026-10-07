using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public sealed class TraceabilityItemTextTests : BunitContext
{
    [Theory]
    [InlineData("Requirement")]
    [InlineData("Test scenario")]
    [InlineData("User story")]
    [InlineData("Acceptance criterion")]
    [InlineData("Orphaned test")]
    public void LongItemsUseTheSameKeyboardAccessibleExpansion(string type)
    {
        var fullText = string.Join(" ", Enumerable.Repeat($"{type} content with a deliberately long technical token https://example.test/a-very-long-route/{type}", 8));
        var cut = Render<TraceabilityItemText>(p => p
            .Add(x => x.ItemType, type)
            .Add(x => x.ItemId, "ID-42")
            .Add(x => x.Text, fullText));

        cut.Markup.Should().Contain(fullText);
        cut.Find(".traceability-item__text").ClassList.Should().Contain("is-collapsed");
        var toggle = cut.Find(".traceability-item__toggle");
        toggle.TextContent.Should().Be("Show more");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();

        cut.Find(".traceability-item__text").ClassList.Should().NotContain("is-collapsed");
        cut.Find(".traceability-item__toggle").TextContent.Should().Be("Show less");
        cut.Find(".traceability-item__toggle").GetAttribute("aria-expanded").Should().Be("true");
        cut.Markup.Should().Contain("ID-42");
    }

    [Fact]
    public void ShortItemsRemainCompactWithoutExpansionControl()
    {
        var cut = Render<TraceabilityItemText>(p => p
            .Add(x => x.ItemType, "Requirement")
            .Add(x => x.Text, "Short requirement"));

        cut.Find(".traceability-item__text").TextContent.Should().Be("Short requirement");
        cut.FindAll(".traceability-item__toggle").Should().BeEmpty();
    }

    [Fact]
    public void StructuredScenario_UsesVisibleNonErrorKeywordSemantics()
    {
        var cut = Render<TraceabilityItemText>(p => p
            .Add(x => x.ItemType, "Test scenario")
            .Add(x => x.ItemId, "T-14")
            .Add(x => x.Text, "Given complete When complete Then complete")
            .Add(x => x.Given, "a record exists")
            .Add(x => x.When, "the event is processed")
            .Add(x => x.Then, "the record is saved"));

        var text = cut.Find(".traceability-item__text");
        text.QuerySelector(".traceability-item__given")!.TextContent.Should().Be("Given");
        text.QuerySelector(".traceability-item__when")!.TextContent.Should().Be("When");
        text.QuerySelector(".traceability-item__then")!.TextContent.Should().Be("Then");
        text.TextContent.Should().Contain("the record is saved");
        cut.Markup.Should().NotContain("text-danger").And.NotContain("text-error");
    }

    [Fact]
    public void LongItemBelowLegacyCharacterThresholdStillGetsExpansion()
    {
        var text = "Given a newly created person record When the CDC message arrives Then all identity values are retained.";
        var cut = Render<TraceabilityItemText>(p => p.Add(x => x.ItemType, "Test").Add(x => x.Text, text));

        cut.Find(".traceability-item__text").ClassList.Should().Contain("is-collapsed");
        cut.Find(".traceability-item__toggle").GetAttribute("aria-controls").Should().Be(cut.Find(".traceability-item__text").Id);
        cut.Find(".traceability-item__toggle").Click();
        cut.Find(".traceability-item__text").TextContent.Should().Be(text);
    }

    [Fact]
    public void ExpandingOneItemDoesNotExpandAnother()
    {
        var cut = Render<ItemsHarness>();

        cut.FindAll(".traceability-item__toggle")[0].Click();

        cut.FindAll(".traceability-item__text.is-collapsed").Should().HaveCount(1);
        cut.FindAll(".traceability-item__text:not(.is-collapsed)").Should().HaveCount(1);
    }

    private sealed class ItemsHarness : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<TraceabilityItemText>(0);
            builder.AddAttribute(1, nameof(TraceabilityItemText.ItemType), "Test");
            builder.AddAttribute(2, nameof(TraceabilityItemText.Text), new string('a', 140));
            builder.CloseComponent();
            builder.OpenComponent<TraceabilityItemText>(3);
            builder.AddAttribute(4, nameof(TraceabilityItemText.ItemType), "Requirement");
            builder.AddAttribute(5, nameof(TraceabilityItemText.Text), new string('b', 140));
            builder.CloseComponent();
        }
    }
}
