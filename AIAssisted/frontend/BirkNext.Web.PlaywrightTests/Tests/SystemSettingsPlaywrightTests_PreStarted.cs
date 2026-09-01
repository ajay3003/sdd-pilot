using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

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

    [Theory]
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

        var root = page.Locator(".frontend-quality-engine-settings");
        await root.Locator(".dev-diag-section").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });

        (await root.Locator(".dev-diag-section").CountAsync()).Should().Be(4);
        (await root.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).CountAsync()).Should().Be(1);
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

    [Fact]
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
        await page.Locator(".frontend-quality-engine-settings .dev-diag-section").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Settings", Exact = true }).ClickAsync();
        page.Url.Should().EndWith("section=frontend-quality-engines");
        (await page.Locator(".frontend-quality-engine-settings input[type=checkbox]").CountAsync()).Should().Be(4);
        var nonLayer2Inputs = await page.Locator(".frontend-quality-engine-settings tr")
            .EvaluateAllAsync<int>("rows => rows.filter(row => row.querySelector('td')?.textContent.trim() !== 'System setting').reduce((count, row) => count + row.querySelectorAll('input').length, 0)");
        nonLayer2Inputs.Should().Be(0);
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

    [Theory]
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

        var root = page.Locator("text=Maintenance").Locator("../..").First;
        await root.Locator(".dev-diag-section").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });

        // Verify diagnostic section contains table
        (await root.Locator(".settings-table").CountAsync()).Should().BeGreaterThan(0);

        // Verify rows contain Database Reset and Database Mode with their values
        var resetRow = root.Locator("tr:has-text('Database Reset')");
        (await resetRow.CountAsync()).Should().Be(1);
        (await resetRow.Locator("td:has-text('Allowed')").CountAsync()).Should().Be(1);
        (await resetRow.Locator(".ss-health-sev").CountAsync()).Should().Be(1);

        var modeRow = root.Locator("tr:has-text('Database Mode')");
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

    private async Task GoAsync(IPage page, string section) =>
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section={section}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

    private static bool IsSystemSettingsConsoleError(string error) =>
        !error.Contains("WorkspacePersistenceApiService", StringComparison.Ordinal);
}
