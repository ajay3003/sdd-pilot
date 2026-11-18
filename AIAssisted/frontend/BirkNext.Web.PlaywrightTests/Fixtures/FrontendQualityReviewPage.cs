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
