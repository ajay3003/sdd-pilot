using FluentAssertions;
using Microsoft.Playwright;
using F = BirkNext.Api.Tests.BrowserVerification.TestOverviewFixture;

namespace BirkNext.Api.Tests.BrowserVerification;

/// <summary>
/// Testoversikt browser journeys B01–B24 (V12, 2026-11-27) against <see cref="IsolatedBrowserHost"/>. Every expectation comes from the independent
/// fixture oracle (<see cref="TestOverviewFixture"/>); a journey fails on a different identity set even when the totals agree, on any console error,
/// on any 5xx answer and on any request to an origin outside the isolated host.
/// </summary>
[Collection(IsolatedBrowserCollection.Name)]
[Trait("Category", "IsolatedBrowser")]
public sealed class TestOverviewBrowserJourneys(IsolatedBrowserHost host) : IAsyncLifetime
{
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private static bool _imported;
    private static Exception? _fixtureFailure;
    /// <summary>The seeded Aurora cases as the API stored them (plan id, case id).</summary>
    private static TestOverviewCaseSeeding.Seeded _seeded = null!;
    private IBrowserContext? _context;
    private TestOverviewBrowserSession _s = null!;
    private bool _passed;
    private string _name = "journey";

    private static F.Application A => F.Aurora;
    private static F.Application B => F.Borealis;
    private static F.Spec Spec(F.Application app, string module, string pack) => app.Specs.Single(s => s.Module == module && s.Pack == pack);
    private static F.Spec Booking1 => Spec(A, "Booking", "001-reservations");

