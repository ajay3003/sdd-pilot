using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Recommended Workflow stays the review-path owner next to the All Modules catalog: it follows the selected project, opens its "Available now"
/// summary on demand, links to All modules (where an alias search finds it again), and keeps a review-path deep link across a reload.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class RecommendedWorkflowTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static async Task OpenAsync(IPage page, string query = "")
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/getting-started{query}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-testid=rw-available-now], [data-testid=rw-stages]").First.WaitForAsync(new() { Timeout = 240000 });
    }

    [BrowserFact]
    public async Task Follows_the_selected_project_links_to_all_modules_and_keeps_a_review_path_deep_link()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var project = SyntheticProjects.NewName("PwWorkflow");
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));
            await OpenAsync(page);
            await Assertions.Expect(page.Locator(".rw-page")).ToContainTextAsync(project, new() { Timeout = 120000 });
            (await page.GetByTestId("workspace-scope-notice").CountAsync()).Should().Be(0, "Recommended Workflow belongs to the Manual Testing workspace");

            var availableNow = page.GetByTestId("rw-available-now");
            (await availableNow.GetAttributeAsync("open")).Should().BeNull("Available now starts as a one-line summary");
            await Assertions.Expect(page.GetByTestId("rw-available-count")).ToContainTextAsync("review");
            await availableNow.Locator("summary").ClickAsync();
            await Assertions.Expect(page.GetByTestId("rw-all-modules")).ToBeVisibleAsync();
            await page.GetByTestId("rw-all-modules").ClickAsync();
            await page.GetByTestId("am").WaitForAsync(new() { Timeout = 120000 });
            await page.GetByTestId("am-search").FillAsync("review decisions");
            await Assertions.Expect(page.Locator("[data-testid=am-module][data-route=getting-started]")).ToHaveCountAsync(1);

            await OpenAsync(page, "?path=runtime");
            await Assertions.Expect(page.Locator("[data-testid=rw-chosen-path], [data-testid=rw-path-runtime], [data-path=runtime]").First).ToBeVisibleAsync();
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator("[data-testid=rw-available-now], [data-testid=rw-stages]").First.WaitForAsync(new() { Timeout = 240000 });
            page.Url.Should().Contain("path=runtime", "the review-path deep link survives a reload");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
