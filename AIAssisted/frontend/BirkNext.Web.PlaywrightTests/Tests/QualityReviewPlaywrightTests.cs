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

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
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

            // 1. Select a real sample project. Quality Review consumes the shared
            // workspace selection; its pack checkboxes are not project options.
            await page.GotoAsync($"{_fixture.FrontendUrl}/sample-projects", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            var selectProjectButton = page.GetByRole(AriaRole.Button, new() { Name = "Select Project", Exact = true }).First;
            await selectProjectButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
            await selectProjectButton.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Selected", Exact = true })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 2. Use client-side navigation so the selected project remains in
            // the current WASM application session.
            await page.GetByRole(AriaRole.Navigation)
                .GetByRole(AriaRole.Link, new() { Name = "Quality Review", Exact = true })
                .ClickAsync();
            await page.WaitForURLAsync("**/quality-review", new PageWaitForURLOptions { Timeout = 10000 });

            // Keep this integration test focused and deterministic: run only the
            // Data Model Quality pack instead of relying on changing defaults.
            await page.GetByRole(AriaRole.Button, new() { Name = "Clear", Exact = true }).ClickAsync();
            var dataModelPack = page.Locator("label.qr-pack-option")
                .Filter(new LocatorFilterOptions { HasText = "Data Model Quality" })
                .Locator("input[type=checkbox]");
            await dataModelPack.CheckAsync();

            // 3. Wait for the Run button to become enabled
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
            var showToggleButton = page.Locator("button.qr-show-toggle[aria-controls='data-model-findings-list']");

            // Wait for the button with a longer timeout to allow rendering
            await showToggleButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 9. Verify initial findings are visible
            var dataModelFindings = page.Locator("#data-model-findings-list .issue-card");
            var initialFindingCount = await dataModelFindings.CountAsync();
            initialFindingCount.Should().BeGreaterThan(0, "Should have at least one data model finding");

            // 10. Verify no raw markdown heading syntax is visible
            var pageText = await page.ContentAsync();
            pageText.Should().NotContain("##", "Page should not contain raw markdown ## syntax");

            // 11. Click Show all
            await showToggleButton.ClickAsync();

            // Wait for the button text to change to "Show fewer findings"
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle[aria-controls=data-model-findings-list]');
                    return btn && btn.textContent.includes('Show fewer');
                }
            ");

            // 12. Verify all findings are now visible (count increased)
            var allFindingCount = await dataModelFindings.CountAsync();
            allFindingCount.Should().BeGreaterThan(initialFindingCount,
                "Show all should display more findings than initial preview");

            // 13. Click Show fewer
            await showToggleButton.ClickAsync();

            // Wait for the button text to change back to "Show all"
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle[aria-controls=data-model-findings-list]');
                    return btn && btn.textContent.includes('Show all');
                }
            ");

            // 14. Verify findings returned to preview count
            var previewFindingCount = await dataModelFindings.CountAsync();
            previewFindingCount.Should().BeLessThan(allFindingCount,
                "Show fewer should return to preview count");

            // 15. Repeat the toggle cycle one more time (real interaction stress test)
            await showToggleButton.ClickAsync();

            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle[aria-controls=data-model-findings-list]');
                    return btn && btn.textContent.includes('Show fewer');
                }
            ");

            var secondShowAllCount = await dataModelFindings.CountAsync();
            secondShowAllCount.Should().BeGreaterThan(previewFindingCount,
                "Second Show all should expand findings again");

            await showToggleButton.ClickAsync();

            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle[aria-controls=data-model-findings-list]');
                    return btn && btn.textContent.includes('Show all');
                }
            ");

            var secondShowLessCount = await dataModelFindings.CountAsync();
            secondShowLessCount.Should().BeLessThan(secondShowAllCount,
                "Second Show fewer should return to preview count");

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
