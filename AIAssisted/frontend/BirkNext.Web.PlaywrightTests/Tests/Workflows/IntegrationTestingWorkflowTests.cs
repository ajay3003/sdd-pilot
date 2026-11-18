using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Unified Integration Testing on the test's own project: Technical Testing hands off to it, the overview counts name their entities with one
/// next step and four stages, discovery opens with its own content, and a journey defined from typed steps is saved with its exact (reordered)
/// chain and is still there after a reload. No integration is executed.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class IntegrationTestingWorkflowTests : IAsyncLifetime
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

    [BrowserFact]
    public async Task Handoff_overview_discovery_and_a_journey_saved_with_its_exact_chain()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var project = SyntheticProjects.NewName("PwIntegration");
        var journey = $"PW journey {project}";
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));

            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/technical-testing/assess?area=integration", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator("[data-testid=tts][aria-busy=false]").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 240000 });
            await page.Locator("[data-testid=tt-area-link][href='technical-testing/integrations?stage=discover']").ClickAsync();
            await ReadyAsync(page, "itx");
            await Assertions.Expect(page.Locator("[data-testid=itx][data-stage=discover]")).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByTestId("itx-discover")).ToHaveCountAsync(1);

            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/technical-testing/integrations", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "itx");
            var counts = await page.GetByTestId("itx-count").EvaluateAllAsync<string[][]>("els => els.map(e => [e.getAttribute('data-entity'), e.querySelector('strong')?.textContent ?? ''])");
            counts.Should().HaveCount(6).And.OnlyContain(c => !string.IsNullOrEmpty(c[0]) && c[1].All(char.IsDigit) && c[1].Length > 0);
            await Assertions.Expect(page.GetByTestId("itx-next-action")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("[data-testid=itx-stage] [data-testid=gsl-title-link]")).ToHaveCountAsync(4);

            // Define a journey from typed steps, reorder, save, reload.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/technical-testing/integrations?stage=define", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "itx");
            await page.Locator("[data-testid=itx-define] [data-testid=itx-create-journey]").ClickAsync();
            await ReadyAsync(page, "ijd-page");
            await page.GetByTestId("ijd-components").FillAsync("Pw.Producer\npw-topic\nPw.Consumer");
            await Assertions.Expect(page.GetByTestId("ijd-step")).ToHaveCountAsync(3);
            await page.GetByTestId("ijd-step-down").First.ClickAsync();
            await Assertions.Expect(page.Locator("[data-testid=ijd-step] > span").First).ToHaveTextAsync("1. pw-topic");
            await page.GetByTestId("ijd-name").FillAsync(journey);
            await page.GetByTestId("ijd-declared-by").FillAsync("Playwright tester");
            await page.GetByTestId("ijd-save").ClickAsync();
            var row = page.GetByTestId("ijd-journey").Filter(new() { HasText = journey });
            await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 60000 });
            await Assertions.Expect(row.GetByTestId("ijd-journey-chain")).ToHaveTextAsync("pw-topic → Pw.Producer → Pw.Consumer");
            var journeyId = await row.GetAttributeAsync("data-id");

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "ijd-page");
            var persisted = page.Locator($"[data-testid=ijd-journey][data-id=\"{journeyId}\"]");
            await Assertions.Expect(persisted).ToHaveCountAsync(1);
            await Assertions.Expect(persisted.GetByTestId("ijd-journey-chain")).ToHaveTextAsync("pw-topic → Pw.Producer → Pw.Consumer");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
