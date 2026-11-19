using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Performance Test Review end to end with the real k6 engine (Podman) against a local stand-in target, as a tester does it: add a scenario,
/// set a small workload and a P95 drift policy, save, check readiness, run, promote the run to the baseline, make the target slower, run again,
/// see the parameter changes against the baseline (results, regression comparison) and export the HTML report. Opt-in: start the stand-in
/// with performance-target/start-target.ps1 and set BIRKNEXT_PERFORMANCE_TARGET_URL (for example http://localhost:18091) next to the browser
/// server variables. No external system is contacted. Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class PerformanceTestReviewWorkflowTests : IAsyncLifetime
{
    public const string TargetVariable = "BIRKNEXT_PERFORMANCE_TARGET_URL";
    private const string EnvironmentId = "pt-standin";
    private BirkNextWebApplicationFixture _fixture = null!;
    private static readonly HttpClient Target = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static async Task<JsonElement> ControlAsync(string target, string query) =>
        JsonDocument.Parse(await Target.GetStringAsync($"{target}/__control{query}")).RootElement;

    private static async Task TabAsync(IPage page, string tab)
    {
        await page.GetByTestId($"pt-tab-{tab}").ClickAsync();
        await page.WaitForTimeoutAsync(500);
    }

    private static async Task SetAsync(IPage page, string testId, string value)
    {
        var field = page.GetByTestId(testId).First;
        await field.FillAsync(value);
        await field.DispatchEventAsync("change");
    }

    /// <summary>Starts a run from the Run tab and waits until the Results tab shows it completed; returns the run summary text.</summary>
    private static async Task<ILocator> RunAsync(IPage page, string? previousRun = null)
    {
        await TabAsync(page, "run");
        await Assertions.Expect(page.GetByTestId("pt-run-start")).ToBeEnabledAsync(new() { Timeout = 30000 });
        await page.GetByTestId("pt-run-start").ClickAsync();
        await TabAsync(page, "results");
        var summary = page.GetByTestId("pt-result-summary");
        // The Results tab shows the latest run: wait until it is this run (not the previous one) and it has completed.
        if (previousRun is not null) await Assertions.Expect(summary).Not.ToContainTextAsync($"Run {previousRun}", new() { Timeout = 120000 });
        await Assertions.Expect(summary).ToHaveAttributeAsync("data-state", "Completed", new() { Timeout = 300000 });
        return summary;
    }

    private static async Task<string> RunIdAsync(ILocator summary) => Regex.Match(await summary.InnerTextAsync(), @"Run ([0-9a-f]{8})").Groups[1].Value;

    private static double Milliseconds(string text) => double.Parse(Regex.Match(text, @"([\d.]+)\s*ms").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [BrowserFact(TargetVariable, BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task Scenario_run_baseline_slower_run_shows_the_regression_and_the_report_carries_it()
    {
        var target = Environment.GetEnvironmentVariable(TargetVariable)!.TrimEnd('/');
        (await ControlAsync(target, "?delay=20&reset=1")).GetProperty("delayMs").GetInt32().Should().Be(20, "the stand-in target answers");
        await _fixture.Context.AddInitScriptAsync($$"""
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: '{{EnvironmentId}}', profiles: [{
              id: '{{EnvironmentId}}', name: 'Performance stand-in (local)', environmentType: 'Local', targetUrl: '{{target}}/',
              authentication: { requiresAuthentication: false, authenticationType: 'None' }, integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        try
        {
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/performance-test-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("pt-tab-scenario").WaitForAsync(new() { Timeout = 120000 });

            // 1. Scenario: one read-only request to the endpoint under test.
            if (await page.GetByTestId("pt-new").CountAsync() > 0) await page.GetByTestId("pt-new").ClickAsync();
            await SetAsync(page, "pt-name", "Items API load");
            await TabAsync(page, "scenario");
            await SetAsync(page, "pt-scenario-name", "List items");
            await page.GetByTestId("pt-add-step").ClickAsync();
            await SetAsync(page, "pt-step-path", "/api/items");
            await SetAsync(page, "pt-step-delay", "50");

            // 2. A small workload (2 virtual users, 12 s steady state) and a P95 drift policy (+20 %), so the baseline comparison is assessed.
            await TabAsync(page, "workload");
            foreach (var (field, value) in new[] { ("pt-vus", "2"), ("pt-warmup", "0"), ("pt-rampup", "0"), ("pt-steady", "12"), ("pt-rampdown", "0") })
                await SetAsync(page, field, value);
            await TabAsync(page, "thresholds");
            await page.GetByTestId("pt-add-drift").ClickAsync();

            // 3. Save v1 and check readiness: the k6 container must reach the target.
            await page.GetByTestId("pt-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("pt-save-state")).ToContainTextAsync("Saved", new() { Timeout = 30000 });
            await TabAsync(page, "readiness");
            await page.GetByTestId("pt-check-readiness").ClickAsync();
            // The k6 container's network check (fresh, or a recent one for the same target reused within its validity) says the target is reachable.
            await Assertions.Expect(page.Locator("[data-testid=pt-readiness-item][data-key=network]")).ToContainTextAsync("Ready", new() { Timeout = 180000 });
            await Assertions.Expect(page.Locator("[data-testid=pt-readiness-item][data-blocking=true]")).ToHaveCountAsync(0);

            // 4. First run on the fast target.
            var first = await RunAsync(page);
            var firstP95 = Milliseconds((await page.GetByTestId("pt-result-percentiles").InnerTextAsync()).Split('·')[2]);
            firstP95.Should().BeLessThan(150, "the stand-in answers in about 20 ms");
            (await first.InnerTextAsync()).Should().Contain("Thresholds passed");
            var firstRun = await RunIdAsync(first);
            firstRun.Should().NotBeEmpty();
            (await ControlAsync(target, "")).GetProperty("served").GetInt32().Should().BeGreaterThan(20, "k6 put load on the stand-in");

            // 5. Promote it to the baseline (approved at once).
            await TabAsync(page, "history");
            await page.GetByTestId("pt-promote-open").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("pt-promote-eligibility")).ToContainTextAsync("Enough samples for P95");
            if (await page.GetByTestId("pt-promote-accept").CountAsync() > 0) await page.GetByTestId("pt-promote-accept").CheckAsync();
            if (await page.GetByTestId("pt-promote-reason").CountAsync() > 0) await SetAsync(page, "pt-promote-reason", "First run on the stand-in target");
            await page.GetByTestId("pt-promote-confirm").ClickAsync();
            await Assertions.Expect(page.Locator("[data-testid=pt-baseline-row][data-status=Active]")).ToHaveCountAsync(1, new() { Timeout = 30000 });

            // 6. The target becomes ten times slower; the second run shows the change against the baseline.
            await ControlAsync(target, "?delay=250&reset=1");
            var second = await RunAsync(page, firstRun);
            var secondP95 = Milliseconds((await page.GetByTestId("pt-result-percentiles").InnerTextAsync()).Split('·')[2]);
            secondP95.Should().BeGreaterThan(200);
            await Assertions.Expect(page.GetByTestId("pt-result-baseline")).ToHaveAttributeAsync("data-state", "Regression");
            await Assertions.Expect(page.GetByTestId("pt-result-baseline")).ToContainTextAsync("P95 latency");
            await Assertions.Expect(page.GetByTestId("pt-result-baseline-verdict")).ToContainTextAsync("Regression against baseline v1");
            (await second.GetAttributeAsync("class")).Should().NotContain("prr-tone-ok", "passed thresholds never read as all clear when the run regressed");

            // 7. The regression comparison: P95 is a potential regression (one run after the baseline; three confirm it).
            await TabAsync(page, "compare");
            if (await page.GetByTestId("pt-regression-go").CountAsync() > 0) await page.GetByTestId("pt-regression-go").ClickAsync();
            await Assertions.Expect(page.GetByTestId("pt-regression-result")).ToHaveAttributeAsync("data-verdict", "PotentialRegression", new() { Timeout = 30000 });
            await Assertions.Expect(page.Locator("[data-testid=pt-regression-metric][data-key=p95]")).ToHaveAttributeAsync("data-verdict", "PotentialRegression");

            // 8. The HTML report carries the run, its thresholds and the baseline comparison.
            await TabAsync(page, "results");
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("pt-export").ClickAsync());
            download.SuggestedFilename.Should().MatchRegex(@"^performance-test-[0-9a-f]+\.html$");
            var file = Path.Combine(Path.GetTempPath(), $"pt-e2e-{Guid.NewGuid():N}.html");
            await download.SaveAsAsync(file);
            try
            {
                var html = await File.ReadAllTextAsync(file);
                html.Should().Contain("Baseline comparison (v1)").And.Contain("P95 latency").And.Contain("Baseline: <strong>Regression against baseline v1</strong>")
                    .And.Contain("docker.io/grafana/k6", "the report states which engine measured it");
            }
            finally { File.Delete(file); }
            pageErrors.Should().BeEmpty();
        }
        finally
        {
            await ControlAsync(target, "?delay=20");
            await page.CloseAsync();
        }
    }
}