    public async Task InitializeAsync()
    {
        if (IsolatedBrowserHost.NotConfiguredReason() is not null) return;
        _context = await host.NewContextAsync();
        _s = await TestOverviewBrowserSession.OpenAsync(host, _context);
        await ImportGate.WaitAsync();
        try
        {
            if (_fixtureFailure is { } failed) throw new InvalidOperationException("The isolated fixture could not be prepared: " + failed.Message, failed);
            if (!_imported)
            {
                // Two applications through the real Project Import page (the supported user path; nothing is seeded behind the API's back).
                try
                {
                    await _s.ImportAsync(B);
                    await _s.ImportAsync(A);
                    _imported = true; // never imported twice, even when seeding fails below
                    _seeded = await TestOverviewCaseSeeding.SeedAsync(host, _s.Page, A);
                }
                catch (Exception e) { _fixtureFailure = e; await host.FinishContextAsync(_context, "fixture-import", failed: true); _context = null; throw; }
            }
        }
        finally { ImportGate.Release(); }
        // Each journey starts from the same state: Aurora current, all modules.
        await _s.UseAsync(A);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        _s.ConsoleErrors.Clear();
        _s.FailedRequests.Clear();
        lock (host.Violations) host.Violations.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_context is null) return;
        await host.FinishContextAsync(_context, _name, !_passed);
    }

    /// <summary>End-of-journey checks: no blocked request, no console error, no 5xx.</summary>
    private void Clean()
    {
        host.Violations.Should().BeEmpty("the browser may reach only the isolated host");
        _s.FailedRequests.Should().BeEmpty("no request may fail with a server error");
        _s.ConsoleErrors.Should().BeEmpty("the page logs no error");
        _passed = true;
    }

    private static HashSet<F.Key> Keys(IEnumerable<F.Key> keys) => [.. keys];

    /// <summary>The figures a scope must show, from the oracle: scenario proposals, priority bars, module/spec rows.</summary>
    private async Task ExpectFiguresAsync(F.Application app, F.Scope scope)
    {
        (await _s.KpiAsync("metric.scenarios")).Should().Be(F.ScenarioProposals(app, scope), "«Scenarioforslag» = acceptance scenarios + requirement-only proposals");
        var cases = F.CasesIn(app, scope);
        (await _s.KpiAsync("metric.testCases")).Should().Be(cases.Count, "the seeded cases of the scope's documents");
        (await _s.KpiAsync("metric.approved")).Should().Be(cases.Count(c => F.IsApproved(c.State)));
        (await _s.KpiAsync("metric.ready")).Should().Be(cases.Count(c => F.IsReady(c.State)), "approved is not ready");
        if (await _s.KpiAsync("metric.testPlans") is { } plans) plans.Should().Be(cases.Select(c => c.Plan).Distinct().Count(), "plans with cases in the scope");
        foreach (var p in new[] { "P1", "P2", "P3" })
        {
            var shown = await _s.BarValueAsync("chart.priority", p);
            (shown ?? 0).Should().Be(F.Priority(app, scope, p), $"priority bar {p}");
            ((await _s.BarValueAsync("chart.coverageGaps", p)) ?? 0).Should().Be(F.GapOf(app, scope, p), $"{p} without test cases");
        }
    }

    /// <summary>
    /// P1 follow-up contract (V12, 2026-11-27): the line states the oracle's count, and clicking it opens Finn og forstå with EXACTLY the oracle's
    /// P1-without-case scenarios of the scope — same tokens with the same multiplicity (a shared title in two documents is two scenarios), none
    /// missing, none extra. Equal counts with other identities fail.
    /// </summary>
    private async Task ExpectP1DestinationAsync(F.Application app, F.Scope scope)
    {
        var expected = F.P1WithoutCases(app, scope);
        var line = _s.Page.Locator("li[data-key='p1-uncovered'] [data-testid=mto-attention-open]");
        if (expected.Count == 0) { (await line.CountAsync()).Should().Be(0, "no P1 gap, no follow-up line"); return; }
        (await line.InnerTextAsync()).Should().StartWith($"{expected.Count} P1-scenario");
        var result = await _s.FollowUpToDiscoverAsync("p1-uncovered");
        result.Wanted.Should().Be(expected.Count, "the hand-over carries exactly the counted identities");
        result.Missing.Should().BeFalse("every handed-over scenario is in Finn og forstå's result");
        result.Found.Should().Be(expected.Count);
        result.Tokens.Should().BeEquivalentTo(expected.Select(k => k.Token), "the destination lists exactly the P1 scenarios of the scope");
    }

    /// <summary>Back from Finn og forstå to Testoversikt (browser history).</summary>
    private async Task BackToOverviewAsync()
    {
        await _s.Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await _s.Page.WaitForURLAsync(u => u.EndsWith("/manual-testing", StringComparison.Ordinal), new() { Timeout = 60000 });
        await _s.WaitForFiguresAsync();
    }

    // ================================================================================================ isolation

    [IsolatedBrowserFact]
    public void I00_isolation_is_proven_from_the_effective_runtime()
    {
        _name = "I00";
        var p = host.Proof;
        p.Database.Should().Be(p.RunDatabase).And.MatchRegex("^birknext_testrun_[0-9a-f]{12}$").And.NotBe(p.DeveloperDatabase);
        p.OtherContexts.Should().OnlyContain(d => d == p.RunDatabase);
        p.AppData.Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
        if (p.DeveloperAppData is not null) p.AppData.Should().NotStartWith(p.DeveloperAppData);
        p.LaunchCodePath.Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
        new[] { p.ApiPort, p.WebPort }.Should().NotIntersectWith([5000, 5001, 5173]);
        p.Environment.Should().Be("Test");
        Clean();
    }

    // ================================================================================================ scope journeys

    [IsolatedBrowserFact]
    public async Task B01_project_only()
    {
        _name = "B01";
        var all = new F.Scope();
        await ExpectFiguresAsync(A, all);
        var rows = await _s.RowsAsync();
        rows.Keys.Should().BeEquivalentTo(A.Specs.Select(s => A.OverviewModule(s.Module)).Distinct(), "one row per module, nothing else");
        foreach (var g in A.Specs.GroupBy(s => s.Module))
        {
            rows[A.OverviewModule(g.Key)].Scenarios.Should().Be(g.Sum(s => s.AcceptanceScenarios.Count()), g.Key);
            rows[A.OverviewModule(g.Key)].Requirements.Should().Be(g.Sum(s => s.RequirementsWithoutScenario.Count), g.Key);
        }
        await ExpectP1DestinationAsync(A, all);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B02_project_and_module()
    {
        _name = "B02";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        var scope = new F.Scope("Booking");
        await ExpectFiguresAsync(A, scope);
        var rows = await _s.RowsAsync();
        rows.Values.Select(r => TestOverviewBrowserSession.Normalize(r.Key)).Should().BeEquivalentTo(F.SpecsIn(A, scope).Select(s => s.Path), "one row per specification of the module");
        foreach (var spec in F.SpecsIn(A, scope))
            rows.Values.Single(r => TestOverviewBrowserSession.Normalize(r.Key) == spec.Path).Scenarios.Should().Be(spec.AcceptanceScenarios.Count());
        await ExpectP1DestinationAsync(A, scope);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B03_project_module_and_specification()
    {
        _name = "B03";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        var scope = new F.Scope("Booking", "001-reservations");
        await ExpectFiguresAsync(A, scope);
        var rows = await _s.RowsAsync();
        // Story rows: «US1 Reserve a room», «US2 …», and the requirement-only proposals under «Uten brukerhistorie».
        foreach (var story in Booking1.Stories)
            rows.Where(r => r.Key.StartsWith(story.Id + " ", StringComparison.Ordinal)).Should().ContainSingle().Which.Value.Scenarios.Should().Be(story.Scenarios.Count);
        if (Booking1.RequirementsWithoutScenario.Count > 0) rows.Should().ContainKey("Uten brukerhistorie").WhoseValue.Scenarios.Should().Be(Booking1.RequirementsWithoutScenario.Count);
        // Reload: the scope is the saved shared choice, not page memory (the figures follow it — before the fix they fell back to the project).
        await _s.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _s.WaitForFiguresAsync();
        await ExpectFiguresAsync(A, scope);
        await ExpectP1DestinationAsync(A, scope);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B04_full_hierarchy_with_document()
    {
        _name = "B04";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        var documents = await _s.OptionsAsync("ssp-specification");
        if (documents.Count > 0)
        {
            documents.Where(d => d.Value.Length > 0).Should().ContainSingle(d => d.Text.Contains("spec.md", StringComparison.OrdinalIgnoreCase),
                "the pack's one specification document is offered (plan.md is not a specification)");
            await _s.SelectDocumentAsync("spec.md");
        }
        await ExpectFiguresAsync(A, new F.Scope("Booking", "001-reservations", Booking1.Path));
        (await _s.Page.GetByTestId("ssp-current").InnerTextAsync()).Should().Contain("001-reservations");
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B05_B06_specification_or_document_without_module_is_not_offered()
    {
        _name = "B05-B06";
        // With «Alle moduler» the picker offers neither a specification nor a document: Project + Specification and Project + Document are
        // prevented (the scope always names its module first), not silently widened.
        (await _s.SelectedTextAsync("ssp-module")).Should().StartWith("Alle moduler");
        (await _s.Page.GetByTestId("ssp-feature").CountAsync()).Should().Be(0);
        (await _s.Page.GetByTestId("ssp-specification").CountAsync()).Should().Be(0);
        await ExpectFiguresAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B07_project_level_source()
    {
        _name = "B07";
        // Wrapped archive: the project's own specs/ is the module named after the folder («Aurora»), with its own figures.
        await _s.SelectModuleAsync(A.ModuleKey(""));
        await ExpectFiguresAsync(A, new F.Scope(""));
        // Unwrapped archive: «Prosjektnivå», outside the modules in the table.
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        var rows = await _s.RowsAsync();
        rows.Should().ContainKey("Prosjektnivå").WhoseValue.Scenarios.Should().Be(Spec(B, "", "001-overview").AcceptanceScenarios.Count());
        (await _s.Page.Locator("[data-testid=mto-row][data-outside=true]").CountAsync()).Should().BeGreaterThan(0);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B08_unassigned_source()
    {
        _name = "B08";
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        var unassigned = B.Specs.Single(s => s.Module == F.UnassignedModule);
        var rows = await _s.RowsAsync();
        rows.Should().ContainKey("Uten modultilknytning").WhoseValue.Scenarios.Should().Be(unassigned.AcceptanceScenarios.Count());
        rows["Uten modultilknytning"].Requirements.Should().Be(unassigned.RequirementsWithoutScenario.Count);
        await ExpectFiguresAsync(B, new F.Scope());
        await ExpectP1DestinationAsync(B, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B09_clear_module_restores_the_project()
    {
        _name = "B09";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        await _s.SelectModuleAsync(null);
        (await _s.Page.GetByTestId("ssp-feature").CountAsync()).Should().Be(0, "clearing the module clears its specification");
        await ExpectFiguresAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B10_clear_specification_keeps_the_module()
    {
        _name = "B10";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        await _s.SelectPackAsync(null);
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Booking"));
        await ExpectFiguresAsync(A, new F.Scope("Booking"));
        // Specific → All → Specific: the second choice is honoured as the first was.
        await _s.SelectPackAsync("002-cancellations");
        await ExpectFiguresAsync(A, new F.Scope("Booking", "002-cancellations"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B11_clear_document_keeps_module_and_specification()
    {
        _name = "B11";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        if (await _s.Page.GetByTestId("ssp-specification").CountAsync() > 0)
        {
            await _s.SelectDocumentAsync("spec.md");
            await _s.SelectDocumentAsync(null);
        }
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Booking"));
        (await _s.Page.GetByTestId("ssp-feature").InputValueAsync()).Should().Be("001-reservations");
        await ExpectFiguresAsync(A, new F.Scope("Booking", "001-reservations"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B12_application_switching_replaces_every_figure()
    {
        _name = "B12";
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        await ExpectFiguresAsync(B, new F.Scope());
        var text = await _s.Page.Locator("main").First.InnerTextAsync();
        text.Should().NotContain("Aurora/", "no figure or row of the other application remains");
        var drill = await _s.OpenBarAsync("chart.priority", "P1");
        (await _s.DrillIdentitiesAsync(drill)).Should().BeEquivalentTo(F.Scenarios(B, new F.Scope(), s => s.Priority == "P1"));
        await _s.UseAsync(A);
        await _s.OpenOverviewAsync();
        await ExpectFiguresAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B13_rapid_scope_changes_settle_on_the_last_choice()
    {
        _name = "B13";
        foreach (var module in new[] { "Booking", "Billing", "Catalog" }) await _s.SelectModuleAsync(A.ModuleKey(module), wait: false);
        await _s.WaitForFiguresAsync();
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Catalog"));
        await ExpectFiguresAsync(A, new F.Scope("Catalog"));
        Clean();
    }

    // ================================================================================================ navigation journeys

    [IsolatedBrowserFact]
    public async Task B14_P1_follow_up_opens_Finn_og_forsta_with_the_project_population()
    {
        _name = "B14";
        F.P1WithoutCases(A, new F.Scope()).Select(k => k.SpecPath).Distinct().Count().Should().BeGreaterThan(20, "the population spans many documents");
        await ExpectP1DestinationAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B15_exact_scenario_opens_that_scenario()
    {
        _name = "B15";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        var drill = await _s.OpenBarAsync("chart.priority", "P1");
        var group = drill.GetByTestId("mto-drill-group").First;
        if (await group.GetByTestId("mto-follow-toggle").GetAttributeAsync("aria-expanded") != "true") await group.GetByTestId("mto-follow-toggle").ClickAsync();
        var row = group.Locator("[data-testid=mto-drill-row]", new() { HasTextString = "AUR-BKG1-US1-S2" });
        await row.GetByTestId("mto-drill-scenario").ClickAsync();
        await _s.Page.WaitForURLAsync(u => !u.EndsWith("/manual-testing", StringComparison.Ordinal), new() { Timeout = 60000 });
        var result = await _s.ReadDiscoverAsync();
        result.Tokens.Should().Equal(["AUR-BKG1-US1-S2"], "one scenario was opened, exactly that one");
        result.Wanted.Should().Be(1);
        result.Found.Should().Be(1);
        await Assertions.Expect(_s.Page.GetByTestId("ssp-current")).ToContainTextAsync("001-reservations", new() { Timeout = 60000 });
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B16_test_plan_figure_and_cards_open_their_plans()
    {
        _name = "B16";
        var plans = F.CasesOf(A).Select(c => c.Plan).Distinct().ToList();
        // The «Testplaner» figure is optional (System Settings); when shown it counts the plans with cases in the scope.
        if (await _s.KpiAsync("metric.testPlans") is { } shown) shown.Should().Be(plans.Count);
        foreach (var plan in plans)
        {
            var id = _seeded.Cases.First(e => e.Key.Plan == plan).Value.PlanId;
            var card = _s.Page.Locator("[data-testid=mto-plan-card]", new() { HasTextString = plan });
            (await card.Locator("a.mto-plan-name").GetAttributeAsync("href")).Should().EndWith($"draft={id}", "a plan card opens exactly its own plan");
        }
        await _s.Page.GotoAsync($"{host.WebUrl}/manual-testing/plans", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        foreach (var plan in plans) await Assertions.Expect(_s.Page.Locator("main")).ToContainTextAsync(plan, new() { Timeout = 60000 });
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B17_KPI_tile_and_scenario_KPI_navigation()
    {
        _name = "B17";
        var tile = _s.Page.GetByTestId("mto-tile").First;
        if (await tile.CountAsync() > 0)
        {
            var count = (await tile.GetByTestId("mto-tile-count").InnerTextAsync()).Trim();
            var id = await tile.GetAttributeAsync("data-tile");
            if (count is "0" or "—")
            {
                // An empty tile has nothing to open: its button is disabled (never a broader list).
                await Assertions.Expect(tile.GetByTestId("mto-tile-open")).ToBeDisabledAsync();
            }
            else
            {
                await tile.GetByTestId("mto-tile-open").ClickAsync();
                var result = await _s.ReadDiscoverAsync();
                result.Wanted.Should().Be(int.Parse(count), $"tile {id} hands over exactly what it counts");
                result.Found.Should().Be(result.Wanted);
                await BackToOverviewAsync();
            }
        }
        // «Scenarioforslag» opens Finn og forstå in the same scope.
        await _s.SelectModuleAsync(A.ModuleKey("Billing"));
        await _s.Page.Locator("[data-testid=mto-kpi][data-widget='metric.scenarios']").ClickAsync();
        await _s.Page.WaitForURLAsync(u => !u.EndsWith("/manual-testing", StringComparison.Ordinal), new() { Timeout = 60000 });
        await Assertions.Expect(_s.Page.GetByTestId("ssp-module")).ToHaveValueAsync(A.ModuleKey("Billing"), new() { Timeout = 60000 });
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B18_table_links_and_sorting()
    {
        _name = "B18";
        // Sorting by scenarios (descending, then ascending) orders the rows by the oracle's counts; the population is unchanged.
        var before = (await _s.RowsAsync()).Keys.ToHashSet();
        var sort = _s.Page.Locator("th [data-testid=mto-sort][data-col=scenarios]");
        await sort.ClickAsync();
        var order1 = await _s.Page.GetByTestId("mto-row-scenarios").AllInnerTextsAsync();
        await sort.ClickAsync();
        var order2 = await _s.Page.GetByTestId("mto-row-scenarios").AllInnerTextsAsync();
        var n1 = order1.Select(int.Parse).ToList();
        var n2 = order2.Select(int.Parse).ToList();
        (n1.SequenceEqual(n1.OrderBy(x => x)) || n1.SequenceEqual(n1.OrderByDescending(x => x))).Should().BeTrue($"sorted: {string.Join(",", n1)}");
        n2.Should().BeEquivalentTo(n1, "same population");
        (await _s.RowsAsync()).Keys.Should().BeEquivalentTo(before);
        // A module row opens Finn og forstå on that module.
        await _s.Page.Locator("[data-testid=mto-row-open]", new() { HasTextString = A.OverviewModule("Billing") }).ClickAsync();
        await _s.Page.WaitForURLAsync(u => !u.EndsWith("/manual-testing", StringComparison.Ordinal), new() { Timeout = 60000 });
        await Assertions.Expect(_s.Page.GetByTestId("ssp-module")).ToHaveValueAsync(A.ModuleKey("Billing"), new() { Timeout = 60000 });
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B19_chart_bars_open_exactly_their_records()
    {
        _name = "B19";
        var covered = F.Covered(A);
        foreach (var (chart, key) in new[] { ("chart.priority", "P2"), ("chart.priority", "P3"), ("chart.coverageGaps", "P3"), ("chart.coverageGaps", "P1") })
        {
            var drill = await _s.OpenBarAsync(chart, key);
            var expected = F.Scenarios(A, new F.Scope(), s => s.Priority == key).Where(k => chart == "chart.priority" || !covered.Contains(k)).ToHashSet();
            (await TestOverviewBrowserSession.DrillCountAsync(drill)).Should().Be(expected.Count, $"{chart}/{key}");
            (await _s.DrillIdentitiesAsync(drill)).Should().BeEquivalentTo(expected, $"{chart}/{key}");
            await drill.GetByTestId("mto-drill-close").ClickAsync();
        }
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B20_pagination_and_show_more()
    {
        _name = "B20";
        var drill = await _s.OpenBarAsync("chart.priority", "P1");
        var groups = F.Scenarios(A, new F.Scope(), s => s.Priority == "P1").Select(k => k.SpecPath).Distinct().Count();
        (await drill.GetByTestId("mto-follow-page-status").InnerTextAsync()).Should().Be($"Side 1 av {(groups + 19) / 20}");
        (await drill.GetByTestId("mto-drill-group").CountAsync()).Should().Be(20);
        await drill.GetByTestId("mto-follow-next").ClickAsync();
        (await drill.GetByTestId("mto-drill-group").CountAsync()).Should().Be(groups - 20);
        await drill.GetByTestId("mto-follow-prev").ClickAsync();
        var catalog = drill.Locator($"[data-testid=mto-drill-group][data-spec$='000-catalog-search/spec.md']");
        await catalog.GetByTestId("mto-follow-toggle").ClickAsync();
        (await catalog.GetByTestId("mto-drill-row").CountAsync()).Should().Be(10, "a group shows 10 rows first");
        await catalog.Locator("[data-testid=mto-follow-more] button, button[data-testid=mto-follow-more]").First.ClickAsync();
        (await catalog.GetByTestId("mto-drill-row").CountAsync()).Should().Be(12);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B21_empty_results()
    {
        _name = "B21";
        // A module without P2/P3: those bars are absent (no zero bar opens an empty list); the P1 list filtered to nothing says so.
        await _s.SelectModuleAsync(A.ModuleKey("Catalog"));
        F.Priority(A, new F.Scope("Catalog"), "P2").Should().Be(0);
        (await _s.BarValueAsync("chart.priority", "P2") ?? 0).Should().Be(0);
        var drill = await _s.OpenBarAsync("chart.priority", "P1");
        await drill.GetByTestId("mto-follow-search").FillAsync("ZZZ-NO-SUCH-SCENARIO");
        await Assertions.Expect(drill.GetByTestId("mto-follow-none")).ToBeVisibleAsync();
        (await drill.GetByTestId("mto-drill-row").CountAsync()).Should().Be(0);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B22_stale_reference_from_another_application_is_not_applied()
    {
        _name = "B22";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        // Borealis shows its own figures for what its picker says — never Aurora's Booking/001.
        var module = await _s.Page.GetByTestId("ssp-module").CountAsync() > 0 ? await _s.Page.GetByTestId("ssp-module").InputValueAsync() : "(all-modules)";
        module.Should().NotStartWith("Aurora");
        await ExpectFiguresAsync(B, module == "(all-modules)" ? new F.Scope() : new F.Scope(module == "(project-level)" ? "" : module));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B23_back_and_forward_keep_the_scope()
    {
        _name = "B23";
        await _s.SelectModuleAsync(A.ModuleKey("Billing"));
        var overview = _s.Page.Url;
        await _s.Page.Locator("[data-testid=mto-kpi][data-widget='metric.scenarios']").ClickAsync();
        await _s.Page.WaitForURLAsync(u => u != overview, new() { Timeout = 60000 });
        var destination = _s.Page.Url;
        await _s.Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await _s.Page.WaitForURLAsync(u => u == overview, new() { Timeout = 60000 });
        await _s.WaitForFiguresAsync();
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Billing"));
        await ExpectFiguresAsync(A, new F.Scope("Billing"));
        await _s.Page.GoForwardAsync(new() { WaitUntil = WaitUntilState.Commit });
        await _s.Page.WaitForURLAsync(u => u == destination, new() { Timeout = 60000 });
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task B24_duplicate_display_ids_stay_separate()
    {
        _name = "B24";
        // US1/FR-001 exist in every document and AUR-SHARED-S1 has the same title in Booking/001 and Billing/001: two scenarios, two identities.
        var drill = await _s.OpenBarAsync("chart.priority", "P1");
        var keys = await _s.DrillIdentitiesAsync(drill);
        keys.Where(k => k.Token == "AUR-SHARED-S1").Select(k => k.SpecPath).Should().BeEquivalentTo(
            [Booking1.Path, Spec(A, "Billing", "001-invoices").Path]);
        var identities = await drill.GetByTestId("mto-drill-row").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.identity)");
        identities.Should().OnlyHaveUniqueItems("every listed scenario has its own source identity");
        await drill.GetByTestId("mto-drill-close").ClickAsync();
        // The same title in two documents with two different case states: only the Booking case (awaiting) is in «venter på godkjenning».
        var booking = _seeded.Cases.Single(e => e.Key.Scenario == new F.Key(Booking1.Path, "AUR-SHARED-S1")).Value;
        var billing = _seeded.Cases.Single(e => e.Key.Scenario == new F.Key(Spec(A, "Billing", "001-invoices").Path, "AUR-SHARED-S1")).Value;
        await _s.Page.Locator("li[data-key='p1-approval'] [data-testid=mto-attention-open]").ClickAsync();
        var plan = await _s.ReadPlanSelectionAsync();
        plan.Cases.Should().Contain(booking).And.NotContain(billing);
        Clean();
    }

    // ================================================================================================ P1 follow-up + readiness navigation contract

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_01_project_only()
    {
        _name = "P1-SCOPE-01";
        await ExpectP1DestinationAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_02_module()
    {
        _name = "P1-SCOPE-02";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await ExpectP1DestinationAsync(A, new F.Scope("Booking"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_03_specification()
    {
        _name = "P1-SCOPE-03";
        await _s.SelectModuleAsync(A.ModuleKey("Billing"));
        await _s.SelectPackAsync("001-invoices");
        await ExpectP1DestinationAsync(A, new F.Scope("Billing", "001-invoices"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_04_document()
    {
        _name = "P1-SCOPE-04";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        if (await _s.Page.GetByTestId("ssp-specification").CountAsync() > 0) await _s.SelectDocumentAsync("spec.md");
        await ExpectP1DestinationAsync(A, new F.Scope("Booking", "001-reservations", Booking1.Path));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_05_06_07_clearing_restores_the_wider_population()
    {
        _name = "P1-SCOPE-05-07";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        if (await _s.Page.GetByTestId("ssp-specification").CountAsync() > 0)
        {
            await _s.SelectDocumentAsync("spec.md");
            await _s.SelectDocumentAsync(null); // 07: clear document → the specification's population
        }
        await ExpectFiguresAsync(A, new F.Scope("Booking", "001-reservations"));
        await ExpectP1DestinationAsync(A, new F.Scope("Booking", "001-reservations"));
        await BackToOverviewAsync();
        await _s.SelectPackAsync(null); // 06: clear specification → the module's
        await ExpectP1DestinationAsync(A, new F.Scope("Booking"));
        await BackToOverviewAsync();
        await _s.SelectModuleAsync(null); // 05: clear module → the project's
        await ExpectP1DestinationAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_08_switching_applications()
    {
        _name = "P1-SCOPE-08";
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        await ExpectP1DestinationAsync(B, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_10_duplicate_display_ids_in_different_documents()
    {
        _name = "P1-SCOPE-10";
        // «US1 · AC 1» exists in 23 Catalog documents (and FR-001 in every document): the hand-over holds each document's own scenario.
        var expected = F.P1WithoutCases(A, new F.Scope("Catalog"));
        expected.Select(k => k.SpecPath).Distinct().Count().Should().BeGreaterThan(20);
        await _s.SelectModuleAsync(A.ModuleKey("Catalog"));
        await ExpectP1DestinationAsync(A, new F.Scope("Catalog"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_12_back_forward_and_reload_keep_scope_and_filter()
    {
        _name = "P1-SCOPE-12";
        await _s.SelectModuleAsync(A.ModuleKey("Billing"));
        var expected = F.P1WithoutCases(A, new F.Scope("Billing"));
        var first = await _s.FollowUpToDiscoverAsync("p1-uncovered");
        first.Tokens.Should().BeEquivalentTo(expected.Select(k => k.Token));
        // Reload Finn og forstå: the hand-over stays applied (never a broader list).
        await _s.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var reloaded = await _s.ReadDiscoverAsync();
        reloaded.Tokens.Should().BeEquivalentTo(expected.Select(k => k.Token));
        // Back: Testoversikt in the same scope with the same figures; forward: the same filtered destination.
        await BackToOverviewAsync();
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Billing"));
        await ExpectFiguresAsync(A, new F.Scope("Billing"));
        await _s.Page.GoForwardAsync(new() { WaitUntil = WaitUntilState.Commit });
        var forward = await _s.ReadDiscoverAsync();
        forward.Tokens.Should().BeEquivalentTo(expected.Select(k => k.Token));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_13_zero_matching_scenarios()
    {
        _name = "P1-SCOPE-13";
        // Borealis' «Prosjektnivå» has only a P2 scenario: no P1 line, nothing to open.
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        await _s.SelectModuleAsync("");
        F.P1WithoutCases(B, new F.Scope("")).Should().BeEmpty();
        await ExpectFiguresAsync(B, new F.Scope(""));
        await ExpectP1DestinationAsync(B, new F.Scope(""));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_14_scope_changed_while_a_request_is_pending()
    {
        _name = "P1-SCOPE-14";
        foreach (var module in new[] { "Catalog", "Booking", "Billing" }) await _s.SelectModuleAsync(A.ModuleKey(module), wait: false);
        await _s.WaitForFiguresAsync();
        (await _s.Page.GetByTestId("ssp-module").InputValueAsync()).Should().Be(A.ModuleKey("Billing"));
        await ExpectP1DestinationAsync(A, new F.Scope("Billing"));
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task P1_SCOPE_15_no_inline_P1_or_readiness_list_remains()
    {
        _name = "P1-SCOPE-15";
        // The follow-up line and «Start vurdering» are navigation, not inline lists of scenarios.
        (await _s.Page.Locator("li[data-key='p1-uncovered'] [data-testid=mto-attention-open]").GetAttributeAsync("data-destination")).Should().Be("discover");
        foreach (var button in await _s.Page.GetByTestId("mto-readiness-open").AllAsync())
            (await button.GetAttributeAsync("data-destination")).Should().Be("discover");
        (await _s.Page.Locator("[data-testid=mto-drill][data-chart='panel.p1Attention'], [data-testid=mto-drill][data-chart='chart.scenarioReadiness']").CountAsync())
            .Should().Be(0, "no inline scenario list is rendered on Testoversikt");
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task READY_01_start_assessment_opens_exactly_the_review_needed_scenarios()
    {
        _name = "READY-01";
        foreach (var scope in new (string? Module, string? Pack)[] { (null, null), ("Booking", null), ("Booking", "002-cancellations") })
        {
            if (scope.Module is null) await _s.SelectModuleAsync(null);
            else
            {
                await _s.SelectModuleAsync(A.ModuleKey(scope.Module));
                if (scope.Pack is not null) await _s.SelectPackAsync(scope.Pack);
            }
            // No assessment has been made in the fixture: every acceptance scenario of the scope «Må vurderes»; requirement-only proposals are not
            // scenarios to assess.
            var expected = F.Scenarios(A, new F.Scope(scope.Module, scope.Pack));
            var item = _s.Page.Locator("[data-testid=mto-readiness]").Filter(new() { Has = _s.Page.Locator("[data-testid=mto-readiness-open]") }).First;
            (await item.GetByTestId("mto-readiness-count").InnerTextAsync()).Trim().Should().Be(expected.Count.ToString(), $"{scope}");
            await item.GetByTestId("mto-readiness-open").ClickAsync();
            var result = await _s.ReadDiscoverAsync();
            result.Wanted.Should().Be(expected.Count);
            result.Found.Should().Be(expected.Count);
            result.Tokens.Should().BeEquivalentTo(expected.Select(k => k.Token), $"{scope}");
            await BackToOverviewAsync();
        }
        Clean();
    }

    // ================================================================================================ test-case follow-ups → Planlegg og klargjør

    /// <summary>Clicks a case follow-up (a «Viktig å følge opp» line or a blocker/source row) and compares the destination's (plan, case) set with
    /// the oracle's: exact identities, every plan visited.</summary>
    private async Task ExpectCaseDestinationAsync(ILocator control, Func<F.CaseFixture, bool> oracle, F.Scope scope)
    {
        var expected = F.CasesIn(A, scope).Where(oracle).Select(c => _seeded.Cases[c]).ToHashSet();
        if (expected.Count == 0) { (await control.CountAsync()).Should().Be(0, "no such case in the scope, no line"); return; }
        (await control.GetAttributeAsync("data-destination")).Should().Be("plan");
        await control.ClickAsync();
        var result = await _s.ReadPlanSelectionAsync();
        result.Total.Should().Be(expected.Count, "the hand-over carries exactly the counted cases");
        result.Missing.Should().BeFalse();
        result.Cases.Should().BeEquivalentTo(expected, "Planlegg og klargjør shows exactly the represented cases");
    }

    private ILocator Line(string key) => _s.Page.Locator($"li[data-key='{key}'] [data-testid=mto-attention-open]");
    private ILocator Row(string chart, string key) => _s.Page.Locator($"[data-widget='{chart}'] [data-testid=mto-bar][data-key='{key}'] [data-testid=mto-bar-open]");
    private static bool P1(F.CaseFixture c) => F.PriorityOf(A, c.Scenario) == "P1";

    [IsolatedBrowserFact]
    public async Task CASE_01_P1_awaiting_approval_project()
    {
        _name = "CASE-01";
        await ExpectCaseDestinationAsync(Line("p1-approval"), c => P1(c) && !F.IsApproved(c.State), new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task CASE_02_P1_not_ready_project_and_reload_back()
    {
        _name = "CASE-02";
        await ExpectCaseDestinationAsync(Line("p1-ready"), c => P1(c) && !F.IsReady(c.State), new F.Scope());
        var expected = F.CasesOf(A).Where(c => P1(c) && !F.IsReady(c.State)).Select(c => _seeded.Cases[c]).ToHashSet();
        await _s.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        (await _s.ReadPlanSelectionAsync()).Cases.Should().BeEquivalentTo(expected, "a reload keeps the hand-over");
        await _s.Page.GoBackAsync(new() { WaitUntil = WaitUntilState.Commit });
        await _s.Page.WaitForURLAsync(u => u.EndsWith("/manual-testing", StringComparison.Ordinal), new() { Timeout = 60000 });
        await _s.WaitForFiguresAsync();
        await ExpectFiguresAsync(A, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task CASE_03_blockers_hindrance_and_clarification()
    {
        _name = "CASE-03";
        await ExpectCaseDestinationAsync(Row("chart.blockers", "hindrance"), c => c.State == F.CaseState.Blocked, new F.Scope());
        await _s.OpenOverviewAsync();
        await ExpectCaseDestinationAsync(Row("chart.blockers", "clarify"), c => c.State == F.CaseState.NeedsClarification, new F.Scope());
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task CASE_04_module_scope_narrows_case_follow_ups()
    {
        _name = "CASE-04";
        await _s.SelectModuleAsync(A.ModuleKey("Billing"));
        var scope = new F.Scope("Billing");
        await ExpectFiguresAsync(A, scope);
        await ExpectCaseDestinationAsync(Line("p1-approval"), c => P1(c) && !F.IsApproved(c.State), scope);
        await ExpectCaseDestinationAsync(Line("p1-ready"), c => P1(c) && !F.IsReady(c.State), scope);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task CASE_05_specification_and_document_scope()
    {
        _name = "CASE-05";
        await _s.SelectModuleAsync(A.ModuleKey("Booking"));
        await _s.SelectPackAsync("001-reservations");
        if (await _s.Page.GetByTestId("ssp-specification").CountAsync() > 0) await _s.SelectDocumentAsync("spec.md");
        var scope = new F.Scope("Booking", "001-reservations", Booking1.Path);
        await ExpectFiguresAsync(A, scope);
        await ExpectCaseDestinationAsync(Line("p1-approval"), c => P1(c) && !F.IsApproved(c.State), scope);
        Clean();
    }

    [IsolatedBrowserFact]
    public async Task CASE_06_cases_of_another_application_never_appear()
    {
        _name = "CASE-06";
        await _s.UseAsync(B);
        await _s.OpenOverviewAsync();
        if (await _s.Page.GetByTestId("ssp-module").CountAsync() > 0) await _s.SelectModuleAsync(null);
        (await _s.KpiAsync("metric.testCases")).Should().Be(0, "Borealis has no cases; Aurora's never count here");
        (await Line("p1-approval").CountAsync()).Should().Be(0);
        Clean();
    }
}
