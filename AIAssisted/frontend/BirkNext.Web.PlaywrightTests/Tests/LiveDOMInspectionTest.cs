using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

[Collection("Playwright Tests - PreStarted")]
public sealed class LiveDOMInspectionTest : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Inspect_Live_Maintenance_DOM_For_Structured_Presentation()
    {
        var page = await _fixture.Context.NewPageAsync();

        // Navigate to Maintenance
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=maintenance",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // Wait for content
        await page.WaitForSelectorAsync(".settings-card", new() { Timeout = 10000 });

        // Check for structured presentation elements
        var hasSsStatusPill = await page.Locator(".ss-status-pill").CountAsync() > 0;
        var hasSettingsTable = await page.Locator("table.settings-table").CountAsync() > 0;
        var hasHealthSev = await page.Locator(".ss-health-sev").CountAsync() > 0;
        var hasDevDiagSection = await page.Locator(".dev-diag-section").CountAsync() > 0;

        // Check for raw concatenation
        var bodyContent = await page.ContentAsync();
        var hasRawOverallStatus = bodyContent.Contains("Overall Status Warning") || bodyContent.Contains("Overall Status Pass");
        var hasRawChecksExecuted = bodyContent.Contains("Checks Executed 2");

        // Report findings
        var findings = new
        {
            StructuredElements = new
            {
                HasSsStatusPill = hasSsStatusPill,
                HasSettingsTable = hasSettingsTable,
                HasHealthSev = hasHealthSev,
                HasDevDiagSection = hasDevDiagSection
            },
            RawConcatenation = new
            {
                HasRawOverallStatus = hasRawOverallStatus,
                HasRawChecksExecuted = hasRawChecksExecuted
            }
        };

        // Write to console for manual inspection
        System.Console.WriteLine($"\n=== LIVE DOM INSPECTION RESULTS ===");
        System.Console.WriteLine($"Structured Elements Present:");
        System.Console.WriteLine($"  .ss-status-pill: {hasSsStatusPill}");
        System.Console.WriteLine($"  table.settings-table: {hasSettingsTable}");
        System.Console.WriteLine($"  .ss-health-sev: {hasHealthSev}");
        System.Console.WriteLine($"  .dev-diag-section: {hasDevDiagSection}");
        System.Console.WriteLine($"\nRaw Concatenation Text Found:");
        System.Console.WriteLine($"  'Overall Status Warning/Pass': {hasRawOverallStatus}");
        System.Console.WriteLine($"  'Checks Executed 2': {hasRawChecksExecuted}");

        // Determine status
        var isStructured = hasSsStatusPill && hasSettingsTable && hasHealthSev && hasDevDiagSection;
        var hasRawDefects = hasRawOverallStatus || hasRawChecksExecuted;

        if (isStructured && !hasRawDefects)
        {
            System.Console.WriteLine("\n✓ RESULT: DOM shows structured presentation, no raw concatenation");
        }
        else if (!isStructured && hasRawDefects)
        {
            System.Console.WriteLine("\n✗ RESULT: DOM shows raw/legacy presentation");
        }
        else
        {
            System.Console.WriteLine("\n⚠ RESULT: Mixed state (likely CSS issue)");
        }

        // Assertions
        hasSsStatusPill.Should().BeTrue("ss-status-pill element should be present");
        hasSettingsTable.Should().BeTrue("settings-table should be present");
        hasHealthSev.Should().BeTrue("ss-health-sev badge should be present");
        hasDevDiagSection.Should().BeTrue("dev-diag-section should be present");
        hasRawOverallStatus.Should().BeFalse("raw 'Overall Status X' concatenation should not exist");
        hasRawChecksExecuted.Should().BeFalse("raw 'Checks Executed' concatenation should not exist");

        await page.CloseAsync();
    }
}
