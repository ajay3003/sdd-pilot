using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright browser tests for Data Model Quality presentation.
///
/// These tests verify real WASM rendering and event binding in a browser,
/// catching integration issues that bUnit component tests cannot detect.
///
/// The key test is the repeated Show all / Show less toggle:
/// it exercises real browser event dispatch and component re-rendering,
/// which is where the WASM unboxing error would occur if event binding
/// used incorrect attribute names (@onclick vs onclick).
/// </summary>
public sealed class QualityReviewPlaywrightTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private List<string> _consoleMessages = [];
    private List<string> _pageErrors = [];

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();

        // Attach console and error listeners for this test
        _fixture.Context.Pages[0].Console += (sender, e) =>
        {
            _consoleMessages.Add(e.Text);
        };

        _fixture.Context.Pages[0].PageError += (sender, error) =>
        {
            _pageErrors.Add(error);
        };
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task DataModelQuality_WasmInteractionHasNoRuntimeErrors()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Attach error listeners for this page
            var consoleMessages = new List<string>();
            var pageErrors = new List<string>();

            page.Console += (sender, e) =>
            {
                consoleMessages.Add(e.Text);
            };

            page.PageError += (sender, error) =>
            {
                pageErrors.Add(error);
            };

            // 1. Navigate to Quality Review page
            await page.GotoAsync($"{_fixture.FrontendUrl}/quality-review", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // 2. Wait for the app to load and present the Sample Project selection
            // The page should have a quality-review-page component with project selection
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Give React/Blazor time to fully render after network load
            await page.WaitForTimeoutAsync(1000);

            // 3. Select a sample project
            // Look for a project option that contains "Sample" (e.g., "Sample Project A")
            var projectOptions = await page.QuerySelectorAllAsync("label.qr-pack-option");

            if (projectOptions.Count == 0)
            {
                throw new InvalidOperationException(
                    "No project options found. Quality Review may not have loaded correctly.");
            }

            // Click the first available project option
            await projectOptions[0].ClickAsync();

            // 4. Wait for the Run button to become enabled
            var runButton = page.Locator("button.btn-primary");
            await runButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Verify button is not disabled
            var isDisabled = await runButton.IsDisabledAsync();
            isDisabled.Should().BeFalse("Run button should be enabled");

            // 5. Click Run to execute the quality review
            await runButton.ClickAsync();

            // 6. Wait for the Data Model Quality pack card to appear
            var dmQualityCard = page.Locator("text=Data Model Quality").First;
            await dmQualityCard.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 7. Click the Data Model Quality card to expand it
            await dmQualityCard.ClickAsync();

            // 8. Wait for findings to render and toggle button to appear
            // If there are more than 5 findings, a "Show all" button will appear
            var showToggleButton = page.Locator("button.qr-show-toggle").First;

            // Wait for the button with a longer timeout to allow rendering
            await showToggleButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 9. Verify initial findings are visible
            var initialFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            initialFindings.Count.Should().BeGreaterThan(0, "Should have at least one category of findings");

            // 10. Verify no raw markdown heading syntax is visible
            var pageText = await page.ContentAsync();
            pageText.Should().NotContain("##", "Page should not contain raw markdown ## syntax");

            // 11. Click Show all
            await showToggleButton.ClickAsync();

            // Wait for the button text to change to "Show less"
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show less');
                }
            ");

            // 12. Verify all findings are now visible (count increased)
            var allFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            allFindings.Count.Should().BeGreaterThan(initialFindings.Count,
                "Show all should display more findings than initial preview");

            // 13. Click Show less
            await showToggleButton.ClickAsync();

            // Wait for the button text to change back to "Show all"
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show all');
                }
            ");

            // 14. Verify findings returned to preview count
            var previewFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            previewFindings.Count.Should().BeLessThan(allFindings.Count,
                "Show less should return to preview count");

            // 15. Repeat the toggle cycle one more time (real interaction stress test)
            await showToggleButton.ClickAsync();

            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show less');
                }
            ");

            var secondShowAll = await page.QuerySelectorAllAsync(".qr-cat-title");
            secondShowAll.Count.Should().BeGreaterThan(previewFindings.Count,
                "Second Show all should expand findings again");

            await showToggleButton.ClickAsync();

            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show all');
                }
            ");

            var secondShowLess = await page.QuerySelectorAllAsync(".qr-cat-title");
            secondShowLess.Count.Should().BeLessThan(secondShowAll.Count,
                "Second Show less should return to preview count");

            // 16. Verify WASM console has no unboxing or rendering errors
            var unboxingErrors = consoleMessages
                .Where(m => m.Contains("no idea on how to unbox value types", StringComparison.OrdinalIgnoreCase))
                .ToList();

            unboxingErrors.Should().BeEmpty(
                "WASM should not have unboxing errors. This indicates an incorrect event binding attribute.");

            var renderingErrors = consoleMessages
                .Where(m => m.Contains("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase))
                .ToList();

            renderingErrors.Should().BeEmpty(
                "WASM should not have unhandled component rendering exceptions.");

            pageErrors.Should().BeEmpty(
                "Browser should not have any uncaught errors during interaction");

            // 17. Final verification: confirm semantic text cleanup
            // If the fixture contains text like "No ## Overview section found", it should render as "No Overview section found"
            var semanticText = await page.Locator("text=/^No\\s+Overview\\s+section\\s+found\\.?$/").CountAsync();
            if (semanticText > 0)
            {
                // If this text pattern exists, verify the ## is cleaned
                var rawMarkdownText = await page.Locator("text=/.*##.*Overview.*found.*/").CountAsync();
                rawMarkdownText.Should().Be(0, "Markdown cleanup should remove ## while preserving meaning");
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
