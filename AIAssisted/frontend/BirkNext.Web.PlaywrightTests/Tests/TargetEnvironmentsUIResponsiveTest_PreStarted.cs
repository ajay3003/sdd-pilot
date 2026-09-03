using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Tests;

[Collection("Playwright Tests - PreStarted")]
public sealed class TargetEnvironmentsUIResponsiveTest_PreStarted : IAsyncLifetime
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
    [InlineData(860)]
    [InlineData(800)]
    [InlineData(480)]
    public async Task TargetEnvironmentsUI_ResponsiveProof_AtViewport(int width)
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.SetViewportSizeAsync(width, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // Verify Target Environments section loaded
        var sectionButton = page.GetByRole(AriaRole.Button, new() { Name = "Target Environments", Exact = true });
        await sectionButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        // Get metrics
        var pageScrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        var pageClientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
        var hasPageOverflow = pageScrollWidth > pageClientWidth;

        // Check active card exists
        var activeCard = page.Locator(".fa-active-card");
        var activeCardCount = await activeCard.CountAsync();
        activeCardCount.Should().BeGreaterThan(0, "Active target summary card should be present");

        // Check active card content - should have TYPE BADGE + NAME
        var activeCardLeft = activeCard.Locator(".fa-active-card-left");
        var typeBadges = activeCardLeft.Locator(".fa-env-badge");
        var typeBadgeCount = await typeBadges.CountAsync();
        typeBadgeCount.Should().Be(1, "Active card left should have exactly one type badge");

        var nameSpan = activeCardLeft.Locator(".fa-active-card-name");
        var nameCount = await nameSpan.CountAsync();
        nameCount.Should().Be(1, "Active card left should have name");

        // Verify no redundant context label
        var contextLabel = activeCardLeft.Locator(".fa-active-card-context");
        var contextCount = await contextLabel.CountAsync();
        contextCount.Should().Be(0, "Active card should not have redundant context label");

        // Check tab container
        var tabContainer = page.Locator(".fa-section-tabs");
        var tabContainerCount = await tabContainer.CountAsync();
        tabContainerCount.Should().BeGreaterThan(0, "Tab container should exist");

        if (tabContainerCount > 0)
        {
            var tabScrollWidth = await tabContainer.EvaluateAsync<int>("el => el.scrollWidth");
            var tabClientWidth = await tabContainer.EvaluateAsync<int>("el => el.clientWidth");
            var hasTabOverflow = tabScrollWidth > tabClientWidth;

            // Count visible tabs
            var tabButtons = tabContainer.Locator("button[role=tab]");
            var tabCount = await tabButtons.CountAsync();

            // Check if tabs are wrapping (multiple rows) by checking flex-wrap CSS
            var flexWrap = await tabContainer.EvaluateAsync<string>("el => getComputedStyle(el).flexWrap");

            if (width < 1000)
            {
                flexWrap.Should().Be("wrap", "At narrow widths, tabs should use flex-wrap");
            }
        }

        // Look for warning if it exists
        var warning = page.Locator(".fa-type-conflict");
        var warningCount = await warning.CountAsync();
        if (warningCount > 0)
        {
            // If warning exists, verify it has proper structure
            var warningText = warning.Locator(".fa-type-conflict-text");
            var warningTextCount = await warningText.CountAsync();
            warningTextCount.Should().Be(1, "Warning should have text section");

            var warningAction = warning.Locator(".fa-type-conflict-action");
            var warningActionCount = await warningAction.CountAsync();
            warningActionCount.Should().Be(1, "Warning should have action button");
        }

        // Final overflow check
        hasPageOverflow.Should().BeFalse($"Page should not overflow horizontally at {width}px (scrollWidth={pageScrollWidth}, clientWidth={pageClientWidth})");

        // Check console errors
        var unexpectedErrors = consoleErrors.Where(IsUnexpectedError).ToList();
        unexpectedErrors.Should().BeEmpty($"No unexpected console errors at {width}px viewport");
        pageErrors.Should().BeEmpty($"No page errors at {width}px viewport");

        await page.CloseAsync();
    }

    [Fact]
    public async Task ActiveTargetSummary_ShowsOnlyTypeAndName()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        var activeCard = page.Locator(".fa-active-card");
        await activeCard.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        // Active card should have type badge and name
        var activeCardLeft = activeCard.Locator(".fa-active-card-left");
        var content = await activeCardLeft.InnerTextAsync();

        // Should contain type and name, not redundant labels
        content.Should().NotBeNullOrEmpty();
        (await activeCardLeft.Locator(".fa-env-badge").CountAsync()).Should().Be(1);
        (await activeCardLeft.Locator(".fa-active-card-name").CountAsync()).Should().Be(1);
        (await activeCardLeft.Locator(".fa-active-card-context").CountAsync()).Should().Be(0);

        await page.CloseAsync();
    }

    [Fact]
    public async Task SelectedDetailHeader_ShowsNameAndActiveIndicator()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // Find and click the first target to select it
        var targetRows = page.Locator("[class*='target-environment'][class*='row'], .fa-environment-list button");
        var rowCount = await targetRows.CountAsync();
        if (rowCount > 0)
        {
            await targetRows.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Detail header should show name and potentially Active badge
            var detailHeader = page.Locator(".fa-detail-header-left");
            var detailHeaderCount = await detailHeader.CountAsync();
            if (detailHeaderCount > 0)
            {
                // Should have name
                var nameSpan = detailHeader.Locator(".fa-detail-name");
                var nameCount = await nameSpan.CountAsync();
                nameCount.Should().BeGreaterThan(0, "Detail header should show target name");

                // Should not have redundant type badge in header
                var typeBadges = detailHeader.Locator(".fa-env-badge");
                var typeBadgeCount = await typeBadges.CountAsync();
                typeBadgeCount.Should().Be(0, "Detail header should not duplicate type badge");
            }
        }

        await page.CloseAsync();
    }

    [Fact]
    public async Task Warning_RendersWithCorrectWording_And_ActionButton()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        var warning = page.Locator(".fa-type-conflict");
        var warningCount = await warning.CountAsync();

        if (warningCount > 0)
        {
            // Verify wording: "Stored type:" and "Detected type:"
            var warningText = await warning.InnerTextAsync();
            warningText.Should().Contain("Stored type:", "Warning should show stored type");
            warningText.Should().Contain("Detected type:", "Warning should show detected type");

            // Verify action button
            var actionButton = warning.Locator(".fa-type-conflict-action");
            var actionButtonCount = await actionButton.CountAsync();
            actionButtonCount.Should().Be(1, "Warning should have action button");

            var buttonText = await actionButton.InnerTextAsync();
            buttonText.Should().Contain("Review", "Action button should mention review");
        }

        await page.CloseAsync();
    }

    [Theory]
    [InlineData("General")]
    [InlineData("Target Application")]
    [InlineData("Security Expectations")]
    [InlineData("Diagnostics")]
    public async Task TabActivation_WorksCorrectlyAfterWrap(string tabName)
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(860, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        // Select a target first
        var targetRows = page.Locator("[class*='target-environment'][class*='row'], .fa-environment-list button");
        var rowCount = await targetRows.CountAsync();
        if (rowCount > 0)
        {
            await targetRows.First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Click the tab
            var tabButton = page.GetByRole(AriaRole.Tab, new() { Name = tabName });
            var tabExists = await tabButton.CountAsync() > 0;
            if (tabExists)
            {
                await tabButton.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                // Verify no page overflow
                var pageOverflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
                pageOverflow.Should().BeLessThanOrEqualTo(0, $"Clicking {tabName} should not cause page overflow at 860px");
            }
        }

        await page.CloseAsync();
    }

    [Theory]
    [InlineData(1440, "desktop")]
    [InlineData(480, "mobile")]
    public async Task KeyboardNavigation_IsAccessible(int width, string context)
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.SetViewportSizeAsync(width, 900);
        await page.GotoAsync(
            $"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        var sectionButton = page.GetByRole(AriaRole.Button, new() { Name = "Target Environments", Exact = true });
        await sectionButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        var detectSettings = page.GetByRole(AriaRole.Button, new() { Name = "Detect settings", Exact = true });
        var reviewDetectSettings = page.GetByRole(AriaRole.Button, new() { Name = "Review Detect Settings", Exact = true });
        var generalTab = page.GetByRole(AriaRole.Tab, new() { Name = "General", Exact = true });
        var diagnosticsTab = page.GetByRole(AriaRole.Tab, new() { Name = "Diagnostics", Exact = true });

        await SelectProfileWithWarningAsync(page, reviewDetectSettings);
        var selectedProfile = page.Locator(".fa-profile-chip-selected");

        (await detectSettings.CountAsync()).Should().Be(1, "Detect settings must have its own accessible name");
        (await reviewDetectSettings.CountAsync()).Should().Be(1, "the warning action must have its distinct accessible name");
        (await generalTab.GetAttributeAsync("aria-selected")).Should().Be("true", "General is the selected tab on initial render");
        (await generalTab.InnerTextAsync()).Should().NotBeNullOrWhiteSpace("the selected tab must have an accessible name");

        if (width == 1440)
        {
            await AssertCanReceiveFocusAsync(selectedProfile, "the selected target control");
            await AssertCanReceiveFocusAsync(detectSettings, "Detect settings");
            await AssertCanReceiveFocusAsync(reviewDetectSettings, "Review Detect Settings");
            await AssertCanReceiveFocusAsync(generalTab, "the General section tab");

            var editEnvironment = page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true });
            await AssertCanReceiveFocusAsync(editEnvironment, "the header Edit Environment action");
        }
        else
        {
            await AssertCanReceiveFocusAsync(reviewDetectSettings, "Review Detect Settings");
            await page.Keyboard.PressAsync("Tab");
            (await IsActiveElementAsync(generalTab)).Should().BeTrue("Tab must leave the warning action and reach the wrapped tab set without a focus trap");

            await AssertCanReceiveFocusAsync(diagnosticsTab, "the wrapped Diagnostics tab");
            (await diagnosticsTab.GetAttributeAsync("aria-selected")).Should().Be("false", "keyboard focus must not be conflated with selected state");
        }

        var focusedBeforeTab = await page.EvaluateAsync<string>("() => document.activeElement?.outerHTML ?? ''");
        await page.Keyboard.PressAsync("Tab");
        var focusedAfterTab = await page.EvaluateAsync<string>("() => document.activeElement?.outerHTML ?? ''");
        focusedAfterTab.Should().NotBe(focusedBeforeTab, $"Tab focus must advance without a focus trap at {width}px ({context})");

        var pageScrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        var pageClientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
        Console.WriteLine($"KEYBOARD_METRICS width={width} scrollWidth={pageScrollWidth} clientWidth={pageClientWidth} overflow={pageScrollWidth > pageClientWidth}");
        (pageScrollWidth > pageClientWidth).Should().BeFalse($"Focusing controls must not introduce page overflow at {width}px");

        consoleErrors.Where(IsUnexpectedError).Should().BeEmpty($"No unexpected console errors during keyboard proof at {width}px");
        pageErrors.Should().BeEmpty($"No page errors during keyboard proof at {width}px");

        await page.CloseAsync();
    }

    private static async Task AssertCanReceiveFocusAsync(ILocator locator, string description)
    {
        await locator.FocusAsync();
        (await IsActiveElementAsync(locator)).Should().BeTrue($"{description} must be keyboard-focusable");
    }

    private static Task<bool> IsActiveElementAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("element => element === document.activeElement");

    private static async Task SelectProfileWithWarningAsync(IPage page, ILocator warningAction)
    {
        if (await warningAction.CountAsync() > 0)
            return;

        var profiles = page.Locator(".fa-profile-chip");
        for (var index = 0; index < await profiles.CountAsync(); index++)
        {
            await profiles.Nth(index).ClickAsync();
            if (await warningAction.CountAsync() > 0)
                return;
        }
    }

    private static bool IsUnexpectedError(string errorMessage)
    {
        // Filter out known safe errors
        if (errorMessage.Contains("__react_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (errorMessage.Contains("css", StringComparison.OrdinalIgnoreCase))
            return false;
        if (errorMessage.Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
