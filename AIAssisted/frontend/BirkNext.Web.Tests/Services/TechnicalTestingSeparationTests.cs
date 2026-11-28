using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using D = BirkNext.Web.Services.TechnicalTestingDashboard;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Technical Testing as a workspace parallel to Manual Testing (2026-11-28): one inventory and one plan store, classified by the versioned
/// <see cref="E2ERules"/> and <see cref="TestWorkClassifier"/> — never by title or source technology — and shown per workspace by view-level
/// filtering. Figures carry their exact records, so a drilldown opens exactly what was counted.
/// </summary>
public sealed class TechnicalTestingSeparationTests : BunitContext
{
    private static DiscoveredScenario C(string id, string capability, ScenarioCandidateType type, string? contractType = null, string? canonical = null, bool automated = false) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = capability, Type = type, Title = $"Title {id}", SourceIdentity = "tss-" + id,
        CanonicalKey = canonical ?? $"{capability}:{id}",
        Evidence = contractType is null ? [] : [new CandidateEvidenceRef("contract-operation", $"contracts/{id}.yaml:1#{id}", $"{contractType} Api 1.0")],
        ExistingTests = automated ? [new CandidateTestAssociation("T" + id, "T" + id, null, "names the scenario") { Origin = TestScenarioLinkOrigin.Scenario }]
            : [new CandidateTestAssociation("R" + id, "R" + id, null, "shares a requirement")],
    };

    private static readonly DiscoveredScenario Acceptance = C("ac", ScenarioOwnership.Specification, ScenarioCandidateType.BusinessFunctional, canonical: "scenario:spec.md#1");
    private static readonly DiscoveredScenario Rest = C("rest", ScenarioOwnership.ApiContract, ScenarioCandidateType.ApiContract, "OpenApi");
    private static readonly DiscoveredScenario GraphQl = C("gql", ScenarioOwnership.ApiContract, ScenarioCandidateType.ApiContract, "GraphQlSchema", automated: true);
    private static readonly DiscoveredScenario Journey = C("jr", ScenarioOwnership.IntegrationJourneys, ScenarioCandidateType.Integration);
    private static readonly DiscoveredScenario GuideE2E = C("guide", ScenarioOwnership.SystemGuide, ScenarioCandidateType.BusinessE2E);
    private static readonly DiscoveredScenario TestE = C("teste", E2ERules.TestSpecification, ScenarioCandidateType.BusinessE2E, canonical: "testspec:Hendelse/docs/Testspesifikasjon.md#TEST-E-01");
    private static readonly DiscoveredScenario DevMessaging = C("dev", ScenarioOwnership.DeveloperTests, ScenarioCandidateType.Integration);
    private static readonly DiscoveredScenario Unknown = C("unknown", "some-new-producer", ScenarioCandidateType.BusinessFunctional);

    // ── Classification rules ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_acceptance_scenario_is_manual_business_work_and_never_end_to_end_by_itself()
    {
        var c = TestWorkClassifier.Classify(Acceptance);
        c.Category.Should().Be(TestWorkCategory.ManualBusiness);
        c.E2E.Status.Should().Be(E2EStatus.NotE2E);
        c.ManualDefault.Should().BeTrue();
        c.TechnicalDefault.Should().BeFalse();
    }

    [Fact]
    public void A_contract_operation_is_REST_or_GraphQL_by_its_recorded_contract_type_and_is_technical_only()
    {
        TestWorkClassifier.Classify(Rest).Category.Should().Be(TestWorkCategory.TechnicalApi);
        TestWorkClassifier.Classify(GraphQl).Category.Should().Be(TestWorkCategory.TechnicalGraphQl);
        TestWorkClassifier.Classify(GraphQl).E2E.Status.Should().Be(E2EStatus.NotE2E, "one operation of one service is a contract test, not E2E");
        TestWorkClassifier.Classify(Rest).ManualDefault.Should().BeFalse();
        // The title never decides: a REST operation titled «GraphQL …» stays REST.
        TestWorkClassifier.Classify(Rest with { Title = "GraphQL query getBarn" }).Category.Should().Be(TestWorkCategory.TechnicalApi);
    }

    [Fact]
    public void Multi_system_flows_are_confirmed_E2E_and_hybrid_so_both_workspaces_show_them()
    {
        foreach (var flow in new[] { Journey, GuideE2E })
        {
            var c = TestWorkClassifier.Classify(flow);
            c.E2E.Status.Should().Be(E2EStatus.Confirmed, flow.CapabilityId);
            c.Category.Should().Be(TestWorkCategory.Hybrid);
            c.ManualDefault.Should().BeTrue();
            c.TechnicalDefault.Should().BeTrue();
        }
    }

    [Fact]
    public void Declared_or_suggested_end_to_end_tests_are_candidates_not_confirmed()
    {
        TestWorkClassifier.Classify(TestE).E2E.Status.Should().Be(E2EStatus.Candidate, "TEST-E declares the level; it is not verified");
        TestWorkClassifier.Classify(TestE).TechnicalDefault.Should().BeTrue();
        TestWorkClassifier.Classify(DevMessaging).E2E.Status.Should().Be(E2EStatus.Candidate);
        TestWorkClassifier.Classify(C("ui", ScenarioOwnership.UiSource, ScenarioCandidateType.BusinessE2E)).E2E.Status.Should().Be(E2EStatus.Candidate);
    }

    [Fact]
    public void An_unknown_producer_needs_classification_and_a_reviewed_decision_wins()
    {
        TestWorkClassifier.Classify(Unknown).Category.Should().Be(TestWorkCategory.NeedsClassification);
        TestWorkClassifier.Classify(Unknown).TechnicalDefault.Should().BeTrue("unclassified work is surfaced for the technical tester to sort, never hidden");
        TestWorkClassifier.Classify(Rest, ScenarioTestKind.Manual).Category.Should().Be(TestWorkCategory.ManualBusiness, "a person chose manual");
        TestWorkClassifier.Classify(Journey, ScenarioTestKind.Technical).Category.Should().Be(TestWorkCategory.TechnicalE2E);
        TestWorkClassifier.Classify(Journey).E2E.RuleVersion.Should().Be(E2ERules.RuleVersion);
        TestWorkClassifier.Classify(Journey).RuleVersion.Should().Be(TestWorkClassifier.RuleVersion);
    }

    // ── Dashboard figures and exact identities ───────────────────────────────────────────────────────────────────────

    private static readonly Guid Plan = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static OverviewCase Case(string id, string? identity, ScenarioTestKind? kind, TestCaseReviewState review = TestCaseReviewState.Generated, CaseSourceState? source = null) =>
        new() { PlanId = Plan, CaseId = id, Title = id, ScenarioIdentity = identity, TestKind = kind, Review = review, SourceState = source };

    private static readonly IReadOnlyList<DiscoveredScenario> Inventory = [Acceptance, Rest, Rest with { CandidateId = "rest-copy" }, GraphQl, Journey, GuideE2E, TestE, DevMessaging, Unknown];

    private static readonly IReadOnlyList<OverviewCase> Cases =
    [
        Case("c-rest", "tss-rest", null, TestCaseReviewState.Accepted),             // technical by its scenario
        Case("c-ac", "tss-ac", null),                                               // manual by its scenario
        Case("c-ac-tech", "tss-ac", ScenarioTestKind.Technical, source: CaseSourceState.Changed), // chosen technical
        Case("c-jr-manual", "tss-jr", ScenarioTestKind.Manual),                     // hybrid flow, chosen manual: not technical work
        Case("c-jr", "tss-jr", null, source: CaseSourceState.NotCompared),           // hybrid flow, no choice: both workspaces
    ];

    [Fact]
    public void Every_figure_counts_exactly_its_records_and_one_identity_counts_once()
    {
        var d = D.Build(Inventory, Cases);
        IEnumerable<string> Ids(string key) => d.Find(key)!.Candidates.Select(w => w.Candidate.CandidateId);
        Ids(D.E2E).Should().BeEquivalentTo(["jr", "guide", "teste", "dev"]);
        Ids(D.Api).Should().BeEquivalentTo(["rest"], "the same operation in two contract files is one piece of work");
        Ids(D.GraphQl).Should().BeEquivalentTo(["gql"]);
        Ids(D.Automated).Should().BeEquivalentTo(["gql"], "an automated test found in the source, not an executed test");
        Ids(D.Clarify).Should().BeEquivalentTo(["unknown"]);
        d.Discovered.Should().OnlyContain(f => f.Value == f.Candidates.Count);

        d.Find(D.Prepared)!.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c-rest", "c-ac-tech", "c-jr"], "a case chosen «Manuell» is never technical work");
        d.Find(D.Approved)!.Cases.Select(c => c.CaseId).Should().Equal("c-rest");
        d.Find(D.SourceAffected)!.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c-ac-tech", "c-jr"]);
        D.Href(D.GraphQl).Should().Be("technical-testing/assess?work=graphql");
    }

    // ── Rendering and drilldowns ─────────────────────────────────────────────────────────────────────────────────────

    private void Api()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(a => a.LastDiscoveryAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "m2lb", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = [.. Inventory] }, (string?)null));
        api.Setup(a => a.OverviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ManualTestingOverview { ProjectKey = "m2lb", Cases = [.. Cases] }, (string?)null));
        Services.AddSingleton(api.Object);
    }

    [Fact]
    public void Teknisk_testoversikt_shows_compact_figures_candidate_figures_link_to_Finn_og_analyser_and_case_figures_open_exactly_their_cases()
    {
        Api();
        var cut = Render<TechnicalDashboard>(p => p.Add(x => x.ProjectKey, "m2lb"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ttd-kpi]").Should().HaveCount(9));
        var kpis = cut.FindAll("[data-testid=ttd-kpi]").ToDictionary(k => k.GetAttribute("data-key")!, k => k);
        kpis[D.E2E].GetAttribute("data-value").Should().Be("4");
        kpis[D.Api].QuerySelector("[data-testid=ttd-open]")!.GetAttribute("href").Should().Be("technical-testing/assess?work=api");
        kpis[D.Integration].GetAttribute("data-value").Should().Be("0");
        kpis[D.Integration].QuerySelector("[data-testid=ttd-open]").Should().BeNull("an empty figure opens nothing");
        cut.Markup.Should().NotContain("Title rest", "no inline candidate list on the overview");

        kpis[D.Prepared].QuerySelector("[data-testid=ttd-open]")!.Click();
        var written = JSInterop.Invocations.Last(i => i.Identifier == "sessionStorage.setItem" && (string)i.Arguments[0]! == TabDraftStore.PlanCaseSelectionKey("m2lb"));
        var selection = System.Text.Json.JsonSerializer.Deserialize<PlanPreparePresentation.CaseSelection>((string)written.Arguments[1]!)!;
        selection.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c-rest", "c-ac-tech", "c-jr"]);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().Contain($"manual-testing/plan?draft={Plan}");
    }

    [Fact]
    public void Finn_og_analyser_lists_exactly_the_candidates_of_the_opened_figure_with_their_classification()
    {
        Api();
        var cut = Render<TechnicalWorkList>(p => p.Add(x => x.ProjectKey, "m2lb").Add(x => x.Work, D.E2E));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=twl-item]").Should().HaveCount(4));
        cut.FindAll("[data-testid=twl-item]").Select(i => i.GetAttribute("data-candidate")).Should().BeEquivalentTo(["jr", "guide", "teste", "dev"]);
        cut.Find("[data-testid=twl-total]").GetAttribute("data-total").Should().Be("4");
        cut.FindAll("[data-testid=twl-item]").Single(i => i.GetAttribute("data-candidate") == "jr").GetAttribute("data-e2e").Should().Be("Confirmed");
        cut.Markup.Should().Contain("Declared integration journey");
    }

    [Fact]
    public void An_unknown_or_case_figure_key_is_never_widened_to_a_list()
    {
        Api();
        var cut = Render<TechnicalWorkList>(p => p.Add(x => x.ProjectKey, "m2lb").Add(x => x.Work, "everything"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=twl-unknown]"));
        cut.FindAll("[data-testid=twl-item]").Should().BeEmpty();
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_Technical_Testing_view_has_the_same_shape_as_Manual_Testing_and_no_duplicate_destination()
    {
        var group = WorkspaceViews.Of(WorkspaceView.TechnicalTester).Groups!.Single(g => g.Id == "tt-workspace");
        group.Links!.Select(l => l.Label).Should().Equal("Teknisk testoversikt", "Finn og analyser", "Planlegg og klargjør", "Testplaner", "Integrasjonstesting");
        group.Links!.Select(l => l.Route).Should().OnlyHaveUniqueItems();
        group.Routes.Should().Contain(TechnicalTestingPresentation.VerifyRoute, "Verify & investigate stays reachable as a sub-workflow");
        var manual = WorkspaceViews.Of(WorkspaceView.ManualTesting).Groups!.Single(g => g.Id == "manual-workspace");
        manual.Links!.Select(l => l.Label).Should().Equal("Testoversikt", "Finn og forstå", "Planlegg og klargjør", "Testplaner");
    }
}
