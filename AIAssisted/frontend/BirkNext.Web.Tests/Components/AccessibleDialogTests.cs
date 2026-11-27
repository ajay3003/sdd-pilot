using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Shared.Components.Dialog;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BirkNext.Web.Tests.Components;

/// <summary>V12 (2026-11-27, APP-4): one accessible modal — role and aria-modal on the panel, Escape closes unless the owner forbids it, a click
/// beside it closes only when allowed, and focus is trapped on open and returned on close (through the shared birknextDialog script).</summary>
public sealed class AccessibleDialogTests : BunitContext
{
    private int _closed;

    private IRenderedComponent<AccessibleDialog> Dialog(bool canClose = true, bool backdrop = false, string? returnFocus = null) =>
        Render<AccessibleDialog>(p => p
            .Add(d => d.LabelledBy, "dlg-title")
            .Add(d => d.TestId, "dlg")
            .Add(d => d.BackdropTestId, "dlg-backdrop")
            .Add(d => d.CanClose, canClose)
            .Add(d => d.CloseOnBackdrop, backdrop)
            .Add(d => d.ReturnFocusId, returnFocus)
            .Add(d => d.OnClose, EventCallback.Factory.Create(this, () => _closed++))
            .Add(d => d.ChildContent, (RenderFragment)(b => { b.AddMarkupContent(0, "<h2 id=\"dlg-title\">Title</h2><button type=\"button\">OK</button>"); })));

    [Fact]
    public void V12_the_panel_is_the_modal_dialog_and_Escape_closes_it()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Dialog();
        var panel = cut.Find("[data-testid=dlg]");
        (panel.GetAttribute("role"), panel.GetAttribute("aria-modal"), panel.GetAttribute("aria-labelledby"), panel.GetAttribute("tabindex"))
            .Should().Be(("dialog", "true", "dlg-title", "-1"));
        cut.Find("[data-testid=dlg-backdrop]").GetAttribute("aria-modal").Should().BeNull("aria-modal belongs on the dialog, not the backdrop");

        panel.KeyDown(new KeyboardEventArgs { Key = "Tab" });
        _closed.Should().Be(0, "only Escape closes");
        panel.KeyDown(new KeyboardEventArgs { Key = "Escape" });
        _closed.Should().Be(1);
    }

    [Fact]
    public void V12_nothing_closes_the_dialog_while_the_owner_says_it_cannot_close()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Dialog(canClose: false, backdrop: true);
        cut.Find("[data-testid=dlg]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.Find("[data-testid=dlg-backdrop]").Click();
        _closed.Should().Be(0, "a running save or reset keeps the dialog open");
    }

    [Fact]
    public void V12_a_click_beside_the_dialog_closes_it_only_when_allowed_and_a_click_inside_never_does()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var edit = Dialog(backdrop: false);
        edit.Find("[data-testid=dlg-backdrop]").Click();
        _closed.Should().Be(0, "an edit dialog is never closed by a click beside it (BUG-001)");

        var readOnly = Dialog(backdrop: true);
        readOnly.Invoking(c => c.Find("[data-testid=dlg] button").Click()).Should().Throw<MissingEventHandlerException>("a click inside the dialog stops at the panel and never reaches the backdrop");
        _closed.Should().Be(0);
        readOnly.Find("[data-testid=dlg-backdrop]").Click();
        _closed.Should().Be(1);
    }

    [Fact]
    public void V12_focus_is_trapped_when_it_opens_and_returned_when_it_closes()
    {
        var trap = JSInterop.SetupVoid("birknextDialog.trap", _ => true);
        var release = JSInterop.SetupVoid("birknextDialog.release", _ => true);
        var cut = Dialog(returnFocus: "opener");

        trap.Invocations.Should().ContainSingle("the trap is set once, on the first render");
        trap.Invocations.Single().Arguments[1].Should().Be("opener", "focus goes back to the control that opened the dialog");
        cut.Render();
        trap.Invocations.Should().ContainSingle("re-rendering does not trap again");

        cut.Instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        release.Invocations.Should().ContainSingle("closing releases the trap and restores focus");
    }

    [Fact]
    public void V12_a_missing_focus_script_never_breaks_the_dialog()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;   // no handler planned: the interop call fails
        var cut = Dialog();
        cut.Find("[data-testid=dlg]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        _closed.Should().Be(1);
    }

    [Fact]
    public void V12_the_KPI_tile_editor_uses_the_shared_dialog_Escape_cancels_and_a_click_beside_it_does_not()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cancelled = 0;
        var cut = Render<KpiTileEditor>(p => p
            .Add(e => e.OnCancel, EventCallback.Factory.Create(this, () => cancelled++))
            .Add(e => e.OnSave, EventCallback.Factory.Create<BirkNext.TestPlanning.DashboardKpiTile>(this, _ => { })));
        var dialog = cut.Find("[data-testid=kte-dialog]");
        (dialog.GetAttribute("role"), dialog.GetAttribute("aria-modal"), dialog.GetAttribute("data-dialog")).Should().Be(("dialog", "true", "accessible"));

        cut.Find("[data-testid=kte]").Click();
        cancelled.Should().Be(0, "BUG-001: an edit dialog is not closed by a click beside it");
        cut.Find("[data-testid=kte-dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cancelled.Should().Be(1);
    }
}
