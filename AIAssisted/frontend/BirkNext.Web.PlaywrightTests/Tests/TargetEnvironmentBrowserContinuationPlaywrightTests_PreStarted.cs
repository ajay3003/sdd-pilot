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

    [Theory]
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

        await page.RouteAsync("**/api/frontend-target/detect", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = "{\"originalUrl\":\"https://qa.example.test/\",\"normalizedTargetUrl\":\"https://qa.example.test/\",\"reachability\":1,\"authenticationRequired\":true,\"success\":true,\"warnings\":[]}"
        }));
        await page.RouteAsync("**/api/frontend-target/continue-in-browser", async route =>
        {
            continuationCalls++;
            continuationBody = route.Request.PostData;
            continuationUrl = route.Request.Url;
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = "{\"state\":2,\"isActivationReady\":true,\"strategySuggestion\":\"no-action-needed\",\"detectedUrl\":\"https://qa.example.test/\",\"isUrlCurrent\":true,\"detectionResponse\":{\"originalUrl\":\"https://qa.example.test/\",\"normalizedTargetUrl\":\"https://qa.example.test/\",\"reachability\":0,\"authenticationRequired\":false,\"success\":true,\"warnings\":[]}}"
            });
        });

        await page.GotoAsync($"{_fixture.FrontendUrl}/admin/system-settings?section=target-environments",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
        await page.Locator(".fa-profile-chip").Filter(new() { HasText = "QA" }).ClickAsync();
        var activeBefore = await page.Locator(".fa-active-card-name").InnerTextAsync();
        var selectedUrl = await page.Locator(".fa-selected-url .fa-summary-url").InnerTextAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Detect settings", Exact = true }).ClickAsync();
        await page.GetByText("Auth required", new() { Exact = true }).WaitForAsync();

        continuationCalls.Should().Be(0, "rendering AuthenticationRequired must not auto-launch a browser");
        var continueButton = page.GetByRole(AriaRole.Button, new() { Name = "Continue detection in browser", Exact = true });
        await continueButton.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByText("Checked", new() { Exact = true }).WaitForAsync();

        continuationCalls.Should().Be(1);
        continuationUrl.Should().Be("http://localhost:5000/api/frontend-target/continue-in-browser");
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
