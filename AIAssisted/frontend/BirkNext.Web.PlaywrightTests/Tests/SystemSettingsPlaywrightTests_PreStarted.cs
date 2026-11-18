using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// System Settings layout and edit-mode ownership.
///
/// History (Playwright regression recovery, 2026-11-17): the "frontend-quality-engines" section became Frontend Testing Capabilities; the
/// per-engine installation switches (the four engine sections) moved into its "Technical details" disclosure, which opens itself in edit
/// mode; "Refresh status" became the page-level "Refresh local capabilities"; the editable layer row is "Deployment setting" (was "System
/// setting"); and the Maintenance rows are "Reset capability" / "Database mode" (were "Database Reset" / "Database Mode"). The maintenance test
/// never clicks "Reset data…".
/// </summary>
[Collection("Playwright Tests - PreStarted")]
public sealed class SystemSettingsPlaywrightTests_PreStarted : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [BrowserTheory]
    [InlineData(1440)]
    [InlineData(1280)]
    public async Task Frontend_quality_engines_uses_compact_two_column_system_settings_layout(int width)
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        await page.SetViewportSizeAsync(width, 1000);
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=frontend-quality-engines",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        await OpenEngineSwitchesAsync(page);
        var root = page.Locator(".frontend-quality-engine-settings");

        (await root.Locator(".dev-diag-section").CountAsync()).Should().Be(4);
        (await page.GetByTestId("ftc-refresh").CountAsync()).Should().Be(1, "one refresh action for the local capabilities");
        (await root.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).CountAsync()).Should().Be(0, "the engine table has no second refresh");
        var columns = await root.Locator(".dev-diag-subgrid").EvaluateAsync<int>("el => getComputedStyle(el).gridTemplateColumns.split(' ').length");
        columns.Should().Be(2);
        var badgeWidths = await root.Locator("tr:has(td:text-is('Effective availability')) .settings-badge")
            .EvaluateAllAsync<float[]>("els => els.map(el => el.getBoundingClientRect().width)");
        badgeWidths.Should().HaveCount(4).And.OnlyContain(value => value < 120);
        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - innerWidth");
        overflow.Should().BeLessThanOrEqualTo(0);
        consoleErrors.Where(IsSystemSettingsConsoleError).Should().BeEmpty();
        await page.CloseAsync();
    }

    [BrowserFact]
    public async Task Parent_edit_mode_is_pane_capability_aware_and_edits_fqe_in_place()
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };

        await GoAsync(page, "feature-visibility");
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Settings", Exact = true }).ClickAsync();
        page.Url.Should().EndWith("section=feature-visibility");
        (await page.GetByRole(AriaRole.Button, new() { Name = "Save Settings", Exact = true }).CountAsync()).Should().Be(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        await GoAsync(page, "frontend-quality-engines");
        await page.GetByTestId("ftc").WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });
        // The capability summary checks each engine on this computer first (Lighthouse readiness starts Node and Chromium: ~17 s with the
        // runner installed); the installation switches are listed once that check has answered.
        await Assertions.Expect(page.GetByTestId("ftc-summary")).Not.ToContainTextAsync("Checking local capabilities", new() { Timeout = 120000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Settings", Exact = true }).ClickAsync();
        page.Url.Should().EndWith("section=frontend-quality-engines");
        // Edit mode opens Technical details, where the installation switches live.
        await Assertions.Expect(page.GetByTestId("ftc-technical")).ToHaveAttributeAsync("open", "");
        await Assertions.Expect(page.Locator(".frontend-quality-engine-settings input[type=checkbox]")).ToHaveCountAsync(4);
        var nonLayer2Inputs = await page.Locator(".frontend-quality-engine-settings tr")
            .EvaluateAllAsync<int>("rows => rows.filter(row => row.querySelector('td')?.textContent.trim() !== 'Deployment setting').reduce((count, row) => count + row.querySelectorAll('input').length, 0)");
        nonLayer2Inputs.Should().Be(0, "only the Deployment setting layer is editable here");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        foreach (var section in new[] { "general", "platform", "ai", "maintenance", "system-diagnostics" })
        {
            await GoAsync(page, section);
            (await page.GetByRole(AriaRole.Button, new() { Name = "Edit Settings", Exact = true }).CountAsync()).Should().Be(0);
        }

        await GoAsync(page, "target-environments");
        (await page.GetByRole(AriaRole.Button, new() { Name = "Edit Settings", Exact = true }).CountAsync()).Should().Be(0);
        var childEdit = page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true });
        await childEdit.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        (await childEdit.CountAsync()).Should().Be(1);
        consoleErrors.Where(IsSystemSettingsConsoleError).Should().BeEmpty();
        await page.CloseAsync();
    }

    [BrowserTheory]
    [InlineData(1440)]
    [InlineData(1280)]
    public async Task Maintenance_diagnostic_result_uses_table_structure_with_associated_values_and_badges(int width)
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        await page.SetViewportSizeAsync(width, 1000);
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=maintenance",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // The Maintenance diagnostic section (the old "text=Maintenance" → ../.. root matched the settings navigation first).
        var root = page.Locator(".dev-diag-section").Filter(new() { HasText = "Reset capability and database mode" });
        await root.WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });

        // Verify diagnostic section contains table
        (await root.Locator(".settings-table").CountAsync()).Should().BeGreaterThan(0);

        // Verify rows contain Database Reset and Database Mode with their values
        var resetRow = root.Locator("tr:has-text('Reset capability')");
        (await resetRow.CountAsync()).Should().Be(1);
        (await resetRow.Locator("td:has-text('Allowed')").CountAsync()).Should().Be(1);
        (await resetRow.Locator(".ss-health-sev").CountAsync()).Should().Be(1);

        var modeRow = root.Locator("tr:has-text('Database mode')");
        (await modeRow.CountAsync()).Should().Be(1);
        (await modeRow.Locator("td:has-text('Local')").CountAsync()).Should().Be(1);
        (await modeRow.Locator(".ss-health-sev").CountAsync()).Should().Be(1);

        // Verify status badges are compact (not full page width)
        var badges = root.Locator(".ss-health-sev");
        var badgeWidth = await badges.First.EvaluateAsync<float>("el => el.getBoundingClientRect().width");
        badgeWidth.Should().BeLessThan(120);

        // Verify no horizontal overflow
        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - innerWidth");
        overflow.Should().BeLessThanOrEqualTo(0);

        // Verify no console errors
        consoleErrors.Where(IsSystemSettingsConsoleError).Should().BeEmpty();
        await page.CloseAsync();
    }

    /// <summary>Opens Frontend Testing Capabilities once its local status has loaded, then its Technical details (the engine switches).</summary>
    private static async Task OpenEngineSwitchesAsync(IPage page)
    {
        await page.GetByTestId("ftc").WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });
        await Assertions.Expect(page.GetByTestId("ftc-summary")).Not.ToContainTextAsync("Checking local capabilities", new() { Timeout = 120000 });
        await page.GetByTestId("ftc-technical").Locator("summary").ClickAsync();
        await page.Locator(".frontend-quality-engine-settings .dev-diag-section").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
    }

    /// <summary>Opens a section and waits until the settings header has rendered (its actions are counted afterwards, never before).</summary>
    private async Task GoAsync(IPage page, string section)
    {
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section={section}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator(".btn-copy-diag").WaitForAsync(new LocatorWaitForOptions { Timeout = 120000 });
    }

    private static bool IsSystemSettingsConsoleError(string error) =>
        !error.Contains("WorkspacePersistenceApiService", StringComparison.Ordinal);
}
