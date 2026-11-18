using System.Text.Json;
using BirkNext.Web.PlaywrightTests.Fixtures;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

[Collection(BrowserServersCollection.Name)]
public sealed class SddProjectionResponsiveAccessibilityTests : IAsyncLifetime
{
    private readonly BirkNextWebApplicationFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [BrowserFact]
    public async Task SddReviewRoutesRemainUsableAtResponsiveWidthsAndPassAxeWithinChangedRegions()
    {
        var page = await _fixture.Context.NewPageAsync();
        var axePath = FindAxePath();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        // Was: Demo Projects → a card containing spec.md → "Select Project". Demo Projects are an optional, separately configured feature
        // (the isolated API has none), so the test now imports its own Spec Kit project and makes it current.
        var project = SyntheticProjects.NewName("PwSdd");
        await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));

        foreach (var (route, region) in new[]
        {
            ("/implementation-review", ".sdd-review"),
            ("/traceability", ".requirements-traceability"),
            ("/quality-review", "[aria-labelledby='sdd-evidence-title']")
        })
        {
            await page.GotoAsync(_fixture.FrontendUrl + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            var root = page.Locator(region);
            // The project has Spec Kit requirements, so every SDD region — including Document Quality Review's shared SDD evidence — must
            // render; it is waited for (cold WASM start takes several seconds), never assumed absent after a fixed delay.
            await root.First.WaitForAsync(new() { Timeout = 120000 });

            await page.AddScriptTagAsync(new() { Path = axePath });
            var violations = await page.EvaluateAsync<JsonElement>(@"async selector => {
                const root = document.querySelector(selector);
                const result = await axe.run(root, { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa'] } });
                return result.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.map(n => n.target) }));
            }", region);
            Assert.Equal(JsonValueKind.Array, violations.ValueKind);
            Assert.Empty(violations.EnumerateArray());

            foreach (var width in new[] { 1440, 1100, 768, 390 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await page.WaitForTimeoutAsync(100);
                var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
                Assert.False(overflow, $"{route} overflows horizontally at {width}px");
            }
        }

        await page.CloseAsync();
    }

    private static string FindAxePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "AIAssisted", "browser-companion", "vendor", "axe.min.js");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository axe-core bundle was not found.");
    }
}
