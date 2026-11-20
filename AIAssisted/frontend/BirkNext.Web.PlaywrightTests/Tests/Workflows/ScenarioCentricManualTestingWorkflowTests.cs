using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Scenario-centric Plan &amp; prepare, end to end on an isolated BirkNext with a synthetic project shaped like M2LB's SkoleService specification
/// (own wording: user stories with numbered Given/When/Then acceptance scenarios, functional requirements in their own section, none named by
/// the scenario). A tester chooses three scenarios, opens the "child past compulsory school age" scenario, confirms a suggested requirement,
/// translates the scenario through the LibreTranslate provider (a local stand-in: nothing external), edits the Norwegian text, adds a second
/// test case beneath the scenario, reloads, asks for clarification and approves, reads the version history and exports the scenario's cases in
/// Norwegian to an Azure DevOps XLSX file whose content is checked.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ScenarioCentricManualTestingWorkflowTests : IAsyncLifetime
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static async Task ReadyAsync(IPage page, string testId) =>
        await page.Locator($"[data-testid={testId}][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });

    private static Task MessageAsync(IPage page, string pattern) =>
        Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex(pattern, RegexOptions.IgnoreCase), new() { Timeout = 240000 });

    /// <summary>An SDD project shaped like SkoleService (own synthetic wording).</summary>
    private static byte[] Archive(string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"{name}/{path}").Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Add(Spec, $"""
                # Feature Specification: SkoleService ({name}) - school attendance reporting

                ## User Scenarios & Testing *(mandatory)*

                ### User Story 1 - Accept a valid report (Priority: P1)

                A reporter submits an attendance report and it is accepted.

                **Acceptance Scenarios**:

                1. **Given** a valid attendance report, **When** the report is submitted, **Then** the report is accepted and stored.

                ### User Story 6 - Expected population and coverage (Priority: P2)

                At lock the service works out which children should have been reported.

                **Independent Test**: Set up placements and birth dates for a set of children, lock the period and verify every child lands in the right bucket.

                **Acceptance Scenarios**:

                1. **Given** a child placed only in a state institution during the period, **When** the period locks, **Then** the child is not expected.
                2. **Given** a child past compulsory school age, **When** the period locks, **Then** the child is not expected, even with an upper secondary education relationship; an accepted report for the child is counted as received, not expected.

                ## Requirements *(mandatory)*

                ### Functional Requirements

                - **FR-001**: The service MUST accept a valid attendance report and store it.
                - **FR-027**: A child MUST be counted as expected only when the child is placed in the period, is of compulsory school age and has an education relationship overlapping the period; children past compulsory school age are not expected.
                - **FR-029**: An accepted report for a child outside the expected population MUST be counted as received, not expected.
                - **FR-050**: Operations MUST be alerted when the message queue is unavailable for more than ten minutes.
                """);
            Add("SkoleService/specs/001-skoleservice/tasks.md", "# Tasks: SkoleService\n\n- [ ] T001 [US6] Expected population (FR-027)\n");
        }
        return buffer.ToArray();
    }

    private static async Task<string> SelectAsync(IPage page, string title)
    {
        var candidate = page.Locator("li[data-testid^=tps-candidate-]", new() { HasText = title }).First;
        await candidate.WaitForAsync(new() { Timeout = 60000 });
        var id = (await candidate.GetAttributeAsync("data-testid"))!["tps-candidate-".Length..];
        await page.GetByTestId($"tps-select-{id}").CheckAsync();
        return id;
    }

    private static async Task<ILocator> OpenScenarioAsync(IPage page, string key)
    {
        var scenario = page.Locator($"[data-testid=tps-scenario][data-scenario=\"{key}\"]");
        await scenario.WaitForAsync(new() { Timeout = 60000 });
        if (await scenario.GetAttributeAsync("data-open") != "true") await scenario.GetByTestId("tps-scenario-toggle").ClickAsync();
        await Assertions.Expect(scenario).ToHaveAttributeAsync("data-open", "true");
        return scenario;
    }

    private static async Task<ILocator> OpenCaseAsync(IPage page, string caseId)
    {
        var item = page.GetByTestId($"tps-case-{caseId}");
        await item.WaitForAsync(new() { Timeout = 60000 });
        if ((await item.GetAttributeAsync("class"))?.Contains("is-open") != true) await item.Locator(".tps-case-head").ClickAsync();
        return item;
    }

    private static async Task SetAsync(ILocator input, string value)
    {
        await input.FillAsync(value);
        await input.DispatchEventAsync("change");
    }

    [BrowserFact(LibreTranslateStandIn.PortVariable, BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task A_tester_prepares_cases_under_a_scenario_links_a_requirement_translates_reviews_and_exports_in_Norwegian()
    {
        await using var translator = new LibreTranslateStandIn(LibreTranslateStandIn.ConfiguredPort!.Value);
        translator.Start();
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var project = SyntheticProjects.NewName("PwScenario");
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, Archive(project));

            // 1–5. Plan & prepare: discover and choose three scenarios.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-plan-suggestions", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "tps");
            await SyntheticProjects.ExpectSelectedProjectAsync(page, project);
            await page.GetByTestId("tps-discover-run").ClickAsync();
            await MessageAsync(page, "candidates found");
            var s42 = await SelectAsync(page, "past compulsory school age");
            await SelectAsync(page, "state institution");
            await SelectAsync(page, "valid attendance report");
            await page.GetByTestId("tps-prepare").ClickAsync();
            // A bulk selection may first show a preparation preview (what is selected, what is missing); confirm it when it is shown.
            var confirm = page.GetByTestId("tps-preview-confirm");
            for (var i = 0; i < 60; i++)
            {
                if (await confirm.IsVisibleAsync()) { await confirm.ClickAsync(); break; }
                if (Regex.IsMatch(await page.GetByTestId("tps-message").InnerTextAsync(), "prepared as test cases")) break;
                await page.WaitForTimeoutAsync(500);
            }
            await MessageAsync(page, "3 scenarios prepared as test cases");

            // 6–8. The scenario is the parent: its purpose, source and its generated case (a procedure, not one copied line).
            var scenario = await OpenScenarioAsync(page, s42);
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-overview")).ToContainTextAsync("a child past compulsory school age");
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-evidence")).ToContainTextAsync(Spec);
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-counts")).ToHaveTextAsync("1 test case · 1 needs review");
            var primary = await OpenCaseAsync(page, s42);
            await Assertions.Expect(primary.GetByTestId("tps-case-generated")).ToHaveTextAsync("Generated — not reviewed");
            await Assertions.Expect(page.GetByTestId($"tps-step-action-{s42}-0")).ToHaveValueAsync(new Regex("^Prepare the starting situation"));
            await Assertions.Expect(page.GetByTestId($"tps-step-action-{s42}-1")).ToHaveValueAsync("Carry out or trigger: the period locks");
            await Assertions.Expect(page.GetByTestId($"tps-observation-missing-{s42}")).ToContainTextAsync("verification method not identified");

            // 17–19. Suggested requirements from the specification: FR-027 states the same behaviour; the unrelated FR-050 is not suggested.
            var suggestions = page.GetByTestId($"tps-suggestions-{s42}");
            var fr027 = suggestions.Locator("[data-testid=tps-suggestion][data-requirement=FR-027]");
            await Assertions.Expect(fr027).ToHaveAttributeAsync("data-level", "Semantic");
            await Assertions.Expect(fr027.GetByTestId("tps-suggestion-why")).ToContainTextAsync("compulsory school");
            await Assertions.Expect(suggestions.Locator("[data-testid=tps-suggestion][data-requirement=FR-050]")).ToHaveCountAsync(0);
            await fr027.GetByTestId("tps-suggestion-link").ClickAsync();
            var other = suggestions.Locator("[data-testid=tps-suggestion]").First;
            string? rejected = null;
            if (await other.CountAsync() > 0)
            {
                rejected = await other.GetAttributeAsync("data-requirement");
                await other.GetByTestId("tps-suggestion-reject").ClickAsync();
            }
            await SetAsync(page.GetByTestId($"tps-edit-observation-{s42}"), "Read the frozen expected-population report for the period");
            await primary.GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "Draft saved");
            await Assertions.Expect(primary.Locator("[data-testid=tps-link][data-requirement=FR-027]")).ToHaveAttributeAsync("data-origin", "manual");
            if (rejected is not null) await Assertions.Expect(suggestions.Locator($"[data-testid=tps-suggestion][data-requirement={rejected}]")).ToHaveCountAsync(0);

            // 9–11. Translate the scenario (LibreTranslate provider → local stand-in): Norwegian shown, the original one click away; edit by hand.
            await scenario.GetByTestId("tps-scenario-translate").ClickAsync();
            await MessageAsync(page, "Translated 1 test case to Norwegian");
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-title")).ToContainTextAsync("Barn over opplæringspliktig alder");
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-original")).ToHaveTextAsync(new Regex("Child past compulsory school age"));
            lock (translator.Bodies) translator.Bodies.Should().NotBeEmpty().And.OnlyContain(b => !b.Contains('⟦') && !b.Contains("FR-0"), "identifiers never reach the translator");
            await Assertions.Expect(page.GetByTestId("tps-nb-state")).ToContainTextAsync("Machine translation");
            await SetAsync(page.GetByTestId($"tps-nb-title-{s42}"), "Barn over opplæringspliktig alder ved periodelås");
            await primary.GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "Draft saved");
            await Assertions.Expect(page.GetByTestId("tps-nb-state")).ToContainTextAsync("edited by a person");

            // 12–14. Add another test case beneath the scenario, with a step and its expected result, and save.
            await scenario.GetByTestId("tps-scenario-add").ClickAsync();
            await page.GetByTestId("tps-scenario-add-title").FillAsync("No report submitted for the child");
            await page.GetByTestId("tps-scenario-add-confirm").ClickAsync();
            await MessageAsync(page, "added under the scenario");
            var added = scenario.Locator("[data-case^=manual-]").First;
            var addedId = (await added.GetAttributeAsync("data-case"))!;
            await page.GetByTestId($"tps-step-new-{addedId}").FillAsync("Lock the period without any report for the child");
            await page.GetByTestId($"tps-step-new-expected-{addedId}").FillAsync("The child is not in the expected population");
            await page.GetByTestId($"tps-step-add-{addedId}").ClickAsync();
            await SetAsync(page.GetByTestId($"tps-edit-observation-{addedId}"), "Read the expected-population report");
            await added.GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "Draft saved");
            await Assertions.Expect(page.GetByTestId("tps-save-state").First).ToContainTextAsync("All changes saved");

            // 15–16. Reload: both cases under the scenario, the Norwegian text and the original kept.
            await page.ReloadAsync();
            await ReadyAsync(page, "tps");
            scenario = await OpenScenarioAsync(page, s42);
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-counts")).ToContainTextAsync("2 test cases");
            await Assertions.Expect(scenario.Locator($"[data-case=\"{addedId}\"]")).ToHaveCountAsync(1);
            await scenario.GetByTestId("tps-scenario-language-toggle").ClickAsync();
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-title")).ToHaveTextAsync("Barn over opplæringspliktig alder ved periodelås");
            await scenario.GetByTestId("tps-scenario-language-toggle").ClickAsync();
            await Assertions.Expect(scenario.GetByTestId("tps-scenario-title")).ToContainTextAsync("Child past compulsory school age");

            // 23–25. Needs clarification (with a reason), then approve the complete case.
            primary = await OpenCaseAsync(page, s42);
            await page.GetByTestId($"tps-rationale-{s42}").FillAsync("How is the period lock triggered in the test environment?");
            await page.GetByTestId($"tps-clarify-{s42}").ClickAsync();
            await Assertions.Expect(primary.GetByTestId("tps-case-review-state")).ToContainTextAsync("clarification", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId($"tps-accept-{s42}")).ToBeEnabledAsync(new() { Timeout = 30000 });
            await page.GetByTestId($"tps-accept-{s42}").ClickAsync();
            await Assertions.Expect(primary.GetByTestId("tps-case-review-state")).ToContainTextAsync("Approved", new() { Timeout = 60000 });
            await Assertions.Expect(primary.GetByTestId("tps-case-testability-state")).Not.ToHaveTextAsync("Ready", new() { Timeout = 5000 }); // approval is not testability

            // 26–27. Version history lists the saved revisions.
            await page.GetByTestId("tps-history-open").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("tps-history-item").Nth(5)).ToBeVisibleAsync(new() { Timeout = 60000 });

            // 28–29. Export in Norwegian: each case in one language; the untranslated case in its original language.
            var draftId = Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
            draftId.Should().NotBeNullOrEmpty();
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await ReadyAsync(page, "mt");
            await page.Locator($"[data-testid=mt-plan-export][href*=\"{draftId}\"]").ClickAsync();
            var wizard = page.Locator("[data-testid=adx][aria-busy=false]");
            await wizard.WaitForAsync(new() { Timeout = 120000 });
            await page.GetByTestId("adx-include-unapproved").CheckAsync();
            await Assertions.Expect(page.GetByTestId("adx-unapproved-note")).ToBeVisibleAsync();
            await page.GetByTestId("adx-language").SelectOptionAsync("nb");
            await wizard.WaitForAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adx-language-summary")).ToContainTextAsync("1 in Norwegian", new() { Timeout = 60000 });
            await Assertions.Expect(page.Locator($"[data-testid=adx-case][data-case=\"{addedId}\"] [data-testid=adx-case-scenario]")).ToContainTextAsync("Child past compulsory school age");
            await Assertions.Expect(page.Locator($"[data-testid=adx-case][data-case=\"{addedId}\"] [data-testid=adx-case-ado]")).ToHaveTextAsync("New in Azure DevOps (no id)");
            await page.GetByTestId("adx-area").FillAsync($@"{project}\Manual");
            await page.GetByTestId("adx-area").DispatchEventAsync("change");
            await wizard.WaitForAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adx-can-download")).ToContainTextAsync("ready", new() { Timeout = 60000 });
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("adx-download-xlsx").ClickAsync());
            var file = Path.Combine(Path.GetTempPath(), $"{project}-{download.SuggestedFilename}");
            await download.SaveAsAsync(file);
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var xml = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("xl/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
                xml.Should().Contain("Barn over opplæringspliktig alder ved periodelås", "the approved scenario case is exported in its saved Norwegian text");
                xml.Should().Contain("Utfør eller utløs", "its steps are Norwegian too (one language per case)");
                xml.Should().Contain("Lock the period without any report for the child", "the untranslated case is exported in its original language");
                xml.Should().Contain("under the scenario", "the Description says which scenario a hand-written case belongs to");
            }
            finally { File.Delete(file); }

            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
