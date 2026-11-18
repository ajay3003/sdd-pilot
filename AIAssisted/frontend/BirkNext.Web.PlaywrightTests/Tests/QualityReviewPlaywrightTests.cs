using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright browser tests for Data Model Quality presentation.
///
/// These tests verify real WASM rendering and event binding in a browser,
/// catching integration issues that bUnit component tests cannot detect.
///
/// The key test is the repeated Show all / Show less toggle:
/// it exercises real browser event dispatch and component re-rendering,
/// which is where the WASM unboxing error would occur if event binding
/// used incorrect attribute names (@onclick vs onclick).
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class QualityReviewPlaywrightTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [BrowserFact]
    public async Task DataModelQuality_WasmInteractionHasNoRuntimeErrors()
    {
        // Was: Demo Projects → "Select Project" (an optional, separately configured feature — the isolated API has none) and a sidebar link.
        // Now: the test imports its own synthetic project and opens Document Quality Review directly; the assertions are stronger (exact
        // findings, exact counts, two toggle cycles) — see DataModelQualityScenario.
        var page = await _fixture.Context.NewPageAsync();
        try { await DataModelQualityScenario.RunAsync(page); }
        finally { await page.CloseAsync(); }
    }
}
