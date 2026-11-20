using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// The Skolenærvær chain end to end in Integration suites, as a test lead runs it, against a local stand-in of every system
/// (<see cref="SkolenaervaerStandIn"/>; nothing external): the project → the four source packages (Altinn app + MU, MM, SkoleService + Person,
/// Bufdata) → discover and approve the sources per stage → save the composite context → generate the suite → approve the plan → preflight
/// (FULL_E2E, data-changing calls named) → confirm and authorise → run: every stage observed, every boundary's correlation confirmed, the stand-in's
/// own ledger shows one report through all seven transitions → MM rejects → Incomplete (never a pass) → MM answers with another message id →
/// WrongCorrelation. Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class SkolenaervaerChainWorkflowTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private readonly SkolenaervaerStandIn _chain = new();

    public async Task InitializeAsync()
    {
        _chain.Start();
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _chain.DisposeAsync();
    }

    private static Task AreaAsync(IPage page, string area) => page.GetByTestId($"its-area-{area}").ClickAsync();

    /// <summary>Preflight, confirm the data-changing calls, authorise this one run, start it; the opened run follows itself until it ends.</summary>
    private static async Task<ILocator> RunChainAsync(IPage page, string expectedVerdict)
    {
        await page.GetByTestId("its-preflight").ClickAsync();
        await Assertions.Expect(page.GetByTestId("its-preflight-result")).ToHaveAttributeAsync("data-kind", "FullE2E", new() { Timeout = 30000 });
        await page.GetByTestId("its-confirm-mutating").CheckAsync();
        await page.GetByTestId("its-authorize").CheckAsync();
        await page.GetByTestId("its-run-composite").ClickAsync();
        var run = page.GetByTestId("its-run-view");
        await Assertions.Expect(run).ToHaveAttributeAsync("data-verdict", expectedVerdict, new() { Timeout = 120000 });
        return run;
    }

    [BrowserFact]
    public async Task Altinn_to_Bufdata_is_verified_end_to_end_and_a_broken_handoff_is_reported_as_what_it_is()
    {
        await _fixture.Context.AddInitScriptAsync($$"""
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'skole-standin', profiles: [{ id: 'skole-standin',
              name: 'Skolenærvær stand-in (Local)', environmentType: 'Local', targetUrl: '{{_chain.Url}}/',
              authentication: { requiresAuthentication: false, authenticationType: 'None' }, integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        try
        {
            // 1. The project, and the four source packages through the Source Analysis upload.
            var project = SyntheticProjects.NewName("Skole");
            await SyntheticProjects.ImportAndUseAsync(page, project, SkolenaervaerStandIn.Project(project));
            foreach (var (fileName, zip) in SkolenaervaerStandIn.Packages())
            {
                var form = _fixture.Context.APIRequest.CreateFormData();
                form.Set("file", new FilePayload { Name = fileName, MimeType = "application/zip", Buffer = zip });
                var upload = await _fixture.Context.APIRequest.PostAsync($"{BrowserTestServers.BackendUrl}/api/source-analysis/snapshots", new()
                {
                    Multipart = form,
                    Headers = new Dictionary<string, string> { ["Origin"] = BrowserTestServers.FrontendUrl, ["X-BirkNext-Client"] = "playwright-tests" },
                });
                upload.Ok.Should().BeTrue($"{fileName}: {await upload.TextAsync()}");
            }

            // 2. Sources: one proposal per stage, each approved by the test lead; the composite context is saved.
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/integration-suites", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("its-area-sources").WaitForAsync(new() { Timeout = 120000 });
            await AreaAsync(page, "sources");
            await page.GetByTestId("its-composite-scope").ClickAsync();
            await page.GetByTestId("its-discover").ClickAsync();
            await page.GetByTestId("its-stage-card").First.WaitForAsync(new() { Timeout = 60000 });
            // Other projects in the same database (a real import, an earlier run) can make a stage ambiguous: the test lead then picks the
            // synthetic package for that stage explicitly.
            foreach (var card in await page.GetByTestId("its-stage-card").AllAsync())
            {
                var mine = card.Locator("[data-testid=its-candidate][data-project^='synth.' i] input[type=radio]");
                if (await mine.CountAsync() > 0 && !await mine.First.IsCheckedAsync()) await mine.First.CheckAsync();
            }
            var selected = page.GetByTestId("its-selected-source");
            await Assertions.Expect(selected).ToHaveCountAsync(4, new() { Timeout = 60000 });
            (await selected.AllInnerTextsAsync()).Should().Contain(s => s.Contains("altinn, mu")).And.Contain(s => s.Contains("mm"))
                .And.Contain(s => s.Contains("skole-intake") && s.Contains("person-tjeneste") && s.Contains("period-lock"), "one package serves these three stages, in any order").And.Contain(s => s.Contains("bufdata"));
            foreach (var approve in await page.GetByTestId("its-approve-source").AllAsync()) await approve.ClickAsync();
            await page.GetByTestId("its-save-context").ClickAsync();
            await Assertions.Expect(page.GetByTestId("its-message")).ToContainTextAsync("Revision 1 saved", new() { Timeout = 30000 });

            // 3. Generate the suite (a draft; nothing is sent) and approve its plan.
            await AreaAsync(page, "generate");
            await Assertions.Expect(page.GetByTestId("its-no-project")).ToHaveCountAsync(0, new() { Timeout = 30000 });
            await page.GetByTestId("its-generate-composite-btn").ClickAsync();
            await Assertions.Expect(page.GetByTestId("its-message")).ToContainTextAsync("generated as a draft", new() { Timeout = 60000 });
            await AreaAsync(page, "execute");
            var steps = page.GetByTestId("its-plan-step");
            await Assertions.Expect(steps).ToHaveCountAsync(7, new() { Timeout = 30000 });
            (await steps.EvaluateAllAsync<string[]>("es => es.map(e => e.dataset.stage)"))
                .Should().Equal("altinn", "mu", "mm", "skole-intake", "person-tjeneste", "period-lock", "bufdata");
            await page.GetByTestId("its-approve-plan").ClickAsync();
            await Assertions.Expect(page.GetByTestId("its-plan")).ToHaveAttributeAsync("data-state", "Approved", new() { Timeout = 30000 });

            // 4. The full chain: every stage observed, every boundary confirmed, exactly two data-changing calls (submit, period lock).
            _chain.Reset("normal");
            var run = await RunChainAsync(page, "FullE2EVerified");
            await Assertions.Expect(page.GetByTestId("its-preflight-result")).ToContainTextAsync("POST /altinn/submissions");
            (await run.GetByTestId("its-run-stage").EvaluateAllAsync<string[]>("es => es.map(e => e.dataset.stage + ':' + e.dataset.state)"))
                .Should().OnlyContain(s => s.EndsWith(":Succeeded")).And.HaveCount(7);
            (await run.GetByTestId("its-correlation").EvaluateAllAsync<string[]>("es => es.map(e => e.dataset.state)")).Should().HaveCount(6).And.OnlyContain(s => s == "Confirmed");
            await Assertions.Expect(run.GetByTestId("its-correlation").First).ToContainTextAsync("altinn → mu");
            var tx = _chain.Transactions.Should().ContainSingle().Subject;
            tx.Transitions.Should().Equal("submitted", "mu-received", "mm-validated", "skole-ingested", "person-looked-up", "period-locked", "bufdata-delivered");
            _chain.Ledger.Count(l => l.Method == "POST").Should().Be(2, "each data-changing call exactly once, never retried");

            // 5. MM rejects the message: MM is not observed in time, later stages are not run, the run is Incomplete — never a pass.
            _chain.Reset("mm-reject");
            run = await RunChainAsync(page, "Incomplete");
            await Assertions.Expect(run.Locator("[data-testid=its-run-stage][data-stage=mm]")).ToHaveAttributeAsync("data-state", "TimedOut");
            await Assertions.Expect(run.Locator("[data-testid=its-run-stage][data-stage=bufdata]")).Not.ToHaveAttributeAsync("data-state", "Succeeded");
            _chain.Transactions.Should().ContainSingle().Which.Transitions.Should().Equal("submitted", "mu-received");

            // 6. MM answers with another message's id: the handoff is a wrong correlation, not a success.
            _chain.Reset("wrong-correlation");
            run = await RunChainAsync(page, "WrongCorrelation");
            (await run.GetByTestId("its-correlation").EvaluateAllAsync<string[]>("es => es.map(e => e.dataset.state)")).Should().Contain("WrongCorrelation");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
