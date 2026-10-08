using System.Text.Json;
using BirkNext.Web.PlaywrightTests.Fixtures;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

public sealed class FqrContrastBrowserTests : IAsyncLifetime
{
    private readonly BirkNextWebApplicationFixture_PreStarted _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task FqrLandingHasNoAxeContrastViolations()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.GotoAsync(_fixture.FrontendUrl + "/frontend-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator(".fqr-page").WaitForAsync(new() { Timeout = 30000 });
        await page.AddScriptTagAsync(new() { Path = FindAxePath() });
        var action = page.GetByTestId("fqr-readiness-action");
        foreach (var width in new[] { 1440, 1100, 768, 390 })
        {
            await page.SetViewportSizeAsync(width, 1000);
            var result = await page.EvaluateAsync<JsonElement>(@"async () => {
                const contrast = await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } });
                return contrast.violations.flatMap(v => v.nodes.map(n => ({
                    rule: v.id, impact: v.impact, target: n.target, failureSummary: n.failureSummary,
                    checks: n.any.map(c => c.data)
                })));
            }");
            Assert.NotEqual(JsonValueKind.Undefined, result.ValueKind);
            Assert.True(result.GetArrayLength() == 0, $"Color contrast violations at viewport {width}px: {result}");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"Horizontal overflow at viewport {width}px");

            if (width != 1440 || await action.CountAsync() == 0) continue;
            var contrastRatio = await action.EvaluateAsync<double>(@"el => {
                const parse = value => value.startsWith('#')
                    ? value.slice(1).match(/.{2}/g).map(channel => parseInt(channel, 16))
                    : value.match(/[0-9.]+/g).slice(0, 3).map(Number);
                const luminance = value => {
                    const [r, g, b] = parse(value).map(channel => {
                        const c = channel / 255;
                        return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
                    });
                    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
                };
                const foreground = luminance(getComputedStyle(el).color);
                const background = luminance(getComputedStyle(el.closest('.fqr-readiness')).backgroundColor);
                return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
            }");
            Assert.True(contrastRatio >= 4.5, $"Expected WCAG AA contrast, got {contrastRatio:F2}:1");
            Assert.Equal(6.13, Math.Round(contrastRatio, 2));
        }

        if (await action.CountAsync() > 0)
        {
            await page.SetViewportSizeAsync(1440, 1000);
            foreach (var state in new[] { "hover", "focus" })
            {
                if (state == "hover") await action.HoverAsync();
                else await action.FocusAsync();
                Assert.Equal("underline", await action.EvaluateAsync<string>("el => getComputedStyle(el).textDecorationLine"));
                var stateViolations = await page.EvaluateAsync<int>(@"async () => (await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } })).violations.reduce((count, violation) => count + violation.nodes.length, 0)");
                Assert.Equal(0, stateViolations);
            }
        }
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
