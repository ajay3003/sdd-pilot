using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright E2E tests for target environment detection against an already started, isolated BirkNext (see <see cref="BrowserTestServers"/>).
/// The scenarios are shared with the self-starting variant — see <see cref="TargetEnvironmentDetectionScenarios"/>.
/// </summary>
[Collection("Playwright Tests - PreStarted")]
public sealed class TargetEnvironmentDetectionPlaywrightTests_PreStarted : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private async Task OnPageAsync(Func<IPage, Task> scenario)
    {
        var page = await _fixture.Context.NewPageAsync();
        try { await scenario(page); }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_DetectConfiguration_PopulatesDraftSafely() => OnPageAsync(TargetEnvironmentDetectionScenarios.PopulatesDraftSafelyAsync);

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_DetectConfiguration_DoesNotOverwriteConfiguredValues() => OnPageAsync(TargetEnvironmentDetectionScenarios.DoesNotOverwriteConfiguredValuesAsync);

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_UrlChange_InvalidatesDetection() => OnPageAsync(TargetEnvironmentDetectionScenarios.UrlChangeInvalidatesDetectionAsync);

    [BrowserTheory]
    [InlineData(1440, 900)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    [InlineData(860, 900)]
    [InlineData(800, 900)]
    [InlineData(480, 900)]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_ResponsiveLayout_NoHorizontalOverflow(int width, int height) =>
        OnPageAsync(page => TargetEnvironmentDetectionScenarios.ResponsiveLayoutAsync(page, width, height));

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_KeyboardAccessibility_NavigateDetection() => OnPageAsync(TargetEnvironmentDetectionScenarios.KeyboardAccessibilityAsync);

    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_DetectConfiguration_RealBackendEndToEnd() => OnPageAsync(page => TargetEnvironmentDetectionScenarios.RealBackendCheckAsync(page));

    /// <summary>The browser sends the check to the configured HTTP backend (local transport, no TLS errors) and the backend answers it.</summary>
    [BrowserFact]
    [Trait("Category", "PreStarted")]
    public Task TargetEnvironment_LocalTransport_UsesHttpBackendAndReachesRealPreflight() => OnPageAsync(async page =>
    {
        var response = await TargetEnvironmentDetectionScenarios.RealBackendCheckAsync(page);
        response.Url.Should().Be($"{BrowserTestServers.BackendUrl}/api/frontend-target/detect", "the check goes to the configured backend, over HTTP");
    });
}
