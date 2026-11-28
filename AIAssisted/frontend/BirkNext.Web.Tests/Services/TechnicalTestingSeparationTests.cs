using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using D = BirkNext.Web.Services.TechnicalTestingDashboard;
using O = BirkNext.Web.Services.ManualTestingOverviewPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Technical Testing V1/V2 (2026-11-28): one inventory and one plan store, three independent assessments (what / how / how certain), the person's
/// method choice, the shared scope contract, separate manual and technical populations, technical case details and per-case results. Every figure is
/// compared by exact identities against the fixture below (the oracle), never by count alone. T-numbers refer to the V2 test matrix.
/// </summary>
public sealed class TechnicalTestingSeparationTests : BunitContext
{
    // ── Fixture (oracle): two modules (Skole, Hendelse), each with its own specification, contracts and test document ───────────────────────────

    private static DiscoveredScenario C(string id, string capability, ScenarioCandidateType type, string? contractType = null, string? canonical = null,
        bool automated = false, string? path = null, bool complete = true, params string[] morePaths) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = capability, Type = type, Title = $"Title {id}", SourceIdentity = "tss-" + id,
        CanonicalKey = canonical ?? $"{capability}:{id}",
        Steps = complete ? [new DiscoveredStep($"Trigger {id}", null)] : [],
        ExpectedBehaviour = complete ? $"Outcome {id}" : null,
        Evidence = [.. (contractType is null ? [] : new[] { new CandidateEvidenceRef("contract-operation", $"{path ?? "Skole/contracts/api.yaml"}:12#{id}", $"{contractType} Api 1.0") }),
            .. (contractType is null && path is not null ? new[] { new CandidateEvidenceRef("source", $"{path}#{id}") } : []),
            .. morePaths.Select(p => new CandidateEvidenceRef("source", $"{p}#{id}"))],
        ExistingTests = automated ? [new CandidateTestAssociation("T" + id, "T" + id, null, "names the scenario") { Origin = TestScenarioLinkOrigin.Scenario }]
            : [new CandidateTestAssociation("R" + id, "R" + id, null, "shares a requirement")],
    };

    private const string SkoleSpec = "Skole/specs/001-rapport/spec.md";
    /// <summary>The project's stored specifications (what Testoversikt returns): module evidence for the canonical module rule.</summary>
    private static readonly string[] Known = [SkoleSpec, "Hendelse/specs/001-inngrep/spec.md"];

    private static readonly DiscoveredScenario Acceptance = C("ac", ScenarioOwnership.Specification, ScenarioCandidateType.BusinessFunctional, canonical: "scenario:spec.md#1", path: SkoleSpec);
    private static readonly DiscoveredScenario Rest = C("rest", ScenarioOwnership.ApiContract, ScenarioCandidateType.ApiContract, "OpenApi", path: "Skole/contracts/rapport.openapi.yaml");
    private static readonly DiscoveredScenario GraphQl = C("gql", ScenarioOwnership.ApiContract, ScenarioCandidateType.ApiContract, "GraphQlSchema", automated: true, path: "Hendelse/autodoc/schema.graphql");
    private static readonly DiscoveredScenario Journey = C("jr", ScenarioOwnership.IntegrationJourneys, ScenarioCandidateType.Integration, path: "Skole/journeys/rapport.json", morePaths: ["Hendelse/journeys/rapport.json"]);
    private static readonly DiscoveredScenario GuideE2E = C("guide", ScenarioOwnership.SystemGuide, ScenarioCandidateType.BusinessE2E, path: SkoleSpec);
    private static readonly DiscoveredScenario TestE = C("teste", E2ERules.TestSpecification, ScenarioCandidateType.BusinessE2E, canonical: "testspec:Hendelse/docs/Testspesifikasjon.md#TEST-E-01", path: "Hendelse/docs/Testspesifikasjon.md");
    private static readonly DiscoveredScenario TestI = C("testi", E2ERules.TestSpecification, ScenarioCandidateType.Integration, canonical: "testspec:Hendelse/docs/Testspesifikasjon.md#TEST-I-01", path: "Hendelse/docs/Testspesifikasjon.md");
    private static readonly DiscoveredScenario DevMessaging = C("dev", ScenarioOwnership.DeveloperTests, ScenarioCandidateType.Integration, path: "Hendelse/tests/Messaging.cs");
    private static readonly DiscoveredScenario Unknown = C("unknown", "some-new-producer", ScenarioCandidateType.BusinessFunctional, path: SkoleSpec);
    private static readonly DiscoveredScenario IncompleteJourney = C("jr-x", ScenarioOwnership.IntegrationJourneys, ScenarioCandidateType.Integration, path: "Skole/journeys/x.json", complete: false);

    private static readonly IReadOnlyList<DiscoveredScenario> Inventory =
        [Acceptance, Rest, Rest with { CandidateId = "rest-copy" }, GraphQl, Journey, GuideE2E, TestE, TestI, DevMessaging, Unknown, IncompleteJourney];

    private static IReadOnlyList<string> Ids(D.Figure f) => [.. f.Candidates.Select(w => w.Candidate.CandidateId).Order(StringComparer.Ordinal)];

    // ── T01–T13, T32–T34: what / how / how certain ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T01_an_acceptance_scenario_is_manual_business_work_and_never_end_to_end_by_itself()
    {
        var c = TestWorkClassifier.Classify(Acceptance);
        (c.Category, c.Subject, c.E2E.Status, c.ManualDefault, c.TechnicalDefault).Should().Be((TestWorkCategory.ManualBusiness, TestSubject.Business, E2EStatus.NotE2E, true, false));
    }

    [Fact]
    public void T02_T05_T06_T33_contract_operations_are_REST_or_GraphQL_by_recorded_contract_type_technical_only_and_never_E2E()
    {
        TestWorkClassifier.Classify(Rest).Category.Should().Be(TestWorkCategory.TechnicalApi);
        TestWorkClassifier.Classify(GraphQl).Category.Should().Be(TestWorkCategory.TechnicalGraphQl);
        TestWorkClassifier.Classify(GraphQl).Subject.Should().Be(TestSubject.GraphQl);
        TestWorkClassifier.Classify(GraphQl).E2E.Status.Should().Be(E2EStatus.NotE2E, "a GraphQL operation alone is not proof of E2E");
        TestWorkClassifier.Classify(Rest).E2E.Reasons.Should().Contain(r => r.Contains("kontrakttest"));
        TestWorkClassifier.Classify(Rest).ManualDefault.Should().BeFalse();
        TestWorkClassifier.Classify(Rest with { Title = "GraphQL query getBarn" }).Category.Should().Be(TestWorkCategory.TechnicalApi, "the title never decides");
    }

    [Fact]
    public void T07_T08_T09_a_proven_chain_with_trigger_outcome_and_source_is_confirmed_and_hybrid()
    {
        foreach (var flow in new[] { Journey, GuideE2E })
        {
            var c = TestWorkClassifier.Classify(flow);
            c.E2E.Status.Should().Be(E2EStatus.Confirmed, flow.CapabilityId);
            c.E2E.Evidence!.Missing().Should().BeEmpty();
            c.Category.Should().Be(TestWorkCategory.Hybrid);
            (c.ManualDefault, c.TechnicalDefault).Should().Be((true, true), "hybrid flows stay accessible in both workspaces");
        }
        TestWorkClassifier.Classify(Journey).Subject.Should().Be(TestSubject.TechnicalE2E);
        TestWorkClassifier.Classify(GuideE2E).Subject.Should().Be(TestSubject.BusinessE2E);
        TestWorkClassifier.Classify(GuideE2E, ScenarioTestKind.Manual).Category.Should().Be(TestWorkCategory.ManualE2E, "T08 a confirmed flow chosen «Manuell»");
    }

    [Fact]
    public void A_proven_chain_without_trigger_or_outcome_is_only_a_candidate_with_the_missing_evidence_named()
    {
        var c = TestWorkClassifier.Classify(IncompleteJourney);
        c.E2E.Status.Should().Be(E2EStatus.Candidate);
        c.E2E.Evidence!.Missing().Should().Contain(m => m.Contains("startpunkt")).And.Contain(m => m.Contains("sluttresultat"));
    }

    [Fact]
    public void T34_declared_or_suggested_E2E_is_a_candidate_and_a_documented_integration_test_is_technical_integration()
    {
        TestWorkClassifier.Classify(TestE).E2E.Status.Should().Be(E2EStatus.Candidate, "TEST-E declares the level; it is not verified");
        TestWorkClassifier.Classify(DevMessaging).E2E.Status.Should().Be(E2EStatus.Candidate);
        TestWorkClassifier.Classify(DevMessaging).Surface.Should().Be(TechnicalSurface.Messaging);
        var testI = TestWorkClassifier.Classify(TestI);
        (testI.E2E.Status, testI.Category, testI.ManualDefault).Should().Be((E2EStatus.NotE2E, TestWorkCategory.TechnicalIntegration, false));
    }

    [Fact]
    public void T04_unknown_producer_needs_classification_and_stays_visible_in_technical_testing()
    {
        var c = TestWorkClassifier.Classify(Unknown);
        (c.Category, c.E2E.Status, c.TechnicalDefault).Should().Be((TestWorkCategory.NeedsClassification, E2EStatus.NeedsClarification, true));
    }

    [Fact]
    public void T03_T13_the_persons_choice_decides_the_method_is_shown_as_a_deviation_and_never_promotes_certainty()
    {
        var manualApi = TestWorkClassifier.Classify(Rest, ScenarioTestKind.Manual);
        manualApi.Category.Should().Be(TestWorkCategory.ManualBusiness);
        manualApi.E2E.Deviates.Should().BeTrue();
        manualApi.E2E.Proposed.Should().Be(TestExecutionStyle.Technical);
        manualApi.E2E.Reasons.Should().Contain(r => r.Contains("valget gjelder"));
        TestWorkClassifier.Classify(Acceptance, ScenarioTestKind.Both).Category.Should().Be(TestWorkCategory.Hybrid, "«Begge» shows it in both");
        TestWorkClassifier.Classify(Acceptance, ScenarioTestKind.Both).E2E.Status.Should().Be(E2EStatus.NotE2E, "a method choice never makes a flow E2E");
        TestWorkClassifier.Classify(TestE, ScenarioTestKind.Technical).E2E.Status.Should().Be(E2EStatus.Candidate, "never promoted to Confirmed");
        TestWorkClassifier.Classify(Journey, ScenarioTestKind.Technical).Category.Should().Be(TestWorkCategory.TechnicalE2E);
        TestWorkClassifier.Classify(Journey, ScenarioTestKind.Both).E2E.Deviates.Should().BeFalse("same as the proposal");
        TestWorkClassifier.RuleVersion.Should().Be("test-work/2");
        E2ERules.RuleVersion.Should().Be("e2e-rules/2");
    }

    [Fact]
    public void T11_T12_one_identity_is_one_piece_of_work_and_the_same_title_in_two_documents_is_two()
    {
        var sameTitle = Acceptance with { CandidateId = "ac2", SourceIdentity = "tss-ac2" };
        var work = D.Classify([Acceptance, sameTitle, Rest, Rest with { CandidateId = "rest-copy" }]);
        work.Select(w => w.Candidate.CandidateId).Should().BeEquivalentTo(["ac", "ac2", "rest"], "identity decides, never the title");
    }

    // ── Scope (T24–T28, T30) and cross-module flows (T10) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_T24_T25_module_scope_includes_a_cross_module_flow_in_each_module_it_touches_and_marks_it()
    {
        var all = D.Classify(Inventory, null, Known);
        var journey = all.Single(w => w.Candidate.CandidateId == "jr");
        journey.CrossModule.Should().BeTrue();
        journey.Modules.Should().BeEquivalentTo(["Skole", "Hendelse"]);
        IReadOnlyList<string> In(O.Scope s) => [.. all.Where(w => D.InScope(w, s)).Select(w => w.Candidate.CandidateId).Order(StringComparer.Ordinal)];
        In(O.Scope.Project).Should().HaveCount(all.Count, "T24 project = everything");
        In(new O.Scope(O.Level.Module, "Skole", null)).Should().Equal(["ac", "guide", "jr", "jr-x", "rest", "unknown"]);
        In(new O.Scope(O.Level.Module, "Hendelse", null)).Should().Equal(["dev", "gql", "jr", "teste", "testi"]);
    }

    [Fact]
    public void T26_T27_T30_specification_and_document_scope_count_only_their_files_and_an_unknown_choice_is_empty_not_broader()
    {
        var all = D.Classify(Inventory, null, Known);
        IReadOnlyList<string> In(O.Scope s) => [.. all.Where(w => D.InScope(w, s)).Select(w => w.Candidate.CandidateId).Order(StringComparer.Ordinal)];
        In(new O.Scope(O.Level.Specification, "Skole", null, "001-rapport")).Should().Equal(["ac", "guide", "unknown"], "T26 the pack's files only");
        In(new O.Scope(O.Level.Specification, "Skole", SkoleSpec)).Should().Equal(["ac", "guide", "unknown"], "T27 the document exactly");
        In(new O.Scope(O.Level.Specification, "Hendelse", SkoleSpec)).Should().BeEmpty("a document outside the chosen module is never matched");
        In(new O.Scope(O.Level.Specification, "Skole", "Skole/specs/999-gone/spec.md")).Should().BeEmpty("T30 a stale choice is empty, never broadened");
        In(new O.Scope(O.Level.Specification, null, null)).Should().BeEmpty();
    }

    [Fact]
    public void T28_clearing_a_selector_widens_exactly_one_level()
    {
        var all = D.Classify(Inventory, null, Known);
        int Count(O.Scope s) => all.Count(w => D.InScope(w, s));
        var document = Count(new O.Scope(O.Level.Specification, "Skole", SkoleSpec));
        var module = Count(new O.Scope(O.Level.Module, "Skole", null));
        var project = Count(O.Scope.Project);
        (document < module && module < project).Should().BeTrue("document ⊂ module ⊂ project");
    }

    // ── Populations (T21, T22) and exact figures (T23, T32) ─────────────────────────────────────────────────────────────────────────────────

    private static readonly Guid Plan = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static OverviewCase Case(string id, string? identity, ScenarioTestKind? kind, TestCaseReviewState review = TestCaseReviewState.Generated, CaseSourceState? source = null,
        ScenarioExecutionStatus? run = null, bool ready = false, string? spec = null) =>
        new() { PlanId = Plan, CaseId = id, Title = id, ScenarioIdentity = identity, TestKind = kind, Review = review, SourceState = source, Execution = run, Ready = ready,
            SpecificationPath = spec, Module = spec is null ? "Uten modultilknytning" : spec.Split('/')[0] };

    private static readonly IReadOnlyList<OverviewCase> Cases =
    [
        Case("c-rest", "tss-rest", null, TestCaseReviewState.Accepted, run: ScenarioExecutionStatus.Passed, ready: true),
        Case("c-ac", "tss-ac", null, spec: SkoleSpec),
        Case("c-ac-tech", "tss-ac", ScenarioTestKind.Technical, source: CaseSourceState.Changed, run: ScenarioExecutionStatus.Failed, spec: SkoleSpec),
        Case("c-jr-manual", "tss-jr", ScenarioTestKind.Manual, ready: true),
        Case("c-jr", "tss-jr", null, source: CaseSourceState.NotCompared),
        Case("c-legacy", null, null),
    ];

    [Fact]
    public void T22_T23_T32_technical_figures_count_exactly_their_records_once_and_code_evidence_is_never_a_run()
    {
        var d = D.Build(Inventory, Cases);
        Ids(d.Find(D.E2EConfirmed)!).Should().Equal(["guide", "jr"]);
        Ids(d.Find(D.E2ECandidate)!).Should().Equal(["dev", "jr-x", "teste"]);
        Ids(d.Find(D.Api)!).Should().Equal(["rest"]);
        Ids(d.Find(D.GraphQl)!).Should().Equal(["gql"]);
        Ids(d.Find(D.Integration)!).Should().Equal(["testi"]);
        Ids(d.Find(D.Automated)!).Should().Equal(["gql"], "a test that names the scenario; a shared requirement is not a link");
        Ids(d.Find(D.Clarify)!).Should().Equal(["unknown"]);
        d.Discovered.Concat(d.Prepared).Concat(d.Execution).Should().OnlyContain(f => f.Value == f.Candidates.Count + f.Cases.Count);

        IReadOnlyList<string> Cs(string key) => [.. d.Find(key)!.Cases.Select(c => c.CaseId).Order(StringComparer.Ordinal)];
        Cs(D.Prepared).Should().Equal(["c-ac-tech", "c-jr", "c-rest"], "«Manuell» and unclassified manual work are never technical");
        Cs(D.Ready).Should().Equal(["c-rest"]);
        Cs(D.SourceAffected).Should().Equal(["c-ac-tech", "c-jr"]);
        Cs(D.Executed).Should().Equal(["c-ac-tech", "c-rest"]);
        Cs(D.Passed).Should().Equal(["c-rest"]);
        Cs(D.Failed).Should().Equal(["c-ac-tech"]);
        d.Find(D.Executed)!.Cases.Should().NotContain(c => c.CaseId == "c-jr", "never inferred from automated evidence");
    }

    [Fact]
    public void T13_the_persons_choice_moves_a_scenario_between_populations_without_duplicating_it()
    {
        var decided = D.Decisions([new OverviewMethodDecision(SkoleSpec, "ac", "tss-ac", ScenarioTestKind.Technical, "lead", DateTimeOffset.UtcNow)]);
        var d = D.Build(Inventory, Cases, decided);
        d.Find(D.Prepared)!.Cases.Select(c => c.CaseId).Should().Contain("c-ac", "its scenario was decided «Teknisk»");
        d.Find(D.Deviation)!.Candidates.Select(w => w.Candidate.CandidateId).Should().Equal("ac");
        d.All.Count(w => w.Candidate.SourceIdentity == "tss-ac").Should().Be(1, "one logical scenario");
        D.Decisions([new OverviewMethodDecision(SkoleSpec, "old", null, ScenarioTestKind.Technical, "lead", DateTimeOffset.UtcNow)]).Should().BeEmpty("a legacy choice is never guessed onto a scenario");
    }

    [Fact]
    public void T21_the_manual_population_leaves_out_technical_only_work_and_keeps_hybrid_and_legacy()
    {
        var o = new ManualTestingOverview
        {
            ProjectKey = "m2lb", ScenariosAvailable = true,
            Specifications =
            [
                new(SkoleSpec, "Skole", "001-rapport", [new("tss-ac", "k", "US1", "S", 1, "P1", "AC"), new("tss-ac9", "k", "US1", "S", 2, "P1", "AC9")]),
                new("Hendelse/docs/Testspesifikasjon.md", "Hendelse", "Test", [
                    new("tss-teste", null, null, null, null, null, "TEST-E-01") { Level = ScenarioTestLevel.EndToEnd, LevelDeclared = true, Standalone = true },
                    new("tss-testi", null, null, null, null, null, "TEST-I-01") { Level = ScenarioTestLevel.Integration, Standalone = true }]),
            ],
            Cases = [.. Cases, Case("c-ac9", "tss-ac9", null, spec: SkoleSpec)],
            MethodDecisions = [new(SkoleSpec, "ac9", "tss-ac9", ScenarioTestKind.Technical, "lead", DateTimeOffset.UtcNow)],
        };
        var manual = O.ForManual(o);
        manual.Specifications.SelectMany(s => s.Scenarios).Select(s => s.Identity).Should().BeEquivalentTo(["tss-ac", "tss-teste"],
            "a scenario decided «Teknisk» and a documented non-E2E module test are technical-only; a declared TEST-E is hybrid");
        manual.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c-rest", "c-ac", "c-jr-manual", "c-jr", "c-legacy"],
            "a case typed «Teknisk» and a case of a technical-only scenario leave; untyped and legacy cases stay (never gone from both)");
        o.Cases.Should().HaveCount(7, "nothing is removed from the data");
    }

    [Fact]
    public void Manual_E2E_figures_count_only_manual_or_both_flows_and_their_manual_cases()
    {
        var manualCases = O.ForManual(new ManualTestingOverview { Cases = [.. Cases] }).Cases;
        var f = D.ManualE2E(Inventory, manualCases).ToDictionary(x => x.Key);
        Ids(f[D.ManualE2EConfirmed]).Should().Equal(["guide", "jr"], "confirmed never includes unreviewed candidates");
        Ids(f[D.ManualE2ECandidate]).Should().Equal(["dev", "jr-x", "teste"]);
        f[D.ManualE2ECases].Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c-jr-manual", "c-jr"]);
        f[D.ManualE2EReady].Cases.Select(c => c.CaseId).Should().Equal("c-jr-manual");
        Ids(f[D.ManualE2EMissing]).Should().Equal(["dev", "guide", "jr-x", "teste"], "flows without a case");
    }

    // ── Technical case model (T17) and secrets ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T17_technical_details_round_trip_through_the_case_form_without_touching_other_template_values()
    {
        var saved = new TestCaseDraft
        {
            Source = new ScenarioReference { ScenarioId = "c1" }, Generated = new TestCaseContent { Title = "T" },
            Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Technical, TemplateValues = new() { ["operation"] = "candidate field", ["tech.objective"] = "Old" } },
        };
        var form = PlanPreparePresentation.CaseForm.From(saved);
        form.Technical.Should().Equal(new Dictionary<string, string> { ["objective"] = "Old" });
        form.IsDirty(saved).Should().BeFalse();
        form.Technical["objective"] = "Rapporten lagres";
        form.Technical["operation"] = "POST /api/reports";
        form.IsDirty(saved).Should().BeTrue();
        var edits = form.ToEdits(saved);
        edits.TemplateValues.Should().Equal(new Dictionary<string, string> { ["operation"] = "candidate field", ["tech.objective"] = "Rapporten lagres", ["tech.operation"] = "POST /api/reports" });
        TechnicalCaseTemplates.Described(edits).Should().BeFalse("no expected response or state yet");
        TechnicalCaseTemplates.For(TestWorkCategory.TechnicalGraphQl).Select(f => f.Key).Should().Contain("operation").And.NotContain("events");
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", true)]
    [InlineData("client_secret=Abc123xyz789", true)]
    [InlineData("password: hemmelig1", true)]
    [InlineData("Rolle: rapportinnsender (Entra-app), token hentes av testverktøyet", false)]
    [InlineData("POST /api/skole/v1/skolenaervaer/reports", false)]
    [InlineData("correlation-id 9f1c2a", false)]
    public void Secrets_are_recognised_and_ordinary_technical_descriptions_are_not(string value, bool secret) =>
        TechnicalCaseTemplates.LooksLikeSecret(value).Should().Be(secret);

    // ── Rendering and drilldowns ─────────────────────────────────────────────────────────────────────────────────────────────────────────

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
    public void T22_T23_Teknisk_testoversikt_has_three_groups_candidate_figures_link_to_Finn_og_analyser_and_case_figures_open_exactly_their_cases()
    {
        Api();
        var cut = Render<TechnicalDashboard>(p => p.Add(x => x.ProjectKey, "m2lb"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ttd-kpi]").Should().HaveCount(16));
        cut.FindAll("[data-testid=ttd-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("found", "prepared", "execution");
        var kpis = cut.FindAll("[data-testid=ttd-kpi]").ToDictionary(k => k.GetAttribute("data-key")!, k => k);
        kpis[D.E2EConfirmed].GetAttribute("data-value").Should().Be("2");
        kpis[D.Api].QuerySelector("[data-testid=ttd-open]")!.GetAttribute("href").Should().Be("technical-testing/assess?work=api");
        kpis[D.Blocked].QuerySelector("[data-testid=ttd-open]").Should().BeNull("an empty figure opens nothing");
        cut.Markup.Should().NotContain("Title rest", "no inline candidate list on the overview");

        kpis[D.Failed].QuerySelector("[data-testid=ttd-open]")!.Click();
        var written = JSInterop.Invocations.Last(i => i.Identifier == "sessionStorage.setItem" && (string)i.Arguments[0]! == TabDraftStore.PlanCaseSelectionKey("m2lb"));
        var selection = System.Text.Json.JsonSerializer.Deserialize<PlanPreparePresentation.CaseSelection>((string)written.Arguments[1]!)!;
        selection.Cases.Select(c => c.CaseId).Should().Equal("c-ac-tech");
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().Contain($"manual-testing/plan?draft={Plan}");
    }

    [Fact]
    public void Finn_og_analyser_lists_exactly_the_candidates_of_the_opened_figure_with_why_and_missing_evidence()
    {
        Api();
        var cut = Render<TechnicalWorkList>(p => p.Add(x => x.ProjectKey, "m2lb").Add(x => x.Work, D.E2ECandidate));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=twl-item]").Should().HaveCount(3));
        cut.FindAll("[data-testid=twl-item]").Select(i => i.GetAttribute("data-candidate")).Should().BeEquivalentTo(["dev", "jr-x", "teste"]);
        cut.Markup.Should().Contain("Mangler bevis for");
        cut.FindAll("[data-testid=twl-cross-module]").Should().BeEmpty("none of these three spans modules");
    }

    [Fact]
    public void An_unknown_or_case_figure_key_is_never_widened_to_a_list()
    {
        Api();
        var cut = Render<TechnicalWorkList>(p => p.Add(x => x.ProjectKey, "m2lb").Add(x => x.Work, D.Prepared));
        cut.WaitForAssertion(() => cut.Find("[data-testid=twl-unknown]"));
        cut.FindAll("[data-testid=twl-item]").Should().BeEmpty();
    }

    [Fact]
    public void Technical_details_are_shown_only_for_a_technical_case_with_a_type_template_and_advanced_details()
    {
        var form = new PlanPreparePresentation.CaseForm { CaseId = "c1", TestKind = ScenarioTestKind.Technical };
        var cut = Render<TechnicalCaseDetails>(p => p.Add(x => x.Form, form).Add(x => x.Suggested, TestWorkCategory.TechnicalGraphQl));
        cut.FindAll("[data-testid=tcd-field]").Take(8).Select(f => f.GetAttribute("data-key")).Should().Equal(TechnicalCaseTemplates.Templates[TestWorkCategory.TechnicalGraphQl]);
        cut.Find("[data-testid=tcd-advanced]").QuerySelectorAll("[data-testid=tcd-field]").Should().HaveCount(TechnicalCaseTemplates.Fields.Count - 8);
        cut.Find("textarea[data-key=objective]").Input("Barnet hentes");
        form.Technical["objective"].Should().Be("Barnet hentes");
        cut.Find("textarea[data-key=auth]").Input("token=abcdefghijklmnop");
        cut.Find("[data-testid=tcd-secret]");
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

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
