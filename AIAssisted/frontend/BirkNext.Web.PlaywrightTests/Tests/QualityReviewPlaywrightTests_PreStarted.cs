using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Document Quality Review and Frontend Quality Review browser tests against an already started, isolated BirkNext — its frontend and backend
/// URLs come from BIRKNEXT_PLAYWRIGHT_FRONTEND_URL / BIRKNEXT_PLAYWRIGHT_BACKEND_URL (never the developer's own ports 5000/5173) and the browser
/// is connected with the instance's launch code; see BrowserTestServers. Each test establishes the project it needs itself.
/// </summary>
[Collection("Playwright Tests - PreStarted")]
public sealed class QualityReviewPlaywrightTests_PreStarted : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public async Task DataModelQuality_WasmInteractionHasNoRuntimeErrors()
    {
        // Was: Demo Projects → "Select Project" (an optional, separately configured feature — the isolated API has none) and a sidebar link.
        // Now: the test imports its own synthetic project and opens Document Quality Review directly; the assertions are stronger (exact
        // findings, exact counts, two toggle cycles) — see DataModelQualityScenario.
        var page = await _fixture.Context.NewPageAsync();
        try { await DataModelQualityScenario.RunAsync(page); }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    [Trait("Category", "FrontendQualityPhase2ERealAcceptance")]
    public async Task FrontendQualityReview_RealEnginesReachDecisionSupportExactlyOnce()
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new ConcurrentBag<string>();
        var pageErrors = new ConcurrentBag<string>();
        var engineRequests = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new[]
        {
            "api/frontend-runtime/review",
            "api/frontend-accessibility/review",
            "api/frontend-lighthouse/review",
            "api/frontend-passive-security/review"
        };

        page.Console += (_, message) =>
        {
            if (message.Type == "error") consoleErrors.Add(message.Text);
        };
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Request += (_, request) =>
        {
            var endpoint = endpoints.SingleOrDefault(value => request.Url.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (endpoint is not null) engineRequests.AddOrUpdate(endpoint, 1, (_, count) => count + 1);
        };

        try
        {
            var settings = JsonSerializer.Serialize(new
            {
                profiles = new[]
                {
                    new
                    {
                        id = "phase2e-local",
                        name = "Phase 2E Local",
                        environmentType = "Local",
                        targetUrl = _fixture.FrontendUrl,
                        authentication = new { requiresAuthentication = false, authenticationType = "None" },
                        performance = new { },
                        coreWebVitals = new { },
                        security = new { },
                        features = new
                        {
                            enableSecurityEngine = true,
                            enablePerformanceEngine = true,
                            enableBrowserRuntimeEngine = true,
                            enableAccessibilityEngine = true,
                            enableLighthouseEngine = true,
                            enablePassiveSecurityEngine = true
                        },
                        engineRequirements = new
                        {
                            staticSecurity = "Required",
                            passivePerformance = "Required",
                            browserRuntime = "Optional",
                            accessibility = "Optional",
                            lighthouse = "Optional",
                            passiveSecurity = "Optional"
                        },
                        releasePolicy = new { blockingLogicalIssueIds = Array.Empty<string>(), reviewOptionalEngineFailures = true },
                        integrations = Array.Empty<object>()
                    }
                },
                activeProfileId = "phase2e-local"
            });
            var settingsLiteral = JsonSerializer.Serialize(settings);
            await page.AddInitScriptAsync($"localStorage.setItem('birknext:frontend-analysis-settings', {settingsLiteral});");

            await FrontendQualityReviewPage.OpenAsync(page);
            var run = page.GetByRole(AriaRole.Button, new() { Name = "Run Frontend Quality Review", Exact = true });
            // Run is disabled with a stated reason while the target, engines and browser evidence load; it must become runnable for this target.
            await FrontendQualityReviewPage.WaitUntilRunnableAsync(page);
            await run.ClickAsync();

            var matrix = page.Locator("table.fqr-engine-matrix");
            await matrix.WaitForAsync(new LocatorWaitForOptions { Timeout = 240000 });
            var rows = matrix.Locator("tbody tr");
            (await rows.CountAsync()).Should().Be(6, "all six configured engines must reach aggregate coverage");

            var ids = await rows.EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.engineId)");
            ids.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(
                "StaticSecurity", "PassivePerformance", "BrowserRuntime", "Accessibility", "Lighthouse", "PassiveSecurity");
            (await page.Locator("tr[data-engine-id='BrowserRuntime']").CountAsync()).Should().Be(1);

            await page.GetByRole(AriaRole.Region, new() { Name = "Release disposition" }).ShouldBeVisibleAsync();
            await page.GetByRole(AriaRole.Region, new() { Name = "Automated coverage and engine outcomes" }).ShouldBeVisibleAsync();
            await page.GetByRole(AriaRole.Region, new() { Name = "Logical issues" }).ShouldBeVisibleAsync();
            await page.GetByRole(AriaRole.Region, new() { Name = "Manual verification required" }).ShouldBeVisibleAsync();
            await page.GetByRole(AriaRole.Region, new() { Name = "Browser Runtime details" }).ShouldBeVisibleAsync();
            await page.GetByText("Legacy static review score", new() { Exact = true }).ShouldBeVisibleAsync();

            foreach (var endpoint in endpoints)
            {
                engineRequests.GetValueOrDefault(endpoint).Should().Be(1, $"{endpoint} must execute exactly once");
            }

            var browserRow = page.Locator("tr[data-engine-id='BrowserRuntime']");
            (await browserRow.InnerTextAsync()).Should().Contain("Chromium");
            (await page.GetByRole(AriaRole.Region, new() { Name = "Release disposition" }).InnerTextAsync())
                .Should().MatchRegex("Blocked|ReviewRequired|NoAutomatedBlockDetected");

            // Leave and return inside the same app session (client-side navigation; the sidebar no longer has a fixed Dashboard link in
            // every workspace view, so the always-present All modules link and browser Back are used).
            await page.GetByTestId("nav-all-modules-hint").ClickAsync();
            await page.WaitForURLAsync("**/all-modules**", new PageWaitForURLOptions { Timeout = 30000 });
            await page.GoBackAsync();
            await page.WaitForURLAsync("**/frontend-quality-review", new PageWaitForURLOptions { Timeout = 30000 });
            await matrix.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            (await matrix.Locator("tbody tr").CountAsync()).Should().Be(6, "session reload must preserve aggregate outcomes");
            foreach (var endpoint in endpoints)
            {
                engineRequests.GetValueOrDefault(endpoint).Should().Be(1, "rendering persisted results must not rerun engines");
            }

            pageErrors.Should().BeEmpty("Phase 2E rendering must not raise browser page errors");
            consoleErrors.Where(message => message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("Phase 2E must not introduce rendering exceptions");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [BrowserFact]
    [Trait("Category", "UICorrectness")]
    public async Task TargetEnvironmentsNavigation_OpenCorrectSettingsSection()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            await FrontendQualityReviewPage.OpenAsync(page);

            // The review's target panel links to the Target Environments settings as "Change target" (renamed from "Target Environments").
            var targetEnvLink = page.GetByRole(AriaRole.Link, new() { Name = "Change target", Exact = true });
            await targetEnvLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });
            await targetEnvLink.ClickAsync();

            await page.WaitForURLAsync("**/admin/system-settings?section=target-environments", new PageWaitForURLOptions { Timeout = 10000 });

            var targetEnvSection = page.GetByRole(AriaRole.Region, new() { Name = "Target Environments" });
            await Assertions.Expect(targetEnvSection).ToBeVisibleAsync(new() { Timeout = 60000 });

            var generalSection = page.GetByRole(AriaRole.Region, new() { Name = "General" });
            (await generalSection.IsVisibleAsync()).Should().BeFalse("General section should not be visible");

            var generalNavItem = page.GetByRole(AriaRole.Button, new() { Name = "General" });
            var isGeneralActive = await generalNavItem.GetAttributeAsync("aria-current");
            isGeneralActive.Should().NotBe("page", "General nav item should not be marked as active");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [BrowserFact]
    [Trait("Category", "UICorrectness")]
    public async Task FrontendQualityReview_ResponsiveLayoutNoHorizontalOverflow()
    {
        var viewports = new[] { (1920, 1080), (1440, 900), (1280, 720), (1024, 768) };
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // First load without viewport change to ensure component initializes
            await FrontendQualityReviewPage.OpenAsync(page);

            // The dimension cards render once the landing has loaded the active target (8-10 s on a cold WASM start; 5 s was too short).
            await page.Locator("article.fqr-dimension-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });

            foreach (var (width, height) in viewports)
            {
                await page.SetViewportSizeAsync(width, height);

                var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
                var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

                scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"No horizontal overflow at {width}x{height}");

                var cards = await page.Locator("article.fqr-dimension-card").CountAsync();
                cards.Should().BeGreaterThan(0, "Analysis cards must be visible");
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [BrowserFact]
    [Trait("Category", "UICorrectness")]
    public async Task FrontendQualityReview_KeyboardNavigationToTargetEnvironments()
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();

        page.Console += (_, message) =>
        {
            if (message.Type == "error")
                consoleErrors.Add(message.Text);
        };

        try
        {
            await FrontendQualityReviewPage.OpenAsync(page);

            var targetEnvLink = page.GetByRole(AriaRole.Link, new() { Name = "Change target", Exact = true });
            await targetEnvLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });
            await targetEnvLink.FocusAsync();

            var isFocused = await targetEnvLink.EvaluateAsync<bool>("element => element === document.activeElement");
            isFocused.Should().BeTrue("the Change target link (to Target Environments) should be keyboard-focusable");

            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync("**/admin/system-settings?section=target-environments", new PageWaitForURLOptions { Timeout = 10000 });

            consoleErrors.Where(e => e.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

}

internal static class PlaywrightAssertions
{
    public static async Task ShouldBeVisibleAsync(this ILocator locator) =>
        (await locator.IsVisibleAsync()).Should().BeTrue($"'{locator}' should be visible");
}
