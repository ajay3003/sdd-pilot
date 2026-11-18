using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Target Environment detection scenarios shared by the self-starting and pre-started variants (they used to be two diverging copies).
///
/// History (Playwright regression recovery, 2026-11-17): the check action on the Target Application summary is "Check target" before the first
/// check and "Check target again" after it ("Check target application" is the detailed check inside the collapsed evidence); environments are
/// opened by deep link instead of a renamed sidebar/FQR link; the end-to-end check uses a loopback URL on a closed port instead of real hosts
/// (m2lbdev, example.test), so the real backend path runs without contacting anything outside this machine. Every scenario only edits a
/// draft and cancels it — the shared environments are never saved by a test.
/// </summary>
internal static class TargetEnvironmentDetectionScenarios
{
    public const string DraftUrl = "https://draft.synthetic.example.test/protected";
    /// <summary>Loopback discard port: nothing listens, so the real backend check fails fast and never leaves this machine.</summary>
    public const string ClosedLoopbackUrl = "http://127.0.0.1:9/";
    /// <summary>How long a check may take to show its outcome (a synthetic answer is instant; a loaded machine handles the click later).</summary>
    private const float CheckTimeoutMs = 30000;

    /// <summary>The visible check action on the summary, whichever state it is in.</summary>
    public static ILocator CheckAction(IPage page) =>
        page.Locator("[data-testid=target-check-again], [data-testid=target-next-action][data-action=check]").First;

    private static ILocator UrlInput(IPage page) => page.GetByTestId("fa-edit-target-url");

    private static async Task EditTargetApplicationAsync(IPage page)
    {
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.EditAsync(page);
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        await UrlInput(page).WaitForAsync(new() { Timeout = 30000 });
    }

    private static async Task<string> SavedUrlAsync(IPage page)
    {
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        return (await page.GetByTestId("target-detection-url").InnerTextAsync()).Trim();
    }

    public static async Task PopulatesDraftSafelyAsync(IPage page)
    {
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        var saved = await SavedUrlAsync(page);

        await TargetEnvironmentSettingsPage.EditAsync(page);
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        await UrlInput(page).FillAsync(DraftUrl);
        (await UrlInput(page).InputValueAsync()).Should().Be(DraftUrl, "the draft keeps the typed value");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToBeVisibleAsync();

        // Leaving without saving discards the draft: back in view mode with the saved URL.
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes" })).ToHaveCountAsync(0);
        (await SavedUrlAsync(page)).Should().Be(saved, "an unsaved draft never reaches the saved environment");
        pageErrors.Should().BeEmpty();
    }

