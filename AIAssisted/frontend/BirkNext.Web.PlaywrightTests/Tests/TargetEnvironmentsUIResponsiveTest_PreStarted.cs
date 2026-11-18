using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Target Environments layout, identity and keyboard behaviour across widths.
///
/// History (Playwright regression recovery, 2026-11-17): the tests now wait for the selected environment's detail (the section heading appears
/// first, the tabs a moment later); environments are selected through their chips (".fa-environment-list button" no longer exists, so several
/// tests used to pass without asserting anything); the removed Diagnostics tab is replaced by Validation, which carries its validation status;
/// and the type-conflict warning is produced by an environment the test creates and deletes itself instead of being looked for in shared data.
/// </summary>
[Collection("Playwright Tests - PreStarted")]
public sealed class TargetEnvironmentsUIResponsiveTest_PreStarted : IAsyncLifetime
{
    private const string ConflictUrl = "https://app-dev.synthetic.example.test/";
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
    [InlineData(1024)]
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
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        var tabContainer = page.Locator(".fa-section-tabs");
        await tabContainer.WaitForAsync(new() { Timeout = 30000 });

        var activeCard = page.Locator(".fa-active-card");
        (await activeCard.CountAsync()).Should().BeGreaterThan(0, "Active target summary card should be present");
        var activeCardLeft = activeCard.Locator(".fa-active-card-left");
        (await activeCardLeft.Locator(".fa-env-badge").CountAsync()).Should().Be(0, "Active status should not repeat the environment type as a badge");
        (await activeCardLeft.Locator(".fa-active-card-name").CountAsync()).Should().Be(1, "Active card left should have name");
        (await activeCardLeft.Locator(".fa-active-card-context").CountAsync()).Should().Be(0, "Active card should not have redundant context label");

        (await tabContainer.CountAsync()).Should().Be(1, "one tab list for the selected environment");
        (await tabContainer.GetByRole(AriaRole.Tab).CountAsync()).Should().BeGreaterThan(8, "every section is a tab");
        if (width < 1000)
            (await tabContainer.EvaluateAsync<string>("el => getComputedStyle(el).flexWrap")).Should().Be("wrap", "At narrow widths, tabs should use flex-wrap");

