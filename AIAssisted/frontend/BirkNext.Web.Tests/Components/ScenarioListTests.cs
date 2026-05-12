using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class ScenarioListTests : BunitContext
{
    [Fact]
    public void ScenarioList_RendersOneRowPerScenario()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario",  "Description one",   "Requirement"),
            new ScenarioListItem("id-2", "Second scenario", null,                 "Test"),
            new ScenarioListItem("id-3", "Third scenario",  "Description three", "NeedsClarification"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(3);
    }

    [Fact]
    public void ScenarioList_EachRow_ShowsTitleKindAndDescription()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "My title", "My description", "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        var row = cut.Find("[data-testid='scenario-row']");
        row.TextContent.Should().Contain("My title");
        row.TextContent.Should().Contain("My description");
        row.TextContent.Should().Contain("Requirement");
    }

    [Fact]
    public void ScenarioList_EmptyList_ShowsEmptyStateMessage()
    {
        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, Array.Empty<ScenarioListItem>()));

        cut.FindAll("[data-testid='scenario-row']").Should().BeEmpty();
        cut.Find("[data-testid='empty-state']").Should().NotBeNull();
    }
}
