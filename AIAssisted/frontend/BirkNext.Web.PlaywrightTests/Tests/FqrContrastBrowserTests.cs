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
    public async Task FqrLandingReportsExactAxeContrastFailure()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.GotoAsync(_fixture.FrontendUrl + "/frontend-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator(".fqr-page").WaitForAsync(new() { Timeout = 30000 });
        await page.AddScriptTagAsync(new() { Path = FindAxePath() });
        var result = await page.EvaluateAsync<JsonElement>(@"async () => {
            const contrast = await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } });
            return contrast.violations.flatMap(v => v.nodes.map(n => ({
                rule: v.id, impact: v.impact, help: v.help, target: n.target, html: n.html,
                text: (document.querySelector(n.target[0])?.innerText || '').trim(),
                failureSummary: n.failureSummary, checks: n.any.map(c => c.data)
            })));
        }");
        Assert.NotEqual(JsonValueKind.Undefined, result.ValueKind);
        Assert.NotEmpty(result.EnumerateArray());
        Assert.True(false, "FQR axe contrast evidence: " + result.GetRawText());
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
