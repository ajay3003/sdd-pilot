using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Frontend Quality Review readiness, waited for on what the page states instead of a fixed delay: the Run button stays disabled — with a
/// stated reason ("loading browser evidence…", "Checking N active engines…") — until the active Target Environment, its engines and the
/// browser evidence are loaded. Tests that read the button straight after navigation saw that legitimate loading state as "not configured".
/// </summary>
public static class FrontendQualityReviewPage
{
    public static ILocator Run(IPage page) => page.GetByTestId("fqr-run");

    public static async Task OpenAsync(IPage page)
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/frontend-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await Run(page).WaitForAsync(new() { Timeout = 120000 });
    }

    /// <summary>
    /// After a run: waits for the results (the Engine coverage table renders first), then opens "View engine diagnostics" the way a user does
    /// and returns the detailed engine matrix inside it (post-run result-first hierarchy: the matrix is collapsed by default).
    /// </summary>
    public static async Task<ILocator> EngineMatrixAsync(IPage page, float timeoutMs = 240000)
    {
        // Engine coverage lives on the result's Coverage tab (the Summary tab is selected after a run); the matrix is in its diagnostics disclosure.
        await page.GetByTestId("fqr-tab-coverage").WaitForAsync(new() { Timeout = timeoutMs });
        await OpenResultTabAsync(page, "coverage");
        await page.GetByTestId("fqr-engine-coverage-table").WaitForAsync(new() { Timeout = 30000 });
        var diagnostics = page.GetByTestId("fqr-engine-coverage-toggle");
        if (await diagnostics.GetAttributeAsync("aria-expanded") != "true") await diagnostics.ClickAsync();
        await Assertions.Expect(diagnostics).ToHaveAttributeAsync("aria-expanded", "true");
        var matrix = page.Locator("table.fqr-engine-matrix");
        await matrix.WaitForAsync(new() { Timeout = 30000 });
        return matrix;
    }

    /// <summary>Selects one of the result views (summary, issues, coverage, evidence, history) and waits until its panel is shown.</summary>
    public static async Task OpenResultTabAsync(IPage page, string id)
    {
        var tab = page.GetByTestId($"fqr-tab-{id}");
        await tab.ClickAsync();
        await Assertions.Expect(tab).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.GetByTestId($"fqr-tabpanel-{id}")).ToBeVisibleAsync();
    }

    /// <summary>Waits until Run is enabled; on timeout the assertion names the reason the page showed.</summary>
    public static async Task WaitUntilRunnableAsync(IPage page, float timeoutMs = 120000)
    {
        try { await Assertions.Expect(Run(page)).ToBeEnabledAsync(new() { Timeout = timeoutMs }); }
        catch (PlaywrightException)
        {
            var reason = page.GetByTestId("fqr-run-disabled-reason");
            var stated = await reason.CountAsync() > 0 ? await reason.InnerTextAsync() : "(no reason shown)";
            throw new InvalidOperationException($"Frontend Quality Review never became runnable; the page says: {stated}");
        }
    }
}
