using BirkNext.Web.PlaywrightTests.Fixtures;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright E2E tests for target environment detection (self-starting fixture): real WASM rendering, the check flow and draft-only behaviour.
/// The scenarios are shared with the pre-started variant — see <see cref="TargetEnvironmentDetectionScenarios"/>.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class TargetEnvironmentDetectionPlaywrightTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
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
    public Task TargetEnvironment_DetectConfiguration_PopulatesDraftSafely() => OnPageAsync(TargetEnvironmentDetectionScenarios.PopulatesDraftSafelyAsync);

    [BrowserFact]
    public Task TargetEnvironment_DetectConfiguration_DoesNotOverwriteConfiguredValues() => OnPageAsync(TargetEnvironmentDetectionScenarios.DoesNotOverwriteConfiguredValuesAsync);

    [BrowserFact]
    public Task TargetEnvironment_UrlChange_InvalidatesDetection() => OnPageAsync(TargetEnvironmentDetectionScenarios.UrlChangeInvalidatesDetectionAsync);

    [BrowserTheory]
    [InlineData(1440, 900)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    [InlineData(860, 900)]
    [InlineData(800, 900)]
    [InlineData(480, 900)]
    public Task TargetEnvironment_ResponsiveLayout_NoHorizontalOverflow(int width, int height) =>
        OnPageAsync(page => TargetEnvironmentDetectionScenarios.ResponsiveLayoutAsync(page, width, height));

    [BrowserFact]
    public Task TargetEnvironment_KeyboardAccessibility_NavigateDetection() => OnPageAsync(TargetEnvironmentDetectionScenarios.KeyboardAccessibilityAsync);

    [BrowserFact]
    public Task TargetEnvironment_DetectConfiguration_RealBackendEndToEnd() => OnPageAsync(page => TargetEnvironmentDetectionScenarios.RealBackendCheckAsync(page));
}
