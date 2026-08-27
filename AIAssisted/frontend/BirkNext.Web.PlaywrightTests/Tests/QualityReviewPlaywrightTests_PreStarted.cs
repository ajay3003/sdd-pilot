using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright smoke tests for Data Model Quality WASM rendering.
///
/// This test assumes the application is already running:
/// - Backend on http://localhost:5000
/// - Frontend on http://localhost:5173
///
/// Prerequisites:
/// - PostgreSQL running on localhost:5432
/// - Backend started: dotnet run -p AIAssisted/backend/BirkNext.Api
/// - Frontend started: dotnet run -p AIAssisted/frontend/BirkNext.Web
///
/// To prepare for testing:
/// 1. Start PostgreSQL: cd AIAssisted && podman-compose up -d postgres
/// 2. Start backend: cd AIAssisted/backend && dotnet run
/// 3. Start frontend: cd AIAssisted/frontend && dotnet run --project BirkNext.Web
/// 4. Run this test: dotnet test BirkNext.Web.PlaywrightTests
///
/// This test is designed for CI/CD environments where services are orchestrated
/// separately and for faster local iteration after services are pre-started.
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

    [Fact]
    [Trait("Category", "PreStarted")]
    public async Task DataModelQuality_WasmInteractionHasNoRuntimeErrors()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Track console/errors
            var consoleMessages = new List<string>();
            var pageErrors = new List<string>();

            page.Console += (sender, e) => consoleMessages.Add(e.Text);
            page.PageError += (sender, error) => pageErrors.Add(error);

            // 1. Navigate to Sample Projects page first to select a project
            await page.GotoAsync($"{_fixture.FrontendUrl}/sample-projects", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // 2. Select a real sample project using the stable accessible UI.
            var selectProjectButton = page.GetByRole(AriaRole.Button, new() { Name = "Select Project", Exact = true }).First;
            await selectProjectButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
            await selectProjectButton.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Selected", Exact = true })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 3. Preserve the selected project by navigating within the current
            // WASM application session.
            await page.GetByRole(AriaRole.Navigation)
                .GetByRole(AriaRole.Link, new() { Name = "Quality Review", Exact = true })
                .ClickAsync();
            await page.WaitForURLAsync("**/quality-review", new PageWaitForURLOptions { Timeout = 10000 });

            // 5. Verify Quality Review page loaded with project context
            var pageContent = await page.ContentAsync();
            pageContent.Should().Contain("Quality Review", "Quality Review page should be displayed");

            // Wait longer for WASM component to initialize
            await page.WaitForTimeoutAsync(2000);

            // 6. Select Data Model Quality pack (NOT selected by default)
            // The pack selector has checkboxes within qr-pack-option labels
            var dataModelCheckbox = page.Locator("label.qr-pack-option:has-text('Data Model Quality') input[type='checkbox']");

            // Wait for the checkbox to be available
            await dataModelCheckbox.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Check if it's already checked
            var isChecked = await dataModelCheckbox.IsCheckedAsync();
            if (!isChecked) {
                // Not checked, so check it
                await dataModelCheckbox.ClickAsync();
                await page.WaitForTimeoutAsync(500);
            }

            // Verify Data Model Quality is now checked
            var finalChecked = await dataModelCheckbox.IsCheckedAsync();
            finalChecked.Should().BeTrue("Data Model Quality pack must be selected before running review");

            // Verify checkbox is enabled (not disabled)
            var isDisabled = await dataModelCheckbox.IsDisabledAsync();
            isDisabled.Should().BeFalse("Data Model Quality pack must be enabled/available for this project");

            // 7. Run quality review
            var runButton = page.Locator("button:has-text('Run Quality Review')");
            await runButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            (await runButton.IsDisabledAsync()).Should().BeFalse();
            await runButton.ClickAsync();

            // 8. Wait for analysis to complete
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(3000);

            // 9. Find Data Model Quality section in results
            // It should appear in the "By Review Pack" cards section
            var dmPackCard = page.Locator("button.qr-pack-card:has-text('Data Model Quality')");
            await dmPackCard.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Verify it doesn't show an error
            var errorText = await page.Locator("text=Data model not loaded").IsVisibleAsync();
            errorText.Should().BeFalse("Data Model Quality should have loaded the data model");

            // 10. Click Data Model Quality card to expand it
            await dmPackCard.ClickAsync();
            await page.WaitForTimeoutAsync(1000);

            // 11. Take screenshot to see what Data Model results look like
            var screenshotPath = Path.Combine(Path.GetTempPath(), "dm-quality-results.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });

            // Find the detail section for Data Model Quality findings
            // Look for any element that contains Data Model Quality results
            var dmDetailSection = page.Locator("text=Data Model Quality").Nth(1); // Skip the pack card header
            try {
                await dmDetailSection.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            } catch {
                // If not found, show what's on the page
                var pageHtml = await page.ContentAsync();
                throw new InvalidOperationException($"Data Model Quality detail section not found after click. Screenshot: {screenshotPath}. Page contains Data Model text: {pageHtml.Contains("Data Model")}");
            }

            // 12. Get initial findings count
            var initialFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            initialFindings.Count.Should().BeGreaterThan(0, "Data Model Quality should have findings categories");

            // 13. Verify no raw markdown in results
            var pageContentAfterRun = await page.ContentAsync();
            pageContentAfterRun.Should().NotContain("##", "Should not have raw markdown ##");

            // 14. Find and use the toggle button if findings > 5
            var toggleBtn = page.Locator("button.qr-show-toggle").First;
            var toggleExists = await toggleBtn.IsVisibleAsync();

            if (toggleExists) {
                // 15. Show all findings
                await toggleBtn.ClickAsync();
                await page.WaitForFunctionAsync(@"
                    () => {
                        const btn = document.querySelector('button.qr-show-toggle');
                        return btn && btn.textContent.includes('Show fewer');
                    }
                ");

                var allFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
                // If there are more than initial, great; if equal, that's fine too (all visible initially)
                allFindings.Count.Should().BeGreaterThanOrEqualTo(initialFindings.Count, "Show all should show at least the preview count");

                // Only test Show fewer if there's actually a difference to show
                if (allFindings.Count > 3) {
                    // 16. Show fewer
                    await toggleBtn.ClickAsync();
                    await page.WaitForFunctionAsync(@"
                        () => {
                            const btn = document.querySelector('button.qr-show-toggle');
                            return btn && btn.textContent.includes('Show all');
                        }
                    ");

                    var previewFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
                    previewFindings.Count.Should().BeLessThan(allFindings.Count);

                    // 17. Repeat toggle: Show all again
                    await toggleBtn.ClickAsync();
                    await page.WaitForFunctionAsync(@"
                        () => {
                            const btn = document.querySelector('button.qr-show-toggle');
                            return btn && btn.textContent.includes('Show fewer');
                        }
                    ");
                }
            }

            // 18. Verify no WASM errors
            var unboxErrors = consoleMessages
                .Where(m => m.Contains("no idea on how to unbox value types", StringComparison.OrdinalIgnoreCase))
                .ToList();
            unboxErrors.Should().BeEmpty("No unboxing errors in WASM");

            var renderErrors = consoleMessages
                .Where(m => m.Contains("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase))
                .ToList();
            renderErrors.Should().BeEmpty("No rendering exceptions");

            pageErrors.Should().BeEmpty("No page errors");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
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

            await page.GotoAsync($"{_fixture.FrontendUrl}/frontend-quality-review", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000
            });

            var run = page.GetByRole(AriaRole.Button, new() { Name = "Run Frontend Quality Review", Exact = true });
            await run.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            (await run.IsDisabledAsync()).Should().BeFalse("the local deterministic target is configured");
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

            await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Dashboard" }).ClickAsync();
            await page.WaitForURLAsync("**/dashboard", new PageWaitForURLOptions { Timeout = 10000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Frontend Quality Review" }).ClickAsync();
            await page.WaitForURLAsync("**/frontend-quality-review", new PageWaitForURLOptions { Timeout = 10000 });
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

    [Fact]
    [Trait("Category", "UICorrectness")]
    public async Task TargetEnvironmentsNavigation_OpenCorrectSettingsSection()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            await page.GotoAsync($"{_fixture.FrontendUrl}/frontend-quality-review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var targetEnvLink = page.GetByRole(AriaRole.Link, new() { Name = "Target Environments" });
            await targetEnvLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await targetEnvLink.ClickAsync();

            await page.WaitForURLAsync("**/admin/system-settings?section=target-environments", new PageWaitForURLOptions { Timeout = 10000 });

            var targetEnvSection = page.GetByRole(AriaRole.Region, new() { Name = "Target Environments" });
            (await targetEnvSection.IsVisibleAsync()).Should().BeTrue("Target Environments section must be visible after navigation");

            var generalSection = page.Locator("text=General").First;
            (await generalSection.IsVisibleAsync()).Should().BeFalse("General section should not be visible");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    [Trait("Category", "UICorrectness")]
    public async Task FrontendQualityReview_ResponsiveLayoutNoHorizontalOverflow()
    {
        var viewports = new[] { (1920, 1080), (1440, 900), (1280, 720), (1024, 768) };
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            foreach (var (width, height) in viewports)
            {
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync($"{_fixture.FrontendUrl}/frontend-quality-review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

                var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
                var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");

                scrollWidth.Should().BeLessThanOrEqualTo(clientWidth, $"No horizontal overflow at {width}x{height}");

                var cards = await page.Locator("article.review-landing-card").CountAsync();
                cards.Should().BeGreaterThan(0, "Analysis cards must be visible");
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
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
            await page.GotoAsync($"{_fixture.FrontendUrl}/frontend-quality-review", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            var targetEnvButton = page.GetByRole(AriaRole.Button, new() { Name = "Target Environments" });
            await targetEnvButton.FocusAsync();

            var isFocused = await page.EvaluateAsync<bool>("() => document.activeElement === document.querySelector('a:has-text(\"Target Environments\")')");
            isFocused.Should().BeTrue("Target Environments button should be focusable");

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
