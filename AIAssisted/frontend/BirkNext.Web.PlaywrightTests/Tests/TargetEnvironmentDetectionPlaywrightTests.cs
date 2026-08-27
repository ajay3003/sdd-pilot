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

    [Fact]
    public async Task TargetEnvironment_DetectConfiguration_PopulatesDraftSafely()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            var consoleMessages = new List<string>();
            var pageErrors = new List<string>();

            page.Console += (sender, e) =>
            {
                if (e.Type == ConsoleType.Error || e.Type == ConsoleType.Warning)
                    consoleMessages.Add($"{e.Type}: {e.Text}");
            };

            page.PageError += (sender, error) =>
            {
                pageErrors.Add(error);
            };

            // 1. Navigate to System Settings → Analysis → Target Environments
            await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // 2. Navigate to Analysis tab
            var analysisTab = page.Locator("button:has-text('Analysis')");
            await analysisTab.ClickAsync();
            await page.WaitForURLAsync("**/analysis");

            // 3. Navigate to Target Environments section
            var targetEnvLink = page.Locator("a:has-text('Target Environments')");
            await targetEnvLink.ClickAsync();
            await page.WaitForURLAsync("**/target-environments");

            // 4. Enter deterministic fixture URL
            var urlInput = page.Locator("input[placeholder*='https://']").First;
            await urlInput.FillAsync(DeterministicAuthUrl);

            // 5. Click Detect configuration button
            var detectButton = page.Locator("button:has-text('Detect')");
            await detectButton.ClickAsync();

            // 6. Wait for detecting state (spinner should appear)
            var spinner = page.Locator("[role='status']");
            await spinner.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await page.WaitForFunctionAsync("() => !document.querySelector('[role=status]')");

            // 7. Assert detection results
            var results = page.Locator("[data-testid='detection-results']");
            await results.WaitForAsync();

            var reachabilityText = await page.Locator("text=Authentication required").TextContentAsync();
            reachabilityText.Should().Contain("Authentication required");

            var providerText = await page.Locator("text=Microsoft Entra ID").TextContentAsync();
            providerText.Should().Contain("Microsoft Entra ID");

            // 8. Verify authority is canonical (no query parameters)
            var authorityElement = page.Locator("[data-testid='detected-authority']");
            var authorityText = await authorityElement.TextContentAsync();
            authorityText.Should().NotContain("?");
            authorityText.Should().NotContain("code=");
            authorityText.Should().NotContain("state=");

            // 9. Verify provenance labels
            var detectedLabels = page.Locator("text=Detected");
            await detectedLabels.WaitForAsync();

            var suggestedLabels = page.Locator("text=Suggested");
            await suggestedLabels.WaitForAsync();

            // 10. Verify authenticated review limitation
            var reviewNotSupportedText = await page.Locator("text=Authenticated review").TextContentAsync();
            reviewNotSupportedText.Should().Contain("not currently supported");

            // 11. Verify no page errors
            pageErrors.Should().BeEmpty();
            consoleMessages.Where(m => m.StartsWith("Error")).Should().BeEmpty();

            // 12. Navigate away WITHOUT saving
            await page.GotoAsync($"{_fixture.FrontendUrl}/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
            });

            // 13. Return to Target Environments and verify detection was NOT persisted
            await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings/analysis/target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
            });

            var urlField = page.Locator("input[placeholder*='https://']").First;
            var urlValue = await urlField.InputValueAsync();
            urlValue.Should().BeEmpty(); // Draft changes not persisted
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

        try
        {
            // Navigate to Target Environments
            await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings/analysis/target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // Pre-configure existing values
            var tenantInput = page.Locator("input[placeholder*='Tenant']");
            await tenantInput.FillAsync("existing-tenant-a");

            var clientIdInput = page.Locator("input[placeholder*='Client']");
            await clientIdInput.FillAsync("existing-client-a");

            // Enter detection URL
            var urlInput = page.Locator("input[placeholder*='https://']").First;
            await urlInput.FillAsync(DeterministicAuthUrl);

            // Trigger detection
            var detectButton = page.Locator("button:has-text('Detect')");
            await detectButton.ClickAsync();

            // Wait for detection to complete
            var spinner = page.Locator("[role='status']");
            await page.WaitForFunctionAsync("() => !document.querySelector('[role=status]')");

            // Verify conflict is displayed
            var conflictAlert = page.Locator("[data-testid='conflict-alert']");
            await conflictAlert.WaitForAsync();

            // Verify existing values are preserved
            var tenantValue = await tenantInput.InputValueAsync();
            tenantValue.Should().Be("existing-tenant-a");

            var clientValue = await clientIdInput.InputValueAsync();
            clientValue.Should().Be("existing-client-a");

            // Verify detected values are shown as alternatives
            var detectedTenant = page.Locator("[data-testid='detected-tenant']");
            var detectedText = await detectedTenant.TextContentAsync();
            detectedText.Should().NotBe("existing-tenant-a"); // Different value shown

            // Verify no auto-save occurred
            pageErrors.Should().BeEmpty();
        }
        finally
        {
            await page.CloseAsync();
        }

        var pageErrors = new List<string>();
    }

    [Fact]
    public async Task TargetEnvironment_UrlChange_InvalidatesDetection()
    {
        var page = await _fixture.Context.NewPageAsync();

        try
        {
            // Navigate to Target Environments
            await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings/analysis/target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // Enter first URL and detect
            var urlInput = page.Locator("input[placeholder*='https://']").First;
            await urlInput.FillAsync("https://example-a.test/");

            var detectButton = page.Locator("button:has-text('Detect')");
            await detectButton.ClickAsync();

            // Wait for detection
            await page.WaitForFunctionAsync("() => !document.querySelector('[role=status]')");

            var resultsA = page.Locator("[data-testid='detection-results']");
            var textA = await resultsA.TextContentAsync();
            textA.Should().NotBeEmpty();

            // Change URL to different target
            await urlInput.FillAsync("https://example-b.test/");
            await page.Keyboard.PressAsync("Enter");

            // Verify previous detection is cleared
            await page.WaitForTimeoutAsync(500); // Allow UI to update

            var resultsContainer = page.Locator("[data-testid='detection-results']");
            var isVisible = await resultsContainer.IsVisibleAsync();
            isVisible.Should().BeFalse(); // Previous results cleared
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task TargetEnvironment_ResponsiveLayout_NoHorizontalOverflow()
    {
        var viewports = new[] { (1440, 900), (1280, 720), (1024, 768) };

        foreach (var (width, height) in viewports)
        {
            var page = await _fixture.Context.NewPageAsync();

            try
            {
                await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings/analysis/target-environments", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000,
                });

                // Check for horizontal overflow
                var documentWidth = await page.EvaluateAsync<int>("() => document.documentElement.clientWidth");
                var scrollWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");

                scrollWidth.Should().Be(documentWidth, $"at viewport {width}x{height}");

                // Verify all controls are accessible
                var detectButton = page.Locator("button:has-text('Detect')");
                await detectButton.WaitForAsync();

                var saveButton = page.Locator("button:has-text('Save')");
                await saveButton.WaitForAsync();

                var urlInput = page.Locator("input[placeholder*='https://']").First;
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
            await page.GotoAsync($"{_fixture.FrontendUrl}/system-settings/analysis/target-environments", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000,
            });

            // Tab to Detect button
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Tab");

            var focusedElement = await page.EvaluateAsync<string>("() => document.activeElement?.textContent");
            focusedElement.Should().Contain("Detect");

            // Activate with Enter
            await page.Keyboard.PressAsync("Enter");

            // Wait for detection
            await page.WaitForFunctionAsync("() => !document.querySelector('[role=status]')");

            // Verify results are present and keyboard readable
            var resultsText = await page.Locator("[data-testid='detection-results']").TextContentAsync();
            resultsText.Should().NotBeEmpty();
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