    public static async Task DoesNotOverwriteConfiguredValuesAsync(IPage page)
    {
        var fake = await TargetEnvironmentSettingsPage.FakeTargetCheckAsync(page, TargetEnvironmentSettingsPage.PublicBlazorTarget("Production"));
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        var saved = await SavedUrlAsync(page);
        await TargetEnvironmentSettingsPage.EditAsync(page);
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        await UrlInput(page).FillAsync(DraftUrl);
        await UrlInput(page).DispatchEventAsync("change");
        await CheckAction(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection complete", new() { Timeout = CheckTimeoutMs });

        fake.RequestedUrls.Should().Equal([DraftUrl], "the check is about the draft URL");
        await Assertions.Expect(page.GetByTestId("target-detection-url")).ToHaveTextAsync(DraftUrl);
        (await UrlInput(page).InputValueAsync()).Should().Be(DraftUrl, "a check never rewrites the typed URL");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        (await SavedUrlAsync(page)).Should().Be(saved, "nothing from the check is saved");
    }

    public static async Task UrlChangeInvalidatesDetectionAsync(IPage page)
    {
        var fake = await TargetEnvironmentSettingsPage.FakeTargetCheckAsync(page, TargetEnvironmentSettingsPage.PublicBlazorTarget("Development"));
        await EditTargetApplicationAsync(page);
        await UrlInput(page).FillAsync("https://example-a.synthetic.example.test/");
        await UrlInput(page).DispatchEventAsync("change");
        await CheckAction(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection complete", new() { Timeout = CheckTimeoutMs });

        await UrlInput(page).FillAsync("https://example-b.synthetic.example.test/");
        await UrlInput(page).DispatchEventAsync("change");
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Needs re-check", new() { Timeout = CheckTimeoutMs });
        await Assertions.Expect(CheckAction(page)).ToHaveTextAsync("Check target again");
        fake.RequestedUrls.Should().HaveCount(1, "changing the URL never re-runs the check by itself");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
    }

    public static async Task ResponsiveLayoutAsync(IPage page, int width, int height)
    {
        await page.SetViewportSizeAsync(width, height);
        await EditTargetApplicationAsync(page);
        var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
        overflow.Should().BeLessThanOrEqualTo(0, $"no horizontal overflow at {width}x{height}");
        await Assertions.Expect(CheckAction(page)).ToBeVisibleAsync();
        await Assertions.Expect(CheckAction(page)).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Check target"));
        await Assertions.Expect(UrlInput(page)).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
    }

    public static async Task KeyboardAccessibilityAsync(IPage page)
    {
        var fake = await TargetEnvironmentSettingsPage.FakeTargetCheckAsync(page, TargetEnvironmentSettingsPage.PublicBlazorTarget("Development"));
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        var edit = page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true });
        await edit.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true })).ToBeVisibleAsync();

        var tab = page.GetByRole(AriaRole.Tab, new() { Name = "Target Application", Exact = true });
        await tab.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(tab).ToHaveAttributeAsync("aria-selected", "true");

        await UrlInput(page).FocusAsync();
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.TypeAsync(DraftUrl);
        await page.Keyboard.PressAsync("Tab"); // commits the value (change) and moves on
        // The committed draft re-renders the actions (which buttons exist depends on the next action); focus the one present after that render.
        await Assertions.Expect(page.GetByTestId("target-detection-url")).ToHaveTextAsync(DraftUrl);

        // The check action is reachable with the keyboard and runs on Enter.
        var check = CheckAction(page);
        await check.FocusAsync();
        await Assertions.Expect(check).ToBeFocusedAsync(new() { Timeout = 2000 });
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection complete", new() { Timeout = CheckTimeoutMs });
        fake.RequestedUrls.Should().Equal([DraftUrl]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
    }

    /// <summary>A real backend check (no fake): the browser reaches the configured HTTP backend, which answers for the draft URL.</summary>
    public static async Task<IResponse> RealBackendCheckAsync(IPage page)
    {
        var requestFailures = new List<string>();
        page.RequestFailed += (_, request) => requestFailures.Add($"{request.Url}: {request.Failure}");
        await EditTargetApplicationAsync(page);
        await UrlInput(page).FillAsync(ClosedLoopbackUrl);
        await UrlInput(page).DispatchEventAsync("change");

        var response = await page.RunAndWaitForResponseAsync(() => CheckAction(page).ClickAsync(),
            r => r.Url.EndsWith("/api/frontend-target/detect", StringComparison.Ordinal) && r.Request.Method == "POST", new() { Timeout = 60000 });
        response.Status.Should().Be(200, $"the BirkNext backend answers the check; body: {await response.TextAsync()}");
        requestFailures.Should().NotContain(f => f.Contains("ERR_SSL_PROTOCOL_ERROR", StringComparison.OrdinalIgnoreCase));

        // Nothing listens on the discard port: the check fails honestly, for the draft URL, and the draft stays unsaved.
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection failed", new() { Timeout = CheckTimeoutMs });
        await Assertions.Expect(page.GetByTestId("target-detection-reachability")).ToContainTextAsync("Check failed");
        await Assertions.Expect(page.GetByTestId("target-detection-url")).ToHaveTextAsync(ClosedLoopbackUrl);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        return response;
    }
}
