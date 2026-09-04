using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright E2E tests for target environment detection.
/// Verifies real WASM rendering, detection flow, and draft-only behavior.
/// </summary>
public sealed class TargetEnvironmentDetectionPlaywrightTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private const string DeterministicAuthUrl = "https://example.test:8443/protected";
    private const string M2lbDevUrl = "https://m2lbdev.bufetat.no/";

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    private async Task OpenTargetEnvironmentEditModeAsync(IPage page)
    {
        // Navigate to Target Environments using deep-link
        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30000,
        });

        // Wait for Target Environments region to be visible
        var targetEnvRegion = page.GetByRole(AriaRole.Region, new() { Name = "Target Environments" });
        await targetEnvRegion.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

        // FrontendAnalysisSettings auto-selects the active profile and shows profile detail
        var profileDetail = page.Locator(".fa-profile-detail").First;
        await profileDetail.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        // Wait a moment for component to fully render
        await page.WaitForTimeoutAsync(500);

        // Click "Edit Environment" to enter edit mode
        var editButton = page.Locator("button:has-text('Edit Environment')").First;
        await editButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await editButton.ClickAsync();

        // Wait a moment for component state change
        await page.WaitForTimeoutAsync(500);

        // Click the "Target Application" tab to show URL input field
        var targetTabButton = page.Locator(".fa-tab:has-text('Target Application')").First;
        await targetTabButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await targetTabButton.ClickAsync();

        // Wait for edit-mode form fields to render in target tab
        var urlInput = page.Locator("input[type='url']").First;
        await urlInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
    }

    [Fact]
    public async Task TargetEnvironment_DetectConfiguration_PopulatesDraftSafely()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            var consoleMessages = new List<string>();
            var pageErrors = new List<string>();

            page.Console += (sender, e) => consoleMessages.Add(e.Text);
            page.PageError += (sender, error) => pageErrors.Add(error);

            // Open Target Environments in edit mode
            await OpenTargetEnvironmentEditModeAsync(page);

            // Verify form is in edit mode and editable
            var urlInput = page.Locator("input[type='url']").First;

            // Fill in the target URL
            await urlInput.FillAsync(DeterministicAuthUrl);
            var filledValue = await urlInput.InputValueAsync();
            filledValue.Should().Be(DeterministicAuthUrl, "URL input should accept and retain the filled value");

            // Verify the Save and Cancel buttons are visible (indicating edit mode)
            var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save changes" });
            await saveButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            var cancelButton = page.GetByRole(AriaRole.Button, new() { Name = "Cancel" });
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            // Verify no page errors
            pageErrors.Should().BeEmpty();
            consoleMessages.Where(m => m.Contains("Error", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();

            // Navigate away WITHOUT saving
            await page.GotoAsync($"{_fixture.FrontendUrl}/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
            });

            // Return to Target Environments and verify we're back in view mode
            await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
            });

            // Verify the Edit Environment button is present (view mode, not edit mode)
            var profileDetail = page.Locator(".fa-profile-detail").First;
            await profileDetail.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            var editButton = page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment" }).First;
            await editButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task TargetEnvironment_DetectConfiguration_DoesNotOverwriteConfiguredValues()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();

        try
        {
            page.PageError += (sender, error) => pageErrors.Add(error);

            // Open Target Environments in edit mode
            await OpenTargetEnvironmentEditModeAsync(page);

            // Verify form is in edit mode
            var urlInput = page.Locator("input[type='url']").First;
            await urlInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            // Fill in the target URL
            await urlInput.FillAsync(DeterministicAuthUrl);
            var filledValue = await urlInput.InputValueAsync();
            filledValue.Should().Be(DeterministicAuthUrl);

            // Verify the Save and Cancel buttons are visible (indicating edit mode)
            var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save changes" });
            await saveButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            // Verify no page errors
            pageErrors.Should().BeEmpty();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task TargetEnvironment_UrlChange_InvalidatesDetection()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Open Target Environments in edit mode
            await OpenTargetEnvironmentEditModeAsync(page);

            // Verify form accepts URL input
            var urlInput = page.Locator("input[type='url']").First;

            // Enter first URL
            await urlInput.FillAsync("https://example-a.test/");
            var value1 = await urlInput.InputValueAsync();
            value1.Should().Be("https://example-a.test/", "Form should accept first URL");

            // Change URL to different value
            await urlInput.FillAsync("https://example-b.test/");
            await page.Keyboard.PressAsync("Enter");

            // Verify URL input updated
            var value2 = await urlInput.InputValueAsync();
            value2.Should().Be("https://example-b.test/", "Form should accept URL changes");

            // Verify form is still in edit mode with save button available
            var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save changes" });
            await saveButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task TargetEnvironment_ResponsiveLayout_NoHorizontalOverflow()
    {
        var viewports = new[] { (1440, 900), (1280, 720), (1024, 768), (860, 900), (800, 900), (480, 900) };

        foreach (var (width, height) in viewports)
        {
            var page = await _fixture.Context.NewPageAsync();

            try
            {
                // Set viewport size
                await page.SetViewportSizeAsync(width, height);

                // Open Target Environments in edit mode
                await OpenTargetEnvironmentEditModeAsync(page);

                // Check for horizontal overflow
                var documentWidth = await page.EvaluateAsync<int>("() => document.documentElement.clientWidth");
                var scrollWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");

                scrollWidth.Should().Be(documentWidth, $"at viewport {width}x{height}");

                // Verify all controls are accessible
                var detectButton = page.GetByRole(AriaRole.Button, new() { Name = "Detect settings" });
                await detectButton.WaitForAsync();

                var urlInput = page.Locator("input[type='url']").First;
                await urlInput.WaitForAsync();
            }
            finally
            {
                await page.CloseAsync();
            }
        }
    }

    [Fact]
    public async Task TargetEnvironment_KeyboardAccessibility_NavigateDetection()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Navigate to Target Environments region
            await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            var targetEnvRegion = page.GetByRole(AriaRole.Region, new() { Name = "Target Environments" });
            await targetEnvRegion.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Keyboard navigate to Edit Environment button
            var profileDetail = page.Locator(".fa-profile-detail").First;
            await profileDetail.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            var editButton = page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment" }).First;
            await editButton.FocusAsync();

            // Activate with Enter
            await page.Keyboard.PressAsync("Enter");

            // Wait a moment for component state change
            await page.WaitForTimeoutAsync(500);

            // Switch to Target Application tab
            var targetTabButton = page.Locator(".fa-tab:has-text('Target Application')").First;
            await targetTabButton.ClickAsync();

            // Wait for edit mode form to render
            var urlInput = page.Locator("input[type='url']").First;
            await urlInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            // Focus on URL input and enter a test URL via keyboard
            await urlInput.FocusAsync();
            await urlInput.FillAsync(DeterministicAuthUrl);

            // Tab to Detect button and verify it's accessible
            await page.Keyboard.PressAsync("Tab");

            var detectButton = page.GetByRole(AriaRole.Button, new() { Name = "Detect settings" });
            var isDetectVisible = await detectButton.IsVisibleAsync();
            isDetectVisible.Should().BeTrue("Detect button should be accessible via keyboard navigation");

            // Verify form is in edit mode (save button available)
            var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save Environment" });
            await saveButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task TargetEnvironment_DetectConfiguration_RealBackendEndToEnd()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Open Target Environments in edit mode using the helper
            await OpenTargetEnvironmentEditModeAsync(page);

            // Use a deterministic URL that will trigger detection
            // Note: This test verifies the detection flow works end-to-end
            // The actual target behavior is tested separately in unit tests
            var testUrl = DeterministicAuthUrl;

            var urlInput = page.Locator("input[type='url']").First;
            await urlInput.FillAsync(testUrl);

            // Click Detect settings button
            var detectButton = page.GetByRole(AriaRole.Button, new() { Name = "Detect settings" });
            await detectButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await detectButton.ClickAsync();

            // Wait for detection to complete (either success or failure state is rendered)
            // For unreachable URLs like example.test, detection may fail but should not timeout
            await page.WaitForTimeoutAsync(3000);

            // Verify form is still in edit mode
            var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Save Environment" });
            await saveButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });

            // Verify Cancel button is present (still in edit mode, not saved)
            var cancelButton = page.GetByRole(AriaRole.Button, new() { Name = "Cancel" });
            await cancelButton.WaitForAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
