using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>«Legg til scenarioer» (Testplaner v4, 2026-11-26): existing acceptance scenarios and documented tests into a chosen testgruppe of an
/// existing plan — scenario-only, no case, no result, no duplicate; requirements without a scenario are not offered.</summary>
public sealed class AddScenariosToPlanTests : BunitContext
{
    private static readonly Guid PlanId = Guid.NewGuid();
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private readonly List<CreateProjectTestPlanRequest> _sent = [];

    private static DiscoveredScenario Ac(string id, string story, int n, ScenarioCandidateType type = ScenarioCandidateType.BusinessFunctional, string priority = "P1") => new()
    {
        CandidateId = id, SourceIdentity = "tss-" + id, CapabilityId = "specification", CanonicalKey = $"scenario:M|{id}", Title = $"Scenario {id}", Type = type, NumberInStory = n,
        Story = new CoverageUserStory { Key = story, StoryId = story, Title = "T", SpecificationPath = "Skole/specs/001-x/spec.md", Priority = priority },
    };

    private static readonly ScenarioDiscoveryResult Discovery = new()
    {
        DiscoveryId = "d1", ProjectKey = "m2lb",
        Candidates =
        [
            Ac("a1", "US1", 1), Ac("a2", "US1", 2), Ac("a3", "US2", 1, priority: "P4"),
            new DiscoveredScenario { CandidateId = "e1", SourceIdentity = "tss-e1", CapabilityId = "test-specification", CanonicalKey = "testspec:Hendelse/docs/H – Testspesifikasjon.md#TEST-E-01",
                Title = "TEST-E-01: Full flyt", Type = ScenarioCandidateType.BusinessE2E, Area = "Hendelse" },
            new DiscoveredScenario { CandidateId = "r1", CapabilityId = "specification", CanonicalKey = "requirement:M|FR-01", Title = "FR-01: krav" },
        ],
    };

    private TestPlanDraft _plan = new()
    {
        Id = PlanId, ProjectKey = "m2lb", PlanName = "E2E – Sprint 1", Revision = 2,
        Suites = [new("s-e2e", "Ende til ende", "template", null), new("s-int", "Integrasjon", "template", null)],
        PlannedScenarios = [new PlannedScenario { ScenarioId = "a2", SourceIdentity = "tss-a2", Title = "Scenario a2", SuiteId = "s-e2e" }],
    };

