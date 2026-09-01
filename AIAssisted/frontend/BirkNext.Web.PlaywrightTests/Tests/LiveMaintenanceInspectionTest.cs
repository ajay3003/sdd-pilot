using BirkNext.Web.PlaywrightTests.Fixtures;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Web.PlaywrightTests.Tests;

[Collection("Playwright Tests - PreStarted")]
public sealed class LiveMaintenanceInspectionTest : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted? _fixture;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        try
        {
            await _fixture.InitializeAsync();
        }
        catch
        {
            // Fixture requires backend - continue without it for frontend-only inspection
            _fixture = null;
        }
    }

    public Task DisposeAsync() => _fixture?.DisposeAsync() ?? Task.CompletedTask;

    [Fact]
    public async Task Inspect_Maintenance_Live_DOM()
    {
        // Use direct Playwright for frontend-only testing
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        try
        {
            await page.GotoAsync("http://localhost:5173/admin/system-settings?section=maintenance",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

            // Give page time to render
            await page.WaitForSelectorAsync(".settings-card", new() { Timeout = 10000 });

            // Capture evidence
            var structuralElements = new
            {
                SsStatusPills = await page.Locator(".ss-status-pill").CountAsync(),
                SettingsTables = await page.Locator("table.settings-table").CountAsync(),
                HealthSevs = await page.Locator(".ss-health-sev").CountAsync(),
                DevDiagSections = await page.Locator(".dev-diag-section").CountAsync(),
            };

            var overallPillHtml = await page.Locator(".ss-status-pill").First.EvaluateAsync<string>(
                "el => el.outerHTML.substring(0, 250)");

            var firstTableRowHtml = await page.Locator("table.settings-table tr").First.EvaluateAsync<string>(
                "el => el.outerHTML.substring(0, 300)");

            var badgeComputed = await page.Locator(".ss-health-sev").First.EvaluateAsync<dynamic>(
                "el => ({display: window.getComputedStyle(el).display, bg: window.getComputedStyle(el).backgroundColor})");

            // Check for raw text
            var bodyText = await page.TextContentAsync("body") ?? "";
            var hasRawOverall = bodyText.Contains("Overall Status Warning") || bodyText.Contains("Overall Status Pass");
            var hasRawChecks = bodyText.Contains("Checks Executed 2");

            // Screenshot
            await page.ScreenshotAsync(new() { Path = "C:\\Users\\ajaan\\maintenance-live-inspection.png" });

            // Report
            System.Console.WriteLine("\n========== LIVE BROWSER INSPECTION ==========");
            System.Console.WriteLine($"\\n.ss-status-pill: {structuralElements.SsStatusPills}");
            System.Console.WriteLine($"table.settings-table: {structuralElements.SettingsTables}");
            System.Console.WriteLine($".ss-health-sev: {structuralElements.HealthSevs}");
            System.Console.WriteLine($".dev-diag-section: {structuralElements.DevDiagSections}");

            System.Console.WriteLine($"\\nFirst pill HTML:\\n{overallPillHtml}...");
            System.Console.WriteLine($"\\nFirst row HTML:\\n{firstTableRowHtml}...");
            System.Console.WriteLine($"\\nBadge computed style: {badgeComputed}");

            System.Console.WriteLine($"\\nRaw 'Overall Status X': {hasRawOverall}");
            System.Console.WriteLine($"Raw 'Checks Executed': {hasRawChecks}");

            if (structuralElements.SsStatusPills > 0 && structuralElements.SettingsTables > 0 && !hasRawOverall)
            {
                System.Console.WriteLine("\\n✓ RESULT: Structured presentation in browser");
            }
            else
            {
                System.Console.WriteLine("\\n✗ RESULT: Raw/legacy presentation in browser");
            }

            System.Console.WriteLine("\\n✓ Screenshot: C:\\Users\\ajaan\\maintenance-live-inspection.png");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}
