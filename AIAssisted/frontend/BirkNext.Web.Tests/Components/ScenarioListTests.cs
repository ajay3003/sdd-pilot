using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

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

    [Fact]
    public void ScenarioList_EachRow_HasDeleteButton()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.Find("[data-testid='delete-btn-id-1']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioList_ClickDeleteButton_ShowsInlineConfirmation()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.Find("[data-testid='delete-btn-id-1']").Click();

        var confirm = cut.Find("[data-testid='delete-confirm-id-1']");
        confirm.TextContent.Should().Contain("Delete this scenario? This cannot be undone.");
        cut.Find("[data-testid='delete-confirm-btn']").Should().NotBeNull();
        cut.Find("[data-testid='delete-cancel-btn']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioList_ClickCancelInConfirmation_DismissesConfirmation()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.Find("[data-testid='delete-btn-id-1']").Click();
        cut.Find("[data-testid='delete-cancel-btn']").Click();

        cut.FindAll("[data-testid='delete-confirm-id-1']").Should().BeEmpty();
        cut.Find("[data-testid='delete-btn-id-1']").Should().NotBeNull();
    }

    [Fact]
    public async Task ScenarioList_ClickConfirmDelete_InvokesOnDeleteRequested()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        string? deletedId = null;
        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.OnDeleteRequested, EventCallback.Factory.Create<string>(this, id => deletedId = id)));

        cut.Find("[data-testid='delete-btn-id-1']").Click();
        cut.Find("[data-testid='delete-confirm-btn']").Click();

        await Task.Delay(50);
        deletedId.Should().Be("id-1");
    }

    [Fact]
    public void ScenarioList_DeletingIdSet_ShowsSpinnerForThatScenario()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.DeletingId, "id-1"));

        cut.Find("[aria-label='Deleting scenario']").Should().NotBeNull();
        cut.FindAll("[data-testid='delete-btn-id-1']").Should().BeEmpty();
    }

    [Fact]
    public void ScenarioList_DeleteError_ShowsInlineErrorForScenario()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First scenario", null, "Requirement"),
        };

        var errors = new Dictionary<string, string> { ["id-1"] = "Something went wrong. Please try again." };
        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.DeleteErrors, errors));

        cut.Find("[data-testid='delete-error-id-1']")
            .TextContent.Should().Contain("Something went wrong");
    }

    [Fact]
    public void ScenarioList_DeleteButton_HasAccessibleAriaLabel()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "Login requirement", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.Find("[data-testid='delete-btn-id-1']")
            .GetAttribute("aria-label").Should().Contain("Delete scenario");
    }

    [Fact]
    public void ScenarioList_DeleteButton_HasVisibleTextLabel()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "Login requirement", null, "Requirement"),
        };

        var cut = Render<ScenarioList>(p => p.Add(c => c.Scenarios, scenarios));

        cut.Find("[data-testid='delete-btn-id-1']")
            .TextContent.Trim().Should().NotBeEmpty();
    }
}
