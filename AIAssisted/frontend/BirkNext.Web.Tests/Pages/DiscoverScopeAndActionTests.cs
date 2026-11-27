using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Q = BirkNext.TestPlanning.ScenarioWorkQueue;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Finn og forstå: since 2026-11-25 the scenarios of the chosen module + specification load by themselves (no «Oppdag» / «Oppdag på nytt» for the
/// tester). The page asks for the discovery of exactly its selection; when there is none it is fetched once (a spinner meanwhile), a failure says
/// «Kunne ikke hente scenarioer.» with «Prøv igjen», and every selection (queue, filters, selected scenarios, the chosen specification) is kept. The
/// page still says plainly when the chosen specification gave no scenarios of its own, and never switches the specification back.
/// </summary>
public sealed class DiscoverScopeAndActionTests : BunitContext
{
    /// <summary>Open with «Ingen gruppering» saved (the flat list); grouping tests set false to get the default grouping.</summary>
    private bool FlatList { get; set; } = true;

    private const string MainPath = "SkoleService/specs/001-skoleservice/spec.md";
    private const string DependenciesPath = "SkoleService/docs/avhengigheter-bestilling.md";

    private static readonly CoverageUserStory MainStory = new() { Key = CoverageUserStory.KeyOf(MainPath, "us1"), StoryId = "US1", Ordinal = 1, Title = "Report", Priority = "P1", SpecificationPath = MainPath };

    private static DiscoveredScenario C(string id, string title, string capability = "specification") => new()
    {
        CandidateId = id, ProjectKey = "library", CapabilityId = capability, SourceCategory = capability, Area = "Rapporter", Title = title, Purpose = "Formål",
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "Vises", CanonicalKey = "k:" + id,
        Story = capability == "specification" ? MainStory : null, SourceIdentity = "tss-" + id,
        Template = capability == "specification"
            ? [new("given", "Gitt", $"a report for {id}"), new("when", "Når", "the report is submitted"), new("then", "Så", $"the report {id} is stored exactly as sent")]
            : [],
    };

    private static readonly DiscoveredScenario Spec1 = C("c-1", "A user submits a valid report");
    private static readonly DiscoveredScenario Spec2 = C("c-2", "A user corrects a report");
    private static readonly DiscoveredScenario Ui = C("c-ui", "Report page lists reports", "ui-source");

    private static ScenarioWorkItem W(DiscoveredScenario c, params Q[] queues) => new() { CandidateId = c.CandidateId, Queues = [.. queues], Rank = queues.Contains(Q.Recommended) ? 1 : null };

    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private Mock<ICurrentWorkspaceProjection> _projection = null!;
    private TaskCompletionSource<(ScenarioDiscoveryResult?, string?)>? _discovery;
    /// <summary>False = the server has no discovery for the page's selection yet (until a discovery completes).</summary>
    private bool _known = true;
    /// <summary>Candidates the discovery did not keep (above the safety limit); null = a complete population.</summary>
    private int? _notKept;
    private ScenarioFacets? _notKeptFacets;
    private readonly List<string?> _scopes = [];

    private IRenderedComponent<ManualTestingDiscover> Open(string? selectedSpecification, params DiscoveredScenario[] candidates) => Open(selectedSpecification, true, candidates);

    private IRenderedComponent<ManualTestingDiscover> Open(string? selectedSpecification, bool known, params DiscoveredScenario[] candidates)
    {
        _known = known;
        _projection = BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library"));
        Services.AddSingleton(_projection.Object);
        if (selectedSpecification is not null)
        {
            var main = new ExplorerArtifact("s1", WorkspaceArtifactType.Specification, "Feature Specification: SkoleService", "spec.md", MainPath, ExplorerArtifactSource.Workspace, null, null, 1, "Unknown",
                ExplorerArtifactCurrentness.Current, null, Module: "SkoleService");
            var dependencies = new ExplorerArtifact("s2", WorkspaceArtifactType.Specification, "Avhengigheter for SkoleService før go-live", "avhengigheter-bestilling.md", DependenciesPath,
                ExplorerArtifactSource.Workspace, null, null, 1, "Unknown", ExplorerArtifactCurrentness.Current, null, Module: "SkoleService");
            var selected = selectedSpecification == MainPath ? main : dependencies;
            var explorer = new Mock<IArtifactExplorerContext>();
            explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(new ArtifactExplorerState(WorkspaceArtifactType.Specification,
                ExplorerArtifactStatus.Loaded, "library", "Library", [main, dependencies], selected, ExplorerSelectionReason.Explicit, "# spec", Module: "SkoleService",
                Modules: [new("SkoleService", 2, selected.Id, ExplorerSelectionReason.Explicit)]));
            Services.AddSingleton(explorer.Object);
        }
        _api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true }, (string?)null));
        _api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, string?, CancellationToken>((_, _, scope, _) => _scopes.Add(scope))
            .ReturnsAsync(() => _known ? (new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [.. candidates], NotKept = _notKept, NotKeptFacets = _notKeptFacets }, (string?)null) : (null, null));
        _api.Setup(x => x.WorkQueuesAsync("library", "d1", It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioWorkQueueView
        {
            DiscoveryId = "d1", DiscoveryCurrent = true, CoverageComplete = true,
            Items = [.. candidates.Select(c => c.CandidateId == "c-2" ? W(c, Q.NeedsClarification) : W(c, Q.Recommended))],
        }, (string?)null));
        _api.Setup(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => (_discovery = new TaskCompletionSource<(ScenarioDiscoveryResult?, string?)>()).Task);
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // These tests check the list, drawer and filters, not grouping: the tester's saved choice «Ingen gruppering» gives the flat list (groups start collapsed, 2026-11-26).
        if (FlatList) JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("None>None");
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        if (known) cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void No_discovery_button_is_needed_and_a_known_selection_is_shown_without_fetching()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        cut.FindAll("[data-testid=mtd-discover]").Should().BeEmpty("the tester never has to press «Oppdag» or «Oppdag på nytt»");
        cut.Markup.Should().NotContain("Oppdag scenarioer").And.NotContain("Oppdag på nytt");
        cut.FindAll("[data-testid=mtd-discovering]").Should().BeEmpty("nothing is loading");
        cut.Find(".ssp-row [data-testid=ssp-specification]").Should().NotBeNull();
        cut.Find("[data-testid=mtd-context]").QuerySelector("button").Should().BeNull("the status bar holds no actions");
        cut.FindAll("[data-testid=ssp-discover]").Should().BeEmpty("the picker's own link to another page is not used here");
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never, "a discovery for this selection exists");
        _scopes.Should().NotBeEmpty().And.OnlyContain(s => s != null, "the page always asks for the discovery of its own selection");
    }

    [Fact]
    public void Without_a_discovery_for_the_selection_scenarios_are_fetched_once_with_a_spinner_and_every_selection_is_kept()
    {
        var cut = Open(MainPath, known: false, Spec1, Spec2, Ui);

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-discovering]").TextContent.Should().Contain("Henter scenarioer"));
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Once, "fetched by itself");
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty("nothing old is shown while the selection's scenarios load");

        _known = true;
        cut.InvokeAsync(() => _discovery!.SetResult((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library" }, null)));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=mtd-discovering]").Should().BeEmpty("the spinner is gone after success");
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Once, "once per selection: no loop");
        cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be("s1", "discovery never changes the chosen specification");

        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == "NeedsClarification").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Single().QuerySelector("[data-testid=mtd-select]")!.Change(true);
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1");
    }

    [Fact]
    public void A_failed_fetch_says_so_offers_a_safe_retry_and_the_spinner_ends()
    {
        var cut = Open(MainPath, known: false, Spec1, Spec2, Ui);
        cut.WaitForAssertion(() => (_discovery is not null).Should().BeTrue());
        var first = _discovery!;
        cut.InvokeAsync(() => first.SetResult((null, "Kilden kunne ikke leses.")));

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-discover-error]").TextContent.Should().StartWith("Kunne ikke hente scenarioer.").And.Contain("Kilden kunne ikke leses."));
        cut.FindAll("[data-testid=mtd-discovering]").Should().BeEmpty("the spinner ends after a failure");
        cut.Find("[data-testid=mtd-discover-retry]").Click();
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2), "«Prøv igjen» runs the same safe read again");
        _known = true;
        var second = _discovery!;
        ReferenceEquals(second, first).Should().BeFalse("a new attempt");
        cut.InvokeAsync(() => second.SetResult((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library" }, null)));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=mtd-discover-error]").Should().BeEmpty();
    }

    [Fact]
    public void Scenarios_from_another_specification_are_labelled_without_asking_the_tester_to_discover()
    {
        var cut = Open(DependenciesPath, Spec1, Spec2, Ui);

        var note = cut.Find("[data-testid=mtd-spec-context]");
        note.GetAttribute("data-state").Should().Be("other");
        note.TextContent.Should().Contain($"kommer fra {MainPath}").And.Contain("«Avhengigheter for SkoleService før go-live» ga ingen egne testscenarioer").And.NotContain("Oppdag");
        cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be("s2", "the chosen specification is never switched back");
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=mtd-count-spec]").TextContent.Should().Be("2");
        cut.Find("[data-testid=mtd-count-suggestions]").TextContent.Should().Be("3", "all sources of the application");
    }

    [Fact]
    public void A_specification_without_scenarios_says_so_and_offers_all_scenarios()
    {
        var cut = Open(DependenciesPath, Ui);

        var note = cut.Find("[data-testid=mtd-spec-context]");
        note.GetAttribute("data-state").Should().Be("none");
        note.TextContent.Should().Contain("Ingen testscenarioer funnet for denne spesifikasjonen");
        cut.Find("[data-testid=mtd-spec-show-all]").Click();
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("All");
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("c-ui");
    }

    [Fact]
    public void Scenarios_from_the_chosen_specification_need_no_notice()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        cut.FindAll("[data-testid=mtd-spec-context]").Should().BeEmpty();
    }

    // ── 2026-11-24 top-section refinement: hierarchy, filters on demand, consistent counts, selection kept ─────────────

    [Fact]
    public void An_incomplete_population_is_said_where_the_scenarios_are_listed_and_a_complete_one_says_nothing()
    {
        // 2026-11-26: the safety limit never silently hides scenarios — the page says how many are not kept and how to reach them.
        _notKept = 15;
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-not-kept]").TextContent.Should().Contain("15 scenarioer").And.Contain("Søk finner dem i hele utvalget"));
    }

    [Fact]
    public void Searching_an_incomplete_population_also_searches_the_server_and_a_match_far_beyond_the_loaded_window_opens()
    {
        // Pagination V2 (2026-11-26): scenario no. 12,000 is found on the server without loading the 11,999 before it, and opens like any other.
        _notKept = 2_000;
        var far = C("c-12000", "Scenario nummer 12000 sendes på nytt");
        var calls = new List<(string Query, int Skip)>();
        _api.Setup(x => x.SearchDiscoveryAsync("library", It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string, int, int, ScenarioSearchFilter?, CancellationToken>((_, _, q, skip, _, _, _) => calls.Add((q, skip)))
            .ReturnsAsync((string _, string? _, string q, int skip, int _, ScenarioSearchFilter? _, CancellationToken _) =>
                (new ScenarioSearchPage("d1", q, 41, skip, skip == 0 ? [far, .. Enumerable.Range(1, 39).Select(i => C($"x{i}", $"Scenario nummer 12000 variant {i}"))] : [C("x40", "Scenario nummer 12000 variant 40")]), (string?)null));
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-scenarios]"));

        cut.Find("[data-testid=mtd-search]").Input("nummer 12000");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-beyond-row]").Should().HaveCount(40), TimeSpan.FromSeconds(5));
        calls.Should().ContainSingle().Which.Should().Be(("nummer 12000", 0), "one request for the settled query, not one per keystroke result");
        cut.Find("[data-testid=mtd-beyond]").TextContent.Should().Contain("(41)");
        cut.Find("[data-testid=mtd-beyond-more-count]").TextContent.Should().Be("Viser 40 av 41 treff");

        cut.Find("[data-testid=mtd-beyond-more]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-beyond-row]").Should().HaveCount(41));
        calls.Last().Should().Be(("nummer 12000", 40), "«Vis flere» continues where the page ended");

        cut.Find("[data-testid=mtd-beyond-row][data-id=c-12000] [data-testid=mtd-beyond-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Scenario nummer 12000"));
    }

    [Fact]
    public void The_detail_panel_leads_with_the_testers_next_step_and_folds_classification_and_evidence()
    {
        // V9 (2026-11-26): «Forbered testtilfelle» right under the title; classification, test type, automated tests and the source in one folded section.
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-show-details]"));
        cut.Find("[data-testid=mtd-show-details]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail-actions]"));

        var html = cut.Find("[data-testid=mtd-detail]").InnerHtml;
        html.IndexOf("mtd-detail-actions", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("mtd-expected", StringComparison.Ordinal), "the action comes first");
        html.IndexOf("mtd-detail-cases", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("mtd-detail-secondary", StringComparison.Ordinal));
        cut.Find("[data-testid=mtd-detail-actions] [data-testid=mtd-detail-prepare]").TextContent.Should().Be("Forbered testtilfelle");
        var secondary = cut.Find("[data-testid=mtd-detail-secondary]");
        secondary.HasAttribute("open").Should().BeFalse("classification and evidence are on demand");
        secondary.QuerySelector("[data-testid=mtd-kind]").Should().NotBeNull("nothing is removed, only folded");
        secondary.QuerySelector("[data-testid=mtd-tech]").Should().NotBeNull();
        cut.FindAll("[data-testid=mtd-detail-prepare]").Should().ContainSingle("one primary action, not repeated");
    }

    [Fact]
    public void A_complete_population_never_asks_the_server()
    {
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-scenarios]"));
        cut.Find("[data-testid=mtd-search]").Input("report");
        cut.FindAll("[data-testid=mtd-beyond]").Should().BeEmpty();
        _api.Verify(x => x.SearchDiscoveryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_filter_alone_searches_the_scenarios_not_loaded_with_that_filter_and_names_the_filters_it_cannot_apply()
    {
        // 2026-11-27: the area «Arkiv» exists only among the scenarios not loaded. It is offered (from the facets), and choosing it asks the server
        // for that area's scenarios — no search words needed — with the filter sent, not ignored.
        _notKept = 1_500;
        _notKeptFacets = new(["Arkiv"], [], ["REQ-900"], [], [], false, false);
        var asked = new List<(string Query, ScenarioSearchFilter? Filter)>();
        _api.Setup(x => x.SearchDiscoveryAsync("library", It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string, int, int, ScenarioSearchFilter?, CancellationToken>((_, _, q, _, _, f, _) => asked.Add((q, f)))
            .ReturnsAsync((string _, string? _, string q, int skip, int _, ScenarioSearchFilter? _, CancellationToken _) =>
                (new ScenarioSearchPage("d1", q, 1, skip, [C("c-arkiv", "Arkivert rapport hentes") with { Area = "Arkiv" }]), (string?)null));
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-more-filters]"));
        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.FindAll("[data-testid=mtd-filter-area] option").Select(o => o.TextContent).Should().Contain("Arkiv", "a value of the scenarios not loaded is offered too");
        cut.FindAll("[data-testid=mtd-filter-requirement] option").Select(o => o.TextContent).Should().Contain("REQ-900");

        cut.Find("[data-testid=mtd-filter-area]").Change("Arkiv");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-beyond-row]").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        asked.Should().ContainSingle();
        asked[0].Query.Should().BeEmpty();
        asked[0].Filter.Should().Be(new ScenarioSearchFilter(Area: "Arkiv"));
        cut.Find("[data-testid=mtd-beyond-unfiltered]").TextContent.Should().Contain("arbeidskø").And.Contain("Ikke brukt på disse treffene");
    }

    [Fact]
    public void Without_words_or_a_server_filter_the_scenarios_not_loaded_are_not_searched()
    {
        _notKept = 1_500;
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-scenarios]"));
        cut.Find("[data-testid=mtd-search]").Input("r");
        cut.FindAll("[data-testid=mtd-beyond]").Should().BeEmpty("one letter and no filter: nothing to search for");
        _api.Verify(x => x.SearchDiscoveryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void An_open_scenario_beyond_the_loaded_window_is_fetched_by_id_not_reported_missing()
    {
        // 2026-11-27: coming back to Finn og forstå with a scenario open that the (smaller) window does not hold: it is fetched by id.
        _notKept = 1_500;
        var far = C("c-far", "Scenario langt ute i utvalget");
        _api.Setup(x => x.DiscoveryCandidatesAsync("library", It.IsAny<string?>(), It.Is<IReadOnlyCollection<string>>(ids => ids.Contains("c-far")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([far], (string?)null));
        JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.DiscoverContextKey("library"))
            .SetResult(System.Text.Json.JsonSerializer.Serialize(new ScenarioWorkspacePresentation.WorkContext(null, "c-far", ScenarioWorkspacePresentation.Tabs.Scenarios, "Scenario langt ute i utvalget")));
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Scenario langt ute i utvalget"));
        cut.FindAll("[data-testid=mtd-detail-empty]").Should().BeEmpty();
    }

    [Fact]
    public void A_complete_population_shows_no_incompleteness_notice()
    {
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-scenarios]"));
        cut.FindAll("[data-testid=mtd-not-kept]").Should().BeEmpty();
    }

    private static string[] Rows(IRenderedComponent<ManualTestingDiscover> cut) => [.. cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")!)];
    private static int Tab(IRenderedComponent<ManualTestingDiscover> cut, string queue) =>
        int.Parse(cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == queue).QuerySelector(".msb-queue-n")!.TextContent);
    private static void Queue(IRenderedComponent<ManualTestingDiscover> cut, string queue) =>
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == queue).Click();

    [Fact]
    public void Recommended_and_clarification_are_the_primary_views_and_the_figures_are_one_secondary_line()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        cut.FindAll("[data-testid=mtd-queue].is-primary").Select(e => e.GetAttribute("data-queue")).Should().Equal("Recommended", "NeedsClarification");
        cut.FindAll("[data-testid=mtd-queue]").Should().HaveCount(5, "all five views stay available");
        var facts = cut.Find("[data-testid=mtd-kpis]").TextContent;
        facts.Should().Contain("3 forslag totalt").And.Contain("2 fra spesifikasjonen").And.Contain("0 valgt").And.Contain("kildeendringer");
        cut.FindAll("[data-testid=mtd-count-clarify]").Should().BeEmpty("«Trenger avklaring» has one number: its queue");
        cut.Find("[data-testid=mtd-queue-short]").TextContent.Should().Be("Scenarioer med tydelig forventet resultat og uten forberedte testtilfeller.");
        cut.Find("[data-testid=mtd-queue-help]").TextContent.Should().Contain("Klar for klargjøring betyr ikke klar for kjøring").And.Contain("summeres ikke");
    }

    [Fact]
    public void Every_count_matches_what_its_view_lists_with_and_without_technical_content()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        foreach (var technical in new[] { false, true })
        {
            if (technical) cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").Click();
            var union = new HashSet<string>();
            foreach (var queue in new[] { "Recommended", "NeedsClarification", "Prepared", "SourceChanged" })
            {
                Queue(cut, queue);
                Rows(cut).Should().HaveCount(Tab(cut, queue), $"{queue} (technical {technical}): the tab counts what the view lists");
                union.UnionWith(Rows(cut));
            }
            Queue(cut, "All");
            Rows(cut).Should().HaveCount(Tab(cut, "All"));
            Rows(cut).Should().BeEquivalentTo(union, "«Alle scenarioer» is the current selection; the queues overlap, they do not add up to it");
            cut.Find("[data-testid=mtd-visible]").TextContent.Should().StartWith($"Viser {Tab(cut, "All")} av 3 forslag");
            cut.Find("[data-testid=mtd-count-suggestions]").TextContent.Should().Be("3", "the total is the whole discovery, whatever is shown");
        }
    }

    [Fact]
    public void More_filters_open_on_demand_show_how_many_are_set_and_compose_with_the_queue()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        cut.Find("[data-testid=mtd-search]").Should().NotBeNull("search is always visible");
        // V9 (2026-11-26): priority and preparation state are on the main row; area and the rest wait behind «Flere filtre».
        cut.Find("[data-testid=mtd-filter-priority]").Should().NotBeNull("priority is always visible");
        cut.Find("[data-testid=mtd-filter-status]").Should().NotBeNull("preparation state is always visible");
        cut.FindAll("[data-testid=mtd-filter-area]").Should().BeEmpty("area is under «Flere filtre»");
        cut.FindAll("[data-testid=mtd-filters-panel]").Should().BeEmpty("the other filters wait behind «Flere filtre»");

        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.Find("[data-testid=mtd-more-filters]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=mtd-filters-panel] [data-testid=mtd-filter-area]").Should().NotBeNull();
        cut.Find("[data-testid=mtd-filter-priority]").Change("P1");
        cut.Find("[data-testid=mtd-active-filters]").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=mtd-filter-chip]").Select(c => c.TextContent.Replace("×", "").Trim()).Should().Equal(["Prioritet P1"], "each active filter is a removable chip");
        Rows(cut).Should().Equal(["c-1"], "the recommended queue, narrowed to declared P1");
        Queue(cut, "NeedsClarification");
        Rows(cut).Should().Equal(["c-2"], "the filter stays when the queue changes");
        cut.Find("[data-testid=mtd-filter-chip]").Click();
        cut.FindAll("[data-testid=mtd-filter-chip]").Should().BeEmpty("removing the chip removes that filter");
        cut.Find("[data-testid=mtd-filter-priority]").Change("P1");

        cut.Find("[data-testid=mtd-reset-filters]").Click();
        cut.FindAll("[data-testid=mtd-active-filters]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("NeedsClarification", "resetting filters keeps the view");
        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.FindAll("[data-testid=mtd-filters-panel]").Should().BeEmpty();
    }

    [Fact]
    public void Technical_content_stays_hidden_until_asked_and_resetting_filters_keeps_that_choice()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        cut.Find("[data-testid=mtd-kind-view-option][data-view=Manual]").GetAttribute("aria-checked").Should().Be("true");

        cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").Click();
        cut.Find("[data-testid=mtd-search]").Input("report");
        cut.Find("[data-testid=mtd-reset-filters]").Click();

        cut.Find("[data-testid=mtd-search]").GetAttribute("value").Should().BeEmpty();
        cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").GetAttribute("aria-checked").Should().Be("true", "a visibility preference, not a filter");
    }

    [Fact]
    public void Filters_are_kept_for_the_tab_and_restored_when_the_tester_comes_back()
    {
        JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.DiscoverFilterKey("library"))
            .SetResult(System.Text.Json.JsonSerializer.Serialize(new BirkNext.Web.Services.ScenarioBrowserPresentation.Filter(Query: "corrects", Queue: Q.NeedsClarification)));
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        cut.Find("[data-testid=mtd-search]").GetAttribute("value").Should().Be("corrects");
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("NeedsClarification");

        cut.Find("[data-testid=mtd-search]").Input("");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "sessionStorage.setItem" && i.Arguments[0]!.ToString() == TabDraftStore.DiscoverFilterKey("library"));
    }

    [Fact]
    public void A_saved_choice_in_the_same_project_keeps_the_selection_and_does_not_blank_the_page()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        cut.FindAll("[data-testid=mtd-scenario]").First().QuerySelector("[data-testid=mtd-select]")!.Change(true);
        Queue(cut, "NeedsClarification");

        _projection.Raise(p => p.Changed += null);

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1", "the same project keeps the tester's selection");
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("NeedsClarification");
        cut.FindAll("[data-testid=mtd-no-project]").Should().BeEmpty();
    }
    // ── «Fra kilden (ordrett)» collapsed by default (2026-11-25) ────────────────────────────────────────────────────────────

    [Fact]
    public void Fra_kilden_starts_collapsed_opens_and_closes_keeps_the_original_text_and_is_collapsed_again_for_another_scenario()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        var toggle = cut.Find("[data-testid=mtd-source-toggle]");
        toggle.TextContent.Should().Be("Fra kilden (ordrett)");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("aria-controls").Should().Be("mtd-source-body");
        cut.Find("[data-testid=mtd-source-body]").HasAttribute("hidden").Should().BeTrue("collapsed by default");
        var text = cut.Find("[data-testid=mtd-source-body]").TextContent;
        text.Should().Contain("Gitt").And.Contain("a report for c-1").And.Contain("Når").And.Contain("Så").And.Contain("the report c-1 is stored exactly as sent", "the original text is kept, only hidden");

        toggle.Click();
        cut.Find("[data-testid=mtd-source-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=mtd-source-body]").HasAttribute("hidden").Should().BeFalse();
        cut.Find("[data-testid=mtd-source-toggle]").Click();
        cut.Find("[data-testid=mtd-source-body]").HasAttribute("hidden").Should().BeTrue("a second click collapses it");

        cut.Find("[data-testid=mtd-source-toggle]").Click();
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == "All").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-2").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-source-body]").TextContent.Should().Contain("a report for c-2"));
        cut.Find("[data-testid=mtd-source-toggle]").GetAttribute("aria-expanded").Should().Be("false", "another acceptance scenario starts collapsed");

        cut.Find("[data-testid=mtd-source-toggle]").Click();
        cut.Find($"[data-testid=mtd-story][data-story='{MainStory.Key}']").Click();
        cut.Find("[data-testid=mtd-source-toggle]").GetAttribute("aria-expanded").Should().Be("false", "choosing a user story starts collapsed");
    }

    [Fact]
    public void Brukerhistorier_is_the_default_grouping()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        // The plain story list (no functional areas) is shown by default; with grouping inputs the select starts on «Brukerhistorier».
        cut.FindAll("[data-testid=mtd-group]").Should().BeEmpty("no area grouping unless the tester chooses it");
        if (cut.FindAll("[data-testid=mtd-group-mode]").Count > 0) cut.Find("[data-testid=mtd-group-mode] option[selected]").TextContent.Should().Be("Brukerhistorier");
        cut.FindAll("[data-testid=mtd-story]").Should().Contain(s => s.GetAttribute("data-story") == MainStory.Key);
    }

    [Fact]
    public void A_wide_page_shows_stories_scenarios_and_the_open_scenario_as_three_columns_that_follow_the_selection()
    {
        // 2026-11-27: at 84rem or more the page reports «wide» (js birknextWatchWidth) and the detail is a column, not a drawer.
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-workspace]"));
        cut.Find("[data-testid=mtd-workspace]").GetAttribute("data-layout").Should().Be("two");
        cut.Find("[data-testid=mtd-detail]").HasAttribute("hidden").Should().BeTrue("narrower: the drawer is closed until «Vis detaljer»");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextWatchWidth" && (int)i.Arguments[2]! == ManualTestingDiscover.ThreeColumnsMinPx);

        cut.InvokeAsync(() => cut.Instance.OnLayoutWidth(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-workspace]").GetAttribute("data-layout").Should().Be("three"));
        var detail = cut.Find("[data-testid=mtd-detail]");
        detail.HasAttribute("hidden").Should().BeFalse("the open scenario is always shown in its column");
        detail.GetAttribute("role").Should().Be("region");
        detail.ClassList.Should().Contain("is-docked");
        cut.FindAll("[data-testid=mtd-drawer-close]").Should().BeEmpty("a column has nothing to close");

        // A row click is enough: the column follows the selection.
        cut.Find("[data-testid=mtd-queue][data-queue=All]").Click(); // c-2 is in «Må avklares», not the default queue
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Count.Should().BeGreaterThan(1));
        var second = cut.FindAll("[data-testid=mtd-scenario]").First(r => !r.ClassList.Contains("is-active"));
        var title = second.QuerySelector("[data-testid=mtd-scenario-title]")!.TextContent.Trim();
        second.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain(title));

        cut.InvokeAsync(() => cut.Instance.OnLayoutWidth(false));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").HasAttribute("hidden").Should().BeTrue("back to a closed drawer when narrow"));
    }

    [Fact]
    public void A_user_story_whose_scenarios_are_all_beyond_the_window_is_offered_and_choosing_it_searches_the_server()
    {
        // 2026-11-27: US120 has no loaded scenario. It is listed under «Ikke lastet inn»; choosing it asks the server for exactly that story.
        _notKept = 100;
        var far = new CoverageUserStory { Key = "us-120", StoryId = "US120", Title = "Siste historie", Ordinal = 120 };
        _notKeptFacets = new([], [], [], [], [far], false, false);
        var asked = new List<ScenarioSearchFilter?>();
        _api.Setup(x => x.SearchDiscoveryAsync("library", It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ScenarioSearchFilter?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string, int, int, ScenarioSearchFilter?, CancellationToken>((_, _, _, _, _, f, _) => asked.Add(f))
            .ReturnsAsync((string _, string? _, string q, int skip, int _, ScenarioSearchFilter? _, CancellationToken _) =>
                (new ScenarioSearchPage("d1", q, 100, skip, [C("c-far", "AC 1 i siste historie") with { Story = far }]), (string?)null));
        var cut = Open(MainPath, Spec1, Spec2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-unloaded-stories] [data-testid=mtd-story][data-story=us-120]"));
        cut.Find("[data-testid=mtd-unloaded-stories-h]").TextContent.Should().Contain("Ikke lastet inn (1)");

        cut.Find("[data-testid=mtd-unloaded-stories] [data-testid=mtd-story][data-story=us-120]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-beyond-row]").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        asked.Should().ContainSingle().Which!.Story.Should().Be("us-120");
        cut.Find("[data-testid=mtd-beyond]").TextContent.Should().Contain("(100)");
    }
}

