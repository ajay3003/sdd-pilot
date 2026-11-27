using System.Text.Json;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Finn og forstå (2026-11-26): every user story card shows its task-derived implementation status (from the server's engine) and the type
/// distribution of its acceptance scenarios; the evidence opens on demand; scenarios carry one short type label, can be grouped and filtered by
/// type, and a mixed scenario appears once. Two columns and the drawer are unchanged; nothing here writes a scenario, case or plan.
/// </summary>
public sealed class DiscoverStoryStatusAndTypeTests : BunitContext
{
    /// <summary>Open with «Ingen gruppering» saved (the flat list); grouping tests set false to get the default grouping.</summary>
    private bool FlatList { get; set; } = true;
    /// <summary>The explorer's modules (null = one module, no module chooser); S03 gives two so a module change can be made.</summary>
    private IReadOnlyList<ArtifactModuleRoleState>? TestModules { get; set; }
    /// <summary>More specifications of the module (S04 gives one so a specification change can be made).</summary>
    private List<ExplorerArtifact> ExtraSpecs { get; } = [];

    private const string Spec = "Frontend/specs/002-child-search-profile/spec.md";
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();

    private static CoverageUserStory Story(int n, string title) => new()
    {
        Key = CoverageUserStory.KeyOf(Spec, $"US{n}"), StoryId = $"US{n}", Ordinal = n, Title = title, Priority = "P1", SpecificationPath = Spec, Scenarios = 2,
    };

    private static readonly CoverageUserStory Us1 = Story(1, "Search for a Child");
    private static readonly CoverageUserStory Us2 = Story(2, "View Child Profile Overview");

    private static DiscoveredScenario Ac(string id, CoverageUserStory story, int number, string when, string then) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = "specification", SourceCategory = "Specification", CanonicalKey = $"scenario:Frontend|{id}", Area = "Søk",
        Title = when, Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = then, Story = story, NumberInStory = number,
        SourceIdentity = "tss-" + id, Fingerprint = "fp-" + id, Steps = [new(when, then)],
        Template = [new("given", "Gitt", "the caseworker is on the page"), new("when", "Når", when), new("then", "Så", then)],
        Evidence = [new("specification", $"{Spec}#{id}", "Akseptansescenario")],
    };

    // Real M2LB acceptance scenarios (Frontend 002 US1 #1 and #4; US2 #5; Frontend 003 US5 #2).
    private static readonly DiscoveredScenario Search = Ac("us1-1", Us1, 1, "they type a child's name in the search field", "a list of matching children appears with name, age, birth date, and current case status");
    private static readonly DiscoveredScenario NoMatch = Ac("us1-4", Us1, 2, "the search completes", "the message \"Ingen barn funnet\" is displayed");
    private static readonly DiscoveredScenario ErrorPanel = Ac("us2-5", Us2, 1, "the profile loads", "that panel displays \"OBS! Det skjedde en teknisk feil\" in a visually distinct error style, without affecting the other panels");
    private static readonly DiscoveredScenario Focus = Ac("us2-6", Us2, 2, "the user presses Enter", "focus moves to the page's primary content region");

    private static UserStoryImplementationStatus Status(CoverageUserStory s, UserStoryTaskStatus status, int completed, int eligible, int? pct) => new()
    {
        StoryKey = s.Key, StoryId = s.StoryId!, Title = s.Title, SpecificationPath = Spec, Module = "Frontend", TasksPath = "Frontend/specs/002-child-search-profile/tasks.md",
        Status = status, Evidence = eligible == 0 ? UserStoryEvidenceQuality.Missing : UserStoryEvidenceQuality.Complete, EvidenceText = eligible == 0 ? "tasks.md mangler for spesifikasjonen." : "Oppgaver funnet og koblet til US.",
        Completed = completed, Eligible = eligible, Open = eligible - completed, ProgressPercent = pct,
        Explanation = eligible == 0 ? "Kan ikke beregne sikker status fordi oppgavegrunnlaget mangler." : "Statusen er beregnet fra oppgavene som er markert i tasks.md.",
        Tasks = eligible == 0 ? [] :
        [
            new("T050", "T050 [US1] Extend `PersonSearchResult` model … (depends on T003)", UserStoryTaskState.Open, " ", UserStoryTaskLink.ExplicitTag, "Phase 3: User Story 1", "tasks.md", 79),
            .. Enumerable.Range(22, completed).Select(n => new UserStoryTaskRef($"T0{n}", $"T0{n} [US1] done", UserStoryTaskState.Complete, "X", UserStoryTaskLink.ExplicitTag, "Phase 3: User Story 1", "tasks.md", 60 + n)),
        ],
        Dependencies = eligible == 0 ? [] : [new("T050", "T003", UserStoryDependencyKind.Blocking, false, "T050: «… (depends on T003)»", null), new("T050", "T030", UserStoryDependencyKind.Related, true, "T050 nevner T030.", "US2")],
        SharedOpen = eligible == 0 ? [] : [new("T047", "T047 [P] WCAG 2.1 AA audit …", UserStoryTaskState.Open, " ", UserStoryTaskLink.StoryPhase, "Phase 6: Polish", "tasks.md", 158)],
        EvidenceFingerprint = "fp",
    };

    private IRenderedComponent<ManualTestingDiscover> Open(UserStoryStatusView? status, string? error = null, ScenarioBrowserPresentation.Filter? filter = null)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "m2lb", "M2LB")).Object);
        var spec = new ExplorerArtifact("s1", WorkspaceArtifactType.Specification, "Child Search", "spec.md", Spec, ExplorerArtifactSource.Workspace, null, null, 1, "Unknown",
            ExplorerArtifactCurrentness.Current, null, Module: "Frontend");
        var explorer = new Mock<IArtifactExplorerContext>();
        explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(new ArtifactExplorerState(WorkspaceArtifactType.Specification,
            ExplorerArtifactStatus.Loaded, "m2lb", "M2LB", [spec, .. ExtraSpecs], spec, ExplorerSelectionReason.Explicit, "# spec", Module: "Frontend", Modules: TestModules));
        Services.AddSingleton(explorer.Object);
        _api.Setup(x => x.ProjectAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "m2lb", GuideKey = "project:m2lb", CanWrite = true }, (string?)null));
        _api.Setup(x => x.LastDiscoveryAsync("m2lb", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioDiscoveryResult
        {
            DiscoveryId = "d1", ProjectKey = "m2lb", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [Search, NoMatch, ErrorPanel, Focus],
        }, (string?)null));
        _api.Setup(x => x.WorkQueuesAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioWorkQueueView
        {
            DiscoveryId = "d1", DiscoveryCurrent = true, CoverageComplete = true,
            Items = [.. new[] { Search, NoMatch, ErrorPanel, Focus }.Select(c => new ScenarioWorkItem { CandidateId = c.CandidateId, Queues = [ScenarioWorkQueue.Recommended] })],
        }, (string?)null));
        _api.Setup(x => x.StoryGroupingAsync("m2lb", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Spec }, (string?)null));
        _api.Setup(x => x.StoryStatusAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((status, error));
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        // These tests check the list, drawer and filters, not grouping: the tester's saved choice «Ingen gruppering» gives the flat list (groups start collapsed, 2026-11-26).
        if (FlatList) JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("None>None");
        JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.DiscoverFilterKey("m2lb")).SetResult(JsonSerializer.Serialize(filter ?? new ScenarioBrowserPresentation.Filter(All: true)));
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private static UserStoryStatusView View(params UserStoryImplementationStatus[] stories) => new()
    {
        ProjectKey = "m2lb", SnapshotId = Guid.Parse("7347da78-008f-49cd-8d0f-bc9d7d4e92a6"), CalculatedAt = DateTimeOffset.UtcNow, RuleVersion = "us-status/1", TasksAvailable = true, Stories = [.. stories],
    };

    private static void Choose(IRenderedComponent<ManualTestingDiscover> cut, CoverageUserStory story) =>
        cut.Find($"[data-testid=mtd-story][data-story='{story.Key}']").Click();

    [Fact]
    public void The_story_card_shows_task_progress_remaining_work_confirmation_and_scenario_types_and_the_evidence_opens_on_demand()
    {
        var cut = Open(View(Status(Us1, UserStoryTaskStatus.InProgress, 8, 9, 89), Status(Us2, UserStoryTaskStatus.Unknown, 0, 0, null)));
        Choose(cut, Us1);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-us-status]"));
        var card = cut.Find("[data-testid=mtd-story-head]");
        card.QuerySelector("[data-testid=mtd-us-status-badge]")!.TextContent.Should().Be("Under utvikling");
        card.QuerySelector("[data-testid=mtd-us-status-count]")!.TextContent.Should().Be("8 av 9 oppgaver fullført");
        card.QuerySelector("[data-testid=mtd-us-status-pct]")!.TextContent.Should().Be("89 %");
        card.QuerySelector("[data-testid=mtd-us-status-remaining]")!.TextContent.Should().Be("1 oppgave gjenstår");
        card.QuerySelector("[data-testid=mtd-us-confirmation]")!.TextContent.Should().Be("Ikke bekreftet");
        card.QuerySelector("[data-testid=mtd-us-ac-types]")!.TextContent.Should().Be("Akseptansescenarioer: 2 funksjonelle");
        cut.FindAll("[data-testid=mtd-us-status-details]").Should().BeEmpty("closed by default");

        cut.Find("[data-testid=mtd-us-status-toggle]").Click();
        var details = cut.Find("[data-testid=mtd-us-status-details]");
        details.QuerySelectorAll("[data-testid=mtd-us-status-remaining-list] [data-testid=mtd-us-task]").Select(t => t.GetAttribute("data-task")).Should().Equal("T050");
        details.QuerySelector("[data-testid=mtd-us-status-done]")!.HasAttribute("open").Should().BeFalse("completed tasks are collapsed");
        details.QuerySelectorAll("[data-testid=mtd-us-dependency]").Select(d => d.GetAttribute("data-kind")).Should().Equal("Blocking", "Related");
        details.QuerySelector("[data-testid=mtd-us-shared]")!.TextContent.Should().Contain("mulig avhengighet, ikke bekreftet");
        details.TextContent.Should().Contain("kildeøyeblikksbilde 7347da78").And.Contain("tasks.md");
        details.QuerySelector("[data-testid=mtd-us-confirm-button]")!.TextContent.Should().Be("Bekreft implementert");
    }

    [Fact]
    public void Unknown_status_says_so_with_no_progress_bar_no_zero_percent_and_no_bare_question_mark()
    {
        var cut = Open(View(Status(Us1, UserStoryTaskStatus.InProgress, 8, 9, 89), Status(Us2, UserStoryTaskStatus.Unknown, 0, 0, null)));
        Choose(cut, Us2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-us-status-badge]").TextContent.Should().Be("Ukjent"));
        var card = cut.Find("[data-testid=mtd-story-head]");
        card.QuerySelector("[data-testid=mtd-us-status-unavailable]")!.TextContent.Should().Be("Oppgavestatus er ikke tilgjengelig.");
        card.QuerySelectorAll("[role=progressbar]").Should().BeEmpty();
        card.TextContent.Should().NotContain("0 %").And.NotContain(" ?");
        card.QuerySelector("[data-testid=mtd-us-ac-types]")!.TextContent.Should().Be("Akseptansescenarioer: 1 ikke-funksjonelt · 1 blandet",
            "the card shows the distribution of its own scenarios, each counted once; never one type for the whole story");
    }

    [Fact]
    public void A_status_that_cannot_be_read_is_reported_with_retry_and_the_scenarios_stay_usable()
    {
        var cut = Open(null, "Tjenesten svarte ikke.");
        Choose(cut, Us1);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-us-status-error]").TextContent.Should().Contain("Tjenesten svarte ikke."));
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(2);
        _api.Setup(x => x.StoryStatusAsync("m2lb", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((View(Status(Us1, UserStoryTaskStatus.InProgress, 8, 9, 89)), (string?)null));
        cut.Find("[data-testid=mtd-us-status-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-us-status-badge]").TextContent.Should().Be("Under utvikling"));
    }

    [Fact]
    public void Rows_carry_one_short_type_label_and_grouping_by_type_shows_each_scenario_once_under_its_type()
    {
        var cut = Open(View());
        // 2026-11-26: test level and type moved from the story sidebar's control to the list's own grouping («Grupper etter» above the results).
        cut.FindAll("[data-testid=mtd-group-mode] option").Select(o => o.TextContent).Should().StartWith(["Brukerhistorier"]).And.NotContain(["Testnivå", "Scenariotype"]);
        cut.FindAll("[data-testid=mtd-list-group] option").Select(o => o.TextContent).Should().Equal("Brukerhistorie", "Testnivå", "Scenariotype", "Funksjonsområde", "Modul", "Testmetode", "Ingen gruppering");
        string? Label(DiscoveredScenario c) => cut.Find($"[data-testid=mtd-scenario][data-id='{c.CandidateId}']").QuerySelector("[data-testid=mtd-scenario-type]")?.TextContent;
        Label(Search).Should().BeNull("a functional scenario needs no label");
        Label(ErrorPanel).Should().Be("Blandet · Brukervennlighet");
        Label(Focus).Should().Be("Ikke-funksjonell · Universell utforming");

        cut.Find("[data-testid=mtd-list-group]").Change("ScenarioType");
        cut.Find("[data-testid=mtd-list-group2]").Change("None");
        cut.Find("[data-testid=mtd-groups-expand]").Click(); // groups start collapsed (2026-11-26)
        cut.FindAll("[data-testid=mtd-sgroup]").Select(g => (g.QuerySelector("[data-testid=mtd-sgroup-label]")!.TextContent, g.QuerySelector("[data-testid=mtd-sgroup-count]")!.TextContent))
            .Should().Equal(("Funksjonelle", "2"), ("Ikke-funksjonelle", "1"), ("Blandede", "1"));
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("us1-1", "us1-4", "us2-6", "us2-5");
        cut.FindAll("[data-testid=mtd-scenario][data-id='us2-5']").Should().ContainSingle("a mixed scenario is listed once, under «Blandede»");
    }

    [Fact]
    public void The_type_and_implementation_status_filters_combine_with_search_and_never_change_a_scenario()
    {
        var cut = Open(View(Status(Us1, UserStoryTaskStatus.InProgress, 8, 9, 89), Status(Us2, UserStoryTaskStatus.Unknown, 0, 0, null)),
            filter: new ScenarioBrowserPresentation.Filter(All: true, ScenarioType: nameof(ScenarioRequirementType.NonFunctional)));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("us2-6"));
        cut.Find("[data-testid=mtd-more-filters]").Click();
        cut.Find("[data-testid=mtd-filter-scenario-type]").Change("");
        cut.Find("[data-testid=mtd-filter-impl-status]").Change(nameof(UserStoryTaskStatus.InProgress));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().BeEquivalentTo(["us1-1", "us1-4"]));
        cut.Find("[data-testid=mtd-search]").Input("completes");
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("us1-4");
        _api.Verify(x => x.ApplyStoryGroupingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StoryGroupingChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_drawer_explains_the_scenario_type_and_a_reviewer_can_confirm_it_through_the_grouping_decisions()
    {
        var cut = Open(View());
        cut.Find("[data-testid=mtd-scenario][data-id='us2-6'] [data-testid=mtd-show-details]").Click();
        var type = cut.Find("[data-testid=mtd-detail-type]");
        type.GetAttribute("data-type").Should().Be("NonFunctional");
        type.QuerySelector("[data-testid=mtd-detail-type-basis]")!.TextContent.Should().Be("Foreslått fra kildegrunnlag");
        type.QuerySelector("[data-testid=mtd-detail-type-why]")!.TextContent.Should().Contain("Universell utforming");
        type.TextContent.Should().Contain("uavhengig av testtype");
    }

    // ── Scenario grouping (2026-11-26) ───────────────────────────────────────────────────────────────────────────────────────────────

    private static List<(string Label, string Count, string Depth)> Groups(IRenderedComponent<ManualTestingDiscover> cut) =>
        [.. cut.FindAll("[data-testid=mtd-sgroup]").Select(g => (g.QuerySelector("[data-testid=mtd-sgroup-label]")!.TextContent, g.QuerySelector("[data-testid=mtd-sgroup-count]")!.TextContent, g.GetAttribute("data-depth")!))];

    [Fact]
    public void G01_every_group_starts_collapsed_and_opens_and_closes_only_when_the_tester_says_so()
    {
        FlatList = false;
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-group-tree]"));
        cut.Find("[data-testid=mtd-list-group] option[selected]").GetAttribute("value").Should().Be("Story");
        cut.Find("[data-testid=mtd-group-tree]").GetAttribute("data-secondary").Should().Be("ScenarioType", "the default is Brukerhistorie → Scenariotype (AC-level)");
        cut.FindAll("[data-testid=mtd-groups-reset]").Should().BeEmpty("nothing to reset at the default");
        Groups(cut).Should().Equal(("US1 — Search for a Child", "2", "0"), ("US2 — View Child Profile Overview", "2", "0")); // counts are shown while collapsed
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "every group starts collapsed — even a small list");
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty("no rows are rendered for collapsed groups");

        cut.FindAll("[data-testid=mtd-sgroup-toggle]")[0].Click();
        var nested = cut.Find("[data-testid=mtd-sgroup][data-depth='1'] [data-testid=mtd-sgroup-toggle]");
        nested.GetAttribute("aria-expanded").Should().Be("false", "the scenario-type group under the story starts collapsed too");
        cut.FindAll("[data-testid=mtd-sgroup][data-depth='1']").Should().OnlyContain(g => g.GetAttribute("data-axis") == "ScenarioType");
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty();
        cut.Find("[data-testid=mtd-sgroup][data-depth='1'] [data-testid=mtd-sgroup-toggle]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().NotBeEmpty().And.BeSubsetOf(["us1-1", "us1-4"]);
        cut.FindAll("[data-testid=mtd-sgroup][data-depth='0'] > .mtg-head [data-testid=mtd-sgroup-toggle]")[1].GetAttribute("aria-expanded").Should().Be("false", "opening one group never opens another");
        JSInterop.Invocations.Where(i => i.Identifier == "sessionStorage.setItem").Select(i => i.Arguments[0]).Should().Contain("birknext.manualTesting.openGroups.m2lb", "the tester's expansion is kept for the project");

        cut.Find("[data-testid=mtd-groups-expand]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(4);
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().OnlyHaveUniqueItems();
        cut.Find("[data-testid=mtd-groups-collapse]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty();
    }

    [Fact]
    public void G04_nested_groups_start_collapsed_and_a_new_grouping_starts_collapsed_too()
    {
        FlatList = false;
        JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("TestLevel>ScenarioType");
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-group-tree]").GetAttribute("data-primary").Should().Be("TestLevel"));
        cut.FindAll("[data-testid=mtd-sgroup]").Should().ContainSingle("only the primary group is rendered while it is collapsed");
        cut.Find("[data-testid=mtd-sgroup-toggle]").Click();
        cut.FindAll("[data-testid=mtd-sgroup][data-depth='1'] > .mtg-head [data-testid=mtd-sgroup-toggle]").Should().HaveCount(3)
            .And.OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "secondary groups start collapsed");
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty();

        cut.Find("[data-testid=mtd-list-group]").Change("Method");
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "another grouping is a new context: collapsed");
    }

    [Fact]
    public void G05_the_testers_open_groups_survive_leaving_and_coming_back_and_the_drawer()
    {
        FlatList = false;
        JSInterop.Setup<string?>("sessionStorage.getItem", "birknext.manualTesting.openGroups.m2lb").SetResult(JsonSerializer.Serialize(new[] { $"/Story:{Spec}#{Us2.Key}" }
            .Concat(new[] { "Functional", "NonFunctional", "Mixed", "Unclassified" }.Select(t => $"/Story:{Spec}#{Us2.Key}/ScenarioType:{t}"))));
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().BeEquivalentTo("us2-5", "us2-6"));
        cut.FindAll("[data-testid=mtd-sgroup-toggle]")[0].GetAttribute("aria-expanded").Should().Be("false", "only the group the tester had opened");

        cut.Find("[data-testid=mtd-scenario][data-id='us2-5'] [data-testid=mtd-show-details]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true"));
        cut.Find("[data-testid=mtd-drawer-close]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().BeEquivalentTo(["us2-5", "us2-6"], "the drawer never changes expansion");
    }

    [Fact]
    public void G02_a_saved_preference_overrides_the_default_and_two_level_grouping_keeps_the_axes_independent()
    {
        FlatList = false;
        JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("TestLevel>ScenarioType");
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-group-tree]").GetAttribute("data-primary").Should().Be("TestLevel"));
        cut.Find("[data-testid=mtd-groups-expand]").Click();
        Groups(cut).Should().Equal(("Uavklart testnivå", "4", "0"), ("Funksjonelle", "2", "1"), ("Ikke-funksjonelle", "1", "1"), ("Blandede", "1", "1"));
        cut.FindAll("[data-testid=mtd-list-group2] option").Select(o => o.GetAttribute("value")).Should().NotContain("TestLevel", "the secondary axis cannot repeat the primary");
        cut.FindAll("[data-testid=mtd-list-group2] option")[0].TextContent.Should().Be("Én gruppering");

        cut.Find("[data-testid=mtd-list-group]").Change("Method");
        cut.FindAll("[data-testid=mtd-sgroup][data-depth='0']").Select(g => g.GetAttribute("data-axis")).Should().OnlyContain(a => a == "Method");
        JSInterop.VerifyInvoke("localStorage.setItem").Arguments.Should().Equal("birknext.manualTesting.scenarioGrouping", "Method>ScenarioType");

        cut.Find("[data-testid=mtd-list-group]").Change("None");
        cut.FindAll("[data-testid=mtd-sgroup]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(4, "«Ingen gruppering» is the flat list");
    }

    [Fact]
    public void G03_group_selection_counts_and_selection_outside_the_current_filter_are_explicit()
    {
        FlatList = false;
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-group-tree]"));
        cut.FindAll("[data-testid=mtd-sgroup-select]")[1].Click();
        cut.FindAll("[data-testid=mtd-sgroup]")[1].QuerySelector("[data-testid=mtd-sgroup-selected]")!.TextContent.Should().Be("2 valgt");
        cut.Find("[data-testid=mtd-selection-summary]").TextContent.Should().Contain("2 valgt totalt · 2 i gjeldende visning");

        cut.Find("[data-testid=mtd-search]").Input("search field");
        Groups(cut).Should().Equal(("US1 — Search for a Child", "1", "0"));
        cut.Find("[data-testid=mtd-selection-summary]").TextContent.Should().Contain("2 valgt totalt · 0 i gjeldende visning · 2 utenfor gjeldende filter", "a filter never drops a selection");

        cut.Find("[data-testid=mtd-search]").Input("");
        cut.FindAll("[data-testid=mtd-sgroup-clear]").Single().Click();
        cut.Find("[data-testid=mtd-selection-summary]").TextContent.Should().Contain("0 valgt totalt");
    }

    // ── Dashboard subset from Testoversikt (2026-11-26 blocker) ─────────────────────────────────────────────────────────────────────────

    private static ScenarioBrowserPresentation.Filter Subset(params DiscoveredScenario[] scenarios) =>
        ScenarioBrowserPresentation.FromOverview([.. scenarios.Select(c => c.SourceIdentity)], "P1 uten testtilfeller");

    private static string[] RailStories(IRenderedComponent<ManualTestingDiscover> cut) =>
        [.. cut.FindAll("[data-testid=mtd-story]").Select(e => e.GetAttribute("data-story")!).Where(k => k.Length > 0)];

    // ── V10 P1 (2026-11-27): «Åpne» on one scenario in Testoversikt showed «Utvalg fra Testoversikt» and 0 rows in a real browser ──
    // Root cause: the scenario passed every filter, but the DEFAULT list grouping (Brukerhistorie → Scenariotype) starts collapsed, so its row was
    // never rendered. These tests use that default (not the flat list the other tests choose).

    [Fact]
    public void P1_an_exact_scenario_from_Testoversikt_is_listed_in_its_opened_group_active_focused_and_open_with_the_default_grouping()
    {
        FlatList = false;
        var cut = Open(View(), filter: Subset(Search));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal(["us1-1"],
            "the selected scenario is rendered: before the fix the list showed 0 rows because its group was collapsed"));
        cut.Find("[data-testid=mtd-group-tree]").GetAttribute("data-primary").Should().Be("Story", "the default grouping, not a flat list");
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "true", "every group holding the selection is opened");
        cut.Find("[data-testid=mtd-scenario][data-id='us1-1']").ClassList.Should().Contain("is-active");
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "msb-row-us1-1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true", "one selected scenario opens its details"));
        cut.Find("[data-testid=mtd-overview-selection]").TextContent.Should().Contain("1 av 1 scenario");
    }

    [Fact]
    public void P1_a_group_the_tester_closes_during_the_selection_stays_closed_and_a_multi_selection_opens_no_details()
    {
        FlatList = false;
        var cut = Open(View(), filter: Subset(Search, NoMatch));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(2));
        cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().NotBe("true", "two scenarios: the list is shown, the tester chooses");
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").First().Click();
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").First().GetAttribute("aria-expanded").Should().Be("false", "an explicit close is respected");
    }

    [Fact]
    public void P1_a_selection_that_other_filters_hide_is_explained_with_Vis_likevel_never_an_unexplained_empty_list()
    {
        var cut = Open(View(), filter: Subset(Search) with { Status = "approved" });
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-overview-selection-hidden]").TextContent.Should().Contain("1 skjules av andre aktive filtre"));
        cut.FindAll("[data-testid=mtd-scenario]").Should().BeEmpty();
        cut.Find("[data-testid=mtd-overview-selection-show]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal(["us1-1"]));
        cut.FindAll("[data-testid=mtd-overview-selection-hidden]").Should().BeEmpty();
        cut.Find("[data-testid=mtd-overview-selection]").Should().NotBeNull("the selection itself is kept");
    }

    [Fact]
    public void P1_a_selected_scenario_that_is_not_in_the_current_source_version_is_explained()
    {
        var gone = Search with { CandidateId = "gone", SourceIdentity = "tss-gone-000000000" };
        var cut = Open(View(), filter: ScenarioBrowserPresentation.FromOverview([gone.SourceIdentity], "P1 uten testtilfeller"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-overview-selection-missing]").TextContent.Should().Contain("finnes ikke i gjeldende kildeversjon"));
        cut.Find("[data-testid=mtd-overview-selection]").TextContent.Should().Contain("0 av 1 scenario");
    }

    [Fact]
    public void S01_the_story_list_and_the_scenario_list_show_the_same_dashboard_subset()
    {
        var cut = Open(View(), filter: Subset(Search, NoMatch));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("us1-1", "us1-4"));
        RailStories(cut).Should().Equal([Us1.Key], "only the subset's stories are listed (US2 is not part of the subset)");
        cut.Find("[data-testid=mtd-overview-selection]").Should().NotBeNull();
    }

    [Fact]
    public void S02_the_drawer_grouping_and_checkboxes_keep_the_subset_and_Fjern_utvalget_ends_it()
    {
        var cut = Open(View(), filter: Subset(Search));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Should().ContainSingle());
        cut.Find("[data-testid=mtd-scenario][data-id='us1-1'] [data-testid=mtd-show-details]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-detail]").GetAttribute("data-open").Should().Be("true"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-scenario][data-id='us1-1'] [data-testid=mtd-select]").Change(true));
        cut.Find("[data-testid=mtd-list-group]").Change("TestLevel");
        cut.Find("[data-testid=mtd-list-group]").Change("None");
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal(["us1-1"], "drawer, selection and grouping never end the subset");
        RailStories(cut).Should().Equal([Us1.Key]);

        cut.Find("[data-testid=mtd-overview-selection-clear]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(4);
        RailStories(cut).Should().Equal([Us1.Key, Us2.Key], "after «Fjern utvalget» the story list follows the full scope");
        cut.Find("[data-testid=mtd-selection-summary]").TextContent.Should().Contain("1 valgt totalt", "the selection survives");
    }

    [Fact]
    public void S03_an_explicit_module_change_ends_the_subset_and_the_story_filter_while_the_selection_stays()
    {
        TestModules = [new("Frontend", 1, "s1", ExplorerSelectionReason.Explicit), new("SkoleService", 1, null, null)];
        var cut = Open(View(), filter: Subset(Search, NoMatch) with { Story = Us1.Key });
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-overview-selection]"));
        cut.Find("[data-testid=mtd-scenario][data-id='us1-1'] [data-testid=mtd-select]").Change(true);

        cut.Find("[data-testid=ssp-module]").Change("SkoleService");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-overview-selection]").Should().BeEmpty("an explicit scope change ends the dashboard subset"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(4, "the new scope shows its scenarios with no hidden restriction"));
        RailStories(cut).Should().Equal([Us1.Key, Us2.Key]);
        JSInterop.Invocations.Where(i => i.Identifier == "sessionStorage.setItem" && i.Arguments[0]!.ToString() == TabDraftStore.DiscoverFilterKey("m2lb"))
            .Last().Arguments[1]!.ToString().Should().NotContain("tss-us1-1", "the stored filter no longer holds the subset (no reactivation on reload or Back)");
        cut.Find("[data-testid=mtd-selection-summary]").TextContent.Should().Contain("1 valgt totalt", "the checkbox selection survives a scope change");
    }

    [Fact]
    public void S04_an_explicit_specification_change_ends_the_subset()
    {
        ExtraSpecs.Add(new ExplorerArtifact("s2", WorkspaceArtifactType.Specification, "Profile", "spec.md", "Frontend/specs/003-profile/spec.md", ExplorerArtifactSource.Workspace, null, null, 1, "Unknown",
            ExplorerArtifactCurrentness.Current, null, Module: "Frontend"));
        var cut = Open(View(), filter: Subset(Search));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-overview-selection]"));
        cut.Find("[data-testid=ssp-specification]").Change("s2");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtd-overview-selection]").Should().BeEmpty("an explicit specification change ends the dashboard subset"));
    }

    [Fact]
    public void S05_story_less_scenarios_of_a_subset_are_listed_under_Uten_brukerhistorie_and_others_are_not()
    {
        var items = ScenarioBrowserPresentation.Items(
        [
            Search, ErrorPanel,
            new DiscoveredScenario { CandidateId = "e2e-1", ProjectKey = "m2lb", Title = "Altinn → Receiver → SkoleService", SourceIdentity = "tss-e2e-1", CanonicalKey = "flow:1" },
            new DiscoveredScenario { CandidateId = "e2e-2", ProjectKey = "m2lb", Title = "Not in the subset", SourceIdentity = "tss-e2e-2", CanonicalKey = "flow:2" },
        ], new Dictionary<string, ScenarioBrowserPresentation.Prepared>());
        var stories = ScenarioWorkspacePresentation.Stories(items, ScenarioBrowserPresentation.FromOverview([Search.SourceIdentity, "tss-e2e-1"], "Utvalg"));
        stories.Select(st => (st.Key, st.Discovered)).Should().Equal((Us1.Key, 1), (ScenarioWorkspacePresentation.NoStory, 1));
    }

    [Fact]
    public void G06_Tilbakestill_gruppering_restores_Brukerhistorie_then_Scenariotype_collapsed()
    {
        FlatList = false;
        JSInterop.Setup<string?>("localStorage.getItem", "birknext.manualTesting.scenarioGrouping").SetResult("Method>ScenarioType");
        var cut = Open(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd-group-tree]").GetAttribute("data-primary").Should().Be("Method"));
        cut.Find("[data-testid=mtd-groups-reset]").Click();
        var tree = cut.Find("[data-testid=mtd-group-tree]");
        (tree.GetAttribute("data-primary"), tree.GetAttribute("data-secondary")).Should().Be(("Story", "ScenarioType"));
        cut.FindAll("[data-testid=mtd-sgroup][data-depth='0'] [data-testid=mtd-sgroup-type-summary]").Should().NotBeEmpty("a story shows its ACs' summary");
        cut.FindAll("[data-testid=mtd-sgroup-type-summary]").Should().OnlyContain(s => s.TextContent.Trim().StartsWith("Akseptansescenarioer:"), "a summary of ACs, never «US type»");
        cut.FindAll("[data-testid=mtd-sgroup-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "collapsed");
        JSInterop.VerifyInvoke("localStorage.removeItem").Arguments.Should().Equal("birknext.manualTesting.scenarioGrouping");
        cut.FindAll("[data-testid=mtd-groups-reset]").Should().BeEmpty();
    }
}
