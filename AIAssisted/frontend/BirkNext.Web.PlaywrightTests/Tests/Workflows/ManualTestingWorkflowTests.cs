using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Manual Testing, end to end on the test's own synthetic project: discover scenarios from the specification → prepare one as a test case →
/// edit and save it → reload and find exactly the saved content → approve it → the Manual Testing home lists the plan → export it to an XLSX file
/// for Azure DevOps Test Plans and read the file back. Execution happens in Azure DevOps, not in BirkNext.
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
    public async Task Discover_prepare_save_approve_and_export_on_a_synthetic_project()
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

            // Approve: the export's content rules apply (an expected result for the case and for every step), and a review decision is recorded
            // on saved content (the button is disabled while edits are unsaved). Fill what the evidence left open, save, then approve.
            var changed = false;
            var expectedCase = page.GetByTestId($"tps-edit-expected-{candidateId}");
            if (await expectedCase.CountAsync() > 0 && string.IsNullOrWhiteSpace(await expectedCase.InputValueAsync()))
            {
                await expectedCase.FillAsync(expected);
                await expectedCase.DispatchEventAsync("change");
                changed = true;
            }
            var stepExpected = page.Locator($"[data-testid^=\"tps-step-expected-{candidateId}-\"]");
            for (var i = 0; i < await stepExpected.CountAsync(); i++)
            {
                if (!string.IsNullOrWhiteSpace(await stepExpected.Nth(i).InputValueAsync())) continue;
                await stepExpected.Nth(i).FillAsync($"PW-STEP-EXPECTED {i + 1}");
                await stepExpected.Nth(i).DispatchEventAsync("change");
                changed = true;
            }
            if (changed)
            {
                await saved.GetByTestId("tps-save").ClickAsync();
                await MessageAsync(page, "tps-message", "Draft saved");
            }
            var approve = page.GetByTestId($"tps-accept-{candidateId}");
            await Assertions.Expect(approve).ToBeEnabledAsync(new() { Timeout = 30000 });
            await approve.ClickAsync();
            // The decision is saved by the API: wait until the case shows it before leaving the page.
            await Assertions.Expect(page.GetByTestId($"tps-case-{candidateId}")).ToContainTextAsync("Approved", new() { Timeout = 60000 });
            var draftId = Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
            draftId.Should().NotBeNullOrEmpty("the saved plan has its own address");

            // Manual Testing home: the saved plan with its approved case, and the way to export it.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "mt");
            await Assertions.Expect(page.GetByTestId("mt-plan").First).ToContainTextAsync("1 approved", new() { Timeout = 60000 });
            await page.Locator($"[data-testid=mt-plan-export][href*=\"{draftId}\"]").ClickAsync();

            // Export to a file for Azure DevOps Test Plans (BirkNext never writes to Azure DevOps): the XLSX carries the saved step.
            var wizard = page.Locator("[data-testid=adx][aria-busy=false]");
            await wizard.WaitForAsync(new() { Timeout = 120000 });
            await page.GetByTestId("adx-area").FillAsync($@"{project}\Manual");
            await page.GetByTestId("adx-area").DispatchEventAsync("change");
            await wizard.WaitForAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adx-can-download")).ToContainTextAsync("ready", new() { Timeout = 60000 });
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("adx-download-xlsx").ClickAsync());
            download.SuggestedFilename.Should().MatchRegex(@"-azure-test-plans-r\d+\.xlsx$");
            var file = Path.Combine(Path.GetTempPath(), $"{project}-{download.SuggestedFilename}");
            await download.SaveAsAsync(file);
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(file);
                var xml = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("xl/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
                xml.Should().Contain("Step Action").And.Contain(System.Security.SecurityElement.Escape(stepText), "the exported file carries the saved step");
            }
            finally { File.Delete(file); }
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
