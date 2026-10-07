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
}
