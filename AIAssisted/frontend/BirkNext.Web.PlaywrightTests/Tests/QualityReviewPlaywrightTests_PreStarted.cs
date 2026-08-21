using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

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
[Collection("Playwright Tests")]
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

            // 1. Navigate to Quality Review page
            await page.GotoAsync($"{_fixture.FrontendUrl}/quality-review", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(500);

            // 2. Select first available sample project
            var projectOptions = await page.QuerySelectorAllAsync("label.qr-pack-option");
            projectOptions.Count.Should().BeGreaterThan(0, "Should have project options");

            await projectOptions[0].ClickAsync();

            // 3. Run quality review
            var runButton = page.Locator("button.btn-primary");
            await runButton.WaitForAsync();
            (await runButton.IsDisabledAsync()).Should().BeFalse();
            await runButton.ClickAsync();

            // 4. Expand Data Model Quality
            var dmCard = page.Locator("text=Data Model Quality").First;
            await dmCard.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await dmCard.ClickAsync();

            // 5. Verify toggle button exists (indicates findings > 5)
            var toggleBtn = page.Locator("button.qr-show-toggle").First;
            await toggleBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // 6. Verify initial findings
            var findings = await page.QuerySelectorAllAsync(".qr-cat-title");
            findings.Count.Should().BeGreaterThan(0);

            // 7. Verify no raw markdown
            var pageContent = await page.ContentAsync();
            pageContent.Should().NotContain("##", "Should not have raw markdown ##");

            // 8. Show all
            await toggleBtn.ClickAsync();
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show less');
                }
            ");

            var allFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            allFindings.Count.Should().BeGreaterThan(findings.Count);

            // 9. Show less
            await toggleBtn.ClickAsync();
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show all');
                }
            ");

            var previewFindings = await page.QuerySelectorAllAsync(".qr-cat-title");
            previewFindings.Count.Should().BeLessThan(allFindings.Count);

            // 10. Repeat toggle cycle
            await toggleBtn.ClickAsync();
            await page.WaitForFunctionAsync(@"
                () => {
                    const btn = document.querySelector('button.qr-show-toggle');
                    return btn && btn.textContent.includes('Show less');
                }
            ");

            // 11. Verify no WASM errors
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
}
