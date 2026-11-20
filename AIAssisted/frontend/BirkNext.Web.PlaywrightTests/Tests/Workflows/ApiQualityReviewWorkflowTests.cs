using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// API Quality Review end to end against a local stand-in API (<see cref="ApiStandIn"/>; nothing external): the Target Environment names the
/// REST base URL, health endpoint and OpenAPI contract → readiness → Run → the deliberate defects are reported (server error, contract
/// violation, slow response, unsafe CORS, a leaking error page, missing headers, no security scheme in the contract) → the stand-in is fixed →
/// Run review again → exactly those findings are gone, plain HTTP is still reported → the HTML report carries the result.
/// Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ApiQualityReviewWorkflowTests(ITestOutputHelper output) : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private readonly ApiStandIn _api = new();

    public async Task InitializeAsync()
    {
        _api.Start();
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _api.DisposeAsync();
    }

    private static async Task<string[]> FindingIdsAsync(IPage page)
    {
        await page.GetByTestId("aqr-tab-findings").ClickAsync();
        await page.GetByTestId("aqr-findings-table").WaitForAsync(new() { Timeout = 30000 });
        // A finding id is its rule plus a stable suffix (rest-5xx-59426): compare rules.
        return await page.GetByTestId("aqr-finding-row").EvaluateAllAsync<string[]>("rs => [...new Set(rs.map(r => r.dataset.findingId.replace(/-\\d+$/, '')))]");
    }

    [BrowserFact]
    public async Task Defects_are_found_and_a_fixed_API_no_longer_shows_them()
    {
        // The Target Environment of a tester: Development, the API's base URL, its health endpoint and its OpenAPI contract. A fresh browser
        // history (an earlier report in this browser would be shown as the result).
        await _fixture.Context.AddInitScriptAsync($$"""
            if (!sessionStorage.getItem('pw-aqr')) { localStorage.removeItem('birknext:api-review'); sessionStorage.setItem('pw-aqr', '1'); }
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'aqr-standin', profiles: [{ id: 'aqr-standin',
              name: 'Children API stand-in (Development)', environmentType: 'Development', targetUrl: '{{_api.Url}}/',
              restBaseUrl: '{{_api.Url}}/api/children', healthEndpoint: '{{_api.Url}}/health', swaggerUrl: '{{_api.Url}}/openapi.json',
              authentication: { requiresAuthentication: false, authenticationType: 'None' }, apiAuth: { authType: 'None' }, integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        try
        {
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/api-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var readiness = page.GetByTestId("aqr-readiness");
            await readiness.WaitForAsync(new() { Timeout = 120000 });
            await Assertions.Expect(readiness).Not.ToHaveAttributeAsync("data-readiness", "Assessing", new() { Timeout = 120000 });
            output.WriteLine($"readiness {await readiness.GetAttributeAsync("data-readiness")}: {await readiness.InnerTextAsync()}");
            await Assertions.Expect(page.GetByTestId("aqr-target-review").First).ToBeVisibleAsync(new() { Timeout = 30000 });
            output.WriteLine("targets: " + string.Join(" | ", await page.GetByTestId("aqr-target-review").AllInnerTextsAsync()));

            // 1. The defective API.
            _api.Broken = true;
            await page.GetByTestId("aqr-run").ClickAsync();
            await page.GetByTestId("aqr-results").WaitForAsync(new() { Timeout = 120000 });
            await Assertions.Expect(page.GetByTestId("aqr-outcome-execution")).ToContainTextAsync("Completed", new() { Timeout = 60000 });
            var before = await FindingIdsAsync(page);
            output.WriteLine("before: " + string.Join(", ", before));
            before.Should().Contain("rest-5xx", "/health answers 500")
                .And.Contain("cors-wildcard-credentials", "any origin with credentials")
                .And.Contain("rest-slow", "/api/children takes 2 s")
                .And.Contain("sec-no-xcto")
                .And.Contain("oas-no-security-schemes", "the contract documents no security scheme")
                .And.Contain("contract-type-mismatch", "id is a number where the contract says string")
                .And.Contain("contract-missing-required", "the required name is missing")
                .And.Contain(id => id == "errors-leak" || id == "errors-unknown-route-5xx", "the unknown route answers 500 with a stack trace");

            // 2. The API is fixed; the same review again.
            _api.Broken = false;
            await page.GetByTestId("aqr-run").First.ClickAsync();
            // The finding rows also vanish while the Findings tab is not shown, so "rest-5xx is gone" alone could read the previous run under
            // load: wait for the run to end, then read the Findings tab until the new result is there.
            await Assertions.Expect(page.GetByTestId("aqr-run").First).Not.ToContainTextAsync("Reviewing", new() { Timeout = 120000 });
            await Assertions.Expect(page.GetByTestId("aqr-outcome-execution")).ToContainTextAsync("Completed", new() { Timeout = 60000 });
            var after = await FindingIdsAsync(page);
            for (var deadline = DateTime.UtcNow.AddMinutes(2); after.Contains("rest-5xx") && DateTime.UtcNow < deadline; after = await FindingIdsAsync(page))
                await page.WaitForTimeoutAsync(2000);
            output.WriteLine("after: " + string.Join(", ", after));
            after.Should().NotContain(["rest-5xx", "cors-wildcard-credentials", "rest-slow", "sec-no-xcto", "errors-leak", "errors-unknown-route-5xx"])
                .And.NotContain(id => id.StartsWith("contract-", StringComparison.Ordinal), "the response now matches the contract");
            after.Should().Contain("sec-no-tls", "plain HTTP is still reported").And.Contain("oas-no-security-schemes", "the contract was not changed");

            // 3. The report.
            await page.GetByTestId("aqr-export-toggle").ClickAsync();
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("aqr-export-html").ClickAsync());
            download.SuggestedFilename.Should().MatchRegex(@"^api-quality-review-\d{4}-\d{2}-\d{2}\.html$");
            var file = Path.Combine(Path.GetTempPath(), $"aqr-{Guid.NewGuid():N}.html");
            await download.SaveAsAsync(file);
            try { (await File.ReadAllTextAsync(file)).Should().Contain("/api/children", "the report names the reviewed operation"); }
            finally { File.Delete(file); }
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
