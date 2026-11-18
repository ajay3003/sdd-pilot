using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// One Document Quality Review → Data Model Quality scenario shared by the self-starting and pre-started variants: the test imports its own
/// synthetic project (a data model with nine known warnings), runs only the Data Model Quality pack, reads the exact findings and drives the
/// Show all / Show fewer toggle twice through real WASM event dispatch (the unboxing regression the original test was written for).
/// </summary>
internal static class DataModelQualityScenario
{
    public static async Task RunAsync(IPage page)
    {
        var console = new List<string>();
        var pageErrors = new List<string>();
        page.Console += (_, message) => console.Add(message.Text);
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        var project = SyntheticProjects.NewName("PwDmq");
        await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project, withDataModel: true));

        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await Assertions.Expect(page.GetByText($"Imported project: {project}")).ToBeVisibleAsync(new() { Timeout = 120000 });
        var pack = page.Locator("label.qr-pack-option").Filter(new() { HasText = "Data Model Quality" });
        var packBox = pack.Locator("input[type=checkbox]");
        // The pack is enabled once the review has read the project's documents (it names the Data Model as an available artifact).
        await Assertions.Expect(packBox).ToBeEnabledAsync(new() { Timeout = 120000 });
        await Assertions.Expect(pack).ToContainTextAsync("Ready to run");
        await page.GetByRole(AriaRole.Button, new() { Name = "Clear", Exact = true }).ClickAsync();
        await packBox.CheckAsync();
        (await page.Locator("label.qr-pack-option input[type=checkbox]:checked").CountAsync()).Should().Be(1, "only Data Model Quality runs");

        await page.GetByRole(AriaRole.Button, new() { Name = "Run Document Quality Review", Exact = true }).ClickAsync();
        var header = page.Locator("button.qr-result-header").Filter(new() { HasText = "Data Model Quality" });
        await Assertions.Expect(header).ToContainTextAsync("9 findings", new() { Timeout = 60000 });
        await header.ClickAsync();
        await Assertions.Expect(header).ToHaveAttributeAsync("aria-expanded", "true");

        var list = page.Locator("#data-model-findings-list");
        var cards = list.Locator(".issue-card");
        var toggle = page.Locator("button.qr-show-toggle[aria-controls='data-model-findings-list']");
        await Assertions.Expect(cards).ToHaveCountAsync(5, new() { Timeout = 10000 });
        await Assertions.Expect(toggle).ToContainTextAsync("Show all 9 findings");
        (await page.Locator("main").InnerTextAsync()).Should().NotContain("##", "findings are plain text, never raw Markdown headings");

        for (var cycle = 1; cycle <= 2; cycle++)
        {
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).ToContainTextAsync("Show fewer");
            await Assertions.Expect(cards).ToHaveCountAsync(9);
            await toggle.ClickAsync();
            await Assertions.Expect(toggle).ToContainTextAsync("Show all");
            await Assertions.Expect(cards).ToHaveCountAsync(5);
        }

        // The findings are the ones the synthetic model was written to produce (read from the review, not from a toast).
        await toggle.ClickAsync();
        var text = await list.InnerTextAsync();
        foreach (var column in new[] { "name", "phone", "card_number", "book_number", "due_date" })
            text.Should().Contain($"Column \"{column}\" is missing a type.");
        foreach (var column in new[] { "email", "phone", "card_number" })
            text.Should().Contain($"Column \"{column}\" appears to contain sensitive data but has no classification annotation.");
        text.Should().Contain("Column \"member_id\" looks like a foreign key but no relationship is defined for it.");

        console.Where(m => m.Contains("no idea on how to unbox value types", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("no WASM unboxing error");
        console.Where(m => m.Contains("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("no rendering exception");
        pageErrors.Should().BeEmpty();
    }
}
