using System.IO.Compression;
using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Manual testing on the real M2LB archive, end to end, as a tester does it (opt-in: set <c>BIRKNEXT_M2LB_MANUAL_ARCHIVE</c> to a local M2LB ZIP
/// and point the browser servers at an isolated BirkNext with an empty database). Import → the home asks to choose the specification (M2LB is a
/// monorepo) → choose a module in the Specification Explorer → Discover lists its requirements → discover scenarios → prepare a selection → fill
/// what the evidence left open, save, approve → the home offers the export → export an XLSX for Azure DevOps Test Plans and read it back. Nothing
/// is written to Azure DevOps and no external system is contacted. Guide: docs/guides/manual-testing-m2lb.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class RealM2lbManualTestingTests : IAsyncLifetime
{
    public const string ArchiveVariable = "BIRKNEXT_M2LB_MANUAL_ARCHIVE";
    public const string ModuleVariable = "BIRKNEXT_M2LB_MANUAL_MODULE";
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static Task ReadyAsync(IPage page, string testId) =>
        page.Locator($"[data-testid={testId}][aria-busy=false]").WaitForAsync(new() { Timeout = 240000 });

    private static Task MessageAsync(IPage page, string pattern) =>
        Assertions.Expect(page.GetByTestId("tps-message")).ToHaveTextAsync(new Regex(pattern, RegexOptions.IgnoreCase), new() { Timeout = 300000 });

    private static async Task GoAsync(IPage page, string route, string readyTestId)
    {
        await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/{route}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await ReadyAsync(page, readyTestId);
    }

    [BrowserFact(ArchiveVariable, BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task A_tester_goes_from_the_M2LB_archive_to_an_Azure_DevOps_import_file()
    {
        var archive = Environment.GetEnvironmentVariable(ArchiveVariable)!;
        File.Exists(archive).Should().BeTrue($"{ArchiveVariable} names an existing M2LB archive");
        var module = Environment.GetEnvironmentVariable(ModuleVariable) ?? "SkoleService";
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        try
        {
            // 1. Import, and the result leads to Manual Testing.
            await SyntheticProjects.ImportAndUseAsync(page, "M2LB", await File.ReadAllBytesAsync(archive));

            // 2. A monorepo: the home asks for the specification first, and Discover says why (counts, no silent choice).
            await GoAsync(page, "manual-testing", "mt");
            await Assertions.Expect(page.GetByTestId("mt-next-action")).ToHaveTextAsync("Choose the specification");
            await page.GetByTestId("mt-next-action").ClickAsync();
            await ReadyAsync(page, "mtd");
            await Assertions.Expect(page.GetByTestId("mtd-req-none")).ToHaveAttributeAsync("data-reason", "choose-specification");
            await Assertions.Expect(page.GetByTestId("mtd-req-none")).ToContainTextAsync(new Regex(@"\d+ specifications in \d+ modules"));

            // 3. Choose the module (and its feature or specification when it has several) in Manual Testing's own lists.
            var picker = page.Locator("[data-testid=ssp][aria-busy=false]");
            await Assertions.Expect(page.GetByTestId("ssp-current")).ToHaveAttributeAsync("data-state", "module", new() { Timeout = 60000 });
            await page.GetByTestId("ssp-module").SelectOptionAsync(module);
            await picker.WaitForAsync(new() { Timeout = 120000 });
            if (await page.GetByTestId("ssp-feature").CountAsync() > 0 && await page.GetByTestId("ssp-feature").InputValueAsync() == "")
            {
                var first = (await page.Locator("[data-testid=ssp-feature] option").EvaluateAllAsync<string[]>("os => os.map(o => o.value).filter(Boolean)"))[0];
                await page.GetByTestId("ssp-feature").SelectOptionAsync(first);
                await picker.WaitForAsync(new() { Timeout = 120000 });
            }
            await Assertions.Expect(page.GetByTestId("ssp-current")).ToHaveAttributeAsync("data-state", "chosen", new() { Timeout = 120000 });
            await Assertions.Expect(page.GetByTestId("ssp-current")).ToContainTextAsync(module);

            // 4. Discover & understand lists the chosen specification's requirements as plain statements, without leaving Manual Testing.
            await ReadyAsync(page, "mtd");
            await Assertions.Expect(page.GetByTestId("mtd-req-summary")).ToContainTextAsync(new Regex(@"\d+ requirements?"), new() { Timeout = 120000 });
            (await page.GetByTestId("mtd-requirement").First.InnerTextAsync()).Should().NotContain("**", "requirement text is shown without Markdown");

            // 5. Discover scenarios: the specification now contributes business scenarios.
            // Discover now (offered after the choice) opens Plan & prepare and runs discovery for the chosen specification.
            await page.GetByTestId("ssp-discover").ClickAsync();
            await ReadyAsync(page, "tps");
            await MessageAsync(page, "candidates found");
            page.Url.Should().NotContain("discover=now", "a reload does not run discovery again");
            await Assertions.Expect(page.GetByTestId("tps-capability-specification")).ToHaveAttributeAsync("data-state", "Available");

            // 6. Prepare three scenarios from the specification.
            for (var i = 0; i < 40; i++)
            {
                var collapsed = page.Locator("[data-testid^=tps-group-toggle-][aria-expanded=false]");
                if (await collapsed.CountAsync() == 0) break;
                await collapsed.First.ClickAsync();
            }
            var candidates = page.Locator("li[data-testid^=tps-candidate-]");
            (await candidates.CountAsync()).Should().BeGreaterThan(2);
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var id = (await candidates.Nth(i).GetAttributeAsync("data-testid"))!["tps-candidate-".Length..];
                ids.Add(id);
                await page.GetByTestId($"tps-select-{id}").ClickAsync();
            }
            await page.GetByTestId("tps-prepare").ClickAsync();
            var confirm = page.GetByTestId("tps-preview-confirm");
            if (await confirm.CountAsync() > 0) await confirm.ClickAsync();
            await MessageAsync(page, "prepared as test cases");

            // 7. Fill what the evidence left open (a Norwegian step included), save, approve.
            var sid = ids[0];
            var testCase = page.GetByTestId($"tps-case-{sid}");
            if ((await testCase.GetAttributeAsync("class"))?.Contains("is-open") != true) await testCase.Locator(".tps-case-head").ClickAsync();
            await page.GetByTestId($"tps-step-new-{sid}").FillAsync("Åpne meldingsloggen og finn meldingen for eleven");
            await page.GetByTestId($"tps-step-add-{sid}").ClickAsync();
            var stepExpected = page.Locator($"[data-testid^=\"tps-step-expected-{sid}-\"]");
            for (var i = 0; i < await stepExpected.CountAsync(); i++)
            {
                if (!string.IsNullOrWhiteSpace(await stepExpected.Nth(i).InputValueAsync())) continue;
                await stepExpected.Nth(i).FillAsync("Meldingen vises med riktig periode og status");
                await stepExpected.Nth(i).DispatchEventAsync("change");
            }
            await testCase.GetByTestId("tps-save").ClickAsync();
            await MessageAsync(page, "Draft saved");
            var approve = page.GetByTestId($"tps-accept-{sid}");
            await Assertions.Expect(approve).ToBeEnabledAsync(new() { Timeout = 30000 });
            await approve.ClickAsync();
            await Assertions.Expect(testCase).ToContainTextAsync("Approved", new() { Timeout = 60000 });
            var draftId = Regex.Match(page.Url, @"[?&]draft=([^&]+)").Groups[1].Value;
            draftId.Should().NotBeNullOrEmpty();

            // 8. The home offers the export of the approved case.
            await GoAsync(page, "manual-testing", "mt");
            await Assertions.Expect(page.GetByTestId("mt-next-action")).ToHaveTextAsync("Export to Azure DevOps");
            await page.Locator($"[data-testid=mt-plan-export][href*=\"{draftId}\"]").ClickAsync();

            // 9. Export an XLSX for Azure DevOps Test Plans; the file carries the Norwegian step.
            await ReadyAsync(page, "adx");
            await page.GetByTestId("adx-area").FillAsync(@"M2LB\Manuell test");
            await page.GetByTestId("adx-area").DispatchEventAsync("change");
            await ReadyAsync(page, "adx");
            await Assertions.Expect(page.GetByTestId("adx-can-download")).ToContainTextAsync("ready", new() { Timeout = 60000 });
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("adx-download-xlsx").ClickAsync());
            var file = Path.Combine(Path.GetTempPath(), $"m2lb-e2e-{Guid.NewGuid():N}.xlsx");
            await download.SaveAsAsync(file);
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var xml = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("xl/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
                xml.Should().Contain("Step Expected").And.Contain("Åpne meldingsloggen og finn meldingen for eleven").And.Contain(@"M2LB\Manuell test");
            }
            finally { File.Delete(file); }
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
