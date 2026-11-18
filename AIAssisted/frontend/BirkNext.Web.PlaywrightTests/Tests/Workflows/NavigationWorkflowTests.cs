using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// All Modules is the complete catalog and every workspace view only a preference: every enabled module is listed once and opens at its
/// route, search finds a module by what it does, a module outside the current workspace opens without switching the workspace silently,
/// Back/Forward work, and the chosen workspace survives a reload.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class NavigationWorkflowTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private sealed record Module(string Route, string? Reach, bool Enabled, bool Linked);

    private static async Task<IReadOnlyList<Module>> CatalogAsync(IPage page)
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/all-modules", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("am").WaitForAsync(new() { Timeout = 120000 });
        await Assertions.Expect(page.GetByTestId("am-module").First).ToBeVisibleAsync();
        var raw = await page.GetByTestId("am-module").EvaluateAllAsync<string[][]>(
            "els => els.map(e => [e.getAttribute('data-route'), e.getAttribute('data-reach'), e.getAttribute('data-enabled'), String(!!e.querySelector('[data-testid=am-module-link]'))])");
        return raw.Select(r => new Module(r[0], r[1], r[2] == "true", r[3] == "true")).ToList();
    }

    [BrowserFact]
    public async Task All_modules_lists_every_module_once_and_every_enabled_module_opens()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        try
        {
            var modules = await CatalogAsync(page);
            modules.Count.Should().BeGreaterThanOrEqualTo(60, "the registry lists the whole portfolio");
            modules.Select(m => m.Route).Should().OnlyHaveUniqueItems();
            modules.Where(m => !m.Enabled).Should().OnlyContain(m => !m.Linked, "a module switched off in Feature Visibility is listed, never linked");
            modules.Should().Contain(m => m.Reach == "HiddenByWorkspaceView" && m.Linked, "modules outside the workspace view are marked, not hidden");

            await page.GetByTestId("am-search").FillAsync("declared journeys");
            await Assertions.Expect(page.Locator("[data-testid=am-module][data-route='integration-journeys']")).ToHaveCountAsync(1);

            var unreachable = new List<string>();
            foreach (var module in modules.Where(m => m.Enabled))
            {
                var before = pageErrors.Count;
                await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/{module.Route}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.Locator("main").WaitForAsync(new() { Timeout = 60000 });
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                if (await page.GetByText("Sorry, there's nothing at this address").CountAsync() > 0 || pageErrors.Count > before)
                    unreachable.Add(module.Route);
            }
            unreachable.Should().BeEmpty("every enabled module renders at its route without a page error");
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task A_module_outside_the_workspace_opens_without_switching_it_and_the_choice_survives_a_reload()
    {
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var view = page.GetByTestId("nav-workspace-view");
            await view.WaitForAsync(new() { Timeout = 120000 });
            await view.SelectOptionAsync(new SelectOptionValue { Label = "Manual Testing" });
            await Assertions.Expect(view.Locator("option:checked")).ToHaveTextAsync("Manual Testing");

            // A Technical Testing module opened by link keeps the Manual Testing workspace and adds a temporary sidebar row.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/api-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.Locator("#nav-groups a.nav-link[href='api-quality-review']").First).ToBeVisibleAsync(new() { Timeout = 120000 });
            await Assertions.Expect(view.Locator("option:checked")).ToHaveTextAsync("Manual Testing");
            await page.GoBackAsync();
            await page.WaitForURLAsync("**/manual-testing");
            await page.GoForwardAsync();
            await page.WaitForURLAsync("**/api-quality-review");

            // The workspace view is a saved preference.
            await view.SelectOptionAsync(new SelectOptionValue { Label = "Technical Testing" });
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("nav-workspace-view").Locator("option:checked")).ToHaveTextAsync("Technical Testing", new() { Timeout = 120000 });
            await page.GetByTestId("nav-workspace-view").SelectOptionAsync(new SelectOptionValue { Label = "Manual Testing" });
        }
        finally { await page.CloseAsync(); }
    }
}
