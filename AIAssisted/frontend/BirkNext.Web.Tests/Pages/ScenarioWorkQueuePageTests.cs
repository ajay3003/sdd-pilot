using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Q = BirkNext.TestPlanning.ScenarioWorkQueue;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Finn og forstå's work queues on the page (2026-11-24): the default view is «Anbefalt å forberede» in the server's order; queues compose with
/// search, filters and the technical-content preference; counts follow the other filters; the selection survives queue changes; reasons,
/// clarifications, execution needs and saved cases are explained in plain Norwegian; unknown coverage is never shown as none; hand-off to
/// Planlegg og klargjør. The page only reads the server's queue view (one call) — the classification itself is tested on the server.
/// </summary>
public sealed class ScenarioWorkQueuePageTests : BunitContext
{
    private static CoverageUserStory Story(int ordinal, string? priority) => new()
    {
        Key = CoverageUserStory.KeyOf("spec.md", $"us{ordinal}"), StoryId = $"US{ordinal}", Ordinal = ordinal, Title = $"Story {ordinal}", Priority = priority, SpecificationPath = "spec.md",
    };

    private static DiscoveredScenario C(string id, string title, ScenarioCandidateType type = ScenarioCandidateType.BusinessFunctional, string capability = ScenarioOwnership.Specification,
        CoverageUserStory? story = null, string[]? requirements = null) => new()
    {
        CandidateId = id, ProjectKey = "library", CapabilityId = capability, SourceCategory = capability, Area = "Rapporter", Title = title, Purpose = "Formål " + title, Type = type,
        Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "Vises som dokumentert", CanonicalKey = "k:" + id,
        Evidence = [new("specification", "spec.md#" + id, "Akseptansescenario")], Story = story, RequirementIds = [.. requirements ?? []], SourceIdentity = "tss-" + id,
    };

    private static readonly DiscoveredScenario Second = C("c-second", "A user corrects a submitted report", story: Story(2, "P2"));
    private static readonly DiscoveredScenario First = C("c-first", "A user submits a valid report", story: Story(1, "P1"));
    private static readonly DiscoveredScenario Auth = C("c-auth", "POST /reports — refused without valid authorization", ScenarioCandidateType.SecurityAuthorization,
        ScenarioOwnership.ApiContract, requirements: ["FR-009"]);
    private static readonly DiscoveredScenario Unclear = C("c-unclear", "A report for a closed period");
    private static readonly DiscoveredScenario Done = C("c-done", "A user lists earlier reports");
    private static readonly DiscoveredScenario Changed = C("c-changed", "A user deletes a draft report");
    private static readonly DiscoveredScenario Smoke = C("c-smoke", "Report page opens", ScenarioCandidateType.UiSmoke, ScenarioOwnership.UiSource);

    private static ScenarioWorkItem W(DiscoveredScenario c, Q[] queues, int? rank = null, string[]? reasons = null, ScenarioCaseCoverage? coverage = null,
        string[]? clarify = null, string[]? exec = null) => new()
    {
        CandidateId = c.CandidateId, Queues = [.. queues], Rank = rank, Priority = c.Story?.Priority, Coverage = coverage, Reasons = [.. reasons ?? []],
        Clarifications = [.. clarify ?? []], ExecutionNeeds = [.. exec ?? []],
    };

    /// <summary>The server's view: the P1 scenario first, then P2, then the authorization scenario a requirement names; smoke (technical) is recommended too.</summary>
    private static ScenarioWorkQueueView View(bool complete = true) => new()
    {
        DiscoveryId = "d1", DiscoveryCurrent = true, CoverageComplete = complete, PlansRead = 2,
        Items =
        [
            W(Second, [Q.Recommended], 2, ["Mangler testtilfelle", "Forventet resultat dokumentert i spesifikasjonen", "Prioritet P2"]),
            W(First, [Q.Recommended], 1, ["Mangler testtilfelle", "Forventet resultat dokumentert i spesifikasjonen", "Høy prioritet (P1)"], exec: ["Trenger testdata: Elev"]),
            W(Auth, [Q.Recommended], 3, ["Mangler testtilfelle"]),
            W(Smoke, [Q.Recommended], 4, ["Mangler testtilfelle"]),
            W(Unclear, [Q.NeedsClarification], reasons: ["Trenger avklaring før klargjøring"], clarify: ["Forventet resultat mangler i kilden", "1 åpent spørsmål"]),
            W(Done, [Q.Prepared], reasons: ["Har 2 testtilfeller"], coverage: new ScenarioCaseCoverage { Draft = 1, Approved = 1, Archived = 1, Plans = [Guid.NewGuid()] }),
            W(Changed, [Q.SourceChanged, Q.Prepared], reasons: ["Kildegrunnlaget er endret for 1 testtilfelle", "Har 1 testtilfelle"],
                coverage: new ScenarioCaseCoverage { InProgress = 1, SourceChanged = 1, Plans = [Guid.NewGuid()] }),
        ],
    };

