using BirkNext.CriticalE2E;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class CriticalE2EStepOrderingTests : BunitContext
{
    private readonly Mock<ICriticalE2EApiService> _api = new(MockBehavior.Strict);
    private CriticalE2EFlowDefinition? _saved;

    public CriticalE2EStepOrderingTests()
    {
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static CriticalE2EFlowDefinition Flow() => new()
    {
        Id = "flow", Name = "Search", Module = "Barn", ProfileId = "dev", EnvironmentId = "dev",
        Steps =
        [
            new() { StepId = "A", BrowserAction = CompanionActionKind.Click, Selector = new() { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Start søk" } },
            new() { StepId = "B", BrowserAction = CompanionActionKind.Fill, Selector = new() { Value = "search" }, Value = "lars" },
            new() { StepId = "C", BrowserAction = CompanionActionKind.AssertText, Selector = new() { Value = "result" }, Expected = "Prøv å laste tilganger på nytt", IsFinalAssertion = true },
        ],
    };

    private IRenderedComponent<CriticalE2EFlowEditor> Editor(CriticalE2EFlowDefinition? flow = null, Action? cancel = null) =>
        Render<CriticalE2EFlowEditor>(p => p.Add(e => e.Flow, flow ?? Flow()).Add(e => e.ProfileId, "dev")
            .Add(e => e.EnvironmentId, "dev").Add(e => e.OnSave, f => _saved = f).Add(e => e.OnCancel, () => cancel?.Invoke()));

    private static string[] Order(IRenderedComponent<CriticalE2EFlowEditor> cut) =>
        cut.FindAll("[data-step-id]").Select(e => e.GetAttribute("data-step-id")!).ToArray();

    [Theory]
    [InlineData("up", 2, "A,C,B")]
    [InlineData("down", 0, "B,A,C")]
    public void MovePreservesAllContentsAndSaveReloadOrder(string direction, int index, string expected)
    {
        var flow = Flow();
        var cut = Editor(flow);
        cut.Find($"[data-testid=e2e-step-{direction}-{index}]").Click();
        Order(cut).Should().Equal(expected.Split(','));
        cut.Find("[data-testid=e2e-editor-save]").Click();
        _saved!.Steps.Should().BeEquivalentTo(flow.Steps);
        _saved.Steps.Select(s => s.StepId).Should().Equal(expected.Split(','));
        foreach (var step in _saved.Steps) step.Should().BeSameAs(flow.Steps.Single(s => s.StepId == step.StepId));
        Order(Editor(_saved)).Should().Equal(expected.Split(','));
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public void BoundariesAreDisabledAndHeadersRenumber()
    {
        var cut = Editor();
        cut.Find("[data-testid=e2e-step-up-0]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=e2e-step-down-2]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=e2e-step-up-2]").Click();
        cut.Find("[data-testid=e2e-step-title-1]").TextContent.Should().Be("Step 2 · Assert text");
        cut.Find("[data-testid=e2e-step-final-1]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=e2e-editor-save]").HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData("before", 1, 1)]
    [InlineData("after", 0, 1)]
    [InlineData("before", 0, 0)]
    [InlineData("after", 2, 3)]
    [InlineData("add", -1, 3)]
    public void InsertAndAppendUseSameDefaultsAtExactPosition(string action, int index, int position)
    {
        var cut = Editor();
        cut.Find($"[data-testid=e2e-step-{action}{(index < 0 ? "" : $"-{index}")}]").Click();
        var ids = Order(cut);
        ids.Should().HaveCount(4).And.OnlyHaveUniqueItems();
        ids.Where((_, i) => i != position).Should().Equal("A", "B", "C");
        cut.Find($"[data-testid=e2e-step-action-{position}]").GetAttribute("value").Should().Be("Click");
        cut.Find($"[data-testid=e2e-step-identity-{position}]").TextContent.Should().Contain("No element");
        cut.Find($"[data-testid=e2e-step-action-{position}]").Change("Fill");
        cut.Find($"[data-testid=e2e-step-value-{position}]").Change("new value");
        cut.Find($"[data-testid=e2e-step-{(position == 0 ? "down" : "up")}-{position}]").Click();
        var moved = position == 0 ? 1 : position - 1;
        Order(cut)[moved].Should().Be(ids[position]);
        cut.Find($"[data-testid=e2e-step-value-{moved}]").GetAttribute("value").Should().Be("new value");
    }

    [Fact]
    public void DragMovesOnlyOnDropAndCanMoveToEnd()
    {
        var flow = Flow();
        flow.Steps.Add(new() { StepId = "D", BrowserAction = CompanionActionKind.Navigate, Value = "/done" });
        var cut = Editor(flow);
        cut.Find("[data-testid=e2e-step-3]").HasAttribute("draggable").Should().BeFalse();
        cut.Find("[data-testid=e2e-step-drag-3]").DragStart(new DragEventArgs());
        cut.Find("[data-testid=e2e-step-1]").DragEnter(new DragEventArgs());
        cut.Find("[data-testid=e2e-step-1]").ClassList.Should().Contain("e2e-drop-target");
        Order(cut).Should().Equal("A", "B", "C", "D");
        cut.Find("[data-testid=e2e-step-1]").Drop(new DragEventArgs());
        Order(cut).Should().Equal("A", "D", "B", "C");
        cut.Find("[data-testid=e2e-step-drag-1]").DragStart(new DragEventArgs());
        cut.Find("[data-testid=e2e-drop-end]").Drop(new DragEventArgs());
        Order(cut).Should().Equal("A", "B", "C", "D");
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public void AbortedDragAndCancelLeaveSourceUnchanged()
    {
        var flow = Flow();
        var cancelled = false;
        var cut = Editor(flow, () => cancelled = true);
        cut.Find("[data-testid=e2e-step-drag-2]").DragStart(new DragEventArgs());
        cut.Find("[data-testid=e2e-step-0]").DragEnter(new DragEventArgs());
        cut.Find("[data-testid=e2e-step-drag-2]").DragEnd(new DragEventArgs());
        Order(cut).Should().Equal("A", "B", "C");
        cut.Find("[data-testid=e2e-step-up-1]").Click();
        cut.Find("[data-testid=e2e-editor-cancel]").Click();
        cancelled.Should().BeTrue();
        _saved.Should().BeNull();
        Order(Editor(flow)).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void RemoveAfterMoveRemovesTheMovedIdentity()
    {
        var cut = Editor();
        cut.Find("[data-testid=e2e-step-up-1]").Click();
        cut.Find("[data-testid=e2e-step-remove-0]").Click();
        Order(cut).Should().Equal("A", "C");
        cut.Find("[data-testid=e2e-editor-save]").Click();
        _saved!.Steps.Should().BeEquivalentTo(Flow().Steps.Where(s => s.StepId != "B"));
    }

    [Theory]
    [InlineData(CriticalE2EFlowKind.Critical)]
    [InlineData(CriticalE2EFlowKind.Diagnostic)]
    public void SharedEditorRetainsClassification(CriticalE2EFlowKind kind)
    {
        var cut = Editor(Flow() with { Kind = kind });
        cut.Find("[data-testid=e2e-step-up-1]").Click();
        cut.Find("[data-testid=e2e-editor-save]").Click();
        _saved!.Kind.Should().Be(kind);
    }

    [Fact]
    public void DisclosureStateFollowsTheKeyedStep()
    {
        var cut = Editor();
        cut.Find("[data-testid=e2e-step-selector-details-1-toggle]").Click();
        cut.Find("[data-testid=e2e-step-up-1]").Click();
        cut.Find("[data-testid=e2e-step-selector-details-0-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=e2e-step-selector-details-1-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=e2e-step-0]").GetAttribute("ondragover").Should().Be("event.preventDefault()");
    }

    [Fact]
    public async Task PendingPickDisablesStructuralEditsAndMetadataMovesWithItsStep()
    {
        var pending = new TaskCompletionSource<CriticalE2EElementPickResult>();
        _api.Setup(a => a.PickElementAsync(It.IsAny<CriticalE2EElementPickRequest>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var cut = Editor();
        cut.Render(p => p.Add(e => e.ElementPick, new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready }));
        var pick = cut.Find("[data-testid=e2e-step-pick-1]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("[data-testid=e2e-step-up-1]").HasAttribute("disabled").Should().BeTrue());
        foreach (var action in new[] { "down", "before", "after", "remove" })
            cut.Find($"[data-testid=e2e-step-{action}-1]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=e2e-step-drag-1]").GetAttribute("draggable").Should().Be("false");
        pending.SetResult(new() { Status = CriticalE2EStatus.Passed, Outcome = CriticalE2EPickOutcome.Picked,
            Element = new() { PageRoute = "/search", AccessibleName = "Search", Recommended = new() { Value = "picked-search" } } });
        await pick;
        cut.Find("[data-testid=e2e-step-up-1]").Click();
        cut.Find("[data-testid=e2e-step-captured-0]").TextContent.Should().Be("/search");
        cut.Find("[data-testid=e2e-step-value-0]").GetAttribute("value").Should().Be("lars");
        cut.FindAll("[data-testid=e2e-step-captured-1]").Should().BeEmpty();
        _api.Verify(a => a.PickElementAsync(It.IsAny<CriticalE2EElementPickRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public void LegacyMissingAndDuplicateIdsGetStableDraftKeysWithoutChangingOrderOrSource()
    {
        var flow = Flow();
        flow.Steps[0] = flow.Steps[0] with { StepId = "" };
        flow.Steps[2] = flow.Steps[2] with { StepId = "B" };
        var draft = CriticalE2EFlowDraft.From(flow);
        draft.Steps.Select(s => s.StepId).Should().OnlyHaveUniqueItems().And.NotContain("");
        draft.Steps.Select(s => s.BrowserAction).Should().Equal(flow.Steps.Select(s => s.BrowserAction));
        flow.Steps[0].StepId.Should().BeEmpty();
        var inserted = draft.InsertStep(1);
        draft.MoveStep(inserted.StepId, 0).Should().BeTrue();
        draft.Steps[0].Should().BeSameAs(inserted);
    }
}
