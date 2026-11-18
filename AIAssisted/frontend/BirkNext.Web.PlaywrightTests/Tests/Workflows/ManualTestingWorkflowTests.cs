using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Manual Testing, end to end on the test's own synthetic project: discover scenarios from the specification → prepare one as a test case →
/// edit and save it → reload and find exactly the saved content → run it (a prepared case is not an executed case) → record results →
/// complete → the completed run is read-only and listed in history, and survives a reload.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ManualTestingWorkflowTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static async Task ReadyAsync(IPage page, string testId) =>
        await page.Locator($"[data-testid={testId}][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });

    private static Task MessageAsync(IPage page, string testId, string pattern) =>
        Assertions.Expect(page.GetByTestId(testId)).ToHaveTextAsync(new Regex(pattern, RegexOptions.IgnoreCase), new() { Timeout = 240000 });

    [BrowserFact]
    public async Task Discover_prepare_save_run_and_review_history_on_a_synthetic_project()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var project = SyntheticProjects.NewName("PwManual");
        var stepText = $"PW-STEP {project}: open the loan list";
        var expected = $"PW-EXPECTED {project}: the new loan is listed";
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));

            // Plan & prepare: discovery reads the imported specification.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-plan-suggestions", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "tps");
            await SyntheticProjects.ExpectSelectedProjectAsync(page, project);
            await page.GetByTestId("tps-discover-run").ClickAsync();
            await MessageAsync(page, "tps-message", "candidates found");
            var candidates = page.Locator("li[data-testid^=tps-candidate-]");
            (await candidates.CountAsync()).Should().BeGreaterThan(0, "the specification's requirements yield scenarios");
            var candidateId = (await candidates.First.GetAttributeAsync("data-testid"))!["tps-candidate-".Length..];
            (await candidates.First.InnerTextAsync()).Should().MatchRegex("FR-00[12]", "a scenario names the requirement it comes from");

            await page.GetByTestId($"tps-select-{candidateId}").ClickAsync();
            await page.GetByTestId("tps-prepare").ClickAsync();
            await MessageAsync(page, "tps-message", "prepared as test cases");
            var testCase = page.GetByTestId($"tps-case-{candidateId}");
            await testCase.WaitForAsync();
            if ((await testCase.GetAttributeAsync("class"))?.Contains("is-open") != true) await testCase.Locator(".tps-case-head").ClickAsync();

            // Edit: a new step with its expected result is an unsaved change until saved.
            // Steps are numbered from 0: the added step is the row at the old count, once it has rendered.
            var stepRows = page.Locator($"[data-testid=\"tps-steps-{candidateId}\"] .tps-step-row");
            var before = await stepRows.CountAsync();
            await page.GetByTestId($"tps-step-new-{candidateId}").FillAsync(stepText);
            await page.GetByTestId($"tps-step-add-{candidateId}").ClickAsync();
            await Assertions.Expect(stepRows).ToHaveCountAsync(before + 1);
            await Assertions.Expect(page.GetByTestId($"tps-step-action-{candidateId}-{before}")).ToHaveValueAsync(stepText);
            var expectedInput = page.GetByTestId($"tps-step-expected-{candidateId}-{before}");
            await expectedInput.FillAsync(expected);
            await expectedInput.DispatchEventAsync("change");
            await Assertions.Expect(page.GetByTestId("tps-unsaved").First).ToBeVisibleAsync();
            // The draft's save bar is repeated in each open area (one action); save from the case being edited, as a tester would.
            await testCase.GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "tps-message", "Draft saved");

            // Reload: exactly the saved content is there (persisted, not held by the tab).
            await page.ReloadAsync();
            await ReadyAsync(page, "tps");
            var saved = page.GetByTestId($"tps-case-{candidateId}");
            if ((await saved.GetAttributeAsync("class"))?.Contains("is-open") != true) await saved.Locator(".tps-case-head").ClickAsync();
            (await page.Locator($"[data-testid=\"tps-steps-{candidateId}\"] input, [data-testid=\"tps-steps-{candidateId}\"] textarea")
                .EvaluateAllAsync<string[]>("els => els.map(e => e.value)")).Should().Contain([stepText, expected]);

            // Execute: start a run of the saved case, record every step, complete.
            await saved.GetByTestId("tps-run-case").ClickAsync();
            await ReadyAsync(page, "mte");
            await Assertions.Expect(page.GetByTestId("mte-start")).ToContainTextAsync(stepText);
            await page.GetByTestId("mte-start-env").FillAsync("Playwright isolated (disposable)");
            await page.GetByTestId("mte-start-run").ClickAsync();
            await page.GetByTestId("mte-runner").WaitForAsync(new() { Timeout = 120000 });
            var runId = Regex.Match(page.Url, @"[?&]run=([^&]+)").Groups[1].Value;
            runId.Should().NotBeNullOrEmpty("the run has its own address");
            await Assertions.Expect(page.GetByTestId("mte-complete")).ToBeDisabledAsync(new() { Timeout = 30000 });

            var steps = await page.GetByTestId("mte-step").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-step'))");
            steps.Should().NotBeEmpty();
            foreach (var n in steps) await page.GetByTestId($"mte-outcome-{n}-Pass").CheckAsync();
            await page.GetByTestId("mte-save").ClickAsync();
            await MessageAsync(page, "mte-message", "Progress saved");
            await page.GetByTestId("mte-complete").ClickAsync();
            await MessageAsync(page, "mte-message", "Run completed");
            await Assertions.Expect(page.GetByTestId("mte-run-state")).ToHaveTextAsync("Passed");
            await Assertions.Expect(page.Locator("[data-testid=mte-runner] input[type=radio], [data-testid=mte-save]")).ToHaveCountAsync(0);

            // History lists the completed run; after a reload the run is still completed and read-only.
            await page.GetByTestId("mte-back").ClickAsync();
            await ReadyAsync(page, "mte");
            var row = page.Locator($"[data-testid=mte-history-row][data-run=\"{runId}\"]");
            await Assertions.Expect(row).ToContainTextAsync("Playwright isolated (disposable)");
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing/execute?run={runId}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "mte");
            await Assertions.Expect(page.GetByTestId("mte-run-state")).ToHaveTextAsync("Passed");
            await Assertions.Expect(page.Locator("[data-testid=mte-runner] input[type=radio], [data-testid=mte-save]")).ToHaveCountAsync(0);
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
