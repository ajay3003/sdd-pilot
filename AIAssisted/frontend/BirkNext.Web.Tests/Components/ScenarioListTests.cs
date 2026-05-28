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

    // ── Drag-and-drop ordering tests ──────────────────────────────────────────

    [Fact]
    public void ScenarioList_DragHandle_NotPresent_WhenIsDraggableFalse()
    {
        var scenarios = new[] { new ScenarioListItem("id-1", "A test", null, "Test") };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, false));

        cut.FindAll("[data-testid='drag-handle-id-1']").Should().BeEmpty();
        cut.FindAll("[data-testid='move-up-btn-id-1']").Should().BeEmpty();
        cut.FindAll("[data-testid='move-down-btn-id-1']").Should().BeEmpty();
    }

    [Fact]
    public void ScenarioList_DragHandle_Present_WhenIsDraggableTrue()
    {
        var scenarios = new[] { new ScenarioListItem("id-1", "A test", null, "Test") };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true));

        cut.Find("[data-testid='drag-handle-id-1']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioList_MoveButtons_Present_WhenIsDraggableTrue()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First",  null, "Test"),
            new ScenarioListItem("id-2", "Second", null, "Test"),
        };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true));

        cut.Find("[data-testid='move-up-btn-id-1']").Should().NotBeNull();
        cut.Find("[data-testid='move-down-btn-id-1']").Should().NotBeNull();
        cut.Find("[data-testid='move-up-btn-id-2']").Should().NotBeNull();
        cut.Find("[data-testid='move-down-btn-id-2']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioList_MoveUpButton_Disabled_ForFirstItem()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First",  null, "Test"),
            new ScenarioListItem("id-2", "Second", null, "Test"),
        };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true));

        cut.Find("[data-testid='move-up-btn-id-1']")
            .HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='move-up-btn-id-2']")
            .HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ScenarioList_MoveDownButton_Disabled_ForLastItem()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First",  null, "Test"),
            new ScenarioListItem("id-2", "Second", null, "Test"),
        };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true));

        cut.Find("[data-testid='move-down-btn-id-2']")
            .HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='move-down-btn-id-1']")
            .HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task ScenarioList_MoveUp_FiresOnReordered_WithSwappedOrder()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First",  null, "Test"),
            new ScenarioListItem("id-2", "Second", null, "Test"),
        };

        List<ScenarioListItem>? reorderedList = null;
        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true)
            .Add(c => c.OnReordered, EventCallback.Factory.Create<List<ScenarioListItem>>(
                this, list => reorderedList = list)));

        cut.Find("[data-testid='move-up-btn-id-2']").Click();

        await Task.Delay(50);
        reorderedList.Should().NotBeNull();
        reorderedList![0].Id.Should().Be("id-2");
        reorderedList[1].Id.Should().Be("id-1");
    }

    [Fact]
    public async Task ScenarioList_MoveDown_FiresOnReordered_WithSwappedOrder()
    {
        var scenarios = new[]
        {
            new ScenarioListItem("id-1", "First",  null, "Test"),
            new ScenarioListItem("id-2", "Second", null, "Test"),
        };

        List<ScenarioListItem>? reorderedList = null;
        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true)
            .Add(c => c.OnReordered, EventCallback.Factory.Create<List<ScenarioListItem>>(
                this, list => reorderedList = list)));

        cut.Find("[data-testid='move-down-btn-id-1']").Click();

        await Task.Delay(50);
        reorderedList.Should().NotBeNull();
        reorderedList![0].Id.Should().Be("id-2");
        reorderedList[1].Id.Should().Be("id-1");
    }

    [Fact]
    public void ScenarioList_DragHandle_HasAriaLabel()
    {
        var scenarios = new[] { new ScenarioListItem("id-1", "My test", null, "Test") };

        var cut = Render<ScenarioList>(p => p
            .Add(c => c.Scenarios, scenarios)
            .Add(c => c.IsDraggable, true));

        cut.Find("[data-testid='drag-handle-id-1']")
            .GetAttribute("aria-label").Should().Contain("Drag to reorder test scenario");

        cut.Find("[data-testid='move-up-btn-id-1']")
            .GetAttribute("aria-label").Should().Contain("Move test scenario up: My test");

        cut.Find("[data-testid='move-down-btn-id-1']")
            .GetAttribute("aria-label").Should().Contain("Move test scenario down: My test");
    }
}
