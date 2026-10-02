using System.Text.Json;
using BirkNext.Web.PlaywrightTests.Fixtures;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

public sealed class SddProjectionResponsiveAccessibilityTests : IAsyncLifetime
{
    private readonly BirkNextWebApplicationFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task SddReviewRoutesRemainUsableAtResponsiveWidthsAndPassAxeWithinChangedRegions()
    {
        var page = await _fixture.Context.NewPageAsync();
        var axePath = FindAxePath();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await page.GotoAsync(_fixture.FrontendUrl + "/sample-projects", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var specificationProject = page.Locator(".sp-card").Filter(new() { HasText = "spec.md" }).First;
        var selectProject = specificationProject.GetByRole(AriaRole.Button, new() { Name = "Select Project", Exact = true });
        await selectProject.WaitForAsync(new() { Timeout = 15000 });
        await selectProject.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Selected", Exact = true }).First.WaitForAsync(new() { Timeout = 15000 });

        foreach (var (route, region) in new[]
        {
            ("/implementation-review", ".sdd-review"),
            ("/traceability", ".requirements-traceability"),
            ("/quality-review", "[aria-labelledby='sdd-evidence-title']")
        })
        {
            await page.GotoAsync(_fixture.FrontendUrl + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            var root = page.Locator(region);
            if (route != "/quality-review") await root.WaitForAsync(new() { Timeout = 15000 });
            else await page.WaitForTimeoutAsync(500);
            if (await root.CountAsync() == 0)
            {
                Assert.Equal("/quality-review", route);
                await page.Locator(".qr-page").WaitForAsync(new() { Timeout = 15000 });
                foreach (var width in new[] { 1440, 1100, 768, 390 })
                {
                    await page.SetViewportSizeAsync(width, 1000);
                    var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
                    Assert.False(overflow, $"{route} overflows horizontally at {width}px");
                }
                continue; // This sample has no SDD requirements, so the optional SDD panel is correctly NotApplicable.
            }

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
