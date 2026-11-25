using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Manual Testing → Oppdag og forstå, scenario-first (2026-11-23): relevance by the deterministic ownership rule (technical hidden by default but
/// one toggle away, uncertain never hidden), titles with the real story id and a safe cleanup, statuses from saved records, and the page itself —
/// search and filters together, selection kept across filters, details with stories, requirements and evidence, hand-off to Plan &amp; prepare.
/// </summary>
public sealed class ScenarioBrowserTests : BunitContext
{
    private static CoverageUserStory Story(string id, int ordinal, string title) => new()
    {
        Key = CoverageUserStory.KeyOf("spec.md", $"us{ordinal}"), StoryId = id, Ordinal = ordinal, Title = title, Priority = "P1", SpecificationPath = "spec.md",
    };

    private static DiscoveredScenario C(string id, string capability, string title, ScenarioCandidateType type, string key, string? expected = "Shown as documented",
        CoverageUserStory? story = null, string[]? requirements = null, string area = "Rapporter") => new()
    {
        CandidateId = id, ProjectKey = "library", CapabilityId = capability, SourceCategory = capability, Area = area, Title = title, Purpose = "Formål " + title, Type = type,
        Confidence = expected is null ? CandidateConfidence.Insufficient : CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = expected, CanonicalKey = key,
        Evidence = [new("specification", "spec.md#" + id, "Akseptansescenario")], Story = story, RequirementIds = [.. requirements ?? []], SourceIdentity = "tss-" + id,
        Preconditions = ["en gyldig rapport"], Steps = [new("Send rapporten", expected)],
    };

    private static readonly CoverageUserStory Us14 = Story("US-014", 14, "Rapportering av fravær");

    private static readonly DiscoveredScenario Spec = C("c-spec", ScenarioOwnership.Specification, "A user submits a valid attendance report", ScenarioCandidateType.BusinessFunctional,
        "scenario:|s1", story: Us14, requirements: ["FR-001"]);
    private static readonly DiscoveredScenario Unclear = C("c-unclear", ScenarioOwnership.Specification, "A report for a closed period", ScenarioCandidateType.BusinessFunctional,
        "scenario:|s2", expected: null, story: Us14, requirements: ["FR-002"]);
    private static readonly DiscoveredScenario Api = C("c-api", ScenarioOwnership.ApiContract, "POST /reports", ScenarioCandidateType.ApiContract, "contract-op:api.yaml#post", area: "Rapport-API");
    private static readonly DiscoveredScenario Smoke = C("c-smoke", ScenarioOwnership.UiSource, "Report page opens", ScenarioCandidateType.UiSmoke, "ui:smoke", area: "Portal");
    private static readonly DiscoveredScenario Journey = C("c-journey", ScenarioOwnership.IntegrationJourneys, "Report travels to the registry", ScenarioCandidateType.Integration, "journey:x", area: "Integrasjon");
    private static readonly DiscoveredScenario AuthWithRequirement = C("c-auth", ScenarioOwnership.ApiContract, "POST /reports — refused without valid authorization",
        ScenarioCandidateType.SecurityAuthorization, "contract-auth:api.yaml#post", requirements: ["FR-009"], area: "Rapport-API");
    private static readonly DiscoveredScenario Unknown = C("c-unknown", "future-analyzer", "Something a new analyzer found", ScenarioCandidateType.BusinessFunctional, "x:1");

    // ── Pure rules ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Manual_scenarios_are_shown_by_default_technical_ones_are_hidden_and_uncertain_ones_never_are()
    {
        S.Relevance R(DiscoveredScenario c) => S.RelevanceOf(c, ScenarioOwnership.Of(c));
        R(Spec).Should().Be(S.Relevance.Manual);
        R(Journey).Should().Be(S.Relevance.Manual, "business integration flows are manual-testing relevant");
        R(AuthWithRequirement).Should().Be(S.Relevance.Manual, "authorization behaviour a requirement names is functional security");
        R(Unknown).Should().Be(S.Relevance.Manual, "an unclassified candidate is never hidden");
        R(Api).Should().Be(S.Relevance.Technical);
        R(Smoke).Should().Be(S.Relevance.Technical);
        R(AuthWithRequirement with { RequirementIds = [] }).Should().Be(S.Relevance.Technical, "a contract's own per-operation check is low-level");
        R(Api with { Conflicts = [new ScenarioConflict { Kind = ConflictKind.ContradictoryExpectedResult }] }).Should().Be(S.Relevance.Manual, "contradicting sources need a person");
    }

    [Fact]
    public void Titles_lead_with_the_real_story_id_and_drop_only_a_safe_introduction()
    {
        S.DisplayTitle(Spec).Should().Be("US-014 — Submit valid attendance report");
        S.DisplayTitle(Spec with { Story = null }).Should().Be("Submit valid attendance report", "no story id is ever invented");
        S.DisplayTitle(Spec with { Story = Us14 with { StoryId = null } }).Should().Be("Submit valid attendance report", "an ordinal is not an id");
        S.Concise("A report for a closed period").Should().Be("A report for a closed period", "a report is not an actor: kept as written");
        S.Concise("The user passes the check").Should().Be("Pass check", "a regular -es verb is reduced correctly");
        S.Concise("A user applies for leave").Should().Be("A user applies for leave", "an irregular -ies form is kept as written");
        S.Concise("En saksbehandler godkjenner søknaden").Should().Be("Godkjenn søknaden");
        S.Concise("Rapporten lagres").Should().Be("Rapporten lagres");
        Spec.Title.Should().Be("A user submits a valid attendance report", "the original stays the source text");
    }

    [Fact]
    public void Visible_counts_differ_honestly_from_totals_and_filters_work_together()
    {
        var items = S.Items([Spec, Unclear, Api, Smoke, Journey], new Dictionary<string, S.Prepared>());
        var (shown, hidden) = S.Apply(items, new S.Filter());
        shown.Select(i => i.Id).Should().BeEquivalentTo(["c-spec", "c-unclear", "c-journey"]);
        hidden.Should().Be(2);
        S.Apply(items, new S.Filter(Technical: true)).Shown.Should().HaveCount(5);
        S.Apply(items, new S.Filter(All: true)).HiddenTechnical.Should().Be(0);
        S.Apply(items, new S.Filter(Query: "closed", Status: "clarify")).Shown.Select(i => i.Id).Should().Equal("c-unclear");
        S.Apply(items, new S.Filter(Query: "report", Area: "Rapporter")).Shown.Select(i => i.Id).Should().BeEquivalentTo(["c-spec", "c-unclear"]);
        S.Apply(items, new S.Filter(Requirement: "fr-001")).Shown.Select(i => i.Id).Should().Equal("c-spec");
        S.Apply(items, new S.Filter(Story: Us14.Key)).Shown.Should().HaveCount(2);
    }

    [Fact]
    public void Status_comes_from_saved_cases_and_never_calls_a_suggestion_approved()
    {
        var caseFor = new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "older-import", ScenarioId = "other-id" },
            Generated = new TestCaseContent { SourceIdentity = "tss-c-spec" }, Review = TestCaseReviewState.Accepted };
        var plan = new TestPlanDraft { Cases = [caseFor, caseFor with { Review = TestCaseReviewState.Generated }] };
        var prepared = S.PreparedCases([Spec, Unclear], [plan]);
        prepared["c-spec"].Should().Be(new S.Prepared(2, 1, false, false, false), "a case of an earlier import counts through the scenario's source identity");
        S.StatusOf(Spec, prepared["c-spec"]).Text.Should().Be("Under arbeid", "one of two cases is approved");
        S.StatusOf(Spec, new S.Prepared(1, 1, false, false, false)).Text.Should().Be("Godkjent");
        S.StatusOf(Spec, null).Text.Should().Be("Foreslått", "a discovered scenario is a suggestion, not a test case");
        S.StatusOf(Unclear, null).Text.Should().Be("Trenger avklaring");
        S.StatusOf(Spec, new S.Prepared(1, 1, true, false, false)).Text.Should().Be("Kildegrunnlag endret");
        S.ExpectedBasis(Unclear).Should().Contain("trenger avklaring");
        S.ExpectedBasis(Spec with { Confidence = CandidateConfidence.SourceDerived }).Should().Contain("ikke en fasit");
    }

    // ── The page ────────────────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<ManualTestingDiscover> Open(params DiscoveredScenario[] candidates)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true }, (string?)null));
        api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioDiscoveryResult
        {
            DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [.. candidates],
            Basis = new DiscoverySourceBasis { ApplicationKey = "repo:library", Kind = DiscoveryBasisKind.Current, SpecificationOrigin = SpecificationOrigin.ImportedSnapshot,
                Members = [new DiscoverySourceMember { RepositoryKey = "repo:library", DisplayName = "Library", SnapshotId = Guid.NewGuid(), ArchiveName = "library-v2.zip", StateCode = "SINGLE_SNAPSHOT" }] },
        }, (string?)null));
        // Work queues not available (server error): coverage is unknown, every scenario is listed (the fallback; the queues have their own tests).
        api.Setup(x => x.WorkQueuesAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ScenarioWorkQueueView?)null, "Testplanforslag svarte ikke"));
        Services.AddSingleton(api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/discover");
        var cut = Render<ManualTestingDiscover>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtd]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void Scenarios_are_the_main_content_with_technical_ones_one_toggle_away_and_the_selection_kept_across_filters()
    {
        var cut = Open(Spec, Unclear, Api, Smoke, Journey);
        cut.Find("[data-testid=mtd-basis]").TextContent.Should().Contain("Gjeldende kildegrunnlag").And.Contain("library-v2.zip");
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().BeEquivalentTo(["c-spec", "c-unclear", "c-journey"]);
        cut.Find("[data-testid=mtd-visible]").TextContent.Should().Contain("Viser 3 av 5").And.Contain("2 tekniske skjult");
        cut.Find("[data-testid=mtd-count-suggestions]").TextContent.Should().Be("5", "the count says suggestions, not test cases");
        cut.Find("[data-testid=mtd-count-prepared]").TextContent.Should().Be("ukjent", "without the work queues the saved test cases are unknown, never zero");
        cut.Find("[data-testid=mtd-queues-unavailable]").TextContent.Should().Contain("Prioriteringen er ikke tilgjengelig").And.Contain("ukjent");
        cut.FindAll("[data-testid=mtd-scenario-title]").Select(e => e.TextContent).Should().Contain("US-014 — Submit valid attendance report");

        // Select one, then narrow the filter so it is no longer visible: the selection stays.
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-spec").QuerySelector("[data-testid=mtd-select]")!.Change(true);
        cut.Find("[data-testid=mtd-search]").Input("closed");
        cut.FindAll("[data-testid=mtd-scenario]").Select(e => e.GetAttribute("data-id")).Should().Equal("c-unclear");
        cut.Find("[data-testid=mtd-count-selected]").TextContent.Should().Be("1", "selection persists across filters");
        cut.Find("[data-testid=mtd-search]").Input("");

        cut.Find("[data-testid=mtd-show-all]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(5);
        cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").GetAttribute("aria-checked").Should().Be("true", "the toggle says what is shown: technical scenarios are visible");
        cut.Find("[data-testid=mtd-kind-view-option][data-view=Manual]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(3, "unticking hides them again");
        cut.Find("[data-testid=mtd-kind-view-option][data-view=All]").Click();
        cut.FindAll("[data-testid=mtd-scenario]").Should().HaveCount(5);
        cut.Find("[data-testid=mtd-visible]").TextContent.Should().Contain("Viser 5 av 5");
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-api").GetAttribute("data-relevance").Should().Be("Technical");
    }

    [Fact]
    public void A_scenario_shows_its_story_requirements_clarification_and_evidence_on_demand_and_hands_the_selection_to_Plan_and_prepare()
    {
        var cut = Open(Spec, Unclear);
        cut.FindAll("[data-testid=mtd-scenario]").Single(e => e.GetAttribute("data-id") == "c-unclear").Click();
        var detail = cut.Find("[data-testid=mtd-detail]");
        detail.QuerySelector("[data-testid=mtd-detail-title]")!.TextContent.Should().Be("US-014 — A report for a closed period");
        detail.QuerySelector("[data-testid=mtd-detail-story]")!.TextContent.Should().Contain("US-014").And.Contain("Rapportering av fravær");
        detail.QuerySelector("[data-testid=mtd-detail-requirements]")!.TextContent.Should().Contain("FR-002").And.Contain("Nevnt i kilden");
        detail.QuerySelector("[data-testid=mtd-req-link]")!.GetAttribute("data-relation").Should().Be("Explicit", "a requirement the source names is shown as named, never as a suggestion");
        detail.QuerySelector("[data-testid=mtd-detail-expected]")!.TextContent.Should().Contain("Ikke oppgitt").And.Contain("trenger avklaring");
        var evidence = detail.QuerySelector("[data-testid=mtd-detail-evidence]")!;
        evidence.HasAttribute("open").Should().BeFalse("technical evidence is collapsed by default");
        evidence.TextContent.Should().Contain("spec.md#c-unclear");

        // Keyboard: ↓ moves the active scenario, Space selects it.
        cut.Find("[data-testid=mtd-rows]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Home" });
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("A report for a closed period", "Home: the first scenario of the list");
        cut.Find("[data-testid=mtd-rows]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("[data-testid=mtd-detail-title]").TextContent.Should().Contain("Submit valid attendance report");
        cut.Find("[data-testid=mtd-rows]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = " " });
        cut.Find("[data-testid=mtd-selection]").TextContent.Should().Contain("1 valgt");

        cut.Find("[data-testid=mtd-prepare]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "sessionStorage.setItem" && i.Arguments[0]!.ToString() == TabDraftStore.SelectionKey("library")
            && i.Arguments[1]!.ToString()!.Contains("c-spec"), "Plan & prepare restores this selection");
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("manual-testing/plan");
    }
}