        var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
        (scrollWidth > clientWidth).Should().BeFalse($"Page should not overflow horizontally at {width}px (scrollWidth={scrollWidth}, clientWidth={clientWidth})");
        consoleErrors.Where(IsUnexpectedError).Should().BeEmpty($"No unexpected console errors at {width}px viewport");
        pageErrors.Should().BeEmpty($"No page errors at {width}px viewport");
        await page.CloseAsync();
    }

    [BrowserFact]
    public async Task ActiveTargetSummary_ShowsOneIdentityAndUrl()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await TargetEnvironmentSettingsPage.OpenAsync(page);

        var activeCardLeft = page.Locator(".fa-active-card .fa-active-card-left");
        (await activeCardLeft.InnerTextAsync()).Should().NotBeNullOrEmpty();
        (await activeCardLeft.Locator(".fa-env-badge").CountAsync()).Should().Be(0);
        (await activeCardLeft.Locator(".fa-active-card-name").CountAsync()).Should().Be(1);
        (await activeCardLeft.Locator(".fa-active-card-context").CountAsync()).Should().Be(0);
        (await page.Locator(".fa-active-card .fa-active-stat-mono").CountAsync()).Should().Be(1);
        await page.CloseAsync();
    }

    [BrowserFact]
    public async Task SelectedDetailHeader_ShowsNameAndActiveIndicator()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        var activeName = (await page.Locator(".fa-active-card-name").InnerTextAsync()).Trim();

        // A non-active environment: its header names it, with no type badge, and the active environment is unchanged.
        await TargetEnvironmentSettingsPage.SelectAsync(page, "QA");
        var detailHeader = page.Locator(".fa-detail-header-left");
        await Assertions.Expect(detailHeader.Locator(".fa-detail-name")).ToHaveTextAsync("QA");
        (await detailHeader.Locator(".fa-env-badge").CountAsync()).Should().Be(0, "Detail header should not duplicate type badge");
        await Assertions.Expect(page.Locator(".fa-profile-chip-selected")).ToContainTextAsync("QA");
        (await page.Locator(".fa-active-card-name").InnerTextAsync()).Trim().Should().Be(activeName, "selecting is not activating");

        // Exactly one chip carries the Active indicator, and it is the active environment's ("Active", shown in capitals by CSS).
        var activeBadges = page.Locator(".fa-profile-chip .fa-active-badge");
        await Assertions.Expect(activeBadges).ToHaveCountAsync(1);
        await Assertions.Expect(activeBadges).ToHaveTextAsync("Active");
        await Assertions.Expect(page.Locator(".fa-profile-chip").Filter(new() { Has = page.Locator(".fa-active-badge") })).ToContainTextAsync(activeName);
        await page.CloseAsync();
    }

    [BrowserFact]
    public async Task Warning_RendersWithCorrectWording_And_ActionButton()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        var name = SyntheticProjects.NewName("PwConflict");
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.CreateAsync(page, name, "Production", ConflictUrl);
        try
        {
            var warning = page.Locator(".fa-type-conflict");
            await Assertions.Expect(warning).ToHaveCountAsync(1);
            var warningText = await warning.InnerTextAsync();
            warningText.Should().Contain("Stored type: Production", "Warning should show stored type");
            warningText.Should().Contain("Detected type: Development", "Warning should show the type the URL indicates");
            var action = warning.Locator(".fa-type-conflict-action");
            await Assertions.Expect(action).ToHaveTextAsync("Review on Target Application");
            await action.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Target Application", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
        }
        finally
        {
            await TargetEnvironmentSettingsPage.DeleteSelectedAsync(page, name);
            await page.CloseAsync();
        }
    }

    [BrowserTheory]
    [InlineData("General")]
    [InlineData("Target Application")]
    [InlineData("Endpoint Discovery")]
    [InlineData("Validation")]
    public async Task TabActivation_WorksCorrectlyAfterWrap(string tabName)
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(860, 900);
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.SelectAsync(page, "QA");

        await TargetEnvironmentSettingsPage.OpenTabAsync(page, tabName);
        // The environment's own tabs (a tab's content may carry its own tab list, e.g. Endpoint Discovery's evidence views).
        var selected = page.Locator("button.fa-tab[role=tab][aria-selected=true]");
        await Assertions.Expect(selected).ToHaveCountAsync(1);
        (await selected.InnerTextAsync()).Trim().Should().Be(tabName, "exactly the clicked tab is selected");
        var pageOverflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        pageOverflow.Should().BeLessThanOrEqualTo(0, $"Clicking {tabName} should not cause page overflow at 860px");
        await page.CloseAsync();
    }

    [BrowserTheory]
    [InlineData(1440, "desktop")]
    [InlineData(480, "mobile")]
    public async Task KeyboardNavigation_IsAccessible(int width, string context)
    {
        var page = await _fixture.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        page.PageError += (_, error) => pageErrors.Add(error);
        var name = SyntheticProjects.NewName("PwKeyboard");

        await page.SetViewportSizeAsync(width, 900);
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.CreateAsync(page, name, "Production", ConflictUrl);
        try
        {
            var detectSettings = page.GetByRole(AriaRole.Button, new() { Name = "Detect settings", Exact = true });
            var reviewDetectSettings = page.GetByRole(AriaRole.Button, new() { Name = "Review on Target Application", Exact = true });
            var generalTab = page.GetByRole(AriaRole.Tab, new() { Name = "General", Exact = true });
            var validationTab = page.GetByRole(AriaRole.Tab, new() { Name = "Validation", Exact = true });
            var selectedProfile = page.Locator(".fa-profile-chip-selected");

            (await detectSettings.CountAsync()).Should().Be(0, "there is no environment-wide detect; Target Application owns its own check");
            (await reviewDetectSettings.CountAsync()).Should().Be(1, "the warning action must have its distinct accessible name");
            (await generalTab.GetAttributeAsync("aria-selected")).Should().Be("true", "General is the selected tab on initial render");
            (await generalTab.InnerTextAsync()).Should().NotBeNullOrWhiteSpace("the selected tab must have an accessible name");

            if (width == 1440)
            {
                await AssertCanReceiveFocusAsync(selectedProfile, "the selected target control");
                await AssertCanReceiveFocusAsync(reviewDetectSettings, "Review on Target Application");
                await AssertCanReceiveFocusAsync(generalTab, "the General section tab");
                await AssertCanReceiveFocusAsync(page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true }), "the header Edit Environment action");
            }
            else
            {
                await AssertCanReceiveFocusAsync(reviewDetectSettings, "Review on Target Application");
                await page.Keyboard.PressAsync("Tab");
                (await IsActiveElementAsync(generalTab)).Should().BeTrue("Tab must leave the warning action and reach the wrapped tab set without a focus trap");
                await AssertCanReceiveFocusAsync(validationTab, "the wrapped Validation tab");
                (await validationTab.GetAttributeAsync("aria-selected")).Should().Be("false", "keyboard focus must not be conflated with selected state");
            }

            var focusedBeforeTab = await page.EvaluateAsync<string>("() => document.activeElement?.outerHTML ?? ''");
            await page.Keyboard.PressAsync("Tab");
            var focusedAfterTab = await page.EvaluateAsync<string>("() => document.activeElement?.outerHTML ?? ''");
            focusedAfterTab.Should().NotBe(focusedBeforeTab, $"Tab focus must advance without a focus trap at {width}px ({context})");

            var pageScrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
            var pageClientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
            (pageScrollWidth > pageClientWidth).Should().BeFalse($"Focusing controls must not introduce page overflow at {width}px");
            consoleErrors.Where(IsUnexpectedError).Should().BeEmpty($"No unexpected console errors during keyboard proof at {width}px");
            pageErrors.Should().BeEmpty($"No page errors during keyboard proof at {width}px");
        }
        finally
        {
            await TargetEnvironmentSettingsPage.DeleteSelectedAsync(page, name);
            await page.CloseAsync();
        }
    }

    private static async Task AssertCanReceiveFocusAsync(ILocator locator, string description)
    {
        await locator.FocusAsync();
        (await IsActiveElementAsync(locator)).Should().BeTrue($"{description} must be keyboard-focusable");
    }

    private static Task<bool> IsActiveElementAsync(ILocator locator) =>
        locator.EvaluateAsync<bool>("element => element === document.activeElement");

    private static bool IsUnexpectedError(string errorMessage) =>
        !errorMessage.Contains("__react_", StringComparison.OrdinalIgnoreCase)
        && !errorMessage.Contains("css", StringComparison.OrdinalIgnoreCase)
        && !errorMessage.Contains("stylesheet", StringComparison.OrdinalIgnoreCase);
}
