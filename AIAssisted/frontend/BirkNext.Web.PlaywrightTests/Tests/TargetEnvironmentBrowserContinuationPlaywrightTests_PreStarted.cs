using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Tests;

[Collection("Playwright Tests - PreStarted")]
public sealed class TargetEnvironmentBrowserContinuationPlaywrightTests_PreStarted : IAsyncLifetime
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
    [InlineData(860)]
    [InlineData(800)]
    [InlineData(480)]
    [Trait("Category", "PreStarted")]
    public async Task AuthenticationRequired_ClickContinuesThroughHttpAndUpdatesExactSelectedTarget(int width)
    {
        var page = await _fixture.Context.NewPageAsync();
        var continuationCalls = 0;
        string? continuationBody = null;
        string? continuationUrl = null;
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        page.PageError += (_, error) => pageErrors.Add(error);
        await page.SetViewportSizeAsync(width, 950);

        // Synthetic answers for exactly the URL the page asks about (no target is contacted): sign-in is required, and the browser
        // continuation completes detection.
        await page.RouteAsync("**/api/frontend-target/detect", async route =>
        {
            using var request = JsonDocument.Parse(route.Request.PostData ?? "{}");
            var url = request.RootElement.GetProperty("targetUrl").GetString();
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = JsonSerializer.Serialize(new { originalUrl = url, normalizedTargetUrl = url, reachability = "AuthenticationRequired", authenticationRequired = true, success = true, warnings = Array.Empty<string>() }),
            });
        });
        await page.RouteAsync("**/api/frontend-target/continue-in-browser", async route =>
        {
            continuationCalls++;
            continuationBody = route.Request.PostData;
            continuationUrl = route.Request.Url;
            using var request = JsonDocument.Parse(continuationBody ?? "{}");
            var url = request.RootElement.GetProperty("targetUrl").GetString();
            await route.FulfillAsync(new()
            {
                Status = 200, ContentType = "application/json",
                Body = JsonSerializer.Serialize(new
                {
                    state = 2, isActivationReady = true, strategySuggestion = "no-action-needed", detectedUrl = url, isUrlCurrent = true,
                    detectionResponse = new { originalUrl = url, normalizedTargetUrl = url, reachability = "Reachable", authenticationRequired = false, success = true, isActivationReady = true, warnings = Array.Empty<string>() },
                }),
            });
        });

        // Selects QA (not the active environment) and checks it; History (2026-11-17): the summary action is "Check target", the state labels
        // are "Authentication required" / "Detection complete" (were "Auth required" / "Checked"), and Continue lives in the detection evidence.
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.SelectAsync(page, "QA");
        var activeBefore = await page.Locator(".fa-active-card-name").InnerTextAsync();
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        var selectedUrl = (await page.GetByTestId("target-detection-url").InnerTextAsync()).Trim();
        await TargetEnvironmentDetectionScenarios.CheckAction(page).ClickAsync();
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Authentication required");

        continuationCalls.Should().Be(0, "rendering AuthenticationRequired must not auto-launch a browser");
        await page.GetByTestId("target-detection-evidence-toggle").ClickAsync();
        var continueButton = page.GetByRole(AriaRole.Button, new() { Name = "Continue detection in browser", Exact = true });
        await continueButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection complete");

        continuationCalls.Should().Be(1);
        continuationUrl.Should().Be($"{BrowserTestServers.BackendUrl}/api/frontend-target/continue-in-browser", "the continuation goes to the configured backend");
        using var requestDocument = JsonDocument.Parse(continuationBody!);
        var request = requestDocument.RootElement;
        var qaProfileId = request.GetProperty("profileId").GetString();
        request.GetProperty("targetUrl").GetString().Should().Be(selectedUrl);
        qaProfileId.Should().NotBeNullOrWhiteSpace();
        request.GetProperty("reviewSessionId").GetString().Should().StartWith($"detection-{qaProfileId}-");
        (await page.Locator(".fa-active-card-name").InnerTextAsync()).Should().Be(activeBefore, "continuation must not activate QA");
        (await page.Locator(".fa-detail-name").InnerTextAsync()).Should().Be("QA");
        (await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth")).Should().BeLessThanOrEqualTo(0);
        consoleErrors.Should().BeEmpty();
        pageErrors.Should().BeEmpty();
        await page.CloseAsync();
    }
}
