using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Manual Testing work that is not saved yet survives a reload of the browser tab (it is kept in that tab only, never sent anywhere): the scenario
/// selection in Plan &amp; prepare and export field values not saved as the project's export settings. A hand-written test case opens at its first
/// step, and a step is added together with its expected result.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ManualTestingReloadTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static Task ReadyAsync(IPage page, string testId) =>
        page.Locator($"[data-testid={testId}][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });

    private static Task MessageAsync(IPage page, string pattern) =>
        Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex(pattern, RegexOptions.IgnoreCase), new() { Timeout = 240000 });

    [BrowserFact]
    public async Task A_selection_a_hand_written_case_and_unsaved_export_settings_behave_as_a_tester_expects_across_a_reload()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var project = SyntheticProjects.NewName("PwReload");
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-plan-suggestions", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "tps");
            await page.GetByTestId("tps-discover-run").ClickAsync();
            await MessageAsync(page, "candidates found");

            // 1. The selection survives a reload; nothing is prepared by it.
            var candidate = page.Locator("li[data-testid^=tps-candidate-]").First;
            var id = (await candidate.GetAttributeAsync("data-testid"))!["tps-candidate-".Length..];
            await page.GetByTestId($"tps-select-{id}").CheckAsync();
            await Assertions.Expect(page.GetByTestId("tps-selected-count")).ToContainTextAsync("1");
            await page.ReloadAsync();
            await ReadyAsync(page, "tps");
            await Assertions.Expect(page.GetByTestId($"tps-select-{id}")).ToBeCheckedAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("tps-message")).ToContainTextAsync("kept from before the page was reloaded");
            await page.GetByTestId("tps-clear-selection").ClickAsync();

            // 2. A hand-written case opens at its first step; the step is added with its expected result.
            // Writing a case by hand is an advanced path (tests BirkNext did not find are normally added in Azure DevOps): open it first.
            var advanced = page.GetByTestId("tps-advanced-manual").First;
            await Assertions.Expect(advanced).Not.ToHaveAttributeAsync("open", "");
            await advanced.Locator("summary").ClickAsync();
            await Assertions.Expect(advanced).ToContainTextAsync("Azure DevOps Test Plans");
            await advanced.GetByTestId("tps-manual-title").FillAsync("Return a book after the due date");
            await page.GetByTestId("tps-manual-create").ClickAsync();
            await MessageAsync(page, "Test case added");
            var empty = page.Locator("[data-testid^=tps-steps-empty-]").First;
            await Assertions.Expect(empty).ToContainTextAsync("No steps yet");
            var sid = (await empty.GetAttributeAsync("data-testid"))!["tps-steps-empty-".Length..];
            await Assertions.Expect(page.GetByTestId($"tps-step-new-{sid}")).ToBeFocusedAsync(new() { Timeout = 30000 });
            await page.GetByTestId($"tps-step-new-{sid}").FillAsync("Return the book two days late");
            await page.GetByTestId($"tps-step-new-expected-{sid}").FillAsync("A late fee is shown");
            await page.GetByTestId($"tps-step-add-{sid}").ClickAsync();
            await Assertions.Expect(page.GetByTestId($"tps-step-expected-{sid}-0")).ToHaveValueAsync("A late fee is shown");
            // A step with its expected result is all the Azure DevOps file (and so Approve) needs; no case-level expected result.
            await page.GetByTestId($"tps-case-{sid}").GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "Draft saved");
            await Assertions.Expect(page.GetByTestId($"tps-accept-{sid}")).ToBeEnabledAsync(new() { Timeout = 30000 });
            var draftId = Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
            draftId.Should().NotBeNullOrEmpty();

            // 3. An export field changed but not saved for the project survives a reload, and says so; saving it ends that.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "mt");
            await page.Locator($"[data-testid=mt-plan-export][href*=\"{draftId}\"]").ClickAsync();
            await ReadyAsync(page, "adx");
            var area = $@"{project}\Reload";
            await page.GetByTestId("adx-area").FillAsync(area);
            await page.GetByTestId("adx-area").DispatchEventAsync("change");
            await Assertions.Expect(page.GetByTestId("adx-settings-unsaved")).ToBeVisibleAsync(new() { Timeout = 30000 });
            await page.ReloadAsync();
            await ReadyAsync(page, "adx");
            await Assertions.Expect(page.GetByTestId("adx-area")).ToHaveValueAsync(area, new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adx-settings-unsaved")).ToContainTextAsync("kept from before the page was reloaded");
            await page.GetByTestId("adx-save-profile").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adx-settings-unsaved")).ToHaveCountAsync(0, new() { Timeout = 30000 });
            await page.ReloadAsync();
            await ReadyAsync(page, "adx");
            await Assertions.Expect(page.GetByTestId("adx-area")).ToHaveValueAsync(area, new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adx-settings-unsaved")).ToHaveCountAsync(0);
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
