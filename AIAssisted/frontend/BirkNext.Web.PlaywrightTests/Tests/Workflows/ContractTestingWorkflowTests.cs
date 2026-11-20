using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Contract Testing end to end for REST and GraphQL, as a tester runs it, against a local stand-in API (<see cref="ContractStandIn"/>; nothing
/// external): import the OpenAPI contract and the GraphQL schema by URL → bind each contract's endpoint in the Target Environment → generate tests
/// and synthetic test data → run the ready tests → every call passes against the documented API → the API breaks its contract → the same tests
/// fail with the contract findings in Execution Results → the API is fixed → they pass again → v2 of the REST contract renames a required field
/// → Changes shows the breaking change and the tests it affects. Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ContractTestingWorkflowTests(ITestOutputHelper output) : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private readonly ContractStandIn _api = new();

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

    private static string Page => $"{BrowserTestServers.FrontendUrl}/api-quality-review/contracts";

    /// <summary>Imports a contract by URL as a new contract; returns its key (from the detail heading's URL).</summary>
    private async Task<string> ImportByUrlAsync(IPage page, string url, string name)
    {
        await page.GotoAsync(Page, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("ct-open-import").ClickAsync(new() { Timeout = 120000 });
        await page.GetByTestId("ct-import-url-mode").CheckAsync();
        await page.GetByTestId("ct-import-url").FillAsync(url);
        await page.GetByTestId("ct-import-name").FillAsync(name);
        await page.GetByTestId("ct-import-submit").ClickAsync();
        await page.GetByTestId("ct-detail").WaitForAsync(new() { Timeout = 60000 });
        await Assertions.Expect(page.GetByTestId("ct-detail")).ToContainTextAsync(name, new() { Timeout = 30000 });
        output.WriteLine($"imported {name}: {await page.GetByTestId("ct-version-source").InnerTextAsync()} · {await page.GetByTestId("ct-parse").InnerTextAsync()} · {await page.GetByTestId("ct-validation-depth").InnerTextAsync()}");
        return new Uri(page.Url).Query.Split('&').Select(p => p.TrimStart('?')).First(p => p.StartsWith("contract=", StringComparison.Ordinal))["contract=".Length..];
    }

    /// <summary>Binds the contract's endpoint (GraphQL path when given) with no authentication.</summary>
    private async Task BindAsync(IPage page, string? graphQlPath)
    {
        var panel = page.GetByTestId("cep");
        if (await panel.GetAttributeAsync("open") is null) await page.GetByTestId("cep-summary").ClickAsync();
        if (graphQlPath is not null) await page.GetByTestId("cep-graphql-path").FillAsync(graphQlPath);
        await page.GetByTestId("cep-auth").SelectOptionAsync("None");
        await page.GetByTestId("cep-save").ClickAsync();
        await page.GetByTestId("cep-message").Or(page.GetByTestId("cep-error")).WaitForAsync(new() { Timeout = 30000 });
        output.WriteLine("binding: " + await page.GetByTestId("cep-summary").InnerTextAsync() + " · " + await page.GetByTestId("cep-message").Or(page.GetByTestId("cep-error")).InnerTextAsync());
        await Assertions.Expect(page.GetByTestId("cep-error")).ToHaveCountAsync(0);
    }

    private async Task GenerateAsync(IPage page)
    {
        await page.GetByTestId("ct-tab-operations").ClickAsync();
        await page.GetByTestId("ct-op-row").First.WaitForAsync(new() { Timeout = 30000 });
        output.WriteLine("operations: " + string.Join(" | ", await page.GetByTestId("ct-op-row").AllInnerTextsAsync()));
        await page.GetByTestId("ct-generate-all").ClickAsync();
        await page.GetByTestId("ct-message").Or(page.GetByTestId("ct-error")).WaitForAsync(new() { Timeout = 60000 });
        output.WriteLine("generate: " + await page.GetByTestId("ct-message").Or(page.GetByTestId("ct-error")).InnerTextAsync());
        await Assertions.Expect(page.GetByTestId("ct-error")).ToHaveCountAsync(0);
    }

    /// <summary>The tests view as "kind:operation=readiness"; the last-run state per row when there is one.</summary>
    private async Task<string[]> TestRowsAsync(IPage page) =>
        await page.GetByTestId("ct-test-row").EvaluateAllAsync<string[]>(
            "rs => rs.map(r => r.dataset.kind + ':' + r.dataset.op + '=' + r.dataset.state + ' last=' + (r.querySelector('[data-testid=ct-last]')?.innerText.trim() ?? '-'))");

    /// <summary>Selects every ready test and runs it; waits until every selected row has a finished last run newer than before.</summary>
    private async Task<(string[] Calls, string[] Findings)> RunReadyAsync(IPage page, string label)
    {
        await page.GetByTestId("ct-tab-tests").ClickAsync();
        await page.GetByTestId("ct-test-row").First.WaitForAsync(new() { Timeout = 30000 });
        output.WriteLine($"{label} before: " + string.Join(" | ", await TestRowsAsync(page)));
        output.WriteLine($"{label} blockers: " + string.Join(" | ", await page.GetByTestId("ct-blocker").AllInnerTextsAsync()));
        await page.GetByTestId("ct-select-ready").CheckAsync();
        var run = page.GetByTestId("ct-run");
        await Assertions.Expect(run).ToBeEnabledAsync(new() { Timeout = 30000 });
        await run.ClickAsync();
        await page.GetByTestId("ct-run-link").Or(page.GetByTestId("ct-error")).First.WaitForAsync(new() { Timeout = 60000 });
        output.WriteLine($"{label} run: " + await page.GetByTestId("ct-message").Or(page.GetByTestId("ct-error")).First.InnerTextAsync());
        var runHref = await page.GetByTestId("ct-run-link").GetAttributeAsync("href");
        // The run is executed by Execution Evidence; its page says when it has finished.
        var results = await page.Context.NewPageAsync();
        try
        {
            await results.GotoAsync($"{BrowserTestServers.FrontendUrl}/{runHref}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var runCard = results.GetByTestId("xr-run");
            await runCard.WaitForAsync(new() { Timeout = 60000 });
            for (var deadline = DateTime.UtcNow.AddMinutes(2); DateTime.UtcNow < deadline && await runCard.GetAttributeAsync("data-state") is "Queued" or "Running"; )
            {
                await results.WaitForTimeoutAsync(1500);
                await results.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await runCard.WaitForAsync(new() { Timeout = 60000 });
            }
            var calls = await results.GetByTestId("xr-call-row").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.state + ' ' + r.innerText.replace(/\\s+/g, ' ').trim())");
            output.WriteLine($"{label} run state {await runCard.GetAttributeAsync("data-state")}; calls: " + string.Join(" | ", calls));
            var findings = await FindingsAsync(results);
            output.WriteLine($"{label} findings: " + string.Join(" | ", findings));
            return (calls, findings);
        }
        finally { await results.CloseAsync(); }
    }

    /// <summary>Test Data: a key-value collection <paramref name="alias"/> with id = c-1, saved and approved (identifiers are never invented).</summary>
    private async Task ApprovedIdCollectionAsync(IPage page, string alias)
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-data?section=assets", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("td-add-data").ClickAsync(new() { Timeout = 120000 });
        await page.Locator("[data-testid=tdx-kind][data-kind=KeyValue]").ClickAsync();
        await page.GetByTestId("tdx-next").ClickAsync();
        await page.GetByTestId("tdx-alias").FillAsync(alias);
        if (await page.GetByTestId("tdx-row").CountAsync() == 0) await page.GetByTestId("tdx-add-row").ClickAsync();
        await page.GetByTestId("tdx-row-name").First.FillAsync("id");
        await page.GetByTestId("tdx-row-value").First.FillAsync("c-1");
        await page.GetByTestId("tdx-validate").ClickAsync();
        await page.GetByTestId("tdx-save").ClickAsync(new() { Timeout = 30000 });
        var asset = page.Locator($"[data-testid=td-asset][data-alias='{alias}']");
        await asset.WaitForAsync(new() { Timeout = 30000 });
        await asset.GetByTestId("td-toggle").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = $"Approve {alias}", Exact = true }).ClickAsync();
        await Assertions.Expect(asset).ToHaveAttributeAsync("data-status", "Approved", new() { Timeout = 30000 });
    }

    /// <summary>Pins the approved collection on every test that is not ready because it needs a value.</summary>
    private async Task PinCollectionAsync(IPage page, string contract, string alias)
    {
        await page.GotoAsync($"{Page}?contract={contract}&view=tests", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("ct-test-row").First.WaitForAsync(new() { Timeout = 60000 });
        var notReady = await page.Locator("[data-testid=ct-test-row][data-state=NotReady] [data-testid=ct-test-link]").EvaluateAllAsync<string[]>("ls => ls.map(l => l.getAttribute('href'))");
        foreach (var href in notReady)
        {
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/{href}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("ct-test-detail").WaitForAsync(new() { Timeout = 60000 });
            output.WriteLine("needs: " + string.Join(" | ", await page.GetByTestId("ct-readiness-items").AllInnerTextsAsync()));
            await page.Locator($"[data-testid=ct-option][data-alias='{alias}'] [data-testid=ct-option-use]").ClickAsync(new() { Timeout = 30000 });
            await Assertions.Expect(page.GetByTestId("ct-binding")).ToContainTextAsync($"Pinned to {alias}", new() { Timeout = 30000 });
        }
    }

    /// <summary>The contract findings of the run's failed calls, read from each call's detail in Execution Results.</summary>
    private static async Task<string[]> FindingsAsync(IPage results)
    {
        var findings = new List<string>();
        var failed = await results.Locator("[data-testid=xr-call-row][data-state=Failed] [data-testid=xr-call-open]").EvaluateAllAsync<string[]>("ls => ls.map(l => l.getAttribute('href'))");
        foreach (var href in failed)
        {
            await results.GotoAsync($"{BrowserTestServers.FrontendUrl}/{href}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await results.GetByTestId("xr-call").WaitForAsync(new() { Timeout = 60000 });
            await results.GetByTestId("xr-tab-assertions").ClickAsync();
            await Assertions.Expect(results.GetByTestId("xr-tabpanel")).ToHaveAttributeAsync("data-tab", "assertions");
            findings.AddRange((await results.Locator("[data-testid=xr-assertion][data-outcome=Failed]").AllInnerTextsAsync()).Select(a => "assertion: " + a));
            await results.GetByTestId("xr-tab-contract").ClickAsync();
            await Assertions.Expect(results.GetByTestId("xr-tabpanel")).ToHaveAttributeAsync("data-tab", "contract");
            findings.AddRange((await results.Locator("[data-testid=xr-contract-findings] tbody tr").AllInnerTextsAsync()).Select(f => "contract: " + f));
        }
        return [.. findings.Select(f => System.Text.RegularExpressions.Regex.Replace(f, @"\s+", " ").Trim())];
    }

    [BrowserFact]
    public async Task REST_and_GraphQL_contracts_are_tested_a_broken_API_fails_with_findings_and_a_breaking_version_shows_its_impact()
    {
        await _fixture.Context.AddInitScriptAsync($$"""
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'contract-standin', profiles: [{ id: 'contract-standin',
              name: 'Children API stand-in (Development)', environmentType: 'Development', targetUrl: '{{_api.Url}}/',
              authentication: { requiresAuthentication: false, authenticationType: 'None' }, apiAuth: { authType: 'None' }, integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var alias = $"child-id-{suffix}";
            await ApprovedIdCollectionAsync(page, alias);

            // 1. REST and GraphQL: import by URL, bind the endpoint, generate tests and data, pin the id collection where a value is needed.
            var rest = await ImportByUrlAsync(page, $"{_api.Url}/openapi.json", $"Children REST {suffix}");
            await BindAsync(page, null);
            await GenerateAsync(page);
            await PinCollectionAsync(page, rest, alias);
            var graphQl = await ImportByUrlAsync(page, $"{_api.Url}/schema.graphql", $"Children GraphQL {suffix}");
            await BindAsync(page, "/graphql");
            await GenerateAsync(page);
            await PinCollectionAsync(page, graphQl, alias);

            // 2. The documented API: every ready test passes.
            _api.Broken = false;
            await page.GotoAsync($"{Page}?contract={rest}&view=tests", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var restOk = await RunReadyAsync(page, "REST ok");
            await page.GotoAsync($"{Page}?contract={graphQl}&view=tests", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var gqlOk = await RunReadyAsync(page, "GraphQL ok");

            // 3. The API breaks its contract: the same tests fail, with the contract findings.
            _api.Broken = true;
            await page.GotoAsync($"{Page}?contract={rest}&view=tests", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var restBroken = await RunReadyAsync(page, "REST broken");
            await page.GotoAsync($"{Page}?contract={graphQl}&view=tests", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var gqlBroken = await RunReadyAsync(page, "GraphQL broken");

            // 4. v2 of the REST contract renames the required name: Changes shows the breaking change and the tests it affects.
            await page.GotoAsync(Page, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("ct-open-import").ClickAsync(new() { Timeout = 120000 });
            await page.GetByTestId("ct-import-url-mode").CheckAsync();
            await page.GetByTestId("ct-import-url").FillAsync($"{_api.Url}/openapi-v2.json");
            await page.GetByTestId("ct-import-target").SelectOptionAsync(rest);
            await page.GetByTestId("ct-import-submit").ClickAsync();
            await Assertions.Expect(page.GetByTestId("ct-version")).ToHaveValueAsync("2", new() { Timeout = 60000 });
            await page.GetByTestId("ct-tab-changes").ClickAsync();
            await page.GetByTestId("ct-change-stats").WaitForAsync(new() { Timeout = 60000 });
            output.WriteLine("changes: " + string.Join(" | ", await page.GetByTestId("ct-change-row").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.class + ' ' + r.innerText.replace(/\\s+/g, ' '))")));
            output.WriteLine("breaking: " + string.Join(" ", await page.GetByTestId("ct-breaking-count").AllInnerTextsAsync()));
            output.WriteLine("impact: " + string.Join(" | ", await page.GetByTestId("ct-impact-row").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.level + ' ' + r.innerText.replace(/\\s+/g, ' '))")));

            output.WriteLine("requests: " + string.Join(", ", _api.Requests.Distinct()));
            // The documented API: every test that was ready passed, the id-dependent ones included once the collection was pinned.
            restOk.Calls.Should().HaveCount(2).And.OnlyContain(c => c.StartsWith("Passed"));
            gqlOk.Calls.Should().HaveCount(3).And.OnlyContain(c => c.StartsWith("Passed"), "including the negative test: a missing id is a GraphQL error");
            // The broken API: every positive test fails, with the contract findings that say why; the negative one still passes.
            restBroken.Calls.Should().HaveCount(2).And.OnlyContain(c => c.StartsWith("Failed"));
            restBroken.Findings.Should().Contain(f => f.StartsWith("contract:") && f.Contains("name"), "the required name is missing")
                .And.Contain(f => f.StartsWith("contract:") && f.Contains("id"), "id is a number where the contract says string");
            gqlBroken.Calls.Should().Contain(c => c.StartsWith("Passed") && c.Contains("without id"))
                .And.Contain(c => c.StartsWith("Failed") && c.Contains("query children")).And.Contain(c => c.StartsWith("Failed") && c.Contains("returns data") && c.Contains("query child"));
            gqlBroken.Findings.Should().Contain(f => f.Contains("name"), "name is null for a non-null field").And.Contain(f => f.Contains("age"), "age is a string where the schema says Int");
            // v2: the renamed required field is a breaking change and the REST tests are affected.
            // Child is only ever a response: the new required fullName is compatible (the server sends more), the removed name is breaking.
            int.Parse((await page.GetByTestId("ct-breaking-count").InnerTextAsync()).Trim()).Should().Be(1);
            var changeRows = await page.GetByTestId("ct-change-row").AllInnerTextsAsync();
            changeRows.Should().Contain(r => r.Contains("Child.fullName") && r.Contains("Compatible"))
                .And.Contain(r => r.Contains("Child.name") && r.Contains("Potentially breaking"));
            (await page.GetByTestId("ct-impact-row").CountAsync()).Should().BeGreaterThan(0, "the tests of the changed operations are listed");
            // Nothing else was called: the two contract documents, the four documented operations, no mutation.
            _api.Requests.Distinct().Should().BeSubsetOf(["GET /openapi.json", "GET /openapi-v2.json", "GET /schema.graphql", "GET /api/children", "GET /api/children/c-1", "POST /graphql"]);
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
