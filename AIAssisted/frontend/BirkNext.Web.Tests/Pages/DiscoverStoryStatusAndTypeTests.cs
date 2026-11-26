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
            ExplorerArtifactStatus.Loaded, "m2lb", "M2LB", [spec], spec, ExplorerSelectionReason.Explicit, "# spec", Module: "Frontend"));
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
        cut.FindAll("[data-testid=mtd-group-mode] option").Select(o => o.TextContent).Should().StartWith(["Brukerhistorier", "Scenariotype"]);
        cut.FindAll("[data-testid=mtd-group-mode] option").First(o => o.HasAttribute("selected")).TextContent.Should().Be("Brukerhistorier", "the default grouping is unchanged");
        string? Label(DiscoveredScenario c) => cut.Find($"[data-testid=mtd-scenario][data-id='{c.CandidateId}']").QuerySelector("[data-testid=mtd-scenario-type]")?.TextContent;
        Label(Search).Should().BeNull("a functional scenario needs no label");
        Label(ErrorPanel).Should().Be("Blandet · Brukervennlighet");
        Label(Focus).Should().Be("Ikke-funksjonell · Universell utforming");

        cut.Find("[data-testid=mtd-group-mode]").Change("ScenarioType");
        cut.FindAll("[data-testid=mtd-type-group]").Select(g => g.TextContent).Should().Equal("FUNKSJONELLE (2)", "IKKE-FUNKSJONELLE (1)", "BLANDEDE (1)");
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
}
