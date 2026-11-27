using System.Text.Json;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using W = BirkNext.Web.Services.ScenarioWorkspacePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Finn og forstå as a User Story–aware, scenario-first workspace (2026-11-24). The UX rules: choosing a story shows its scenarios and business
/// context; choosing a scenario shows the source's Given/When/Then and its requirements by relation; a requirement opens with its text, why it
/// is linked and the other scenarios naming it; readiness is six questions; another case keeps the scenario; a specification change never
/// silently opens another scenario; returning restores the story, scenario and tab. Stories never gate: «Alle scenarioer», search and the
/// no-story group reach everything.
/// </summary>
public sealed class DiscoverWorkspaceTests : BunitContext
{
    /// <summary>Open with «Ingen gruppering» saved (the flat list); grouping tests set false to get the default grouping.</summary>
    private bool FlatList { get; set; } = true;

    private static CoverageUserStory Story(string id, int ordinal, string title, int scenarios) => new()
    {
        Key = CoverageUserStory.KeyOf("SkoleService/specs/001-skoleservice/spec.md", $"us{ordinal}"), StoryId = id, Ordinal = ordinal, Title = title, Priority = "P1",
        SpecificationPath = "SkoleService/specs/001-skoleservice/spec.md", Scenarios = scenarios, Description = "Reporters send attendance reports.",
        WhyThisPriority = "Without valid reports nothing else works.", IndependentTest = "Submit a report with valid hours. Verify the report is stored with BirkID.",
        NamedRequirements = ["FR-004"],
    };

    private static readonly CoverageUserStory Us1 = Story("US1", 1, "Accept a valid report without keeping the child's national ID", 11);
    private static readonly CoverageUserStory Us2 = Story("US2", 2, "Protect graded children", 2);