    private IRenderedComponent<ManualTestingDiscover> Open(ScenarioWorkQueueView? view)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true }, (string?)null));
        api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioDiscoveryResult
        {
            DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [First, Second, Auth, Unclear, Done, Changed, Smoke],
        }, (string?)null));
        api.Setup(x => x.WorkQueuesAsync("library", "d1", It.IsAny<CancellationToken>())).ReturnsAsync((view, view is null ? "Testplanforslag svarte ikke" : null));
        Services.AddSingleton(api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private static string[] Rows(IRenderedComponent<ManualTestingDiscover> cut) => [.. cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")!)];
    private static string Count(IRenderedComponent<ManualTestingDiscover> cut, string queue) =>
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == queue).QuerySelector(".msb-queue-n")!.TextContent;
    private static void Queue(IRenderedComponent<ManualTestingDiscover> cut, string queue) =>
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("data-queue") == queue).Click();

    [Fact]
    public void The_default_view_is_the_recommended_queue_in_the_servers_order_with_technical_content_hidden()
    {
        var cut = Open(View());

        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("Recommended");
        Rows(cut).Should().Equal(["c-first", "c-second", "c-auth"], "rank order; the technical smoke check is hidden by default; a requirement-named authorization scenario is not");
        cut.Find("[data-testid=mtd-queue-hint]").TextContent.Should().Contain("Klar for klargjøring betyr ikke klar for kjøring");
        Count(cut, "Recommended").Should().Be("3");
        Count(cut, "NeedsClarification").Should().Be("1");
        Count(cut, "Prepared").Should().Be("2", "a scenario in several queues is counted in each");
        Count(cut, "SourceChanged").Should().Be("1");
        Count(cut, "All").Should().Be("6");
    }

    [Fact]
    public void Queues_compose_with_the_technical_preference_and_search()
    {
        var cut = Open(View());

        cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").Click();
        Rows(cut).Should().Equal(["c-first", "c-second", "c-auth", "c-smoke"]);
        Count(cut, "Recommended").Should().Be("4");

        cut.Find("[data-testid=mtd-search]").Input("corrects");
        Rows(cut).Should().Equal(["c-second"]);
        Count(cut, "Recommended").Should().Be("1");
        Count(cut, "Prepared").Should().Be("0");
        cut.Find("[data-testid=mtd-visible]").TextContent.Should().Contain("Viser 1 av 7");

        Queue(cut, "All");
        Rows(cut).Should().Equal(["c-second"], "the search still applies in every queue");
    }

    [Fact]
    public void Every_queue_is_a_view_of_the_same_scenarios()
    {
        var cut = Open(View());

        Queue(cut, "NeedsClarification");
        Rows(cut).Should().Equal(["c-unclear"]);
        Queue(cut, "Prepared");
        Rows(cut).Should().BeEquivalentTo(["c-done", "c-changed"]);
        Queue(cut, "SourceChanged");
        Rows(cut).Should().Equal(["c-changed"]);
        Queue(cut, "All");
        Rows(cut).Should().BeEquivalentTo(["c-first", "c-second", "c-auth", "c-unclear", "c-done", "c-changed"], "nothing is dropped; technical content stays one toggle away");
    }

    [Fact]
    public void The_selection_survives_queue_and_filter_changes_and_is_handed_to_plan_and_prepare()
    {
        var cut = Open(View());
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-first").QuerySelector("[data-testid=mtd-select]")!.Change(true);

        Queue(cut, "NeedsClarification");
        cut.Find("[data-testid=mtd-search]").Input("closed");
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1", "a selected scenario never silently leaves the selection");

        cut.Find("[data-testid=mtd-prepare]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "sessionStorage.setItem" && i.Arguments[0]!.ToString() == TabDraftStore.SelectionKey("library")
            && i.Arguments[1]!.ToString()!.Contains("c-first"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("manual-testing/plan");
    }

    [Fact]
    public void A_recommendation_explains_itself_and_keeps_execution_needs_apart()
    {
        var cut = Open(View());
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-first").Click();

        var why = cut.Find("[data-testid=mtd-detail-why]");
        why.QuerySelector("summary")!.TextContent.Should().Be("Hvorfor anbefalt?");
        why.TextContent.Should().Contain("Mangler testtilfelle").And.Contain("Forventet resultat dokumentert i spesifikasjonen").And.Contain("Høy prioritet (P1)");
        cut.Find("[data-testid=mtd-detail-exec]").TextContent.Should().Contain("Før kjøring trengs").And.Contain("Trenger testdata: Elev");
        cut.Find("[data-testid=mtd-detail-coverage]").TextContent.Should().Be("Ingen testtilfeller ennå");
        cut.Markup.Should().NotContain("score", "no numerical readiness score");
    }

    [Fact]
    public void Clarifications_and_saved_cases_are_spelled_out()
    {
        var cut = Open(View());
        Queue(cut, "NeedsClarification");
        cut.FindAll("[data-testid=mtd-scenario]").Single().Click();
        cut.Find("[data-testid=mtd-detail-clarify]").TextContent.Should().Contain("Må avklares før klargjøring").And.Contain("Forventet resultat mangler i kilden").And.Contain("1 åpent spørsmål");

        Queue(cut, "Prepared");
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-done").Click();
        cut.Find("[data-testid=mtd-detail-coverage]").TextContent.Should().Be("2 testtilfeller (1 utkast, 1 godkjent) i 1 aktiv plan · 1 i arkiverte planer");
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-changed").Click();
        cut.Find("[data-testid=mtd-detail-coverage]").TextContent.Should().Contain("1 laget fra en kilde som er endret");
    }

    [Fact]
    public void Unknown_coverage_is_never_shown_as_no_test_case()
    {
        var unknown = View(complete: false) with { Items = [W(First, []), W(Second, [])] };
        var cut = Open(unknown);

        cut.Find("[data-testid=mtd-count-prepared]").TextContent.Should().Be("ukjent");
        cut.Find("[data-testid=mtd-empty]").TextContent.Should().Contain("en lagret plan kunne ikke leses");
        Queue(cut, "All");
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-first").Click();
        cut.Find("[data-testid=mtd-detail-coverage]").TextContent.Should().Be("Ukjent — en lagret plan kunne ikke leses");
    }

    [Fact]
    public void An_empty_recommended_queue_points_to_what_needs_clarification()
    {
        var view = View() with { Items = [.. View().Items.Select(i => i.Queues.Contains(Q.Recommended) ? i with { Queues = [Q.NeedsClarification], Rank = null } : i)] };
        var cut = Open(view);

        cut.Find("[data-testid=mtd-empty]").TextContent.Should().Contain("Ingen scenarioer er klare for klargjøring");
        cut.Find("[data-testid=mtd-empty-clarify]").Click();
        cut.FindAll("[data-testid=mtd-queue]").Single(e => e.GetAttribute("aria-pressed") == "true").GetAttribute("data-queue").Should().Be("NeedsClarification");
    }

    [Fact]
    public void Without_the_queue_view_every_scenario_is_listed_and_coverage_is_unknown()
    {
        var cut = Open(null);

        cut.FindAll("[data-testid=mtd-queue]").Should().BeEmpty();
        cut.Find("[data-testid=mtd-queues-unavailable]").TextContent.Should().Contain("Testplanforslag svarte ikke");
        Rows(cut).Should().HaveCount(6, "all manual-relevant scenarios (technical content stays hidden)");
        cut.Find("[data-testid=mtd-count-prepared]").TextContent.Should().Be("ukjent");
    }

    [Fact]
    public void Ranking_and_counts_stay_fast_for_two_thousand_scenarios()
    {
        var candidates = Enumerable.Range(0, 2000).Select(i => C($"s{i:0000}", $"Scenario {i}", story: Story(i % 40 + 1, $"P{i % 3 + 1}"))).ToList();
        var work = candidates.Select((c, i) => W(c, i % 3 == 0 ? [Q.Recommended] : i % 3 == 1 ? [Q.Prepared] : [Q.NeedsClarification], i % 3 == 0 ? i / 3 + 1 : null))
            .ToDictionary(w => w.CandidateId, StringComparer.Ordinal);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var items = S.Items(candidates, work);
        var shown = S.Apply(items, S.DefaultView).Shown;
        var (counts, all) = S.QueueCounts(items, S.DefaultView);
        watch.Stop();

        shown.Should().HaveCount(667);
        shown.Select(i => i.Work!.Rank).Should().BeInAscendingOrder();
        counts[Q.Recommended].Should().Be(667);
        counts[Q.Prepared].Should().Be(667);
        counts[Q.NeedsClarification].Should().Be(666);
        all.Should().Be(2000);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "hang guard only; the measured cost is reported from the real M2LB data");
    }
}
