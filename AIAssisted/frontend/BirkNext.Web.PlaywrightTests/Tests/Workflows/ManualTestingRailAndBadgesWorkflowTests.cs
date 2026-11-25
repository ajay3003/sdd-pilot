using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Three small Manual Testing changes (2026-11-25) in a real browser on an isolated BirkNext with a synthetic specification that has three functional
/// areas (bold section lines under Functional Requirements) and three user stories:
/// Finn og forstå → every functional area header has the same neutral background (open or closed, with or without the selected story), only the
/// selected user story is soft green with the blue bar; Planlegg og klargjør → each scenario row shows its US and AC badges from the saved identity
/// (AC 1 under US1 and AC 1 under US2 are different rows), the detail heading names the same AC, «Kildegrunnlag» starts collapsed, expands,
/// collapses and is collapsed again for another case; no horizontal scroll at 390 px. Screenshots go to BIRKNEXT_PLAYWRIGHT_SHOTS.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ManualTestingRailAndBadgesWorkflowTests : IAsyncLifetime
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static string ShotFolder => Environment.GetEnvironmentVariable("BIRKNEXT_PLAYWRIGHT_SHOTS") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "birknext-shots");

    private static async Task ShotAsync(IPage page, string name, ILocator? element = null)
    {
        Directory.CreateDirectory(ShotFolder);
        var file = Path.Combine(ShotFolder, $"mt-{name}.png");
        if (element is not null) await element.ScreenshotAsync(new() { Path = file });
        else await page.ScreenshotAsync(new() { Path = file, FullPage = true });
    }

    private static byte[] Archive(string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{name}/{Spec}").Open(), new UTF8Encoding(false));
            writer.Write($"""
                # Feature Specification: SkoleService ({name}) - school attendance reporting

                ## User Scenarios & Testing *(mandatory)*

                ### User Story 1 - Register a report (Priority: P1)

                A reporter submits an attendance report.

                **Acceptance Scenarios**:

                1. **Given** a valid attendance report, **When** the report is submitted, **Then** the report is accepted and stored (FR-001).
                2. **Given** a report without a school, **When** the report is submitted, **Then** the report is rejected with a reason (FR-002).

                ### User Story 2 - Classify a protected child (Priority: P1)

                Reports for children with a protected address are classified.

                **Acceptance Scenarios**:

                1. **Given** a child with a protected address, **When** the report is classified, **Then** the stored data for the child is protected and never shown to the school (FR-010).

                ### User Story 3 - Check report quality (Priority: P2)

                Reports are checked before the period locks.

                **Acceptance Scenarios**:

                1. **Given** a report with missing days, **When** quality control runs, **Then** the report is flagged for follow-up (FR-020).

                ## Requirements *(mandatory)*

                ### Functional Requirements

                **Intake and identity**

                - **FR-001**: The service MUST accept a valid attendance report and store it.
                - **FR-002**: The service MUST reject a report without a school.

                **Classification**

                - **FR-010**: Data for a child with a protected address MUST never be shown to the school.

                **Quality control**

                - **FR-020**: Reports with missing days MUST be flagged for follow-up.
                """);
        }
        return buffer.ToArray();
    }

    private static Task<string> BackgroundAsync(ILocator element) => element.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");

    private static Task<bool> OverflowsAsync(IPage page) => page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");

    [BrowserFact(BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task Areas_are_neutral_the_selected_story_is_green_rows_show_US_and_AC_and_Kildegrunnlag_starts_collapsed()
    {
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1366, 900);
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var project = SyntheticProjects.NewName("PwRail");
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, Archive(project));

            // ── Planlegg og klargjør: prepare every scenario through the full discovery page, then open the plan.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-plan-suggestions?advanced=1", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator("[data-testid=tps][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });
            await page.GetByTestId("tps-discover-run").ClickAsync();
            await Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex("candidates found", RegexOptions.IgnoreCase), new() { Timeout = 240000 });
            foreach (var title in new[] { "valid attendance report", "without a school", "protected address", "missing days" })
            {
                var candidate = page.Locator("li[data-testid^=tps-candidate-]", new() { HasText = title }).First;
                await candidate.WaitForAsync(new() { Timeout = 60000 });
                var id = (await candidate.GetAttributeAsync("data-testid"))!["tps-candidate-".Length..];
                await page.GetByTestId($"tps-select-{id}").CheckAsync();
            }
            await page.GetByTestId("tps-prepare").ClickAsync();
            var confirm = page.GetByTestId("tps-preview-confirm");
            for (var i = 0; i < 60; i++)
            {
                if (await confirm.IsVisibleAsync()) { await confirm.ClickAsync(); break; }
                if (Regex.IsMatch(await page.GetByTestId("tps-message").InnerTextAsync(), "prepared as test cases")) break;
                await page.WaitForTimeoutAsync(500);
            }
            await Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex("4 scenarios prepared as test cases", RegexOptions.IgnoreCase), new() { Timeout = 240000 });
            var draftId = Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
            // ── Finn og forstå: functional areas and user stories.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing/discover", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var groups = page.GetByTestId("mtd-group");
            try { await Assertions.Expect(groups.First).ToBeVisibleAsync(new() { Timeout = 120000 }); }
            catch (PlaywrightException) { await ShotAsync(page, "00-failed-no-areas"); throw; }
            // Areas are suggested from the requirements until a reviewer accepts them («Gjennomgå gruppering»): accept each suggestion (isolated data).
            if (await page.GetByTestId("mtd-review-grouping").CountAsync() > 0)
            {
                await page.GetByTestId("mtd-review-grouping").ClickAsync();
                foreach (var row in await page.GetByTestId("grv-row").AllAsync())
                    if (await row.GetByTestId("grv-choose").CountAsync() > 0) await row.GetByTestId("grv-choose").First.ClickAsync();
                await page.GetByTestId("grv-save").ClickAsync();
                await Assertions.Expect(groups.Nth(1)).ToBeVisibleAsync(new() { Timeout = 60000 });
            }
            await ShotAsync(page, "00-discover-rail");
            var groupTitles = string.Join(" | ", await page.Locator("[data-testid=mtd-group] .mtw-group-title").AllInnerTextsAsync());
            var mode = await page.GetByTestId("mtd-group-mode").InputValueAsync();
            (await groups.CountAsync()).Should().BeGreaterThanOrEqualTo(2, $"several functional areas (mode {mode}; groups: {groupTitles})");
            Console.WriteLine($"Functional areas shown: {groupTitles}");
            foreach (var toggle in await page.GetByTestId("mtd-group-toggle").AllAsync())
                if (await toggle.GetAttributeAsync("aria-expanded") == "false") await toggle.ClickAsync();
            var us1 = page.Locator("[data-testid=mtd-group] [data-testid=mtd-story]", new() { HasText = "US1" }).First;
            await us1.ClickAsync();
            await Assertions.Expect(us1).ToHaveAttributeAsync("aria-pressed", "true");

            var heads = await page.GetByTestId("mtd-group-toggle").AllAsync();
            var headColours = new List<string>();
            foreach (var h in heads) headColours.Add(await BackgroundAsync(h));
            headColours.Distinct().Should().ContainSingle("every functional area header has the same neutral background, also the one holding the selected story");
            var selected = await BackgroundAsync(us1);
            var middle = await BackgroundAsync(page.Locator(".mtw-story-card.is-story").First);
            selected.Should().Be(middle, "the selected story uses the middle column's soft-green story tint");
            var others = page.Locator("[data-testid=mtd-group] [data-testid=mtd-story]:not(.is-current)");
            (await others.CountAsync()).Should().BeGreaterThan(0);
            foreach (var o in await others.AllAsync()) (await BackgroundAsync(o)).Should().NotBe(selected, "unselected stories never look selected");
            (await BackgroundAsync(page.GetByTestId("mtd-group-toggle").First)).Should().NotBe(selected, "an area is never green");
            await ShotAsync(page, "01-areas-expanded", page.GetByTestId("mtd-stories"));

            // Collapsing the area that holds the selected story changes neither its colour nor the selection.
            var holdingKey = await page.GetByTestId("mtd-group").Filter(new() { Has = page.Locator("[data-testid=mtd-story].is-current") }).First.GetAttributeAsync("data-group");
            var holdingHead = page.Locator($"[data-testid=mtd-group][data-group=\"{holdingKey}\"] [data-testid=mtd-group-toggle]");
            var before = await BackgroundAsync(holdingHead);
            await holdingHead.ClickAsync();
            await Assertions.Expect(holdingHead).ToHaveAttributeAsync("aria-expanded", "false");
            (await BackgroundAsync(holdingHead)).Should().Be(before);
            await holdingHead.ClickAsync();
            await Assertions.Expect(page.Locator("[data-testid=mtd-story].is-current")).ToContainTextAsync("US1");
            await holdingHead.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(holdingHead).ToHaveAttributeAsync("aria-expanded", "false", new() { Timeout = 10000 });
            await ShotAsync(page, "02-area-collapsed", page.GetByTestId("mtd-stories"));

            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing/plan?draft={draftId}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("mtp-scenario").First.WaitForAsync(new() { Timeout = 120000 });
            await page.GetByTestId("mtp-technical").CheckAsync();

            var badges = new List<string>();
            foreach (var row in await page.GetByTestId("mtp-scenario").AllAsync())
                badges.Add($"{await row.GetByTestId("mtp-row-story").InnerTextAsync()}/{await row.GetByTestId("mtp-row-ac").InnerTextAsync()}");
            badges.Should().BeEquivalentTo(["US1/AC 1", "US1/AC 2", "US2/AC 1", "US3/AC 1"], "each row shows its own story and acceptance scenario; AC 1 under US1 and US2 are different rows");
            var protectedRow = page.GetByTestId("mtp-scenario").Filter(new() { HasText = "protected" }).First;
            await protectedRow.GetByTestId("mtp-case").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("mtp-scenario-heading")).ToHaveTextAsync(new Regex("^AC 1 — "));
            await Assertions.Expect(protectedRow.GetByTestId("mtp-case").First).ToHaveClassAsync(new Regex("is-selected"));

            var toggleKilde = page.GetByTestId("mtp-context-toggle");
            await Assertions.Expect(toggleKilde).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page.GetByTestId("mtp-context-body")).ToBeHiddenAsync();
            await ShotAsync(page, "03-plan-badges-kilde-collapsed");
            await toggleKilde.ClickAsync();
            await Assertions.Expect(page.GetByTestId("mtp-context-body")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("mtp-ctx-requirements")).ToContainTextAsync("FR-010");
            await ShotAsync(page, "04-kilde-expanded", page.GetByTestId("mtp-context"));
            await toggleKilde.ClickAsync();
            await Assertions.Expect(page.GetByTestId("mtp-context-body")).ToBeHiddenAsync();
            await toggleKilde.ClickAsync();
            await page.GetByTestId("mtp-scenario").Filter(new() { HasText = "missing days" }).First.GetByTestId("mtp-case").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("mtp-context-toggle")).ToHaveAttributeAsync("aria-expanded", "false", new() { Timeout = 10000 });

            await page.SetViewportSizeAsync(390, 844);
            (await OverflowsAsync(page)).Should().BeFalse("no horizontal page scroll at 390 px");
            await ShotAsync(page, "05-plan-390");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
