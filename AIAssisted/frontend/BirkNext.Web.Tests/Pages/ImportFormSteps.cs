using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// What a tester does before choosing a ZIP on Import Project (2026-11-22, the Connected Systems-like flow): open the ZIP form from the header
/// when it is not open, and answer "What does this ZIP contain?" ("Not sure" unless a role is already ticked). The file button is locked until
/// then. Returns the form's file input.
/// </summary>
internal static class ImportFormSteps
{
    public static IRenderedComponent<InputFile> Upload(IRenderedComponent<IComponent> cut)
    {
        Answer(cut);
        return cut.FindComponents<InputFile>().Single(i => i.Instance.AdditionalAttributes?["data-testid"]?.ToString() == "pi-choose-file");
    }

    /// <summary>Opens the ZIP form when needed and answers the content question ("Not sure" unless a role is ticked): the file input exists then.</summary>
    public static void Answer(IRenderedComponent<IComponent> cut)
    {
        if (cut.FindAll("[data-testid=pi-choose]").Count == 0) cut.Find("[data-testid=pi-header-import]").Click();
        var answered = cut.FindAll("[data-testid=pi-zip-role], [data-testid=pi-zip-detect]").Any(i => i.HasAttribute("checked"));
        if (!answered) cut.Find("[data-testid=pi-zip-detect]").Change(true);
    }
}
