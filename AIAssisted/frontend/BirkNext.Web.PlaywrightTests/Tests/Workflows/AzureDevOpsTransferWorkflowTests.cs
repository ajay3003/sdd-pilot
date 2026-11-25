using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Azure DevOps – Test Plans and «Overfør testplan» end to end (2026-11-25) on an isolated BirkNext whose Azure DevOps is a local stand-in
/// (<see cref="AzureDevOpsStandIn"/>, reached through the development-only DevelopmentBaseUrl seam; nothing external, no real organization):
/// System Settings (English, Git Sources design) adds a connection, chooses a project, creates a NEW test plan and a NEW suite after a review, and
/// saves the destination locally; Manual Testing opens «Overfør testplan» on «Eksporter til fil» every time, downloads a file without writing to
/// Azure DevOps, shows the test lead approval warning for «Send direkte», cancels without writing, transfers after the final review, reports a
/// partial failure per case, lists the history with date and time, fetches results back, and leaves the plan's revision unchanged. Screenshots of
/// Git Sources and the new screens (desktop and 390 px) go to BIRKNEXT_PLAYWRIGHT_SHOTS for the UX review.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class AzureDevOpsTransferWorkflowTests : IAsyncLifetime
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
        var file = Path.Combine(ShotFolder, $"ado-{name}.png");
        if (element is not null) await element.ScreenshotAsync(new() { Path = file });
        else await page.ScreenshotAsync(new() { Path = file, FullPage = true });
    }

    private static Task<bool> OverflowsAsync(IPage page) => page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");

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
                2. **Given** a report for a closed period, **When** the report is submitted, **Then** the report is rejected with a reason.

                ## Requirements *(mandatory)*

                ### Functional Requirements

                - **FR-001**: The service MUST accept a valid attendance report and store it.
                - **FR-002**: The service MUST reject a report for a closed period.
                """);
        }
        return buffer.ToArray();
    }

    private static async Task<string> PreparePlanAsync(IPage page, string project)
    {
        await SyntheticProjects.ImportAndUseAsync(page, project, Archive(project));
        // The full discovery page is the plan's advanced tools (?advanced=1); an ordinary link is sent to Planlegg og klargjør.
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/test-plan-suggestions?advanced=1", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("[data-testid=tps][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });
        await page.GetByTestId("tps-discover-run").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex("candidates found", RegexOptions.IgnoreCase), new() { Timeout = 240000 });
        foreach (var title in new[] { "valid attendance report", "closed period" })
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
        await Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex("2 scenarios prepared as test cases", RegexOptions.IgnoreCase), new() { Timeout = 240000 });
        return Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
    }

    private static async Task<int> PlanRevisionAsync(IPage page, string draftId)
    {
        var response = await page.Context.APIRequest.GetAsync($"{BrowserTestServers.BackendUrl}/api/test-plan-suggestions/drafts/{draftId}");
        if (!response.Ok) return -1;
        using var json = System.Text.Json.JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.GetProperty("revision").GetInt32();
    }

    private static async Task OpenTransferAsync(IPage page, string draftId)
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/manual-testing/plan?draft={draftId}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("mtp-transfer").ClickAsync(new() { Timeout = 120000 });
        await page.GetByTestId("atp").WaitForAsync(new() { Timeout = 60000 });
    }

    [BrowserFact(AzureDevOpsStandIn.PortVariable, BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task A_tester_sets_up_a_new_test_plan_exports_to_file_by_default_and_transfers_directly_only_after_the_approval_warning_and_review()
    {
        await using var ado = new AzureDevOpsStandIn(AzureDevOpsStandIn.ConfiguredPort!.Value);
        ado.Start();
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1366, 900);
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var failedApi = new List<string>();
        page.Response += (_, r) => { if (r.Url.Contains("/api/") && r.Status >= 500) failedApi.Add($"{r.Status} {r.Url}"); };
        var project = SyntheticProjects.NewName("PwAdo");
        try
        {
            var draftId = await PreparePlanAsync(page, project);
            draftId.Should().NotBeNullOrEmpty();
            var revision = await PlanRevisionAsync(page, draftId);

            // ── 1. Git Sources first: the design reference.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/admin/system-settings?section=git-sources", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("gss").WaitForAsync(new() { Timeout = 120000 });
            await ShotAsync(page, "01-git-sources", page.GetByTestId("gss"));

            // ── 2. System Settings → Azure DevOps – Test Plans (English): connection, project, NEW plan and suite after review, local save.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/admin/system-settings?section=azure-devops-test-plans", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var ats = page.GetByTestId("ats");
            await Assertions.Expect(ats.Locator("#ats-title")).ToHaveTextAsync("Azure DevOps – Test Plans", new() { Timeout = 120000 });
            await Assertions.Expect(page.Locator("nav.ss-nav")).ToContainTextAsync("Azure DevOps – Test Plans");
            await Assertions.Expect(page.GetByTestId("ats-no-connection")).ToBeVisibleAsync();
            await ShotAsync(page, "02-settings-empty", ats);
            await page.GetByTestId("ats-org").FillAsync(AzureDevOpsStandIn.Organization);
            await page.GetByTestId("ats-pat").FillAsync("fake-pat-for-the-local-stand-in-0000000000000000000000");
            await page.GetByTestId("ats-add-save").ClickAsync();
            await page.GetByTestId("ats-check").ClickAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("ats-check-result")).ToHaveTextAsync($"Connected to {AzureDevOpsStandIn.Organization}.", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("ats-overall")).ToHaveTextAsync("Connected");

            await page.GetByTestId("ats-edit").ClickAsync();
            await page.Locator("[data-testid=adp-project-option]", new() { HasText = "M2LB" }).ClickAsync(new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adp-plans-empty")).ToContainTextAsync("No test plans were found in this project.", new() { Timeout = 60000 });
            await page.GetByTestId("adp-plans-empty-create").ClickAsync();
            await page.GetByTestId("adp-plan-name").FillAsync("M2LB – Skolenærvær – Testplan");
            await page.GetByTestId("adp-plan-review").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adp-plan-confirm-name")).ToHaveTextAsync("M2LB – Skolenærvær – Testplan");
            await ShotAsync(page, "03-settings-create-plan-review", ats);
            ado.WriteCount.Should().Be(0, "nothing is created before «Create in Azure DevOps»");
            await page.GetByTestId("adp-plan-create-confirm").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adp-notice")).ToHaveTextAsync("The test plan \"M2LB – Skolenærvær – Testplan\" was created.", new() { Timeout = 60000 });
            ado.Plans.Should().ContainSingle(p => p.Name == "M2LB – Skolenærvær – Testplan");

            await page.GetByTestId("adp-suite-new").ClickAsync();
            await page.GetByTestId("adp-suite-name").FillAsync("Kritiske brukerreiser");
            await page.GetByTestId("adp-suite-review").ClickAsync();
            await page.GetByTestId("adp-suite-create-confirm").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adp-notice")).ToHaveTextAsync("The test suite \"Kritiske brukerreiser\" was created.", new() { Timeout = 60000 });
            ado.WriteCount.Should().Be(2, "one plan and one suite, each after its own review");

            // A second plan with the same name is refused before anything is sent.
            await page.GetByTestId("adp-plan-new").ClickAsync();
            await page.GetByTestId("adp-plan-name").FillAsync("m2lb – skolenærvær – testplan");
            await Assertions.Expect(page.GetByTestId("adp-plan-duplicate")).ToContainTextAsync("already exists");
            await Assertions.Expect(page.GetByTestId("adp-plan-review")).ToBeDisabledAsync();
            await page.GetByTestId("adp-plan-use-existing").ClickAsync();
            await page.Locator("[data-testid=adp-suite-option]", new() { HasText = "Kritiske brukerreiser" }).ClickAsync(new() { Timeout = 60000 });
            await ShotAsync(page, "04-settings-picker", ats);
            await page.GetByTestId("ats-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("ats-notice")).ToContainTextAsync("Nothing was changed in Azure DevOps.", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("ats-system-suite")).ToContainTextAsync("Kritiske brukerreiser");
            ado.WriteCount.Should().Be(2, "saving the default destination is local");
            await ShotAsync(page, "05-settings-saved", ats);
            await page.SetViewportSizeAsync(390, 844);
            (await OverflowsAsync(page)).Should().BeFalse("no horizontal page scroll at 390 px");
            await ShotAsync(page, "06-settings-390");
            await page.SetViewportSizeAsync(1366, 900);

            // ── 3. Overfør testplan: file is the default every time; the file download writes nothing to Azure DevOps.
            await OpenTransferAsync(page, draftId);
            await Assertions.Expect(page.GetByTestId("atp-panel")).ToHaveAttributeAsync("data-method", "file");
            await Assertions.Expect(page.GetByTestId("atp-file-safe")).ToHaveTextAsync("Ingenting blir endret i Azure DevOps.");
            var wizard = page.Locator("[data-testid=adx][aria-busy=false]");
            await wizard.WaitForAsync(new() { Timeout = 120000 });
            await page.GetByTestId("adx-include-unapproved").CheckAsync();
            await page.GetByTestId("adx-area").FillAsync("M2LB");
            await page.GetByTestId("adx-area").DispatchEventAsync("change");
            try { await Assertions.Expect(page.GetByTestId("adx-can-download")).ToContainTextAsync("klar", new() { Timeout = 60000 }); }
            catch (PlaywrightException) { await ShotAsync(page, "07-failed-file-export"); throw; }
            await ShotAsync(page, "07-transfer-file-default", page.GetByTestId("atp"));
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("adx-download-xlsx").ClickAsync());
            download.SuggestedFilename.Should().EndWith(".xlsx");
            ado.WriteCount.Should().Be(2, "a file export never writes to Azure DevOps");

            await page.GetByTestId("atp-method-direct").ClickAsync();
            await Assertions.Expect(page.Locator(".adt-warning-title")).ToHaveTextAsync("GODKJENNING FRA TESTLEDER KREVES");
            await page.GetByTestId("adt-consequences").Locator("summary").ClickAsync();
            await page.GetByTestId("adt-not-happen").Locator("summary").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-declare")).Not.ToBeCheckedAsync();
            await Assertions.Expect(page.GetByTestId("adt-continue")).ToBeDisabledAsync();
            await ShotAsync(page, "08-direct-warning", page.GetByTestId("atp"));
            await page.SetViewportSizeAsync(390, 844);
            (await OverflowsAsync(page)).Should().BeFalse();
            await ShotAsync(page, "09-direct-warning-390", page.GetByTestId("adt-warning"));
            await page.SetViewportSizeAsync(1366, 900);
            await page.GetByTestId("adt-cancel").ClickAsync();
            await Assertions.Expect(page.GetByTestId("atp-panel")).ToHaveAttributeAsync("data-method", "file");
            ado.WriteCount.Should().Be(2, "cancelling writes nothing");

            // Reopening (another tab and back, then a reload) starts on file again.
            await page.Locator("[data-testid=mtp-tool][data-tool=cases]").ClickAsync();
            await page.GetByTestId("mtp-transfer").ClickAsync();
            await Assertions.Expect(page.GetByTestId("atp-panel")).ToHaveAttributeAsync("data-method", "file");

            // ── 4. Send direkte: declaration → destination and choices → final review → transfer.
            await page.GetByTestId("atp-method-direct").ClickAsync();
            await page.GetByTestId("adt-declare").CheckAsync();
            ado.WriteCount.Should().Be(2, "the declaration alone sends nothing");
            await page.GetByTestId("adt-continue").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-destination-suite")).ToContainTextAsync("Kritiske brukerreiser", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adt-destination-source")).ToHaveTextAsync("Systemstandard");
            await page.GetByTestId("adt-include-unapproved").CheckAsync();
            await Assertions.Expect(page.GetByTestId("adt-case")).ToHaveCountAsync(2, new() { Timeout = 60000 });
            await ShotAsync(page, "10-direct-setup", page.GetByTestId("atp"));
            await page.GetByTestId("adt-to-review").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-review-create")).ToHaveTextAsync("2");
            await Assertions.Expect(page.GetByTestId("adt-send")).ToHaveTextAsync("Bekreft og send til Azure DevOps");
            await ShotAsync(page, "11-direct-review", page.GetByTestId("atp"));
            ado.WriteCount.Should().Be(2, "no remote write before the final confirmation");
            await page.GetByTestId("adt-send").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-result-status")).ToHaveTextAsync("Overføring fullført", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adt-not-execution")).ToContainTextAsync("ikke at testene er kjørt eller bestått");
            ado.Items.Should().HaveCount(2);
            ado.SuiteCases.Values.SelectMany(v => v).Should().HaveCount(2);
            await ShotAsync(page, "12-direct-result", page.GetByTestId("atp"));

            // ── 5. A second transfer: linked cases are updated, not duplicated; a refused create is reported per case.
            await page.GetByTestId("adt-done").ClickAsync();
            await page.GetByTestId("atp-method-direct").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-declare")).Not.ToBeCheckedAsync(); // the declaration is renewed for every transfer
            await page.GetByTestId("adt-declare").CheckAsync();
            await page.GetByTestId("adt-continue").ClickAsync();
            await page.GetByTestId("adt-include-unapproved").CheckAsync();
            var cases = page.GetByTestId("adt-case");
            await Assertions.Expect(cases).ToHaveCountAsync(2, new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("adt-case-linked")).ToHaveCountAsync(2);
            await Assertions.Expect(page.GetByTestId("adt-to-review")).ToBeDisabledAsync(); // nothing changed: every case is suggested «Hopp over»
            await cases.Nth(0).Locator("[data-testid=adt-case-action][data-value=Update]").ClickAsync();
            await cases.Nth(1).Locator("[data-testid=adt-case-action][data-value=Create]").ClickAsync();
            await Assertions.Expect(cases.Nth(1)).ToHaveAttributeAsync("data-action", "Create", new() { Timeout = 60000 });
            await page.GetByTestId("adt-to-review").ClickAsync();
            ado.Refuse = ("/wit/workitems/$Test", HttpStatusCode.BadRequest);
            await page.GetByTestId("adt-send").ClickAsync();
            await Assertions.Expect(page.GetByTestId("adt-result-status")).ToHaveTextAsync("Delvis overført", new() { Timeout = 60000 });
            await Assertions.Expect(page.Locator("[data-testid=adt-result-case][data-outcome=Failed]")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("[data-testid=adt-result-case][data-outcome=Updated]")).ToHaveCountAsync(1);
            ado.Items.Should().HaveCount(2, "the update changed a test case, the refused create made none");
            await ShotAsync(page, "13-direct-partial", page.GetByTestId("atp"));

            // ── 6. History and results fetched back.
            await page.GetByTestId("adt-done").ClickAsync();
            await Assertions.Expect(page.GetByTestId("atp-history-status")).ToHaveTextAsync(["Delvis overført", "Overført", "Fil laget"], new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("atp-history-item").First).ToContainTextAsync(" kl. ");
            var done = DateTimeOffset.UtcNow.AddMinutes(-3);
            var ids = ado.Items.Keys.OrderBy(i => i).ToList();
            ado.LastResults[ids[0]] = ("Passed", 701, 200001, done, "Kari Tester");
            ado.LastResults[ids[1]] = ("Failed", 701, 200002, done, "Kari Tester");
            var writes = ado.WriteCount;
            await page.GetByTestId("atp-fetch").ClickAsync();
            await Assertions.Expect(page.GetByTestId("atp-fetch-summary")).ToContainTextAsync("2 nye resultater", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("atp-results-summary")).ToContainTextAsync("1 bestått · 1 feilet");
            ado.WriteCount.Should().Be(writes, "fetching results is read-only");
            await ShotAsync(page, "14-history-and-results", page.GetByTestId("atp"));
            await page.Locator("[data-testid=mtp-tool][data-tool=cases]").ClickAsync();

            (await PlanRevisionAsync(page, draftId)).Should().Be(revision, "neither a file export nor a transfer changes the BirkNext plan");
            pageErrors.Should().BeEmpty();
            failedApi.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
