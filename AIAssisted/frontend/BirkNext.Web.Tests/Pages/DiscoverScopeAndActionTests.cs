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
/// Finn og forstå (2026-11-24): «Oppdag på nytt» sits beside the Module / Specification choice (not in the status bar), runs once at a time, and
/// keeps every selection (queue, filters, selected scenarios); the page says plainly when the shown scenarios come from another specification
/// than the chosen one, or when the chosen one gave none — it never runs discovery by itself and never switches the specification back.
/// </summary>
public sealed class DiscoverScopeAndActionTests : BunitContext
{
    private const string MainPath = "SkoleService/specs/001-skoleservice/spec.md";
    private const string DependenciesPath = "SkoleService/docs/avhengigheter-bestilling.md";

    private static readonly CoverageUserStory MainStory = new() { Key = CoverageUserStory.KeyOf(MainPath, "us1"), StoryId = "US1", Ordinal = 1, Title = "Report", Priority = "P1", SpecificationPath = MainPath };

    private static DiscoveredScenario C(string id, string title, string capability = "specification") => new()
    {
        CandidateId = id, ProjectKey = "library", CapabilityId = capability, SourceCategory = capability, Area = "Rapporter", Title = title, Purpose = "Formål",
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "Vises", CanonicalKey = "k:" + id,
        Story = capability == "specification" ? MainStory : null, SourceIdentity = "tss-" + id,
    };

    private static readonly DiscoveredScenario Spec1 = C("c-1", "A user submits a valid report");
    private static readonly DiscoveredScenario Spec2 = C("c-2", "A user corrects a report");
    private static readonly DiscoveredScenario Ui = C("c-ui", "Report page lists reports", "ui-source");

    private static ScenarioWorkItem W(DiscoveredScenario c, params Q[] queues) => new() { CandidateId = c.CandidateId, Queues = [.. queues], Rank = queues.Contains(Q.Recommended) ? 1 : null };

    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private Mock<ICurrentWorkspaceProjection> _projection = null!;
    private TaskCompletionSource<(ScenarioDiscoveryResult?, string?)>? _discovery;

    private IRenderedComponent<ManualTestingDiscover> Open(string? selectedSpecification, params DiscoveredScenario[] candidates)
    {
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
        _api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [.. candidates] }, (string?)null));
        _api.Setup(x => x.WorkQueuesAsync("library", "d1", It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioWorkQueueView
        {
            DiscoveryId = "d1", DiscoveryCurrent = true, CoverageComplete = true,
            Items = [.. candidates.Select(c => c.CandidateId == "c-2" ? W(c, Q.NeedsClarification) : W(c, Q.Recommended))],
        }, (string?)null));
        _api.Setup(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => (_discovery = new TaskCompletionSource<(ScenarioDiscoveryResult?, string?)>()).Task);
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void The_discovery_action_sits_beside_the_module_and_specification_and_not_in_the_status_bar()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);

        cut.FindAll("[data-testid=mtd-discover]").Should().ContainSingle("one discovery action on the page");
        cut.Find("[data-testid=ssp-actions] [data-testid=mtd-discover]").TextContent.Should().Be("Oppdag på nytt");
        cut.Find(".ssp-row [data-testid=ssp-specification]").Should().NotBeNull("the button shares the row with the specification choice");
        cut.Find("[data-testid=mtd-context]").QuerySelector("button").Should().BeNull("the status bar holds no actions");
        cut.FindAll("[data-testid=ssp-discover]").Should().BeEmpty("the picker's own link to another page is not used here");
    }

    [Fact]
    public void Discovery_runs_once_at_a_time_and_keeps_every_selection()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == "NeedsClarification").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Single().QuerySelector("[data-testid=mtd-select]")!.Change(true);

        cut.Find("[data-testid=mtd-discover]").Click();
        cut.Find("[data-testid=mtd-discover]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtd-discover]").TextContent.Should().Be("Finner scenarioer …");
        cut.Find("[data-testid=mtd-discover]").Click(); // a second click that slips through is ignored
        _api.Verify(x => x.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        _discovery!.SetResult((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library" }, null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-discover]").HasAttribute("disabled").Should().BeFalse());

        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("NeedsClarification");
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1");
        cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be("s1", "discovery never changes the chosen specification");
    }

    [Fact]
    public void A_failed_discovery_is_explained_and_changes_no_selection()
    {
        var cut = Open(MainPath, Spec1, Spec2, Ui);
        cut.FindAll("[data-testid=mtd-scenario]").First().QuerySelector("[data-testid=mtd-select]")!.Change(true);

        cut.Find("[data-testid=mtd-discover]").Click();
        _discovery!.SetResult((null, "Kilden kunne ikke leses."));

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-discover-error]").TextContent.Should().Be("Kilden kunne ikke leses."));
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("Recommended");
    }

    [Fact]
    public void Scenarios_from_another_specification_are_labelled_and_discovery_is_not_run_automatically()
    {
        var cut = Open(DependenciesPath, Spec1, Spec2, Ui);

        var note = cut.Find("[data-testid=mtd-spec-context]");
        note.GetAttribute("data-state").Should().Be("stale");
        note.TextContent.Should().Contain($"funnet i {MainPath}").And.Contain("ikke i valgt «Avhengigheter for SkoleService før go-live»").And.Contain("Oppdag på nytt");
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
        cut.Find("[data-testid=mtd-filter-area]").Should().NotBeNull("the common filter is always visible");
        cut.FindAll("[data-testid=mtd-filters-panel]").Should().BeEmpty("the other filters wait behind «Flere filtre»");

        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.Find("[data-testid=mtd-more-filters]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=mtd-filter-priority]").Change("P1");
        cut.Find("[data-testid=mtd-active-filters]").TextContent.Should().Be("1");
        Rows(cut).Should().Equal(["c-1"], "the recommended queue, narrowed to declared P1");
        Queue(cut, "NeedsClarification");
        Rows(cut).Should().Equal(["c-2"], "the filter stays when the queue changes");

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
}