    public AddScenariosToPlanTests()
    {
        _api.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync(() => (_plan, (string?)null));
        _api.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateProjectTestPlanRequest r, CancellationToken _) =>
            {
                _sent.Add(r);
                _plan = _plan with
                {
                    Revision = _plan.Revision + 1,
                    PlannedScenarios = [.. _plan.PlannedScenarios ?? [], .. r.CandidateIds.Select(id => new PlannedScenario { ScenarioId = id, SourceIdentity = "tss-" + id, SuiteId = r.SuiteId ?? "" })],
                };
                return (_plan, (string?)null);
            });
    }

    private IRenderedComponent<AddScenariosToPlan> Open() =>
        Render<AddScenariosToPlan>(p => p.Add(x => x.Api, _api.Object).Add(x => x.ProjectKey, "m2lb").Add(x => x.PlanId, PlanId).Add(x => x.Discovery, Discovery));

    private static string[] Rows(IRenderedComponent<AddScenariosToPlan> cut) => [.. cut.FindAll("[data-testid=asp-row]").Select(r => r.GetAttribute("data-id")!)];

    [Fact]
    public void Offers_acceptance_scenarios_and_documented_tests_marks_planned_ones_and_filters()
    {
        var cut = Open();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        Rows(cut).Should().BeEquivalentTo(["a1", "a2", "a3", "e1"], "a requirement without a scenario is not a scenario");
        var planned = cut.Find("[data-testid=asp-row][data-id=a2]");
        (planned.GetAttribute("data-in-plan"), planned.QuerySelector("input")!.HasAttribute("disabled")).Should().Be(("true", true));
        cut.Find("[data-testid=asp-level]").Change("EndToEnd");
        Rows(cut).Should().Equal("e1");
        cut.Find("[data-testid=asp-level]").Change("");
        cut.Find("[data-testid=asp-priority]").Change("P4");
        Rows(cut).Should().Equal("a3");
        cut.Find("[data-testid=asp-priority]").Change("");
        cut.Find("[data-testid=asp-search]").Input("TEST-E");
        Rows(cut).Should().Equal("e1");
        cut.FindAll("[data-testid=asp-suite] option").Select(o => o.TextContent.Trim()).Should().Equal("Ende til ende", "Integrasjon");
    }

    [Fact]
    public void Adds_to_the_chosen_testgruppe_as_scenario_only_twice_into_different_suites_without_cases_or_duplicates()
    {
        var cut = Open();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        cut.Find("[data-testid=asp-row][data-id=e1] input").Change(true);
        cut.Find("[data-testid=asp-add]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-notice]").TextContent.Should().Be("1 scenario lagt til i «Ende til ende»."));

        cut.Find("[data-testid=asp-suite]").Change("s-int");
        cut.Find("[data-testid=asp-row][data-id=a1] input").Change(true);
        cut.Find("[data-testid=asp-add]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-notice]").TextContent.Should().Be("1 scenario lagt til i «Integrasjon»."));

        _sent.Select(r => (string.Join(",", r.CandidateIds), r.SuiteId, r.BaseRevision, r.ScenarioOnly, r.DraftId)).Should().Equal(
            ("e1", "s-e2e", (int?)2, true, (Guid?)PlanId), ("a1", "s-int", 3, true, PlanId));
        cut.Find("[data-testid=asp-row][data-id=e1]").GetAttribute("data-in-plan").Should().Be("true", "the list follows the saved plan");
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never, "no case is created");
        _api.Verify(x => x.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<RecordScenarioResultRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── BUG-001 (manual E2E 2026-11-27): the dialog closed on a click beside it and lost the tester's choices ──

    private (IRenderedComponent<AddScenariosToPlan> Cut, List<int> Closed) OpenTracked()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closed = new List<int>();
        var cut = Render<AddScenariosToPlan>(p => p.Add(x => x.Api, _api.Object).Add(x => x.ProjectKey, "m2lb").Add(x => x.PlanId, PlanId).Add(x => x.Discovery, Discovery)
            .Add(x => x.OnClose, () => closed.Add(1)).Add(x => x.ReturnFocusId, "mtp-add-scenarios"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        return (cut, closed);
    }

    [Fact]
    public void BUG001_a_click_on_the_backdrop_never_closes_the_dialog_or_loses_the_choice()
    {
        var (cut, closed) = OpenTracked();
        cut.Find("[data-testid=asp-search]").Input("Scenario");
        cut.Find("[data-testid=asp-row][data-id=a1] input").Change(true);
        cut.Find("[data-testid=asp]").Click();
        closed.Should().BeEmpty("a click beside the dialog is not a decision to close");
        JSInterop.Invocations.Should().Contain(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase), "focus goes back into the dialog, so Escape keeps working");
        cut.Find("[data-testid=asp-row][data-id=a1] input").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=asp-search]").GetAttribute("value").Should().Be("Scenario");
    }

    [Fact]
    public void BUG001_Escape_and_Lukk_ask_first_with_unsaved_choices_and_close_at_once_without()
    {
        var (cut, closed) = OpenTracked();
        cut.Find("[data-testid=asp-dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        // Closing and returning focus are asynchronous (the focus is a JS call after the close): wait for them, never assert before they ran.
        cut.WaitForAssertion(() => closed.Should().ContainSingle("nothing was chosen: Escape closes at once"));
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "mtp-add-scenarios", "focus returns to the button that opened it"));

        (cut, closed) = OpenTracked();
        cut.Find("[data-testid=asp-row][data-id=a1] input").Change(true);
        cut.Find("[data-testid=asp-dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        closed.Should().BeEmpty();
        cut.Find("[data-testid=asp-confirm]").TextContent.Should().Contain("1 valgt scenario er ikke lagt til");
        cut.Find("[data-testid=asp-dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("[data-testid=asp-confirm]").Should().BeEmpty("Escape on the question keeps the dialog and the choice");
        closed.Should().BeEmpty();

        cut.Find("[data-testid=asp-close]").Click();
        cut.Find("[data-testid=asp-confirm-keep]").Click();
        closed.Should().BeEmpty();
        cut.Find("[data-testid=asp-row][data-id=a1] input").HasAttribute("checked").Should().BeTrue();

        cut.Find("[data-testid=asp-close]").Click();
        cut.Find("[data-testid=asp-confirm-discard]").Click();
        cut.WaitForAssertion(() => closed.Should().ContainSingle("closing without adding is an explicit choice"));
    }

    // -- «Legg til scenarioer» beyond the loaded window (2026-11-27, P1). Before: only Discovery.Candidates (the first 2,000) could be found. --

    private readonly List<(string Query, int Skip, ScenarioSearchFilter? Filter, string? Scope)> _searches = [];

    private IRenderedComponent<AddScenariosToPlan> OpenWindowed(Func<string, int, ScenarioSearchFilter?, ScenarioSearchPage> server)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.SearchDiscoveryAsync("m2lb", It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? scope, string q, int skip, int _, ScenarioSearchFilter? f, CancellationToken _) => { _searches.Add((q, skip, f, scope)); return (server(q, skip, f), (string?)null); });
        var windowed = Discovery with { NotKept = 1_500, SelectionScope = "scope:Skole|spec", NotKeptFacets = new([], [], [], ["P2"], [], false, false) { Modules = ["Arkiv"] } };
        var cut = Render<AddScenariosToPlan>(p => p.Add(x => x.Api, _api.Object).Add(x => x.ProjectKey, "m2lb").Add(x => x.PlanId, PlanId).Add(x => x.Discovery, windowed));
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        return cut;
    }

    private static DiscoveredScenario Far(string id, string title) => new()
    {
        CandidateId = id, SourceIdentity = "tss-" + id, CapabilityId = "test-specification", CanonicalKey = $"testspec:Arkiv/docs/T.md#{id}", Title = title,
        Type = ScenarioCandidateType.BusinessE2E, Area = "Arkiv",
    };

    [Fact]
    public void ASP_a_scenario_beyond_the_first_window_is_found_on_the_server_chosen_and_added_with_the_loaded_choice()
    {
        var cut = OpenWindowed((q, skip, _) => q.Contains("TEST-E-99") ? new ScenarioSearchPage("d1", q, 1, skip, [Far("far99", "TEST-E-99: Arkivering")]) : new ScenarioSearchPage("d1", q, 0, skip, []));
        cut.Find("[data-testid=asp-beyond-hint]").TextContent.Should().Contain("1500 scenarioer er ikke lastet inn");
        _searches.Should().BeEmpty("nothing is fetched without a search or filter");

        cut.Find("[data-testid=asp-row][data-id=a1] input").Change(true);
        cut.Find("[data-testid=asp-search]").Input("TEST-E-99");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=asp-beyond-row]").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        _searches.Should().ContainSingle().Which.Should().Match<(string Query, int Skip, ScenarioSearchFilter? Filter, string? Scope)>(s =>
            s.Query == "TEST-E-99" && s.Skip == 0 && s.Filter!.ClassifiableOnly && s.Scope == "scope:Skole|spec");
        cut.Find("[data-testid=asp-beyond-row][data-id=far99] input").Change(true);

        // Another search: the earlier choices stay chosen.
        cut.Find("[data-testid=asp-search]").Input("noe annet");
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-beyond-none]"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=asp-count]").TextContent.Should().Contain("2 valgt");

        cut.Find("[data-testid=asp-add]").Click();
        cut.WaitForAssertion(() => _sent.Should().ContainSingle());
        _sent[0].CandidateIds.Should().BeEquivalentTo(["a1", "far99"]);
        _sent[0].DiscoveryId.Should().Be("d1");
    }

    [Fact]
    public void ASP_a_filter_alone_searches_the_unloaded_scenarios_with_the_dialogs_module_and_never_repeats_a_loaded_row()
    {
        var cut = OpenWindowed((q, skip, _) => new ScenarioSearchPage("d1", q, 2, skip, [Discovery.Candidates[0], Far("far1", "TEST-E-01: Arkiv")]));
        cut.FindAll("[data-testid=asp-module] option").Select(o => o.TextContent).Should().Contain("Arkiv", "a module of the unloaded scenarios is offered");
        cut.FindAll("[data-testid=asp-priority] option").Select(o => o.TextContent).Should().Contain("P2");
        cut.Find("[data-testid=asp-module]").Change("Arkiv");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=asp-beyond-row]").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=asp-beyond-row]").GetAttribute("data-id").Should().Be("far1", "a row already in the loaded list is not shown twice");
        _searches.Single().Filter!.Module.Should().Be("Arkiv");
        _searches.Single().Query.Should().BeEmpty();
    }

    [Fact]
    public void ASP_a_scenario_already_in_the_plan_is_marked_and_cannot_be_chosen_again_from_the_server_results()
    {
        _plan = _plan with { PlannedScenarios = [.. _plan.PlannedScenarios!, new PlannedScenario { ScenarioId = "far7", SourceIdentity = "tss-far7", SuiteId = "s-e2e" }] };
        var cut = OpenWindowed((q, skip, _) => new ScenarioSearchPage("d1", q, 1, skip, [Far("far7", "TEST-E-07: Arkiv")]));
        cut.Find("[data-testid=asp-search]").Input("TEST-E-07");
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-beyond-row][data-id=far7]"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=asp-beyond-row][data-id=far7]").GetAttribute("data-in-plan").Should().Be("true");
        cut.Find("[data-testid=asp-beyond-row][data-id=far7] input").HasAttribute("disabled").Should().BeTrue();
    }
}
