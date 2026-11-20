using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Frontend Quality Review end to end against a local stand-in website (frontend-target/server.js in Podman; nothing external): all six
/// engines on (HTTP Security, HTTP Performance, Browser Runtime, Accessibility, Lighthouse, Passive Web Security/ZAP) → Run → the deliberate
/// defects are reported and the release policy blocks → the site is fixed → Run again → those issues are gone, the decision is no longer
/// Blocked, both runs are in the history → the HTML report carries the result.
/// Start the stand-in with frontend-target/start-target.ps1 and set BIRKNEXT_FQR_TARGET_URL (http://localhost:18092); the backend needs the
/// engines allowed and the stand-in registered for ZAP (scripts/run-workflow-tests.ps1 does both). Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class FrontendQualityReviewWorkflowTests(ITestOutputHelper output) : IAsyncLifetime
{
    public const string TargetVariable = "BIRKNEXT_FQR_TARGET_URL";
    /// <summary>The Target Environment id the backend registers for ZAP (FrontendPassiveSecurity:TrustedProfiles:fqr-standin).</summary>
    public const string ProfileId = "fqr-standin";

    private BirkNextWebApplicationFixture _fixture = null!;
    private static string Target => Environment.GetEnvironmentVariable(TargetVariable)!.TrimEnd('/');

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private async Task SetModeAsync(string mode)
    {
        var response = await _fixture.Context.APIRequest.GetAsync($"{Target}/__control?mode={mode}");
        response.Ok.Should().BeTrue();
        (await response.TextAsync()).Should().Contain($"\"mode\":\"{mode}\"");
    }

    private sealed record RunResult(string Disposition, string Release, string[] Issues, Dictionary<string, string> Engines);

    private async Task<RunResult> ReadResultAsync(IPage page, string label)
    {
        var matrix = await FrontendQualityReviewPage.EngineMatrixAsync(page, 600000);
        var engines = (await matrix.Locator("tbody tr").EvaluateAllAsync<string[][]>("rs => rs.map(r => [r.dataset.engineId, r.dataset.engineState, r.innerText.replace(/\\s+/g, ' ')])"))
            .ToDictionary(r => r[0], r => r[1]);
        foreach (var r in await matrix.Locator("tbody tr").AllInnerTextsAsync()) output.WriteLine($"{label} engine: {r.ReplaceLineEndings(" ")}");
        await FrontendQualityReviewPage.OpenResultTabAsync(page, "summary");
        var disposition = await page.GetByTestId("fqr-release").GetAttributeAsync("data-disposition") ?? "";
        var release = (await page.GetByTestId("fqr-release").InnerTextAsync()).ReplaceLineEndings(" ");
        output.WriteLine($"{label} release: {disposition} — {release}");
        await FrontendQualityReviewPage.OpenResultTabAsync(page, "issues");
        var issues = await page.GetByTestId("fqr-tabpanel-issues").GetByTestId("fqr-logical-issue").EvaluateAllAsync<string[]>("es => es.map(e => e.dataset.logicalId + ' [' + e.dataset.severity + ']')");
        foreach (var i in issues) output.WriteLine($"{label} issue: {i}");
        return new(disposition, release, issues.Select(i => i[..i.LastIndexOf(" [", StringComparison.Ordinal)]).ToArray(), engines);
    }

    [BrowserFact(TargetVariable, BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task Defects_block_the_release_and_a_fixed_site_no_longer_shows_them()
    {
        // The tester's Target Environment: the stand-in, all six engines on, a team rule that a missing CSP blocks the release. A fresh history.
        await _fixture.Context.AddInitScriptAsync($$"""
            if (!sessionStorage.getItem('pw-fqr')) {
              for (const k of Object.keys(localStorage)) if (k.toLowerCase().includes('frontend-quality')) localStorage.removeItem(k);
              sessionStorage.setItem('pw-fqr', '1');
            }
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: '{{ProfileId}}', profiles: [{ id: '{{ProfileId}}',
              name: 'Children site stand-in (Development)', environmentType: 'Development', targetUrl: '{{Target}}/',
              authentication: { requiresAuthentication: false, authenticationType: 'None' },
              features: { enableSecurityEngine: true, enablePerformanceEngine: true, enableBrowserRuntimeEngine: true, enableAccessibilityEngine: true,
                enableLighthouseEngine: true, enablePassiveSecurityEngine: true },
              releasePolicy: { blockingLogicalIssueIds: ['headers:csp:missing'], reviewOptionalEngineFailures: true },
              integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        try
        {
            // 1. The defective site.
            await SetModeAsync("broken");
            await FrontendQualityReviewPage.OpenAsync(page);
            await FrontendQualityReviewPage.WaitUntilRunnableAsync(page, 180000);
            // Before any runtime probe has answered, no engine is called "not available in this environment" (it used to be, for four of six).
            var capabilities = await page.GetByTestId("fqr-capability").EvaluateAllAsync<string[][]>("cs => cs.map(c => [c.dataset.engineId, c.dataset.state])");
            foreach (var c in capabilities) output.WriteLine($"capability {c[0]}: {c[1]}");
            capabilities.Where(c => c[0] is "StaticSecurity" or "PassivePerformance" or "BrowserRuntime" or "Accessibility" or "Lighthouse" or "PassiveSecurity")
                .Should().HaveCount(6).And.OnlyContain(c => c[1] == "Enabled" || c[1] == "Ready" || c[1] == "Checking");
            await FrontendQualityReviewPage.Run(page).ClickAsync();
            var before = await ReadResultAsync(page, "broken");

            // 2. The site is fixed; the same review again.
            await SetModeAsync("fixed");
            await page.GetByTestId("fqr-run-again").ClickAsync();
            await page.GetByTestId("fqr-tab-coverage").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60000 });
            var after = await ReadResultAsync(page, "fixed");

            // 3. History and the report.
            await FrontendQualityReviewPage.OpenResultTabAsync(page, "history");
            await Assertions.Expect(page.GetByTestId("fqr-history-run")).ToHaveCountAsync(2, new() { Timeout = 30000 });
            await page.GetByTestId("fqr-export-toggle").ClickAsync();
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("fqr-export-html").ClickAsync());
            download.SuggestedFilename.Should().MatchRegex(@"^frontend-quality-review-\d{4}-\d{2}-\d{2}\.html$");
            var file = Path.Combine(Path.GetTempPath(), $"fqr-{Guid.NewGuid():N}.html");
            await download.SaveAsAsync(file);
            string report;
            try { report = await File.ReadAllTextAsync(file); } finally { File.Delete(file); }
            report.Should().Contain(new Uri(Target).Authority, "the report names the reviewed site");

            // Every engine ran both times: none failed, none was skipped.
            before.Engines.Should().HaveCount(6).And.OnlyContain(e => e.Value.StartsWith("completed", StringComparison.Ordinal));
            after.Engines.Should().HaveCount(6).And.OnlyContain(e => e.Value.StartsWith("completed", StringComparison.Ordinal));

            // The defective site: what each engine is for, found; the team's rule (missing CSP) blocks the release.
            before.Disposition.Should().Be("Blocked");
            before.Release.Should().Contain("Content Security Policy");
            before.Issues.Should().Contain(["headers:csp:missing", "headers:nosniff:missing", "headers:referrer-policy:missing",
                "headers:permissions-policy:missing", "headers:frame-options:missing", "wcag:1.4.3:contrast-minimum"]);
            foreach (var prefix in new[]
            {
                "rule:PassivePerformance:CCH-005:",                                  // CSS/JS without caching headers
                "rule:BrowserRuntime:runtime-PageError:", "rule:BrowserRuntime:runtime-ConsoleError:", "rule:BrowserRuntime:runtime-ResourceFailure:",
                "rule:Accessibility:image-alt:", "rule:Accessibility:label:", "rule:Accessibility:button-name:",
                "rule:Accessibility:html-has-lang:", "rule:Accessibility:document-title:",
                "rule:Lighthouse:uses-long-cache-ttl:",
                "rule:PassiveSecurity:10010:", "rule:PassiveSecurity:10054:",     // the session cookie without HttpOnly / SameSite (ZAP)
            })
                before.Issues.Should().Contain(i => i.StartsWith(prefix, StringComparison.Ordinal), prefix);
            before.Issues.Should().NotContain(i => i.Contains(":10116:", StringComparison.Ordinal), "'ZAP is out of date' is about ZAP, not the site");

            // The fixed site: every one of those is gone; nothing blocks; nothing reads as missing coverage.
            after.Issues.Should().NotContain(i => i.StartsWith("headers:", StringComparison.Ordinal) || i.StartsWith("wcag:", StringComparison.Ordinal)
                || i.StartsWith("rule:BrowserRuntime:", StringComparison.Ordinal) || i.StartsWith("rule:Accessibility:", StringComparison.Ordinal)
                || i.StartsWith("rule:PassivePerformance:CCH-", StringComparison.Ordinal) || i.StartsWith("rule:Lighthouse:uses-long-cache-ttl:", StringComparison.Ordinal)
                || i.StartsWith("rule:PassiveSecurity:10010:", StringComparison.Ordinal) || i.StartsWith("rule:PassiveSecurity:10054:", StringComparison.Ordinal));
            after.Disposition.Should().Be("ReviewRequired", "automated accessibility checks always leave manual WCAG testing; nothing blocks");
            after.Release.Should().NotContain("Coverage is partial", "every engine assessed the clean site");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
