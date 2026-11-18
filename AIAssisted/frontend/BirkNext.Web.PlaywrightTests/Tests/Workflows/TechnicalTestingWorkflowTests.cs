using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Technical Testing on the test's own project: home → Discover &amp; assess (five areas, area filter keeps the stage) → Configure &amp; prepare →
/// a specialist module and Back to the same stage and area with the same project → Verify &amp; investigate. A project with no recorded runs must
/// say so for every area — static evidence is never shown as a runtime result — and a reload keeps the stage.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class TechnicalTestingWorkflowTests : IAsyncLifetime
{
    private static readonly string[] Areas = ["frontend", "api", "integration", "performance", "e2e"];
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static Task StageReadyAsync(IPage page, string stage) =>
        page.Locator($"[data-testid=tts][data-stage={stage}][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });

    private static Task<string[]> AreasAsync(IPage page) =>
        page.GetByTestId("tt-area").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-area'))");

    [BrowserFact]
    public async Task Stages_keep_project_and_area_and_verify_never_reports_a_run_that_did_not_happen()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var project = SyntheticProjects.NewName("PwTechnical");
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/technical-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("twh-project")).ToContainTextAsync(project, new() { Timeout = 240000 });
            await Assertions.Expect(page.GetByTestId("tt-next-action")).ToHaveCountAsync(1);

            await page.Locator("[data-testid=twh-stage][data-stage=assess]").ClickAsync();
            await StageReadyAsync(page, "assess");
            (await AreasAsync(page)).Should().Equal(Areas);
            await Assertions.Expect(page.GetByTestId("tt-applicability")).ToHaveCountAsync(5);
            await page.Locator("[data-testid=tt-filter-area][data-area=integration]").ClickAsync();
            await StageReadyAsync(page, "assess");
            (await AreasAsync(page)).Should().Equal(["integration"]);
            page.Url.Should().EndWith("technical-testing/assess?area=integration");

            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/technical-testing/configure?area=api", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await StageReadyAsync(page, "configure");
            var specialist = page.Locator("[data-testid=tt-area-link][href='api-quality-review']");
            await Assertions.Expect(specialist).ToHaveCountAsync(1);
            await specialist.ClickAsync();
            await page.WaitForURLAsync("**/api-quality-review", new() { Timeout = 60000 });
            await page.GoBackAsync();
            await StageReadyAsync(page, "configure");
            page.Url.Should().EndWith("technical-testing/configure?area=api");
            (await AreasAsync(page)).Should().Equal(["api"]);
            await Assertions.Expect(page.GetByTestId("twh-project")).ToContainTextAsync(project);

            await page.Locator("[data-testid=twh-stage][data-stage=verify]").ClickAsync();
            await StageReadyAsync(page, "verify");
            var verifyAreas = await AreasAsync(page);
            verifyAreas.Should().NotBeEmpty();
            await Assertions.Expect(page.GetByTestId("tt-runs-none")).ToHaveCountAsync(verifyAreas.Length);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await StageReadyAsync(page, "verify");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
