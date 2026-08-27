#!/usr/bin/env dotnet-script
// Direct Target Environments Deep Link Verification

#r "nuget: Microsoft.Playwright, 1.45.0"

using Microsoft.Playwright;

var playwright = await Playwright.CreateAsync();
var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
var context = await browser.NewContextAsync();
var page = await context.NewPageAsync();

try
{
    Console.WriteLine("=== DIRECT DEEP-LINK TEST ===\n");

    // Step 1: Direct navigation with query parameter
    Console.WriteLine("1. Navigating to /admin/system-settings?section=target-environments");
    await page.GotoAsync("http://localhost:5173/admin/system-settings?section=target-environments",
        new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 10000 });

    Console.WriteLine($"   URL: {page.Url}");

    // Step 2: Check for Target Environments region
    Console.WriteLine("\n2. Checking for Target Environments region...");
    var targetEnvRegion = page.GetByRole(AriaRole.Region, new() { Name = "Target Environments" });
    bool isVisible = await targetEnvRegion.IsVisibleAsync();
    Console.WriteLine($"   Region visible: {isVisible}");

    if (isVisible)
    {
        Console.WriteLine("   ✓ Target Environments section rendered!");
    }
    else
    {
        Console.WriteLine("   ✗ Target Environments section NOT visible");

        // Debug: Check what's actually visible
        var regions = await page.Locator("[role='region']").AllAsync();
        Console.WriteLine($"\n   Found {regions.Count} regions:");
        foreach (var region in regions)
        {
            var label = await region.GetAttributeAsync("aria-label");
            Console.WriteLine($"     - {label}");
        }
    }

    // Step 3: Check for General section (should be inactive)
    Console.WriteLine("\n3. Checking General section (should be hidden)...");
    var generalText = page.Locator("text=General").First;
    bool generalVisible = await generalText.IsVisibleAsync();
    Console.WriteLine($"   General visible: {generalVisible}");
    if (!generalVisible) Console.WriteLine("   ✓ General correctly hidden");

    // Step 4: Check nav item active state
    Console.WriteLine("\n4. Checking nav item active state...");
    var targetEnvNavButton = page.GetByRole(AriaRole.Button, new() { Name = "Target Environments" });
    var ariaCurrentValue = await targetEnvNavButton.GetAttributeAsync("aria-current");
    Console.WriteLine($"   aria-current: {ariaCurrentValue}");
    if (ariaCurrentValue == "page") Console.WriteLine("   ✓ Nav item correctly marked as active");

    // Step 5: Check console errors
    Console.WriteLine("\n5. Checking for console errors...");
    var consoleErrors = new List<string>();
    page.Console += (_, msg) =>
    {
        if (msg.Type == "error")
            consoleErrors.Add(msg.Text);
    };

    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    if (consoleErrors.Any())
    {
        Console.WriteLine($"   ✗ Found {consoleErrors.Count} console errors:");
        foreach (var err in consoleErrors.Take(3))
            Console.WriteLine($"     - {err}");
    }
    else
    {
        Console.WriteLine("   ✓ No console errors");
    }

    // Final result
    Console.WriteLine("\n=== RESULT ===");
    if (isVisible && ariaCurrentValue == "page" && !generalVisible && consoleErrors.Count == 0)
    {
        Console.WriteLine("✓ DIRECT DEEP-LINK TEST: PASSED");
        return 0;
    }
    else
    {
        Console.WriteLine("✗ DIRECT DEEP-LINK TEST: FAILED");
        return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n✗ ERROR: {ex.Message}");
    return 2;
}
finally
{
    await context.CloseAsync();
    await browser.CloseAsync();
}
