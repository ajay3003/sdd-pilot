using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Recommended Workflow was retired on 2026-11-19 (the Dashboard owns the review path). Old /getting-started links, bookmarks and deep links
/// land on the Dashboard with their query kept, and the retired page is not left in the history (Back does not bounce to it again).
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

    [BrowserFact]
    public async Task Old_getting_started_links_land_on_the_dashboard_with_their_query_kept()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        try
        {
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/getting-started?path=runtime", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForURLAsync("**/dashboard?path=runtime", new() { Timeout = 240000 });
            await Assertions.Expect(page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Project Dashboard"), new() { Timeout = 120000 });
            (await page.Locator("[data-testid=rw-available-now], [data-testid=rw-stages]").CountAsync()).Should().Be(0, "the retired page is gone");

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Project Dashboard"), new() { Timeout = 120000 });
            page.Url.Should().EndWith("/dashboard?path=runtime", "the query survives a reload");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