    private static DiscoveredScenario C(string id, CoverageUserStory? story, int? number, string title, string[] requirements, string capability = ScenarioOwnership.Specification) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = capability, SourceCategory = capability, Area = "Rapporter", Title = title, Purpose = "Formål " + title,
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "it is accepted",
        Evidence = [new("specification", $"{story?.SpecificationPath ?? "spec.md"}#{id}", "Akseptansescenario")], Story = story, NumberInStory = number,
        RequirementIds = [.. requirements], SourceIdentity = "tss-" + id, Steps = [new("Send rapporten", "it is accepted")],
        Template = [new("given", "Gitt", $"a report for a child Person knows ({title})"), new("when", "Når", "it arrives"), new("then", "Så", "it is accepted")],
    };

    private static readonly DiscoveredScenario Valid = C("US1-AS1", Us1, 1, "Valid report accepted", ["FR-001", "FR-002"]);
    private static readonly DiscoveredScenario Above = C("US1-AS2", Us1, 2, "Attendance above plan", ["FR-015a"]);
    private static readonly DiscoveredScenario Graded = C("US2-AS1", Us2, 1, "Graded child hidden from lists", ["FR-001"]);
    private static readonly DiscoveredScenario Journey = C("journey-1", null, null, "Report travels to the registry", [], ScenarioOwnership.IntegrationJourneys) with { Type = ScenarioCandidateType.Integration };

    private IRenderedComponent<ManualTestingDiscover> Open(W.WorkContext? remembered = null, string? rememberedStory = null, params DiscoveredScenario[] candidates)
    {
        candidates = candidates.Length == 0 ? [Valid, Above, Graded, Journey] : candidates;
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "m2lb", "M2LB")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "m2lb", GuideKey = "project:m2lb", CanWrite = true }, (string?)null));
        api.Setup(x => x.LastDiscoveryAsync("m2lb", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioDiscoveryResult
        {
            DiscoveryId = "d1", ProjectKey = "m2lb", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [.. candidates], TestsReadForScenarios = true,
        }, (string?)null));
        api.Setup(x => x.WorkQueuesAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioWorkQueueView
        {
            DiscoveryId = "d1", DiscoveryCurrent = true, CoverageComplete = true,
            Items = [new ScenarioWorkItem { CandidateId = "US1-AS1", Queues = [ScenarioWorkQueue.Prepared], Coverage = new ScenarioCaseCoverage { Draft = 1, Approved = 1 } }],
        }, (string?)null));
        Services.AddSingleton(api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // These tests check the list, drawer and filters, not grouping: the tester's saved choice «Ingen gruppering» gives the flat list (groups start collapsed, 2026-11-26).
        if (FlatList) JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("None>None");
        JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.DiscoverFilterKey("m2lb"))
            .SetResult(JsonSerializer.Serialize(new ScenarioBrowserPresentation.Filter(All: true, Story: rememberedStory)));
        if (remembered is not null)
            JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.DiscoverContextKey("m2lb")).SetResult(JsonSerializer.Serialize(remembered));
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private static IReadOnlyList<string?> Ids(IRenderedComponent<ManualTestingDiscover> cut) => [.. cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id"))];

    [Fact]
    public void User_stories_group_the_scenarios_without_gating_them()
    {
        var cut = Open();

        var stories = cut.FindAll("[data-testid=mtd-story]");
        stories.Select(s => s.GetAttribute("data-story")).Should().Equal("", Us1.Key, Us2.Key, W.NoStory);
        stories[0].TextContent.Should().Contain("Alle scenarioer");
        stories[1].TextContent.Should().Contain("US1").And.Contain("P1").And.Contain("11 scenarioer").And.Contain("1 med testtilfeller");
        stories[3].TextContent.Should().Contain("Uten brukerhistorie");
        Ids(cut).Should().HaveCount(4, "all scenarios are listed until a story is chosen");

        stories[1].Click();
        Ids(cut).Should().Equal("US1-AS1", "US1-AS2");
        cut.Find("[data-testid=mtd-story-head]").TextContent.Should().Contain("Accept a valid report").And.Contain("11 akseptansescenarioer i spesifikasjonen");
        cut.FindAll("[data-testid=mtd-scenario-label]").Select(e => e.TextContent).Should().Equal("AC 1", "AC 2");

        cut.Find($"[data-testid=mtd-story][data-story='{W.NoStory}']").Click();
        Ids(cut).Should().Equal("journey-1");
        cut.Find("[data-testid=mtd-story][data-story='']").Click();
        Ids(cut).Should().HaveCount(4);
    }

    [Fact]
    public void Choosing_a_story_shows_all_of_its_scenarios_even_when_a_work_queue_was_remembered()
    {
        // Real M2LB walk (2026-11-24): with «Anbefalt å forberede» remembered, US1 listed 9 of its 11 scenarios — the two with cases were hidden.
        var cut = Open();
        cut.Find("[data-testid=mtd-queue][data-queue='Prepared']").Click();
        Ids(cut).Should().Equal("US1-AS1");

        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();

        Ids(cut).Should().Equal("US1-AS1", "US1-AS2");
        cut.Find("[data-testid=mtd-queue][data-queue='All']").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void The_story_details_show_description_priority_rationale_and_independent_test_objectives()
    {
        var cut = Open();
        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();
        cut.FindAll("[data-testid=mtd-story-details]").Should().BeEmpty("details are on demand");

        cut.Find("[data-testid=mtd-story-toggle]").Click();

        cut.Find("[data-testid=mtd-story-description]").TextContent.Should().Contain("attendance reports");
        cut.Find("[data-testid=mtd-story-why]").TextContent.Should().Contain("nothing else works");
        cut.FindAll("[data-testid=mtd-story-objective]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=mtd-story-req]").Select(e => e.TextContent).Should().Contain("FR-004");
    }

    [Fact]
    public void Search_reaches_other_stories_and_says_so()
    {
        var cut = Open();
        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();
        cut.Find("[data-testid=mtd-search]").Input("graded");

        Ids(cut).Should().BeEmpty();
        cut.Find("[data-testid=mtd-search-elsewhere]").TextContent.Should().Contain("1 treff i andre brukerhistorier");
        cut.Find("[data-testid=mtd-search-elsewhere] button").Click();
        Ids(cut).Should().Equal("US2-AS1");
    }

    [Fact]
    public void A_scenario_shows_its_source_requirements_readiness_and_cases_with_a_path_back_to_its_story()
    {
        var cut = Open();
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS2']").Click();

        cut.Find("[data-testid=mtd-breadcrumb]").TextContent.Should().Contain("US1").And.Contain("AC 2");
        var source = cut.Find("[data-testid=mtd-detail-source]").TextContent;
        source.Should().Contain("Gitt").And.Contain("a report for a child Person knows (Attendance above plan)").And.Contain("Når").And.Contain("it arrives").And.Contain("Så");
        cut.FindAll("[data-testid=mtd-req-link]").Select(e => (e.GetAttribute("data-relation"), e.QuerySelector("[data-testid=mtd-req-chip]")!.TextContent))
            .Should().Equal([("Explicit", "FR-015a")]);

        var items = cut.FindAll("[data-testid=mtd-readiness-item]");
        items.Should().HaveCount(6);
        string? State(string key) => items.Single(i => i.GetAttribute("data-key") == key).GetAttribute("data-state");
        State("trigger").Should().Be("Yes", "the scenario's own step says how it is triggered");
        State("source").Should().Be("Unknown", "stated in the source is not confirmed until a case is approved");
        State("env").Should().Be("Unknown", "nobody recorded the environment");
        State("review").Should().Be("No");
        cut.Find("[data-testid=mtd-readiness-count]").TextContent.Should().Be("1 / 6");
        cut.Find("[data-testid=mtd-detail-prepare]").TextContent.Should().Be("Forbered testtilfelle");
    }

    [Fact]
    public void A_requirement_opens_with_why_it_is_linked_and_the_other_scenarios_that_name_it_across_stories()
    {
        var cut = Open();
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();

        cut.FindAll("[data-testid=mtd-req-chip]").First(e => e.TextContent == "FR-001").Click();

        var panel = cut.Find("[data-testid=mtd-req-panel]");
        panel.TextContent.Should().Contain("FR-001").And.Contain("Nevnt i kilden").And.Contain("Kravet er nevnt i scenarioets egen kilde.");
        cut.FindAll("[data-testid=mtd-req-other]").Select(e => e.TextContent).Should().Equal("AC 1 (US2)");

        cut.Find("[data-testid=mtd-req-other]").Click();
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Graded child hidden from lists");
        cut.Find("[data-testid=mtd-breadcrumb]").TextContent.Should().Contain("US2");
    }

    [Fact]
    public void A_scenario_with_cases_offers_another_case_and_keeps_its_relations()
    {
        var cut = Open();
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();

        cut.Find("[data-testid=mtd-detail-coverage]").TextContent.Should().Contain("2 testtilfeller").And.Contain("1 godkjent");
        cut.Find("[data-testid=mtd-detail-prepare]").TextContent.Should().Be("Lag enda et testtilfelle");
        cut.Find("[data-testid=mtd-detail-cases]").TextContent.Should().Contain("beholder koblingen til scenarioet og US1");

        cut.Find("[data-testid=mtd-detail-prepare]").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"{ManualTestingPresentation.PlanRoute}?scenario=US1-AS1", "the chosen scenario travels explicitly (cross-page context hardening)");
    }

    [Fact]
    public void The_tabs_show_cases_and_requirement_coverage_of_the_chosen_story()
    {
        var cut = Open();
        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();

        cut.Find("[data-testid=mtd-tab][data-tab='coverage']").Click();
        cut.FindAll("[data-testid=mtd-coverage-row]").Select(r => r.GetAttribute("data-requirement")).Should().Equal("FR-001", "FR-002", "FR-004", "FR-015a");
        cut.Find("[data-testid=mtd-coverage-row][data-requirement='FR-004']").TextContent.Should().Contain("nevnt i brukerhistorien");
        cut.Find("[data-testid=mtd-coverage-row][data-requirement='FR-001']").TextContent.Should().Contain("ukjent", "without the trace, case counts are unknown — never zero");

        cut.Find("[data-testid=mtd-tab][data-tab='cases']").Click();
        cut.Find("[data-testid=mtd-cases-none]").TextContent.Should().Contain("2 testtilfeller finnes", "counted cases are never hidden behind an empty list");
    }

    [Fact]
    public void Returning_restores_the_story_the_scenario_and_the_tab()
    {
        var cut = Open(new W.WorkContext(Us1.Key, "US1-AS2", W.Tabs.Coverage, "Attendance above plan"), rememberedStory: Us1.Key);

        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=mtd-tab][data-tab='coverage']").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Attendance above plan");
    }

    [Fact]
    public void A_scenario_that_disappears_is_reported_never_silently_replaced()
    {
        var cut = Open(new W.WorkContext(null, "US9-AS1", W.Tabs.Scenarios, "Old scenario from another specification"));

        cut.FindAll("[data-testid=mtd-detail-title]").Should().BeEmpty("no other scenario is opened in its place");
        cut.Find("[data-testid=mtd-detail-empty]").TextContent.Should().Contain("Old scenario from another specification").And.Contain("finnes ikke");

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Valid report accepted");
    }

    [Fact]
    public void A_filter_that_hides_the_open_scenario_keeps_it_open_and_says_so()
    {
        var cut = Open();
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS2']").Click();

        cut.Find($"[data-testid=mtd-story][data-story='{Us2.Key}']").Click();

        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Attendance above plan");
        cut.Find("[data-testid=mtd-detail-outside]").TextContent.Should().Contain("vises ikke med gjeldende brukerhistorie");
    }

    [Fact]
    public void The_story_card_summarises_developer_tests_without_claiming_coverage_and_filters_to_ACs_without_a_test()
    {
        var test = new CandidateTestAssociation("t-9", "Scenario_1_valid_report_is_stored", "SkoleService/tests/IntakeTests.cs:42", "The method name names scenario 1")
        { Origin = TestScenarioLinkOrigin.Scenario, Layer = "Integration", MethodName = "Scenario_1_valid_report_is_stored", SourceFingerprint = "fp" };
        var cut = Open(null, null, Valid with { ExistingTests = [test] }, Above);
        cut.Find("[data-testid=mtd-queue][data-queue='All']").Click();

        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();
        var line = cut.Find("[data-testid=mtd-us-devtests]");
        line.TextContent.Should().Contain("1 av 2 AC har koblet test").And.Contain("1 uten kjent test").And.NotContain("dekket");
        line.GetAttribute("title").Should().Contain("ikke at akseptansescenarioet er tilstrekkelig testet");

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();
        cut.Find("[data-testid=mtd-tech-meaning]").TextContent.Should().Contain("ingen har vurdert hvor mye den kontrollerer");
        cut.Find("[data-testid=mtd-tech-caveat]").TextContent.Should().Contain("ikke at akseptansescenarioet er tilstrekkelig testet");
        cut.FindAll("[data-testid=mtd-checkpoint]").Should().OnlyContain(li => li.GetAttribute("data-state") == "NotConfirmed");

        cut.Find("[data-testid=mtd-us-devtests-filter]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("US1-AS2");
    }

    [Fact]
    public void Technical_coverage_shows_what_a_source_test_checks_in_plain_words_with_details_on_demand_and_never_a_result()
    {
        var test = new CandidateTestAssociation("t-9", "Scenario_1_valid_report_is_stored", "SkoleService/tests/M2LB.SkoleService.Integration/IntakeTests.cs:42",
            "The method name names scenario 1; the class summary names US1 scenario 1")
        { Origin = TestScenarioLinkOrigin.Scenario, Layer = "Integration", Framework = "xUnit", ClassName = "M2LB.SkoleService.Integration.IntakeTests", MethodName = "Scenario_1_valid_report_is_stored", SourceFingerprint = "fp" };
        var cut = Open(null, null, Valid with { ExistingTests = [test] }, Above);
        cut.Find("[data-testid=mtd-queue][data-queue='All']").Click();

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1'] [data-testid=mtd-scenario-tech]").TextContent.Should().Contain("automatisk test");
        cut.FindAll("[data-testid=mtd-scenario][data-id='US1-AS2'] [data-testid=mtd-scenario-tech]").Should().BeEmpty();

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();
        cut.Find("[data-testid=mtd-tech-state]").TextContent.Should().Be("Automatisk test funnet");
        cut.Find("[data-testid=mtd-tech-result]").TextContent.Should().Be("Testresultat ikke tilgjengelig");
        cut.Find("[data-testid=mtd-tech-link]").TextContent.Should().Be("Testen nevner scenarioet");
        cut.FindAll("[data-testid=mtd-tech-extent]").Should().BeEmpty("no extent before a reviewer assesses one");
        cut.Find("[data-testid=mtd-tech-test]").TextContent.Should().Contain("Integrasjonstest").And.Contain("Scenario 1 valid report is stored");
        cut.Find("[data-testid=mtd-tech-class]").TextContent.Should().Be("M2LB.SkoleService.Integration.IntakeTests");
        cut.Find("[data-testid=mtd-tech-file]").TextContent.Should().EndWith("IntakeTests.cs:42");
        cut.Find("[data-testid=mtd-tech-details]").HasAttribute("open").Should().BeFalse("technical details are on demand");
        cut.FindAll("[data-testid=mtd-tech-confirm]").Should().BeEmpty("no review actions without write access to the specification's decisions");
        cut.Markup.Should().NotContain("Bestått");

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS2']").Click();
        cut.Find("[data-testid=mtd-tech-state]").TextContent.Should().Be("Ingen kjent automatisk test");
        cut.Find("[data-testid=mtd-tech-none]").TextContent.Should().Contain("betyr ikke at det er risikofritt");

        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.Find("[data-testid=mtd-filter-tech]").Change("linked");
        Ids(cut).Should().Equal("US1-AS1");
        cut.Find("[data-testid=mtd-filter-tech]").Change("none");
        Ids(cut).Should().Equal("US1-AS2");
    }

    [Fact]
    public void Related_scenarios_are_named_with_why_and_can_be_opened_never_merged()
    {
        var related = Valid with { Relations = [new ScenarioRelationNote(Enum.GetValues<ScenarioRelation>()[0], "US1-AS2", "Samme situasjon, annet utfall")], PossibleDuplicates = ["US2-AS1"] };
        var cut = Open(null, null, related, Above, Graded);
        cut.Find("[data-testid=mtd-queue][data-queue='All']").Click();
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();

        var items = cut.FindAll("[data-testid=mtd-related]");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("Samme situasjon, annet utfall");
        items[1].TextContent.Should().Contain("Mulig overlapp").And.Contain("kan dele forberedelse");
        cut.FindAll("[data-testid=mtd-related-open]")[0].Click();
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().StartWith("AC 2", "opening a related scenario shows it; both keep their own identity");
        Ids(cut).Should().Contain("US1-AS1").And.Contain("US1-AS2");
    }
    // ── Functional areas vs user stories (2026-11-25) ────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_chosen_user_story_is_marked_current_and_the_others_stay_neutral()
    {
        var cut = Open();
        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();
        var stories = cut.FindAll("[data-testid=mtd-story]");
        stories.Where(s => s.ClassList.Contains("is-current")).Select(s => s.GetAttribute("data-story")).Should().Equal([Us1.Key], "one selected story");
        stories.Single(s => s.GetAttribute("data-story") == Us1.Key).GetAttribute("aria-pressed").Should().Be("true");
        stories.Where(s => s.GetAttribute("data-story") != Us1.Key).Should().OnlyContain(s => s.GetAttribute("aria-pressed") == "false");
        stories.Should().OnlyContain(s => s.TagName == "BUTTON", "keyboard: every story is a button");

        cut.Find($"[data-testid=mtd-story][data-story='{Us2.Key}']").Click();
        cut.FindAll("[data-testid=mtd-story].is-current").Select(s => s.GetAttribute("data-story")).Should().Equal([Us2.Key], "the selection moves; it never spreads");
    }

    [Fact]
    public void Functional_areas_share_one_neutral_surface_and_only_the_selected_story_is_green()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web", "wwwroot", "css", "manual-testing.css"));
        css.Should().Contain(".mtw-group > .mtw-group-head, .mtw-group > .mtw-group-head[aria-expanded=true] { background: var(--msb-subtle);",
            "every area header, open or closed, has the same neutral token");
        css.Should().NotMatchRegex(@"\.mtw-group[^{]*:has\(", "an area is never coloured by the story it contains");
        css.Should().NotMatchRegex(@"\.mtw-group-head[^{]*\{[^}]*success", "areas are never green");
        css.Should().MatchRegex(@"\.mtw-story\.is-current:not\(\.is-all\) \{ background: var\(--clr-success-bg-light[^}]*border-color: var\(--msb-accent\)[^}]*inset 3px 0 0 var\(--msb-accent\)",
            "the selected story: the middle column's soft green with the blue selection bar");
        css.Should().Contain(".mtw-story-list .mtw-story:not(.is-current):not(.is-all) { background: var(--msb-surface);", "unselected stories are neutral");
        css.Should().Contain(".mtw-story-card.is-story, .mtp-us.is-story {\n    background: var(--clr-success-bg-light", "the same green token as the middle column's story card");
    }

    // ── Two columns + on-demand detail drawer (2026-11-25) ──

    [Fact]
    public void The_detail_drawer_is_closed_by_default_opens_on_Vis_detaljer_follows_the_selection_and_closes_without_losing_it()
    {
        var cut = Open();
        var drawer = cut.Find("[data-testid=mtd-detail]");
        drawer.GetAttribute("data-open").Should().Be("false", "closed on first load");
        drawer.HasAttribute("hidden").Should().BeTrue("no reserved third column");

        // V12 (2026-11-27, MT-1): the whole row opens the scenario at every width (before, a click only highlighted it).
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true", "a row click opens its details"));
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Valid report accepted");
        cut.Find("[data-testid=mtd-drawer-close]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "msb-row-US1-AS1",
            "closing returns focus to the scenario's row"));

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS2'] [data-testid=mtd-show-details]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true"));
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Attendance above plan", "the drawer shows the scenario it was opened for");

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").Click();
        cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true", "while open it follows the selection");
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Valid report accepted");

        cut.Find("[data-testid=mtd-detail]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("false");
        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS1']").ClassList.Should().Contain("is-active", "closing keeps the selected scenario");

        cut.Find("[data-testid=mtd-scenario][data-id='US1-AS2']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true", "the row opens it again"));
        cut.Find("[data-testid=mtd-drawer-close]").Click();
        cut.FindAll("[data-testid=mtd-show-details]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true"));
        cut.Find("[data-testid=mtd-drawer-close]").Click();
        cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("false");
    }

    [Fact]
    public void Story_objectives_and_readiness_use_words_never_a_bare_question_mark()
    {
        var cut = Open();
        cut.Find($"[data-testid=mtd-story][data-story='{Us1.Key}']").Click();
        cut.Find("[data-testid=mtd-story-toggle]").Click();

        cut.FindAll("[data-testid=mtd-objective-state]").Select(e => e.TextContent).Should().OnlyContain(t => t == "Nevnt i et scenario" || t == "Ikke nevnt i scenarioene", "a word match is a mention, never «dekket» (2026-11-26)");
        cut.FindAll("[data-testid=mtd-story-objective]").Should().OnlyContain(li => !li.TextContent.TrimStart().StartsWith("?"));
        W.Mark(Tri.Unknown).Should().NotBe("?");
        W.StateText(Tri.Unknown).Should().Be("Ikke vurdert");
    }
}
