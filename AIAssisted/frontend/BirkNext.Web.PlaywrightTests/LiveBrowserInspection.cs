using Microsoft.Playwright;
using FluentAssertions;

namespace BirkNext.Web.PlaywrightTests.Standalone;

/// <summary>
/// Standalone live browser inspection against running frontend.
/// No backend required - inspects frontend rendering only.
/// </summary>
public sealed class LiveBrowserInspection
{
    public static async Task Main()
    {
        Console.WriteLine("=== LIVE BROWSER INSPECTION ===");
        Console.WriteLine("Starting fresh Playwright browser...\n");

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        var context = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            // Fresh context - no cache
        });

        var page = await context.NewPageAsync();
        page.Console += (_, msg) => 
        {
            if (msg.Type == "error")
                Console.WriteLine($"[CONSOLE ERROR] {msg.Text}");
        };

        try
        {
            Console.WriteLine("Navigating to Maintenance...");
            var response = await page.GotoAsync("http://localhost:5173/admin/system-settings?section=maintenance", 
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
            Console.WriteLine($"Response: {response?.Status}\n");

            // Wait for content
            await page.WaitForSelectorAsync(".settings-card", new() { Timeout = 10000 });
            Console.WriteLine("Page loaded. Analyzing DOM...\n");

            // ========== OVERALL STATUS ==========
            Console.WriteLine("=== OVERALL STATUS ===");
            var overallPills = await page.Locator(".ss-status-pill").CountAsync();
            Console.WriteLine($"Total .ss-status-pill elements: {overallPills}");

            if (overallPills > 0)
            {
                var pillHtml = await page.Locator(".ss-status-pill").First.EvaluateAsync<string>(
                    "el => el.outerHTML.substring(0, 200)");
                Console.WriteLine($"First pill HTML: {pillHtml}...");
                
                var computedDisplay = await page.Locator(".ss-status-pill").First.EvaluateAsync<string>(
                    "el => window.getComputedStyle(el).display");
                var computedGap = await page.Locator(".ss-status-pill").First.EvaluateAsync<string>(
                    "el => window.getComputedStyle(el).gap");
                Console.WriteLine($"Computed: display={computedDisplay}, gap={computedGap}");
            }

            // ========== SUMMARY PILLS ==========
            Console.WriteLine("\n=== SUMMARY SECTION ===");
            var summaryBars = await page.Locator(".ss-status-bar").CountAsync();
            Console.WriteLine($".ss-status-bar elements: {summaryBars}");

            var summaryPills = await page.Locator(".ss-status-bar .ss-status-pill").CountAsync();
            Console.WriteLine($"Summary pills (.ss-status-bar .ss-status-pill): {summaryPills}");

            for (int i = 0; i < Math.Min(3, summaryPills); i++)
            {
                var text = await page.Locator(".ss-status-bar .ss-status-pill").Nth(i).TextContentAsync();
                Console.WriteLine($"  Pill {i + 1}: {text?.Trim().Replace("\n", " ")}");
            }

            // ========== SETTINGS TABLE ==========
            Console.WriteLine("\n=== DIAGNOSTIC TABLE ===");
            var hasTable = await page.Locator("table.settings-table").CountAsync() > 0;
            Console.WriteLine($"table.settings-table present: {hasTable}");

            if (hasTable)
            {
                var rows = await page.Locator("table.settings-table tr").CountAsync();
                Console.WriteLine($"Table rows: {rows}");

                // First data row
                if (rows > 0)
                {
                    var firstRowHtml = await page.Locator("table.settings-table tr").First.EvaluateAsync<string>(
                        "el => el.outerHTML.substring(0, 300)");
                    Console.WriteLine($"First row HTML: {firstRowHtml}...");
                }
            }

            // ========== CHECK BADGES ==========
            Console.WriteLine("\n=== STATUS BADGES ===");
            var badges = await page.Locator(".ss-health-sev").CountAsync();
            Console.WriteLine($".ss-health-sev elements: {badges}");

            if (badges > 0)
            {
                var badgeText = await page.Locator(".ss-health-sev").First.TextContentAsync();
                var computedBg = await page.Locator(".ss-health-sev").First.EvaluateAsync<string>(
                    "el => window.getComputedStyle(el).backgroundColor");
                Console.WriteLine($"First badge: {badgeText?.Trim()}");
                Console.WriteLine($"Computed background: {computedBg}");
            }

            // ========== RAW CONCATENATION CHECK ==========
            Console.WriteLine("\n=== RAW CONCATENATION CHECK ===");
            var bodyText = await page.TextContentAsync("body");
            var hasRawOverall = bodyText?.Contains("Overall Status Warning") == true;
            var hasRawChecks = bodyText?.Contains("Checks Executed 2") == true;
            Console.WriteLine($"Raw 'Overall Status Warning': {hasRawOverall}");
            Console.WriteLine($"Raw 'Checks Executed': {hasRawChecks}");

            // ========== CSS ISOLATION ==========
            Console.WriteLine("\n=== CSS ISOLATION ===");
            var rootAttrs = await page.Locator(".settings-card").First.EvaluateAsync<string>(
                "el => Array.from(el.attributes).map(a => a.name + '=\"' + a.value + '\"').join(', ')");
            Console.WriteLine($"Root attributes: {rootAttrs?.Substring(0, 100)}...");

            // ========== CONCLUSION ==========
            Console.WriteLine("\n=== CONCLUSION ===");
            var isStructured = overallPills > 0 && (await page.Locator("table.settings-table").CountAsync() > 0) && badges > 0;
            var hasDefects = hasRawOverall || hasRawChecks;

            if (isStructured && !hasDefects)
                Console.WriteLine("✓ BROWSER RENDERS STRUCTURED PRESENTATION");
            else if (!isStructured && hasDefects)
                Console.WriteLine("✗ BROWSER RENDERS RAW CONCATENATION");
            else
                Console.WriteLine("⚠ MIXED STATE - Possible CSS issue");

            // ========== SCREENSHOT ==========
            Console.WriteLine("\nCapturing screenshot...");
            await page.ScreenshotAsync(new() { Path = "C:\\Users\\ajaan\\maintenance-live-screenshot.png" });
            Console.WriteLine("✓ Screenshot saved to C:\\Users\\ajaan\\maintenance-live-screenshot.png");
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }
}
